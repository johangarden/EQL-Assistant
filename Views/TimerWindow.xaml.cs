using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;
using EQLOverlay.ViewModels;

namespace EQLOverlay.Views;

/// <summary>
/// A Time-Timer-style circular countdown: a red pie that shrinks as time runs
/// down, with Set / Play / Pause / Restart buttons. Always clickable (never
/// click-through), draggable by its card, and anchored via <see cref="PanelPlacement"/>.
/// </summary>
public partial class TimerWindow : Window
{
    private readonly AlertService _alerts;
    private readonly Action<double>? _onDurationSet;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;

    private double _total;
    private double _remaining;
    private bool _running;
    private DateTime _endTime;
    private string _lastText;
    private double _manualSeconds;   // duration used by "Normal (manual)" mode
    private string? _modeName;       // null = Normal; otherwise the selected preset/mob name

    /// <summary>Recent kills for the ➕ quick-add menu (Tracked = already a respawn).</summary>
    public Func<IReadOnlyList<(string Name, string Zone, DateTime When, bool Tracked)>>? RecentKillsProvider { get; set; }

    /// <summary>Quick-add confirmed: (mobName, zone, respawnSeconds).</summary>
    public Action<string, string, double>? AddRespawnRequested { get; set; }

    /// <summary>"Manage respawns…" picked from the ➕ menu.</summary>
    public Action? ManageRespawnsRequested { get; set; }

    /// <summary>Resolves a repop name to its RespawnEntry (alert config) —
    /// live edits in the Manager apply to already-running timers.</summary>
    public Func<string, RespawnEntry?>? RespawnLookup { get; set; }

    /// <summary>Test hooks (gated self-test only).</summary>
    internal (string? Mode, double Remaining, bool Running) BigState => (_modeName, _remaining, _running);
    internal IReadOnlyList<string> SecondaryNames => _repops.Select(e => e.Name).ToList();
    internal IReadOnlyList<(string Name, string State)> RowStates => _repops
        .Select(e => (e.Name, RankOf(e) switch { 0 => "up", 2 => "due", 3 => "learning", _ => "countdown" }))
        .ToList();

    // Named repops in the row list. The big pie shows the soonest LIVE
    // countdown; every other state — due (estimate elapsed, nothing seen),
    // UP (the log named it), learning (no estimate yet, counts up) — is a row.
    private sealed class RepopEntry
    {
        public required string Name;
        /// <summary>The estimate at kill time; 0 = none — a learning row.</summary>
        public double Total;
        /// <summary>The death that started this cycle.</summary>
        public DateTime StartTime;
        public DateTime EndTime; // meaningful only when Total > 0
        public required SecondaryTimerViewModel Vm;
        /// <summary>The before-it-spawns notice already fired for this run.</summary>
        public bool Warned;
        /// <summary>The spawn notice fired — estimate elapsed OR first sighting.</summary>
        public bool SpawnFired;
        /// <summary>Last log line naming this mob — the row reads UP.</summary>
        public DateTime? SeenAt;
    }
    private bool _bigWarned;    // ibid., for the repop on the big pie
    private DateTime _bigStart; // the death that started the big pie's cycle
    private readonly List<RepopEntry> _repops = new();
    private readonly ObservableCollection<SecondaryTimerViewModel> _secondaries = new();

    // How long finished rows stay on the panel before tidying themselves: an
    // UP row after its last sighting, a due row after the estimate elapsed, a
    // learning row after the kill. The Manager entry always survives — the
    // next death restarts any of them.
    private const double UpLingerSec = 180, DueLingerSec = 900, LearnLingerSec = 3600;

    private static readonly Brush WarnRed = Freeze(Color.FromRgb(0xFF, 0x52, 0x52));
    private static readonly Brush UpGreen = Freeze(Color.FromRgb(0x66, 0xBB, 0x6A));
    private static readonly Brush LearnDim = Freeze(Color.FromRgb(0x8A, 0x99, 0xB0));

    public TimerWindow(ConfigService config, AlertService alerts,
        double initialSeconds, double opacity, Action<double>? onDurationSet)
    {
        InitializeComponent();

        _alerts = alerts;
        _onDurationSet = onDurationSet;
        _total = _remaining = initialSeconds <= 0 ? 1 : initialSeconds;
        _manualSeconds = _total;
        _lastText = Format(_total);
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);

        _placement = new PanelPlacement(this, config, "timer", Anchor.TopRight, 40, 40);

        SecondariesControl.ItemsSource = _secondaries;

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
        _tick.Tick += (_, _) => OnTick();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        // Without this, a closed watch keeps ticking invisibly and beeps
        // when its repops hit zero (the "ghost spawn alert" bug).
        Closed += (_, _) => _tick.Stop();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _placement.Attach();
        UpdateModeToggle();
        UpdateVisual();
        _tick.Start();
    }

    /// <summary>Apply changed settings live — running repop timers survive.</summary>
    public void ApplySettings(double opacity)
    {
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement.Reload();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Interactive (NOT click-through) but no-activate + tool-window, so the
        // buttons always work yet it never steals focus from the game.
        NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, false);
    }

    // ---- public API (driven by the main overlay) ----------------------------

    public void PromptSet()
    {
        string? input = PromptDialog.Show(this, "Repop timer", "Duration — m:ss, 90s, 6m or 9m12s:", _lastText);
        if (input is null) return;
        double? sec = ParseDuration(input);
        if (sec is not > 0) return;

        _lastText = input.Trim();
        // The pen takes the pie for a hand-set duration. A repop holding it is
        // parked in the rows (it keeps counting there), never discarded.
        if (_modeName is not null) DemoteBig(spawnFired: false, seenAt: null);
        SetMode(null);
        _manualSeconds = sec.Value;
        _onDurationSet?.Invoke(sec.Value);
        SetDuration(sec.Value, start: false); // set, but don't auto-start — press Play
    }

    public void ResetPosition() => _placement.ResetToDefault();
    public void ReloadPlacement() => _placement.Reload();

    /// <summary>
    /// Start a repop for a named kill. The big pie always shows the repop that
    /// will spawn SOONEST — a fresh kill with a long respawn slots into the
    /// secondary list until it becomes the next one up.
    /// </summary>
    public void StartWith(double seconds, string? name = null)
    {
        if (name is null)
        {
            if (seconds > 0) SetDuration(seconds, start: true);
            return;
        }

        // Upsert: a fresh death replaces any old row for this mob (whatever
        // state it was in), keeps the current big repop as a candidate, then
        // the soonest countdown claims the pie. No estimate (seconds 0) =
        // a learning row that counts UP until the log names the mob again.
        RemoveSecondary(name);
        if (_modeName is not null
            && string.Equals(_modeName, name, StringComparison.OrdinalIgnoreCase))
        {
            // Re-kill of the mob on the pie: its old cycle is over — without
            // this, a learning re-kill would leave the stale countdown running.
            SetMode(null);
            _running = false;
            _remaining = 0;
        }
        else if (_modeName is not null && _running && _remaining > 0)
            DemoteBig(spawnFired: false, seenAt: null);

        var now = DateTime.Now;
        AddRow(new RepopEntry
        {
            Name = name,
            Total = seconds,
            StartTime = now,
            EndTime = now.AddSeconds(Math.Max(0, seconds)),
            Vm = new SecondaryTimerViewModel(name),
        });
        PromoteSoonest();
    }

    /// <summary>The log just NAMED this mob (RespawnLearner) — it is UP. Flip
    /// its row green and fire the spawn notice NOW instead of when the guess
    /// runs out. A sighting never moves any clock's base.</summary>
    public void NotifySighted(string name)
    {
        var now = DateTime.Now;
        if (_modeName is not null && _running
            && string.Equals(_modeName, name, StringComparison.OrdinalIgnoreCase))
        {
            FireSpawnAlert(_modeName);
            DemoteBig(spawnFired: true, seenAt: now);
            SetMode(null);
            _running = false;
            _remaining = 0;
            PromoteSoonest();
            return;
        }

        var e = _repops.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (e is null) return;
        e.SeenAt = now; // repeats refresh the UP linger, never re-alert
        if (!e.SpawnFired)
        {
            e.SpawnFired = true;
            FireSpawnAlert(e.Name);
        }
        SortSecondaries();
        UpdateSecondaries();
    }

    /// <summary>Move the big pie's repop back into the row list — as a
    /// countdown candidate, a due row, or an UP row (its fields say which).</summary>
    private void DemoteBig(bool spawnFired, DateTime? seenAt)
    {
        if (_modeName is null) return;
        AddRow(new RepopEntry
        {
            Name = _modeName,
            Total = _total,
            StartTime = _bigStart,
            EndTime = _endTime,
            Vm = new SecondaryTimerViewModel(_modeName),
            Warned = _bigWarned,
            SpawnFired = spawnFired,
            SeenAt = seenAt,
        });
    }

    /// <summary>Move the soonest still-counting repop onto the big pie. Due,
    /// UP and learning rows never take the pie — they have nothing to count.
    /// In manual mode nothing does: the pie is the user's egg timer.</summary>
    private void PromoteSoonest()
    {
        if (_manualMode) return;
        var now = DateTime.Now;
        var next = _repops
            .Where(e => e.SeenAt is null && !e.SpawnFired && e.Total > 0 && e.EndTime > now)
            .MinBy(e => e.EndTime);
        if (next is null)
        {
            UpdateVisual();
            return;
        }
        _repops.Remove(next);
        _secondaries.Remove(next.Vm);

        SetMode(next.Name);
        _total = next.Total;
        _endTime = next.EndTime;
        _bigStart = next.StartTime;
        _remaining = Math.Max(0, (_endTime - now).TotalSeconds);
        _running = _remaining > 0;
        _bigWarned = next.Warned; // the warn state rides along, no double notice
        SortSecondaries();
        UpdateVisual();
    }

    private void AddRow(RepopEntry entry)
    {
        RemoveSecondary(entry.Name);
        _repops.Add(entry);
        _secondaries.Add(entry.Vm);
        SortSecondaries();
    }

    /// <summary>Row order: UP first (go get it), then countdowns soonest-first,
    /// then due (freshest first), then learning (newest kill first).</summary>
    private static int RankOf(RepopEntry e) =>
        e.SeenAt is not null ? 0 : e.Total <= 0 ? 3 : e.SpawnFired || e.EndTime <= DateTime.Now ? 2 : 1;

    private void SortSecondaries()
    {
        _repops.Sort((a, b) =>
        {
            int ra = RankOf(a), rb = RankOf(b);
            if (ra != rb) return ra.CompareTo(rb);
            return ra switch
            {
                0 => Nullable.Compare(b.SeenAt, a.SeenAt),
                2 => b.EndTime.CompareTo(a.EndTime),
                3 => b.StartTime.CompareTo(a.StartTime),
                _ => a.EndTime.CompareTo(b.EndTime),
            };
        });
        for (int i = 0; i < _repops.Count; i++)
        {
            int cur = _secondaries.IndexOf(_repops[i].Vm);
            if (cur != i) _secondaries.Move(cur, i);
        }
    }

    private void RemoveSecondary(string name)
    {
        var e = _repops.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (e is null) return;
        _repops.Remove(e);
        _secondaries.Remove(e.Vm);
    }

    // ---- auto / manual toggle -------------------------------------------------
    // The learner made the old preset dropdown obsolete: deaths start their own
    // clocks now. What remains is one decision — does the soonest respawn claim
    // the pie (auto, the default), or does your egg timer keep it while the
    // respawns run in the row list (manual)?

    private bool _manualMode;

    private void OnModeToggle(object sender, RoutedEventArgs e) => SetManualMode(!_manualMode);

    internal void SetManualMode(bool manual)
    {
        if (_manualMode == manual) return;
        _manualMode = manual;
        if (manual)
        {
            // Park the pie's repop in the rows (it keeps counting there) and
            // hand the pie back to the manual timer, idle at its last duration.
            if (_modeName is not null) DemoteBig(spawnFired: false, seenAt: null);
            SetMode(null);
            _total = _remaining = _manualSeconds <= 0 ? 1 : _manualSeconds;
            _running = false;
            UpdateVisual();
        }
        else
        {
            PromoteSoonest();
        }
        UpdateModeToggle();
    }

    private void UpdateModeToggle()
    {
        ModeToggleBtn.Content = _manualMode ? "" : ""; // clock / sync
        ModeToggleBtn.ToolTip = _manualMode
            ? "Manual timer — respawns run in the list and never take the pie. Click for auto."
            : "Auto — the soonest respawn claims the pie. Click for a manual timer.";
    }

    private void SetMode(string? name)
    {
        _modeName = name;
        ModeLabel.Text = name ?? "Timer";
    }

    // ---- ➕ quick-add respawn -------------------------------------------------

    /// <summary>Kill something → ➕ → pick it → type the respawn time. No Manager trip.</summary>
    private void OnAddRespawn(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var kills = RecentKillsProvider?.Invoke() ?? Array.Empty<(string, string, DateTime, bool)>();

        if (kills.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No kills seen in the log yet",
                IsEnabled = false,
            });
        }

        foreach (var (name, zone, when, tracked) in kills)
        {
            string cn = name, cz = zone;
            string zoneText = zone.Length > 0 ? $"  ·  {zone}" : "";
            var mi = new MenuItem
            {
                Header = tracked
                    ? $"{name} — already tracked"
                    : $"{name}  ·  {Ago(when)}{zoneText}",
                IsEnabled = !tracked,
            };
            mi.Click += (_, _) => PromptAddRespawn(cn, cz);
            menu.Items.Add(mi);
        }

        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage respawns…" };
        manage.Click += (_, _) => ManageRespawnsRequested?.Invoke();
        menu.Items.Add(manage);

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PromptAddRespawn(string name, string zone)
    {
        string? input = PromptDialog.Show(this, "Add respawn",
            $"Respawn time for '{name}' — 15m, 6m40s… or leave empty to learn it from your kills:");
        if (input is null) return;
        double sec = 0; // empty / "auto" = the learner's job
        string t = input.Trim();
        if (t.Length > 0 && !t.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (ParseDuration(t) is not { } s || s <= 0) return;
            sec = s;
        }
        AddRespawnRequested?.Invoke(name, zone, sec);
    }

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        return span.TotalSeconds < 90 ? $"{Math.Max(0, span.TotalSeconds):0}s ago"
            : span.TotalMinutes < 90 ? $"{span.TotalMinutes:0} min ago"
            : $"{when:HH:mm}";
    }

    // ---- controls -----------------------------------------------------------

    private void OnSet(object sender, RoutedEventArgs e) => PromptSet();

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _running = false;               // pause
        }
        else if (_remaining > 0)
        {
            _running = true;                // start / resume
            _endTime = DateTime.Now.AddSeconds(_remaining);
        }
        UpdateVisual();
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        _remaining = _total;
        _running = true;
        _endTime = DateTime.Now.AddSeconds(_total);
        UpdateVisual();
    }

    private void SetDuration(double seconds, bool start)
    {
        _total = _remaining = seconds <= 0 ? 1 : seconds;
        _running = start;
        _endTime = DateTime.Now.AddSeconds(_total);
        _bigWarned = false; // a fresh run earns a fresh before-notice
        UpdateVisual();
    }

    private void Card_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ---- tick / render ------------------------------------------------------

    private void OnTick()
    {
        if (_running)
        {
            _remaining = (_endTime - DateTime.Now).TotalSeconds;
            // The configurable before-notice for the repop on the pie.
            if (_modeName is not null && !_bigWarned && _remaining > 0
                && RespawnLookup?.Invoke(_modeName) is { WarnEnabled: true } we
                && _remaining <= we.WarnSeconds)
            {
                _bigWarned = true;
                if (we.WarnPayload() is { } wp) _alerts.Fire(wp.Speak, wp.Sound);
            }
            if (_remaining <= 0)
            {
                _remaining = 0;
                _running = false;
                FireSpawnAlert(_modeName);
                if (_modeName is not null)
                {
                    // The estimate elapsed — that's all it means ("due", not
                    // "it's up"): the mob keeps a due row until the log names
                    // it or the next death restarts its cycle.
                    DemoteBig(spawnFired: true, seenAt: null);
                    SetMode(null);
                    PromoteSoonest();
                }
            }
        }
        UpdateVisual();
        UpdateSecondaries();
    }

    /// <summary>The spawn notice: the entry's configured phrase/sound (or the
    /// spoken default when the mob has no config — the manual timer too).</summary>
    private void FireSpawnAlert(string? name)
    {
        var entry = name is null ? null : RespawnLookup?.Invoke(name);
        if (entry is null)
        {
            // Spoken, not the Windows pling — a named mob announces itself.
            _alerts.Speak(name is null ? "Respawn" : $"{name} respawn");
            return;
        }
        if (entry.SpawnPayload() is { } p) _alerts.Fire(p.Speak, p.Sound);
    }

    private void UpdateSecondaries()
    {
        var now = DateTime.Now;
        bool resort = false;
        for (int i = _repops.Count - 1; i >= 0; i--)
        {
            var e = _repops[i];

            // UP: the log named it. Reads green until sightings stop coming.
            if (e.SeenAt is { } seen)
            {
                double ago = (now - seen).TotalSeconds;
                if (ago > UpLingerSec) { DropRow(i); continue; }
                e.Vm.RemainingText = $"UP {Format(ago)}";
                e.Vm.Foreground = UpGreen;
                continue;
            }

            // Learning: no estimate yet — count UP; this span becomes sample #1.
            if (e.Total <= 0)
            {
                double up = (now - e.StartTime).TotalSeconds;
                if (up > LearnLingerSec) { DropRow(i); continue; }
                e.Vm.RemainingText = $"{Format(up)}↑";
                e.Vm.Foreground = LearnDim;
                continue;
            }

            double rem = (e.EndTime - now).TotalSeconds;

            // Due: the estimate elapsed and nothing has been seen. The row
            // stays (counting how overdue the GUESS is) instead of vanishing.
            if (rem <= 0)
            {
                if (!e.SpawnFired)
                {
                    e.SpawnFired = true;
                    FireSpawnAlert(e.Name);
                    resort = true;
                }
                if (-rem > DueLingerSec) { DropRow(i); continue; }
                e.Vm.RemainingText = $"due {Format(-rem)}";
                e.Vm.Foreground = WarnRed;
                continue;
            }

            if (!e.Warned && RespawnLookup?.Invoke(e.Name) is { WarnEnabled: true } we
                && rem <= we.WarnSeconds)
            {
                e.Warned = true;
                if (we.WarnPayload() is { } wp) _alerts.Fire(wp.Speak, wp.Sound);
            }
            e.Vm.RemainingText = Format(rem);
            e.Vm.Foreground = new SolidColorBrush(ColorFor(e.Total > 0 ? rem / e.Total : 0));
        }
        if (resort) SortSecondaries();
    }

    private void DropRow(int index)
    {
        _secondaries.Remove(_repops[index].Vm);
        _repops.RemoveAt(index);
    }

    private void UpdateVisual()
    {
        double frac = _total > 0 ? Math.Clamp(_remaining / _total, 0, 1) : 0;
        BuildWedge(frac);
        Wedge.Fill = new SolidColorBrush(ColorFor(frac)); // green -> amber -> red as it runs down
        TimeText.Text = Format(_remaining);

        // Toggle button reflects state: ▶ when stopped, ⏸ when running.
        PlayPauseBtn.Content = _running ? "" : "";
        PlayPauseBtn.ToolTip = _running ? "Pause" : "Start / resume";

        bool warn = _running && _remaining > 0 && _remaining <= 10;
        if (warn)
        {
            bool on = (DateTime.Now.Millisecond / 300) % 2 == 0; // ~3 blinks/sec
            TimeText.Foreground = on ? Brushes.White : WarnRed;
            Wedge.Opacity = on ? 1.0 : 0.5;
        }
        else
        {
            TimeText.Foreground = Brushes.White;
            Wedge.Opacity = _running ? 1.0 : 0.82; // dim while paused/idle
        }
    }

    /// <summary>Countdown color ramp: green (full) → amber (half) → red (empty).</summary>
    private static Color ColorFor(double f)
    {
        Color green = Color.FromRgb(0x4C, 0xAF, 0x50);
        Color amber = Color.FromRgb(0xFF, 0xA7, 0x26);
        Color red = Color.FromRgb(0xE5, 0x39, 0x35);
        return f >= 0.5
            ? Lerp(amber, green, (f - 0.5) / 0.5)
            : Lerp(red, amber, f / 0.5);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private void BuildWedge(double frac)
    {
        const double cx = 66, cy = 66, r = 63;
        if (frac <= 0) { Wedge.Data = null; return; }
        if (frac >= 0.9999) { Wedge.Data = new EllipseGeometry(new Point(cx, cy), r, r); return; }

        double sweepDeg = 360 * frac;
        double a = sweepDeg * Math.PI / 180.0;
        var start = new Point(cx, cy - r);                        // 12 o'clock
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)); // clockwise

        var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
        fig.Segments.Add(new LineSegment(start, true));
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweepDeg > 180,
            SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        Wedge.Data = geo;
    }

    // ---- helpers ------------------------------------------------------------

    private static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    /// <summary>Parse "m:ss", "90s", "6m", "9m12s", or a plain number (seconds).</summary>
    public static double? ParseDuration(string text) => Services.DurationText.Parse(text);

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
