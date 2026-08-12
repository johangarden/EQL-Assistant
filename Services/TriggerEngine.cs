using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Models;
using EQLOverlay.ViewModels;

namespace EQLOverlay.Services;

/// <summary>
/// Turns log lines into live countdown bars, fires alerts, and nags about
/// missing buffs. Must be used from the UI thread (owns the bound collection
/// and a DispatcherTimer that refreshes the bars).
/// </summary>
public sealed class TriggerEngine
{
    private readonly AlertService _alerts;
    private readonly DispatcherTimer _tick;

    private readonly Dictionary<string, TimerBarViewModel> _active = new();
    private readonly Dictionary<string, TimerBarViewModel> _missing = new(); // keyed by trigger id
    private readonly Dictionary<string, DateTime> _lastRemind = new();
    private readonly HashSet<string> _seen = new();

    private List<TriggerDefinition> _triggers;
    private double _warnSeconds;
    private double _remindInterval;
    private int _missingCheckAccum;

    /// <summary>Bound directly by the overlay's ItemsControl.</summary>
    public ObservableCollection<TimerBarViewModel> Bars { get; } = new();

    /// <summary>Missing-buff REBUFF indicators — their own panel since 2.10
    /// (they used to sit inside the bars panel).</summary>
    public ObservableCollection<TimerBarViewModel> Reminders { get; } = new();

    /// <summary>Persistent present/missing cells for the Self-Buffs matrix panel.</summary>
    public ObservableCollection<MatrixCellViewModel> SelfCells { get; } = new();
    private readonly Dictionary<string, MatrixCellViewModel> _selfById = new();

    /// <summary>Persistent present/missing cells for the Target-Debuffs matrix panel.</summary>
    public ObservableCollection<MatrixCellViewModel> TargetCells { get; } = new();
    private readonly Dictionary<string, MatrixCellViewModel> _targetById = new();

    /// <summary>Raised when a "timerAuto" trigger matches: (durationSeconds, triggerName).</summary>
    public event Action<double, string>? TimerRequested;

    /// <summary>Raised when a trigger with flash text matches: (text, colorString).</summary>
    public event Action<string, string>? FlashRequested;

    /// <summary>Raised when a cooldown reducer cuts a running bar: (triggerName, seconds).</summary>
    public event Action<string, double>? BarReduced;

    /// <summary>Optional observed-duration lookup (SpellDurations): returns the
    /// learned recent-window max for a trigger name, or null.</summary>
    public Func<string, double?>? LearnedDuration { get; set; }

    private const double CastAnchorWindowSec = 15;   // begin-cast -> landing (same as SpellDurations)
    private const double QuickBuffWindowSec = 8;     // activation -> burst (observed: 3s)
    private (string Key, DateTime At)? _lastOwnCast; // rank-stripped, from "You begin casting X."
    private DateTime _quickBuffAt = DateTime.MinValue;
    private readonly HashSet<string> _everCast = new(StringComparer.Ordinal); // session, rank-stripped

    private static readonly Regex BeginCastRx = new(
        @"^You begin (?:casting|singing) (?<s>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The cast-anchor gate (the Companion's ruling): an anchored
    /// trigger's start pattern only counts when it follows YOUR own
    /// "You begin casting &lt;Name&gt;." within the window — an unanchored
    /// ambiguous landing draws nothing, because a bar that guesses which of
    /// four hastes just landed would lie about the duration. Auto (null)
    /// anchors EVERY library trigger — EQL is solo-first, so by default a
    /// groupmate's buff landing on you starts nothing; untick per trigger to
    /// opt into group play. Manual triggers stay unanchored on auto (their
    /// names often aren't castable spell names, so an anchor would silently
    /// never arrive).</summary>
    private bool AnchorAllows(TriggerDefinition trigger, DateTime eventTime)
    {
        bool anchored = trigger.CastAnchored
            ?? trigger.Id.StartsWith("lib-", StringComparison.Ordinal);
        if (!anchored) return true;

        string key = SpellDurations.BaseKey(trigger.Name);
        if (_lastOwnCast is { } c && c.Key == key
            && (eventTime - c.At).TotalSeconds is >= 0 and <= CastAnchorWindowSec)
            return true;

        // Quick Buff burst (the Companion's case 3): the AA lands the whole
        // spellbar at once with NO cast lines, so the named anchor never
        // arrives. During the activation window an anchored landing is
        // admitted when the spell is plausibly YOURS: cast at some point this
        // session, its bar/cell already running (a rebuff refresh), or known
        // to the duration learner (samples only mint from your own casts).
        if ((eventTime - _quickBuffAt).TotalSeconds is >= 0 and <= QuickBuffWindowSec)
        {
            if (_everCast.Contains(key)) return true;
            if (_active.Keys.Any(k => k == trigger.Id
                    || k.StartsWith(trigger.Id + "|", StringComparison.Ordinal))) return true;
            if (_selfById.TryGetValue(trigger.Id, out var sc) && sc.IsActive) return true;
            if (_targetById.TryGetValue(trigger.Id, out var tc) && tc.IsActive) return true;
            if (LearnedDuration?.Invoke(trigger.Name) is not null) return true;
        }
        return false;
    }

    /// <summary>Auto-learn triggers run on the learned estimate once samples
    /// exist (the configured value is only the starting point — the estimate
    /// may correct in EITHER direction, e.g. level-scaled durations shorter
    /// than the library's max-level number). Manual triggers enforce the
    /// configured duration exactly.</summary>
    /// <summary>The trigger's two alert payloads, each already narrowed to its
    /// chosen channel (phrase OR sound — never both). Disabled notices come
    /// back empty so downstream code needs no further gating.</summary>
    private static (double WarnAt, string? WarnSpeak, string? WarnSound,
                    bool OnFaded, string? FadedSpeak, string? FadedSound)
        AlertOf(TriggerDefinition t)
    {
        if (t.Alert is not { } a) return (0, null, null, false, null, null);
        bool warnOn = a.WarnEnabled == true;
        bool fadedOn = a.FadedEnabled == true;
        bool warnSpeaks = a.WarnMode != AlertConfig.ModeSound;
        bool fadedSpeaks = a.FadedMode != AlertConfig.ModeSound;
        return (
            warnOn ? a.AtSeconds : 0,
            warnOn && warnSpeaks ? a.Speak : null,
            warnOn && !warnSpeaks ? a.Sound : null,
            fadedOn,
            fadedOn && fadedSpeaks ? a.FadedSpeak : null,
            fadedOn && !fadedSpeaks ? a.FadedSound : null);
    }

    private double EffectiveDuration(TriggerDefinition trigger)
    {
        if (!trigger.DurationAuto) return trigger.DurationSeconds;
        return LearnedDuration?.Invoke(trigger.Name) is double learned && learned > 1
            ? learned
            : trigger.DurationSeconds;
    }

    private static readonly Regex TimestampPrefix =
        new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);

    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    public TriggerEngine(AppConfig config, AlertService alerts)
    {
        _alerts = alerts;
        _triggers = config.Triggers;
        _warnSeconds = config.Overlay.WarnSeconds;
        _remindInterval = config.Overlay.RemindIntervalSeconds;

        _tick = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(66) // ~15 fps
        };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        BuildMatrices();
    }

    public void UpdateConfig(AppConfig config)
    {
        _triggers = config.Triggers;
        _warnSeconds = config.Overlay.WarnSeconds;
        _remindInterval = config.Overlay.RemindIntervalSeconds;

        // Carry running matrix timers across the rebuild — saving in the
        // Manager must not blank an active buff (same idea as the repop fix).
        var running = new Dictionary<string, (DateTime End, bool FadeFired)>();
        foreach (var cell in SelfCells.Concat(TargetCells))
            if (cell.IsActive) running[cell.Key] = (cell.EndTimeLocal, cell.FadeAlertFired);

        BuildMatrices();

        foreach (var cell in SelfCells.Concat(TargetCells))
        {
            if (!running.TryGetValue(cell.Key, out var state)) continue;
            cell.Activate(state.End);
            cell.FadeAlertFired = state.FadeFired;
        }

        // Drop running bars whose trigger was deleted, disabled, or moved off
        // the bars panel; everything else keeps counting down untouched.
        var barIds = _triggers
            .Where(t => t.Enabled && t.Panel == Panels.Bars)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in _active.Keys.ToList())
        {
            if (key.StartsWith("__demo", StringComparison.Ordinal)) continue;
            int pipe = key.IndexOf('|');
            string id = pipe < 0 ? key : key[..pipe];
            if (!barIds.Contains(id)) Remove(key);
        }

        // Drop missing indicators whose trigger no longer wants them.
        foreach (var id in _missing.Keys.ToList())
        {
            var t = _triggers.FirstOrDefault(x => x.Id == id);
            if (t is null || !t.Enabled || !t.RemindWhenMissing)
            {
                if (_missing.Remove(id, out var mb)) Reminders.Remove(mb);
                _lastRemind.Remove(id);
            }
        }
    }

    /// <summary>Clear all bars and tracking state (used when switching loadouts).</summary>
    public void Reset()
    {
        _active.Clear();
        _missing.Clear();
        _lastRemind.Clear();
        _seen.Clear();
        Bars.Clear();
        Reminders.Clear();
        foreach (var c in SelfCells) c.Deactivate();
        foreach (var c in TargetCells) c.Deactivate();
    }

    /// <summary>(Re)build the matrix cells from the current trigger set (all start missing).</summary>
    private void BuildMatrices()
    {
        SelfCells.Clear(); _selfById.Clear();
        TargetCells.Clear(); _targetById.Clear();

        foreach (var t in _triggers)
        {
            if (!t.Enabled) continue;
            var (cells, byId) = t.Panel switch
            {
                Panels.SelfBuffs => (SelfCells, _selfById),
                Panels.TargetDebuffs => (TargetCells, _targetById),
                _ => (null, null),
            };
            if (cells is null || byId is null) continue;

            var al = AlertOf(t);
            var cell = new MatrixCellViewModel(t.Id, t.Name, t.DurationSeconds,
                al.WarnAt, al.OnFaded, al.WarnSpeak, al.WarnSound,
                al.FadedSpeak, al.FadedSound);
            byId[t.Id] = cell;
            cells.Add(cell);
        }
    }

    public void ProcessLine(string rawLine)
    {
        DateTime eventTime = ExtractTimestamp(rawLine, out string body);

        var cast = BeginCastRx.Match(body);
        if (cast.Success)
        {
            string castKey = SpellDurations.BaseKey(cast.Groups["s"].Value);
            _lastOwnCast = (castKey, eventTime);
            _everCast.Add(castKey);
        }
        else if (body.StartsWith("You activate Quick Buff.", StringComparison.Ordinal))
        {
            // ("Caladar activates Quick Buff." is someone else — no window.)
            _quickBuffAt = eventTime;
        }

        foreach (var trigger in _triggers)
        {
            if (!trigger.Enabled) continue;

            // Flash-only panel: screen-center text on match, nothing else.
            if (trigger.Panel == Panels.Flash)
            {
                if (trigger.StartRegex is { } fr && fr.IsMatch(body))
                {
                    string text = string.IsNullOrWhiteSpace(trigger.Alert?.FlashText)
                        ? trigger.Name : trigger.Alert!.FlashText!;
                    FlashRequested?.Invoke(text, TriggerColors.For(trigger));
                }
                continue;
            }

            // Any other trigger may also carry an optional flash on its start match.
            if (!string.IsNullOrWhiteSpace(trigger.Alert?.FlashText)
                && trigger.StartRegex is { } sr && sr.IsMatch(body))
                FlashRequested?.Invoke(trigger.Alert!.FlashText!, TriggerColors.For(trigger));

            if (trigger.Panel == Panels.SelfBuffs)
            {
                ProcessMatrixLine(_selfById, trigger, body, eventTime);
                continue;
            }
            if (trigger.Panel == Panels.TargetDebuffs)
            {
                ProcessMatrixLine(_targetById, trigger, body, eventTime);
                continue;
            }

            if (trigger.Panel == Panels.TimerAuto)
            {
                if (trigger.StartRegex is { } tr && tr.IsMatch(body))
                    TimerRequested?.Invoke(trigger.DurationSeconds, trigger.Name);
                continue;
            }

            // Anything else that isn't "bars" is skipped rather than falling
            // through to the bars logic.
            if (trigger.Panel != Panels.Bars) continue;

            // Default: countdown bars (Area 1).
            if (trigger.EndRegex is { } endRx)
            {
                var m = endRx.Match(body);
                if (m.Success) Remove(BuildKey(trigger, m));
            }

            if (trigger.StartRegex is { } startRx)
            {
                var m = startRx.Match(body);
                if (m.Success && AnchorAllows(trigger, eventTime))
                    StartOrRefresh(trigger, m, eventTime);
            }

            // Cooldown reducer: each match cuts time off this trigger's RUNNING
            // bars (SK: Reave landing shaves 60s off the Harm Touch cooldown).
            if (trigger.ReduceRegex is { } reduceRx && trigger.ReduceSeconds > 0
                && reduceRx.IsMatch(body))
            {
                bool reduced = false;
                foreach (var (key, bar) in _active)
                {
                    if (key != trigger.Id
                        && !key.StartsWith(trigger.Id + "|", StringComparison.Ordinal))
                        continue;
                    bar.ReduceBy(trigger.ReduceSeconds);
                    bar.Refresh(eventTime, _warnSeconds);
                    reduced = true;
                }
                if (reduced) BarReduced?.Invoke(trigger.Name, trigger.ReduceSeconds);
            }
        }
    }

    private void ProcessMatrixLine(Dictionary<string, MatrixCellViewModel> byId,
        TriggerDefinition trigger, string body, DateTime eventTime)
    {
        if (!byId.TryGetValue(trigger.Id, out var cell)) return;

        if (trigger.EndRegex is { } endRx && endRx.IsMatch(body))
            cell.Deactivate();

        if (trigger.StartRegex is { } startRx && startRx.IsMatch(body)
            && AnchorAllows(trigger, eventTime))
            cell.Activate(eventTime.AddSeconds(EffectiveDuration(trigger)));
    }

    private void StartOrRefresh(TriggerDefinition trigger, Match match, DateTime eventTime)
    {
        _seen.Add(trigger.Id);

        string key = BuildKey(trigger, match);
        double duration = EffectiveDuration(trigger);
        DateTime end = eventTime.AddSeconds(duration);

        if (_active.TryGetValue(key, out var existing))
        {
            if (trigger.RefreshOnRetrigger)
            {
                existing.Restart(duration, end);
                Reposition(existing);
            }
            return;
        }

        var al = AlertOf(trigger);
        var vm = TimerBarViewModel.CreateTimer(
            key, BuildLabel(trigger, match), trigger.Category,
            duration, end, MakeBrush(TriggerColors.For(trigger)),
            al.WarnAt, al.OnFaded, al.WarnSpeak, al.WarnSound,
            al.FadedSpeak, al.FadedSound,
            waitsForFade: trigger.EndRegex is not null);

        _active[key] = vm;
        InsertSorted(vm);
    }

    private void Remove(string key)
    {
        if (_active.Remove(key, out var vm))
            Bars.Remove(vm);
    }

    public void AddDemoTimer()
    {
        int n = _active.Count(k => k.Key.StartsWith("__demo", StringComparison.Ordinal)) + 1;
        string[] cats = { "Buffs", "HoTs", "DoTs" };
        string[] colors = { "#4FC3F7", "#81C784", "#E57373" };
        int idx = (n - 1) % cats.Length;
        double dur = 10 + n * 4;

        string key = "__demo" + n;
        var vm = TimerBarViewModel.CreateTimer(key, $"Demo {cats[idx]} {n}", cats[idx],
            dur, DateTime.Now.AddSeconds(dur), MakeBrush(colors[idx]),
            alertAtSeconds: 0, alertOnExpire: false, alertSpeak: null, alertSound: null);
        _active[key] = vm;
        InsertSorted(vm);
    }

    private void Tick()
    {
        var now = DateTime.Now;
        UpdateBars(now);
        UpdateMatrix(SelfCells, now);
        UpdateMatrix(TargetCells, now);
    }

    /// <summary>Test hook: a REBUFF row for ~15s (panel placement without
    /// waiting for a real buff to drop).</summary>
    public void AddDemoReminder()
    {
        var mb = TimerBarViewModel.CreateMissing(
            "missing|__demo" + DateTime.Now.Ticks, "Demo Rebuff", "Buffs", MakeBrush("#E53935"));
        Reminders.Add(mb);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        t.Tick += (_, _) => { t.Stop(); Reminders.Remove(mb); };
        t.Start();
    }

    /// <summary>Test hook: drop a demo cell into the Self matrix (present ~15s, then missing).</summary>
    public void AddDemoMatrixCell() => AddDemoCell(SelfCells, _selfById, "Buff");

    /// <summary>Test hook: drop a demo cell into the Target-Debuffs matrix.</summary>
    public void AddDemoTargetCell() => AddDemoCell(TargetCells, _targetById, "Debuff");

    private static void AddDemoCell(ObservableCollection<MatrixCellViewModel> cells,
        Dictionary<string, MatrixCellViewModel> byId, string label)
    {
        int n = byId.Keys.Count(k => k.StartsWith("__demoCell", StringComparison.Ordinal)) + 1;
        string key = "__demoCell" + n;
        var cell = new MatrixCellViewModel(key, $"Demo {label} {n}", 15, 0, false, null, null);
        cell.Activate(DateTime.Now.AddSeconds(15));
        byId[key] = cell;
        cells.Add(cell);
    }

    private void UpdateMatrix(ObservableCollection<MatrixCellViewModel> cells, DateTime now)
    {
        foreach (var cell in cells)
        {
            if (!cell.IsActive) continue;

            bool expired = cell.Refresh(now, _warnSeconds);
            if (expired)
            {
                if (cell.AlertOnExpire) _alerts.Fire(cell.AlertFadedSpeak, cell.AlertFadedSound);
            }
            else if (cell.AlertAtSeconds > 0 && !cell.FadeAlertFired &&
                     cell.RemainingSeconds > 0 && cell.RemainingSeconds <= cell.AlertAtSeconds)
            {
                cell.FadeAlertFired = true;
                _alerts.Fire(cell.AlertSpeak, cell.AlertSound);
            }
        }
    }

    /// <summary>An overrunning bar squats at most this long past its estimate
    /// before the unwitnessed cull removes it (the fade line never arrived —
    /// death/zoning ate it).</summary>
    public const double OverrunCapSec = 60;

    private void UpdateBars(DateTime now)
    {
        List<TimerBarViewModel>? expired = null;
        foreach (var bar in Bars)
        {
            bar.Refresh(now, _warnSeconds);

            if (bar.IsOverrun)
            {
                if (bar.OverrunSeconds > OverrunCapSec) (expired ??= new()).Add(bar);
                continue; // no fade warnings while gray
            }

            if (!bar.IsMissing && bar.AlertAtSeconds > 0 && !bar.FadeAlertFired &&
                bar.RemainingSeconds > 0 && bar.RemainingSeconds <= bar.AlertAtSeconds)
            {
                bar.FadeAlertFired = true;
                _alerts.Fire(bar.AlertSpeak, bar.AlertSound);
            }

            if (bar.IsExpired)
            {
                // A bar with an end pattern doesn't vanish on a mere estimate:
                // it grays out and counts UP until the real fade line (which
                // also teaches the learner the true duration).
                if (bar.WaitsForFade)
                {
                    if (bar.AlertOnExpire) _alerts.Fire(bar.AlertFadedSpeak, bar.AlertFadedSound);
                    bar.EnterOverrun();
                }
                else
                {
                    (expired ??= new()).Add(bar);
                }
            }
        }

        if (expired is not null)
        {
            foreach (var bar in expired)
            {
                if (bar.AlertOnExpire && !bar.IsOverrun) // overrun already alerted at 0
                    _alerts.Fire(bar.AlertFadedSpeak, bar.AlertFadedSound);
                _active.Remove(bar.Key);
                Bars.Remove(bar);
            }
        }

        // Missing-buff scan ~ once per second (15 ticks).
        if (++_missingCheckAccum >= 15)
        {
            _missingCheckAccum = 0;
            CheckMissing(now);
        }
    }

    private void CheckMissing(DateTime now)
    {
        foreach (var t in _triggers)
        {
            if (!t.Enabled || !t.RemindWhenMissing || !_seen.Contains(t.Id))
                continue;

            bool active = _active.Keys.Any(k =>
                k == t.Id || k.StartsWith(t.Id + "|", StringComparison.Ordinal));

            if (active)
            {
                if (_missing.Remove(t.Id, out var mb)) Reminders.Remove(mb);
                _lastRemind.Remove(t.Id);
                continue;
            }

            if (!_missing.ContainsKey(t.Id))
            {
                var mb = TimerBarViewModel.CreateMissing(
                    "missing|" + t.Id, t.Name, t.Category, MakeBrush("#E53935"));
                _missing[t.Id] = mb;
                Reminders.Add(mb);
                _alerts.Speak($"{t.Name} missing");
                _lastRemind[t.Id] = now;
            }
            else if (!_lastRemind.TryGetValue(t.Id, out var last) ||
                     (now - last).TotalSeconds >= _remindInterval)
            {
                _alerts.Speak($"{t.Name} missing");
                _lastRemind[t.Id] = now;
            }
        }
    }

    // ---- ordering -----------------------------------------------------------

    private void InsertSorted(TimerBarViewModel vm)
    {
        for (int i = 0; i < Bars.Count; i++)
        {
            if (!string.Equals(Bars[i].Category, vm.Category, StringComparison.OrdinalIgnoreCase))
                continue;

            for (; i < Bars.Count &&
                   string.Equals(Bars[i].Category, vm.Category, StringComparison.OrdinalIgnoreCase); i++)
            {
                if (vm.EndTimeLocal < Bars[i].EndTimeLocal)
                {
                    Bars.Insert(i, vm);
                    return;
                }
            }
            Bars.Insert(i, vm);
            return;
        }
        Bars.Add(vm);
    }

    private void Reposition(TimerBarViewModel vm)
    {
        if (Bars.Remove(vm))
            InsertSorted(vm);
    }

    // ---- helpers ------------------------------------------------------------

    private static string BuildKey(TriggerDefinition trigger, Match match)
    {
        var target = match.Groups["target"];
        return target.Success && target.Value.Length > 0
            ? trigger.Id + "|" + target.Value
            : trigger.Id;
    }

    private static string BuildLabel(TriggerDefinition trigger, Match match)
    {
        var target = match.Groups["target"];
        return target.Success && target.Value.Length > 0
            ? $"{trigger.Name} — {target.Value}"
            : trigger.Name;
    }

    private static DateTime ExtractTimestamp(string line, out string body)
    {
        var m = TimestampPrefix.Match(line);
        if (m.Success)
        {
            body = line.Substring(m.Length);
            string ts = Regex.Replace(m.Groups["ts"].Value.Trim(), @"\s+", " ");
            if (DateTime.TryParseExact(ts, TimestampFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed;
        }
        else
        {
            body = line;
        }
        return DateTime.Now;
    }

    private static SolidColorBrush MakeBrush(string color)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(color);
            return new SolidColorBrush(c);
        }
        catch
        {
            return new SolidColorBrush(Colors.DodgerBlue);
        }
    }
}
