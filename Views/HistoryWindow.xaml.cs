using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Browser for finished fights (kept in <see cref="CombatParser.History"/>):
/// a list on the left, and one detail column per selected fight on the right
/// so fights can be compared side by side.
/// </summary>
public partial class HistoryWindow : Window
{
    private const int MaxCompare = 3;
    private const int MaxDamageRows = 8;
    private const int MaxHealingRows = 4;

    private readonly CombatParser _parser;
    private readonly ConfigService _config;
    private readonly RaidKills _raids;
    private RaidKillsWindow? _raidsWindow;
    private readonly DispatcherTimer _tick;
    private readonly List<CombatParser.FightRecord> _saved;
    private List<Entry> _shown = new();

    private sealed record Entry(CombatParser.FightRecord Rec, bool Saved);

    private static readonly Brush NameFg = Freeze(Color.FromRgb(0xC9, 0xD4, 0xE3));
    private static readonly Brush SelfFg = Freeze(Color.FromRgb(0xFF, 0xC1, 0x2E));
    private static readonly Brush EnemyFg = Freeze(Color.FromRgb(0x8F, 0xA6, 0xC4));
    private static readonly Brush IncomingFg = Freeze(Color.FromRgb(0xFF, 0x8A, 0x80));

    public sealed record StatRow(string Name, string Value, Brush NameBrush, string Detail = "")
    {
        public Visibility DetailVisibility =>
            Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>List item wrapper — reference-unique even when two fights render identically.</summary>
    private sealed record FightItem(Entry Entry, string Text)
    {
        public override string ToString() => Text;
    }

    public sealed record FightColumn(string Title, string Subtitle,
        List<StatRow> DamageRows, List<StatRow> HealingRows, List<StatRow> TakenRows,
        List<StatRow> SelfAbilityRows, List<StatRow> PetAbilityRows, List<StatRow> TakenAbilityRows)
    {
        public Visibility SelfSectionVisibility => SelfAbilityRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PetSectionVisibility => PetAbilityRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TakenSectionVisibility => TakenAbilityRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public HistoryWindow(CombatParser parser, ConfigService config, RaidKills raids)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _parser = parser;
        _config = config;
        _raids = raids;
        _saved = config.LoadSavedFights();

        FightsList.SelectionChanged += (_, _) => BuildColumns();

        // New fights finish while the window is open — keep the list in sync.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Sync();
        _tick.Start();

        Loaded += (_, _) =>
        {
            Sync();
            if (FightsList.Items.Count > 0) FightsList.SelectedIndex = 0;
        };
        Closed += (_, _) => _tick.Stop();
    }

    /// <summary>Rebuild the fight list when anything changed, preserving the selection.</summary>
    private void Sync(bool force = false)
    {
        // Kept fights first-class alongside the session's; a session fight that
        // was kept appears once (as its kept copy).
        var entries = _saved.Select(r => new Entry(r, true))
            .Concat(_parser.History
                .Where(r => !_saved.Any(s => SameFight(s, r)))
                .Select(r => new Entry(r, false)))
            .OrderByDescending(e => e.Rec.EndedAt)
            .ToList();

        if (!force && entries.SequenceEqual(_shown))
            return;

        var selected = FightsList.SelectedItems.Cast<FightItem>().Select(x => x.Entry.Rec).ToHashSet();

        _shown = entries;
        FightsList.ItemsSource = entries.Select(e => new FightItem(e, Display(e))).ToList();

        foreach (FightItem item in FightsList.Items)
            if (selected.Contains(item.Entry.Rec))
                FightsList.SelectedItems.Add(item);
    }

    private static bool SameFight(CombatParser.FightRecord a, CombatParser.FightRecord b) =>
        ReferenceEquals(a, b) || (a.EndedAt == b.EndedAt && a.Label == b.Label);

    private static string Display(Entry e)
    {
        var r = e.Rec;
        string star = e.Saved ? "★ " : "";
        string when = r.EndedAt.Date == DateTime.Today
            ? r.EndedAt.ToString("HH:mm")
            : r.EndedAt.ToString("dd MMM HH:mm");
        string zone = r.Zone.Length > 0 ? $"  ·  {r.Zone}" : "";
        return $"{star}{when}  {r.Label}   ·   {FormatDuration(r.DurationSeconds)}   ·   {FormatDps(r.TotalDps)} dps{zone}";
    }

    private void BuildColumns()
    {
        var records = FightsList.SelectedItems.Cast<FightItem>()
            .Select(x => x.Entry)
            .OrderBy(e => _shown.IndexOf(e))
            .Take(MaxCompare)
            .Select(e => e.Rec);
        ColumnsControl.ItemsSource = records.Select(BuildColumn).ToList();
    }

    private void Raids_Click(object sender, RoutedEventArgs e)
    {
        if (_raidsWindow is null)
        {
            _raidsWindow = new RaidKillsWindow(_raids);
            _raidsWindow.Closed += (_, _) => _raidsWindow = null;
            _raidsWindow.Show();
        }
        _raidsWindow.Activate();
        _raidsWindow.Focus();
    }

    // ---- keep / remove --------------------------------------------------------

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        bool changed = false;
        foreach (var item in FightsList.SelectedItems.Cast<FightItem>().ToList())
        {
            if (item.Entry.Saved || _saved.Any(s => SameFight(s, item.Entry.Rec))) continue;
            _saved.Add(item.Entry.Rec);
            changed = true;
        }
        if (!changed) return;
        _saved.Sort((a, b) => b.EndedAt.CompareTo(a.EndedAt));
        _config.SaveSavedFights(_saved);
        Sync(force: true);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        bool changed = false;
        foreach (var item in FightsList.SelectedItems.Cast<FightItem>().ToList())
        {
            if (!item.Entry.Saved) continue;
            _saved.RemoveAll(s => SameFight(s, item.Entry.Rec));
            changed = true;
        }
        if (!changed) return;
        _config.SaveSavedFights(_saved);
        Sync(force: true);
    }

    /// <summary>Melee swing attempts (hits + misses) — the denominator for proc rates.</summary>
    private static int Swings(List<CombatParser.Row> abilities) =>
        abilities.Where(a => CombatParser.IsMeleeAbility(a.Name)).Sum(a => a.Hits + a.Misses);

    private FightColumn BuildColumn(CombatParser.FightRecord r)
    {
        var damage = new List<StatRow>();
        foreach (var row in r.Damage.Where(x => !x.Enemy).Take(MaxDamageRows))
            damage.Add(new StatRow(row.Name,
                $"{FormatDps(row.Dps)} ({FormatNum(row.Total)}, {row.Percent:0}%)", FgFor(row.Name)));

        var enemies = r.Damage.Where(x => x.Enemy).ToList();
        if (enemies.Count > 0)
            damage.Add(new StatRow($"Enemies ({enemies.Count})",
                $"{FormatDps(enemies.Sum(x => x.Dps))} ({FormatNum(enemies.Sum(x => x.Total))})", EnemyFg));
        if (damage.Count == 0) damage.Add(new StatRow("—", "", NameFg));

        var healing = new List<StatRow>();
        foreach (var row in r.Healing.Where(x => !x.Enemy).Take(MaxHealingRows))
            healing.Add(new StatRow(row.Name,
                $"{FormatDps(row.Dps)} ({FormatNum(row.Total)}, {row.Percent:0}%)", FgFor(row.Name)));
        if (healing.Count == 0) healing.Add(new StatRow("—", "", NameFg));

        double dur = Math.Max(1, r.DurationSeconds);
        var taken = new List<StatRow>
        {
            new("You", $"{FormatDps(r.IncomingSelfTotal / dur)} dps · {FormatNum(r.IncomingSelfTotal)}", IncomingFg),
        };
        if (r.IncomingPetTotal > 0)
            taken.Add(new StatRow("Pet",
                $"{FormatDps(r.IncomingPetTotal / dur)} dps · {FormatNum(r.IncomingPetTotal)}", IncomingFg));

        string zone = r.Zone.Length > 0 ? $" · {r.Zone}" : "";
        return new FightColumn(
            r.Label,
            $"{r.EndedAt:HH:mm:ss} · {FormatDuration(r.DurationSeconds)} · total {FormatDps(r.TotalDps)} dps{zone}",
            damage, healing, taken,
            AbilityRows(r.SelfAbilities, NameFg, dur, Swings(r.SelfAbilities)),
            AbilityRows(r.PetAbilities, NameFg, dur, Swings(r.PetAbilities)),
            AbilityRows(r.IncomingSelfAbilities, IncomingFg, dur, 0));
    }

    /// <summary>Format ability drill-down rows ("backstab  12,3 (1.100, 46%)")
    /// with a hit-rate / range / rate detail line when attempts were tracked.</summary>
    private static List<StatRow> AbilityRows(List<CombatParser.Row> rows, Brush fg,
        double durationSec = 0, int swings = 0) =>
        rows.Select(a => new StatRow(a.Name,
            $"{FormatDps(a.Dps)} ({FormatNum(a.Total)}, {a.Percent:0}%)", fg,
            AbilityDetail(a, durationSec, swings))).ToList();

    private static string AbilityDetail(CombatParser.Row a, double durationSec, int swings)
    {
        int attempts = a.Hits + a.Misses + a.Resists;
        if (attempts == 0) return ""; // pre-hit-tracking record (older kept fight)

        var parts = new List<string>();
        if (a.Misses > 0 || a.Resists > 0)
            parts.Add($"{a.Hits}/{attempts} hit ({100.0 * a.Hits / attempts:0}%)");
        else
            parts.Add(a.Hits == 1 ? "1 hit" : $"{a.Hits} hits");
        if (a.Hits > 0)
            parts.Add(a.Min == a.Max ? FormatNum(a.Max) : $"{FormatNum(a.Min)}–{FormatNum(a.Max)}");
        if (a.Crits > 0)
            parts.Add($"{a.Crits} crit");
        if (a.Resists > 0)
            parts.Add($"{a.Resists} resisted");

        // Rates: how often it fires — and for procs/spells, per 100 melee swings
        // (the number that tells you whether a proc weapon is worth it).
        if (durationSec >= 10)
            parts.Add($"{attempts * 60.0 / durationSec:0.#}/min");
        if (swings > 0 && a.Hits > 0 && !CombatParser.IsMeleeAbility(a.Name))
            parts.Add($"{100.0 * a.Hits / swings:0.#}/100 swings");
        return string.Join(" · ", parts);
    }

    private Brush FgFor(string name) =>
        name.Equals(_parser.SelfName, StringComparison.OrdinalIgnoreCase)
        || name.Equals("You", StringComparison.OrdinalIgnoreCase)
            ? SelfFg : NameFg;

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
