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

    /// <summary>Supplies the named-mob presets (from timerAuto triggers) for the menu.</summary>
    public Func<IReadOnlyList<(string Name, double Seconds)>>? PresetProvider { get; set; }

    // Secondary named repops still running (the big watch shows the most recent).
    private sealed class RepopEntry
    {
        public required string Name;
        public double Total;
        public DateTime EndTime;
        public required SecondaryTimerViewModel Vm;
    }
    private readonly List<RepopEntry> _repops = new();
    private readonly ObservableCollection<SecondaryTimerViewModel> _secondaries = new();

    private static readonly Brush WarnRed = Freeze(Color.FromRgb(0xFF, 0x52, 0x52));

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
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _placement.Attach();
        UpdateVisual();
        _tick.Start();
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
        string? input = PromptDialog.Show(this, "Repop timer", "Duration — m:ss, 90s, or 6m:", _lastText);
        if (input is null) return;
        double? sec = ParseDuration(input);
        if (sec is not > 0) return;

        _lastText = input.Trim();
        SetDuration(sec.Value, start: false); // set, but don't auto-start — press Play
        if (_modeName is null) { _manualSeconds = sec.Value; _onDurationSet?.Invoke(sec.Value); }
    }

    public void ResetPosition() => _placement.ResetToDefault();
    public void ReloadPlacement() => _placement.Reload();

    /// <summary>Start (or restart) the timer at the given duration — used by auto-start triggers.</summary>
    public void StartWith(double seconds, string? name = null)
    {
        if (seconds <= 0) return;

        if (name is not null)
        {
            // The big watch shows the most recent kill; if it's currently running a
            // *different* named repop, move that one to the secondary list so it isn't lost.
            if (_modeName is not null && _running && _remaining > 0
                && !string.Equals(_modeName, name, StringComparison.OrdinalIgnoreCase))
                AddSecondary(_modeName, _total, _endTime);

            RemoveSecondary(name); // re-kill of a mob that was a secondary -> it becomes the big watch
            SetMode(name);
        }

        SetDuration(seconds, start: true);
    }

    private void AddSecondary(string name, double total, DateTime endTime)
    {
        RemoveSecondary(name);
        var vm = new SecondaryTimerViewModel(name);
        _repops.Add(new RepopEntry { Name = name, Total = total, EndTime = endTime, Vm = vm });
        _secondaries.Add(vm);
    }

    private void RemoveSecondary(string name)
    {
        var e = _repops.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (e is null) return;
        _repops.Remove(e);
        _secondaries.Remove(e.Vm);
    }

    // ---- mode / preset menu -------------------------------------------------

    private void OnMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var normal = new MenuItem { Header = "Normal mode (manual)", IsChecked = _modeName is null };
        normal.Click += (_, _) => { SetMode(null); SetDuration(_manualSeconds, start: false); };
        menu.Items.Add(normal);

        var presets = PresetProvider?.Invoke();
        if (presets is { Count: > 0 })
        {
            menu.Items.Add(new Separator());
            foreach (var (name, seconds) in presets)
            {
                string cn = name;
                double cs = seconds;
                var mi = new MenuItem
                {
                    Header = $"{name}  ({Format(seconds)})",
                    IsChecked = string.Equals(name, _modeName, StringComparison.OrdinalIgnoreCase),
                };
                mi.Click += (_, _) => { SetMode(cn); SetDuration(cs, start: false); };
                menu.Items.Add(mi);
            }
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SetMode(string? name)
    {
        _modeName = name;
        ModeLabel.Text = name ?? "Timer";
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
            if (_remaining <= 0)
            {
                _remaining = 0;
                _running = false;
                _alerts.Beep();
            }
        }
        UpdateVisual();
        UpdateSecondaries();
    }

    private void UpdateSecondaries()
    {
        var now = DateTime.Now;
        for (int i = _repops.Count - 1; i >= 0; i--)
        {
            var e = _repops[i];
            double rem = (e.EndTime - now).TotalSeconds;
            if (rem <= 0)
            {
                _alerts.Beep();
                _secondaries.Remove(e.Vm);
                _repops.RemoveAt(i);
                continue;
            }
            e.Vm.RemainingText = Format(rem);
            e.Vm.Foreground = new SolidColorBrush(ColorFor(e.Total > 0 ? rem / e.Total : 0));
        }
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

    /// <summary>Parse "m:ss", "90s", "6m", or a plain number (seconds).</summary>
    public static double? ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return null;

        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out int m) && m >= 0
                && int.TryParse(parts[1], out int s) && s is >= 0 and < 60)
                return m * 60 + s;
            return null;
        }

        double mult = 1;
        if (text.EndsWith('m')) { mult = 60; text = text[..^1]; }
        else if (text.EndsWith('s')) { text = text[..^1]; }

        return double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out double n) && n > 0
            ? n * mult
            : null;
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
