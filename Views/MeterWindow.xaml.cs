using System.Collections.ObjectModel;
using System.Windows;
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
/// ACT-style parse meter: ranked damage (or healing) sources for the current
/// fight, with an incoming-damage footer for the player and their pet.
/// Interactive (never click-through) but no-activate, like the repop watch.
/// </summary>
public partial class MeterWindow : Window
{
    private const int MaxRows = 8;

    private readonly CombatParser _parser;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private readonly ObservableCollection<MeterRowViewModel> _rows = new();
    private readonly Dictionary<string, Brush> _fillByName = new(StringComparer.OrdinalIgnoreCase);
    private bool _showHealing;
    private int _nextColor;

    private static readonly Brush SelfFill = Freeze(Color.FromRgb(0xFF, 0xC1, 0x2E));
    private static readonly Brush[] Palette =
    {
        Freeze(Color.FromRgb(0x4F, 0xC3, 0xF7)),
        Freeze(Color.FromRgb(0x81, 0xC7, 0x84)),
        Freeze(Color.FromRgb(0xE5, 0x73, 0x73)),
        Freeze(Color.FromRgb(0xBA, 0x68, 0xC8)),
        Freeze(Color.FromRgb(0xFF, 0xB7, 0x4D)),
        Freeze(Color.FromRgb(0x64, 0xB5, 0xF6)),
        Freeze(Color.FromRgb(0x4D, 0xB6, 0xAC)),
        Freeze(Color.FromRgb(0xF0, 0x62, 0x92)),
        Freeze(Color.FromRgb(0xAE, 0xD5, 0x81)),
        Freeze(Color.FromRgb(0xA1, 0x88, 0x7F)),
    };

    public MeterWindow(ConfigService config, CombatParser parser, double opacity)
    {
        InitializeComponent();

        _parser = parser;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, config, "meter", Anchor.TopRight, 40, 300);

        RowsControl.ItemsSource = _rows;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => Refresh();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _placement.Attach();
        Refresh();
        _tick.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Interactive (NOT click-through) but no-activate + tool-window, so the
        // DPS/HPS toggle always works yet it never steals focus from the game.
        NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, false);
    }

    public void ResetPosition() => _placement.ResetToDefault();

    // ---- controls -------------------------------------------------------------

    private void OnToggleMetric(object sender, RoutedEventArgs e)
    {
        _showHealing = !_showHealing;
        ModeBtn.Content = _showHealing ? "HPS" : "DPS";
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _parser.Reset();
        Refresh();
    }

    private void Card_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ---- refresh ----------------------------------------------------------------

    private void Refresh()
    {
        _parser.Tick(DateTime.Now);

        string metric = _showHealing ? "HPS" : "DPS";
        if (!_parser.HasData)
        {
            TitleText.Text = $"{metric} meter";
            SummaryText.Text = "waiting for combat…";
            _rows.Clear();
            UpdateIncoming();
            return;
        }

        string target = _parser.TargetLabel;
        TitleText.Text = string.IsNullOrEmpty(target) ? $"{metric} meter" : $"{metric} — {target}";

        string state = _parser.InCombat ? "" : " · ended";
        SummaryText.Text =
            $"{FormatDuration(_parser.DurationSeconds)} · total {FormatDps(_parser.TotalPerSecond(_showHealing))} {metric.ToLowerInvariant()}{state}";

        var ranked = _parser.GetRows(_showHealing);
        int count = Math.Min(MaxRows, ranked.Count);
        double top = count > 0 ? ranked[0].Total : 0;

        while (_rows.Count > count) _rows.RemoveAt(_rows.Count - 1);
        while (_rows.Count < count) _rows.Add(new MeterRowViewModel());

        for (int i = 0; i < count; i++)
        {
            var r = ranked[i];
            var row = _rows[i];
            row.Name = r.Name;
            row.Fraction = top > 0 ? r.Total / top : 0;
            row.ValueText = $"{FormatDps(r.Dps)}  ({FormatNum(r.Total)}, {r.Percent:0}%)";
            row.Fill = FillFor(r.Name);
        }

        UpdateIncoming();
    }

    private void UpdateIncoming()
    {
        IncomingSelfValue.Text = _parser.HasData
            ? $"{FormatDps(_parser.IncomingSelfDps)} dps · {FormatNum(_parser.IncomingSelfTotal)}"
            : "—";

        bool hasPet = !string.IsNullOrWhiteSpace(_parser.PetName);
        IncomingPetRow.Visibility = hasPet ? Visibility.Visible : Visibility.Collapsed;
        if (hasPet)
        {
            IncomingPetLabel.Text = _parser.PetName.Trim() + " (pet)";
            IncomingPetValue.Text = _parser.HasData
                ? $"{FormatDps(_parser.IncomingPetDps)} dps · {FormatNum(_parser.IncomingPetTotal)}"
                : "—";
        }
    }

    /// <summary>Stable per-name row color; the logging character is always gold.</summary>
    private Brush FillFor(string name)
    {
        if (name.Equals(_parser.SelfName, StringComparison.OrdinalIgnoreCase)
            || name.Equals("You", StringComparison.OrdinalIgnoreCase))
            return SelfFill;

        if (_fillByName.TryGetValue(name, out var brush)) return brush;
        brush = Palette[_nextColor++ % Palette.Length];
        _fillByName[name] = brush;
        return brush;
    }

    // ---- formatting -------------------------------------------------------------

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private static string FormatDps(double v) =>
        v >= 100 ? v.ToString("N0") : v.ToString("0.0");

    private static string FormatNum(double v) => v.ToString("N0");

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
