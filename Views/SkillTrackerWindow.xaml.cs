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
/// Small session skill tracker: one bar per configured ability (backstab,
/// reave, Smite, …) filled by its session hit rate. Counts accumulate across
/// fights and only the panel's ⟲ button clears them — made for watching a
/// skill's land rate improve while you grind it.
/// </summary>
public partial class SkillTrackerWindow : Window
{
    private readonly CombatParser _parser;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private readonly ObservableCollection<MeterRowViewModel> _rows = new();
    private List<string> _skills;

    private static readonly Brush LowFill = Freeze(Color.FromRgb(0xE5, 0x73, 0x73));  // < 60%
    private static readonly Brush MidFill = Freeze(Color.FromRgb(0xFF, 0xB7, 0x4D));  // 60–85%
    private static readonly Brush HighFill = Freeze(Color.FromRgb(0x81, 0xC7, 0x84)); // ≥ 85%

    public SkillTrackerWindow(ConfigService config, CombatParser parser,
        IEnumerable<string> skills, double opacity)
    {
        InitializeComponent();

        _parser = parser;
        _skills = Clean(skills);
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, config, "skills", Anchor.TopRight, 40, 520);

        RowsControl.ItemsSource = _rows;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { _placement.Attach(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) =>
            // Interactive (the ⟲ button) but no-activate — never steals game focus.
            NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, false);
    }

    public void ResetPosition() => _placement.ResetToDefault();

    /// <summary>Apply changed settings live (skill list, opacity, anchor).</summary>
    public void ApplySettings(IEnumerable<string> skills, double opacity)
    {
        _skills = Clean(skills);
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement.Reload();
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        base.OnClosed(e);
    }

    private static List<string> Clean(IEnumerable<string> skills) =>
        skills.Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ---- controls -------------------------------------------------------------

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _parser.ResetSessionSkills();
        Refresh();
    }

    private void Card_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ---- refresh ----------------------------------------------------------------

    private void Refresh()
    {
        if (_skills.Count == 0)
        {
            SummaryText.Text = "No skills configured — add some under Manage → Skill tracker (e.g. backstab, reave, Smite).";
            _rows.Clear();
            return;
        }

        int attempts = 0;
        while (_rows.Count > _skills.Count) _rows.RemoveAt(_rows.Count - 1);
        while (_rows.Count < _skills.Count) _rows.Add(new MeterRowViewModel());

        for (int i = 0; i < _skills.Count; i++)
        {
            var row = _rows[i];
            row.Name = _skills[i];

            var s = _parser.GetSessionSkill(_skills[i]);
            if (s is null || s.Attempts == 0)
            {
                row.Fraction = 0;
                row.ValueText = "—";
                row.Fill = MidFill;
                row.Detail = "no attempts yet this session";
                continue;
            }

            attempts += s.Attempts;
            double rate = s.HitRate;
            row.Fraction = rate;
            row.ValueText = $"{s.Hits}/{s.Attempts} · {rate * 100:0}%";
            row.Fill = rate < 0.60 ? LowFill : rate < 0.85 ? MidFill : HighFill;

            var extra = new List<string>();
            if (s.Misses > 0) extra.Add($"{s.Misses} missed");
            if (s.Resists > 0) extra.Add($"{s.Resists} resisted");
            if (s.Crits > 0) extra.Add($"{s.Crits} crit");
            if (s.Max > 0) extra.Add($"max {s.Max:N0}");
            row.Detail = extra.Count > 0 ? string.Join(" · ", extra) : "all landed";
        }

        SummaryText.Text = attempts == 0
            ? "session totals — waiting for attempts…"
            : $"session totals · {attempts:N0} attempts";
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
