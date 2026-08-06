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
    private readonly ConfigService _config;
    private readonly RaidKills _raids;
    private readonly LootTracker _loot;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private readonly ObservableCollection<MeterRowViewModel> _rows = new();
    private readonly ObservableCollection<MeterRowViewModel> _skillRows = new();
    private readonly Dictionary<string, Brush> _fillByName = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _skillNames;
    private bool _skillsVisible;
    private bool _showHealing;
    private int _nextColor;

    private static readonly Brush SkillLowFill = Freeze(Color.FromRgb(0xE5, 0x73, 0x73));  // < 60%
    private static readonly Brush SkillMidFill = Freeze(Color.FromRgb(0xFF, 0xB7, 0x4D));  // 60–85%
    private static readonly Brush SkillHighFill = Freeze(Color.FromRgb(0x81, 0xC7, 0x84)); // ≥ 85%

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

    public MeterWindow(ConfigService config, CombatParser parser, RaidKills raids, LootTracker loot,
        double opacity, IEnumerable<string> skills, bool skillsVisible)
    {
        InitializeComponent();

        _parser = parser;
        _config = config;
        _raids = raids;
        _loot = loot;
        _skillNames = CleanSkills(skills);
        _skillsVisible = skillsVisible;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, config, "meter", Anchor.TopRight, 40, 300);

        RowsControl.ItemsSource = _rows;
        SkillRowsControl.ItemsSource = _skillRows;
        SkillsSection.Visibility = _skillsVisible ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>Apply changed settings live — the fight and any open history window survive.</summary>
    public void ApplySettings(double opacity, IEnumerable<string> skills, bool skillsVisible)
    {
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _skillNames = CleanSkills(skills);
        SetSkillsVisible(skillsVisible);
        _placement.Reload();
    }

    /// <summary>Show/hide the skills section (tray toggle / Manager page).</summary>
    public void SetSkillsVisible(bool visible)
    {
        _skillsVisible = visible;
        SkillsSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) RefreshSkills();
    }

    private static List<string> CleanSkills(IEnumerable<string> skills) =>
        skills.Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        try { _historyWindow?.Close(); } catch { /* ignore */ }
        base.OnClosed(e);
    }

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

    private void OnSkillsReset(object sender, RoutedEventArgs e)
    {
        _parser.ResetSessionSkills();
        RefreshSkills();
    }

    private HistoryWindow? _historyWindow;

    private void OnHistory(object sender, RoutedEventArgs e)
    {
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow(_parser, _config, _raids, _loot);
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }

        // This panel is no-activate, so its child won't come to front on its
        // own — the brief Topmost toggle bumps it above everything.
        var w = _historyWindow;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
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
            EnemiesRow.Visibility = Visibility.Collapsed;
            UpdateIncoming();
            RefreshSkills();
            return;
        }

        string target = _parser.TargetLabel;
        TitleText.Text = string.IsNullOrEmpty(target) ? $"{metric} meter" : $"{metric} — {target}";

        string state = _parser.InCombat ? "" : " · ended";
        SummaryText.Text =
            $"{FormatDuration(_parser.DurationSeconds)} · total {FormatDps(_parser.TotalPerSecond(_showHealing))} {metric.ToLowerInvariant()}{state}";

        // Rank players/pets only; enemy-shaped sources collapse into one dim row
        // below (same-named mobs are indistinguishable in the log anyway).
        var ranked = _parser.GetRows(_showHealing);
        var friendly = ranked.Where(r => !r.Enemy).ToList();
        int count = Math.Min(MaxRows, friendly.Count);
        double top = count > 0 ? friendly[0].Total : 0;

        while (_rows.Count > count) _rows.RemoveAt(_rows.Count - 1);
        while (_rows.Count < count) _rows.Add(new MeterRowViewModel());

        for (int i = 0; i < count; i++)
        {
            var r = friendly[i];
            var row = _rows[i];
            row.Name = r.Name;
            row.Fraction = top > 0 ? r.Total / top : 0;
            row.ValueText = $"{FormatDps(r.Dps)}  ({FormatNum(r.Total)}, {r.Percent:0}%)";
            row.Fill = FillFor(r.Name);
        }

        int enemyCount = 0; double enemyTotal = 0, enemyDps = 0;
        foreach (var r in ranked)
        {
            if (!r.Enemy) continue;
            enemyCount++;
            enemyTotal += r.Total;
            enemyDps += r.Dps;
        }
        EnemiesRow.Visibility = enemyCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (enemyCount > 0)
        {
            EnemiesLabel.Text = enemyCount == 1 ? "Enemies (1 name)" : $"Enemies ({enemyCount} names)";
            EnemiesValue.Text = $"{FormatDps(enemyDps)}  ({FormatNum(enemyTotal)})";
        }

        UpdateIncoming();
        RefreshSkills();
    }

    /// <summary>One bar per configured skill, filled and colored by session hit rate.</summary>
    private void RefreshSkills()
    {
        if (!_skillsVisible) return;

        SkillsHint.Visibility = _skillNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int attempts = 0;
        while (_skillRows.Count > _skillNames.Count) _skillRows.RemoveAt(_skillRows.Count - 1);
        while (_skillRows.Count < _skillNames.Count) _skillRows.Add(new MeterRowViewModel());

        for (int i = 0; i < _skillNames.Count; i++)
        {
            var row = _skillRows[i];
            row.Name = _skillNames[i];

            var s = _parser.GetSessionSkill(_skillNames[i]);
            if (s is null || s.Attempts == 0)
            {
                row.Fraction = 0;
                row.ValueText = "—";
                row.Fill = SkillMidFill;
                row.Detail = s is { Level: > 0 }
                    ? $"skill {s.Level} · no attempts yet this session"
                    : "no attempts yet this session";
                continue;
            }

            attempts += s.Attempts;
            double rate = s.HitRate;
            row.Fraction = rate;
            string crit = s.Crits > 0 && s.Hits > 0 ? $" · {100.0 * s.Crits / s.Hits:0}% crit" : "";
            row.ValueText = $"{s.Hits}/{s.Attempts} · {rate * 100:0}%{crit}";
            row.Fill = rate < 0.60 ? SkillLowFill : rate < 0.85 ? SkillMidFill : SkillHighFill;

            var extra = new List<string>();
            if (s.Level > 0)
                extra.Add(s.Ups > 0 ? $"skill {s.Level} (+{s.Ups} this session)" : $"skill {s.Level}");
            if (s.Misses > 0) extra.Add($"{s.Misses} missed");
            if (s.Resists > 0) extra.Add($"{s.Resists} resisted");
            if (s.Crits > 0) extra.Add($"{s.Crits} crit");
            if (s.Max > 0) extra.Add($"max {s.Max:N0}");
            row.Detail = extra.Count > 0 ? string.Join(" · ", extra) : "all landed";
        }

        SkillsSummary.Text = attempts > 0 ? $"session · {attempts:N0} attempts" : "session";
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
