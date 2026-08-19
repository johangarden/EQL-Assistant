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
    private readonly SkyQuests _sky;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private readonly ObservableCollection<MeterRowViewModel> _rows = new();
    private readonly ObservableCollection<MeterRowViewModel> _skillRows = new();
    private readonly Dictionary<string, Brush> _fillByName = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _skillNames;
    private bool _skillsVisible;
    private bool _procsVisible;
    private readonly ObservableCollection<MeterRowViewModel> _procRows = new();
    private readonly ObservableCollection<MeterRowViewModel> _petRows = new();
    private bool _showHealing;
    private bool _soloMode;
    private bool _petExpanded;
    private int _nextColor;

    private const int MaxSoloRows = 10; // a dot build runs more lanes than a group does players

    /// <summary>Raised when the SOLO/GROUP button flips, so the choice persists.</summary>
    public event Action<bool>? SoloModeChanged;

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
        SkyQuests sky, double opacity, IEnumerable<string> skills, bool skillsVisible,
        bool procsVisible = false, bool soloMode = true)
    {
        InitializeComponent();
        _soloMode = soloMode;

        _parser = parser;
        _config = config;
        _raids = raids;
        _loot = loot;
        _sky = sky;
        _skillNames = CleanSkills(skills);
        _skillsVisible = skillsVisible;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, config, "meter", Anchor.TopRight, 40, 300);

        _procsVisible = procsVisible;
        RowsControl.ItemsSource = _rows;
        PetRowsControl.ItemsSource = _petRows;
        SkillRowsControl.ItemsSource = _skillRows;
        ProcRowsControl.ItemsSource = _procRows;
        ScopeBtn.Content = _soloMode ? "SOLO" : "GROUP";
        SkillsSection.Visibility = _skillsVisible ? Visibility.Visible : Visibility.Collapsed;
        ProcsSection.Visibility = _procsVisible ? Visibility.Visible : Visibility.Collapsed;

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
    public void ApplySettings(double opacity, IEnumerable<string> skills, bool skillsVisible,
        bool procsVisible = false, bool? soloMode = null)
    {
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _skillNames = CleanSkills(skills);
        SetSkillsVisible(skillsVisible);
        SetProcsVisible(procsVisible);
        if (soloMode is bool solo && solo != _soloMode)
        {
            _soloMode = solo;
            ScopeBtn.Content = _soloMode ? "SOLO" : "GROUP";
            Refresh();
        }
        _placement.Reload();
    }

    /// <summary>Show/hide the proc watcher section (Manager page).</summary>
    public void SetProcsVisible(bool visible)
    {
        _procsVisible = visible;
        ProcsSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) RefreshProcs();
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

    private void OnToggleScope(object sender, RoutedEventArgs e)
    {
        _soloMode = !_soloMode;
        ScopeBtn.Content = _soloMode ? "SOLO" : "GROUP";
        SoloModeChanged?.Invoke(_soloMode);
        Refresh();
    }

    private void PetHeader_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // don't start a window drag
        _petExpanded = !_petExpanded;
        PetHeaderChevron.Text = _petExpanded ? "▼" : "▶";
        PetRowsControl.Visibility = _petExpanded ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _parser.Reset();
        Refresh();
    }

    private void OnSkillsReset(object sender, RoutedEventArgs e)
    {
        _parser.ResetSessionSkills(); // clears skills, procs and session active time
        RefreshSkills();
        RefreshProcs();
    }

    private HistoryWindow? _historyWindow;

    private void OnHistory(object sender, RoutedEventArgs e)
    {
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow(_parser, _config, _raids, _loot, _sky);
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
            _petRows.Clear();
            PetSection.Visibility = Visibility.Collapsed;
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

        if (_soloMode) RefreshSoloRows();
        else RefreshGroupRows();

        int enemyCount = 0; double enemyTotal = 0, enemyDps = 0;
        var ranked = _parser.GetRows(_showHealing);
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
        RefreshProcs();
    }

    /// <summary>GROUP scope: rank players/pets; enemy-shaped sources collapse
    /// into one dim row below (same-named mobs are indistinguishable anyway).</summary>
    private void RefreshGroupRows()
    {
        PetSection.Visibility = Visibility.Collapsed;

        var friendly = _parser.GetRows(_showHealing).Where(r => !r.Enemy).ToList();
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
            row.Detail = "";
            row.Fill = FillFor(r.Name);
        }
    }

    /// <summary>SOLO scope: YOUR abilities ranked (spells, melee, dots, procs),
    /// with the pet folded into a collapsible drill-down of its own.</summary>
    private void RefreshSoloRows()
    {
        var mine = _showHealing
            ? _parser.GetHealAbilityRows(_parser.SelfName)
            : _parser.GetAbilityRows(_parser.SelfName);
        // A utility spell (a slow, a snare) earns a lane only through its
        // resists and would sit at "0,0 dps" forever — the DPS ranking is
        // for things that deal damage. The drill-down keeps the resist rows.
        mine = mine.Where(r => r.Total > 0 || r.Hits > 0).ToList();
        FillAbilityRows(_rows, mine, MaxSoloRows);

        bool hasPet = !string.IsNullOrWhiteSpace(_parser.PetName);
        var pet = hasPet
            ? _showHealing
                ? _parser.GetHealAbilityRows(_parser.PetName)
                : _parser.GetAbilityRows(_parser.PetName)
            : new List<CombatParser.Row>();
        PetSection.Visibility = hasPet && pet.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (pet.Count > 0)
        {
            double dur = _parser.DurationSeconds;
            double petTotal = pet.Sum(r => r.Total);
            PetHeaderLabel.Text = $"{_parser.PetName.Trim()} (pet)";
            PetHeaderValue.Text = $"{FormatDps(dur > 0 ? petTotal / dur : 0)}  ({FormatNum(petTotal)})";
            FillAbilityRows(_petRows, _petExpanded ? pet : new List<CombatParser.Row>(), MaxSoloRows);
        }
        else
        {
            _petRows.Clear();
        }
    }

    /// <summary>Ability rows with the drill-down details on a second look
    /// (tooltip): hits, crits, range, misses/resists.</summary>
    private void FillAbilityRows(ObservableCollection<MeterRowViewModel> rows,
        List<CombatParser.Row> src, int max)
    {
        int count = Math.Min(max, src.Count);
        double top = count > 0 ? src[0].Total : 0;

        while (rows.Count > count) rows.RemoveAt(rows.Count - 1);
        while (rows.Count < count) rows.Add(new MeterRowViewModel());

        for (int i = 0; i < count; i++)
        {
            var r = src[i];
            var row = rows[i];
            row.Name = r.Name;
            row.Fraction = top > 0 ? r.Total / top : 0;
            row.ValueText = $"{FormatDps(r.Dps)}  ({FormatNum(r.Total)}, {r.Percent:0}%)";
            row.Fill = FillFor(r.Name);

            var extra = new List<string>();
            if (r.Hits > 0) extra.Add($"{r.Hits} hit{(r.Hits == 1 ? "" : "s")}");
            if (r.Crits > 0) extra.Add($"{r.Crits} crit");
            if (r.Misses > 0) extra.Add($"{r.Misses} missed");
            if (r.Resists > 0) extra.Add($"{r.Resists} resisted");
            if (r.Max > 0) extra.Add(r.Min < r.Max ? $"{r.Min:N0}–{r.Max:N0}" : $"max {r.Max:N0}");
            row.Detail = string.Join(" · ", extra);
        }
    }

    // Rates hide below these floors rather than lying: 1 proc in a 5-second
    // pull is not "12/min" (the Companion's law 5 — aggregates lie).
    private const double MinActiveSecForPpm = 10;
    private const int MinSwingsForRate = 20;
    private const int MaxProcRows = 8;

    /// <summary>One row per proc lane, busiest first, with PPM and per-100-swings
    /// when the denominators are meaningful. Bars scale to the busiest lane.</summary>
    private void RefreshProcs()
    {
        if (!_procsVisible) return;

        var lanes = _parser.SessionProcs
            .OrderByDescending(kv => kv.Value.Count)
            .Take(MaxProcRows)
            .ToList();
        ProcsHint.Visibility = lanes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        double activeSec = _parser.SessionActiveSeconds;
        int swings = _parser.SessionSwings;
        double maxCount = lanes.Count > 0 ? lanes[0].Value.Count : 1;

        while (_procRows.Count > lanes.Count) _procRows.RemoveAt(_procRows.Count - 1);
        while (_procRows.Count < lanes.Count) _procRows.Add(new MeterRowViewModel());

        for (int i = 0; i < lanes.Count; i++)
        {
            var (name, p) = (lanes[i].Key, lanes[i].Value);
            var row = _procRows[i];
            row.Name = name;
            row.Fraction = p.Count / Math.Max(1, maxCount);
            row.Fill = FillFor(name);

            string ppm = activeSec >= MinActiveSecForPpm
                ? $" · {p.Count * 60 / activeSec:0.0}/min" : "";
            string per100 = swings >= MinSwingsForRate
                ? $" · {100.0 * p.Count / swings:0.0}/100" : "";
            row.ValueText = $"×{p.Count}{ppm}{per100}";

            var extra = new List<string>();
            if (p.Damage > 0) extra.Add($"{FormatNum(p.Damage)} dmg");
            if (p.Heal > 0) extra.Add($"{FormatNum(p.Heal)} healed");
            if (p.Crits > 0) extra.Add($"{p.Crits} crit");
            if (p.Max > 0) extra.Add($"max {p.Max:N0}");
            row.Detail = string.Join(" · ", extra);
        }

        int total = _parser.SessionProcs.Sum(kv => kv.Value.Count);
        ProcsSummary.Text = total == 0 ? "session"
            : activeSec >= MinActiveSecForPpm
                ? $"session · {total:N0} procs · {total * 60 / activeSec:0.0}/min"
                : $"session · {total:N0} procs";
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
            string max = s.Max > 0 ? $" · max {s.Max:N0}" : "";
            row.ValueText = $"{s.Hits}/{s.Attempts} · {rate * 100:0}%{crit}{max}";
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
