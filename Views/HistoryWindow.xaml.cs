using System.Windows;
using System.Windows.Data;
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

    private readonly CombatParser _parser;
    private readonly ConfigService _config;
    private readonly LootTracker _loot;
    private readonly Func<bool> _soloMode; // the meter's SOLO toggle, live
    private readonly DispatcherTimer _tick;
    private readonly List<CombatParser.FightRecord> _saved;
    private List<Entry> _shown = new();

    public sealed record Entry(CombatParser.FightRecord Rec, bool Saved);

    private static readonly Brush SelfFg = Freeze(Color.FromRgb(0xFF, 0xC1, 0x2E));
    private static readonly Brush IncomingFg = Freeze(Color.FromRgb(0xFF, 0x8A, 0x80));


    /// <summary>List item wrapper — reference-unique even when two fights render
    /// identically. Public: the grouped view reflects over Day/Text/Tip.</summary>
    public sealed record FightItem(Entry Entry, string Text, string Day, string Tip)
    {
        public override string ToString() => Text;
    }


    public HistoryWindow(CombatParser parser, ConfigService config, LootTracker loot,
        Func<bool>? soloMode = null)
    {
        InitializeComponent();
        DialogPlacement.Persist(this, "history");
        WindowTheme.ApplyDark(this);
        _parser = parser;
        _config = config;
        _loot = loot;
        _soloMode = soloMode ?? (() => false);
        _saved = config.SavedFights; // the SHARED list — raid auto-keep writes here too

        FightsList.SelectionChanged += (_, _) => BuildColumns();

        // New fights finish while the window is open — keep the list in sync.
        // The SOLO toggle lives on the meter; flipping it refreshes the tables.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            Sync();
            if (_soloMode() != _shownSolo) BuildColumns();
        };
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

        EmptyHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!force && entries.SequenceEqual(_shown))
            return;

        var selected = FightsList.SelectedItems.Cast<FightItem>().Select(x => x.Entry.Rec).ToHashSet();

        _shown = entries;
        var view = new ListCollectionView(
            entries.Select(e => new FightItem(e, RowText(e), DayText(e.Rec.EndedAt), TipText(e))).ToList());
        view.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(FightItem.Day)));
        FightsList.ItemsSource = view;

        foreach (FightItem item in FightsList.Items)
            if (selected.Contains(item.Entry.Rec))
                FightsList.SelectedItems.Add(item);
    }

    private static bool SameFight(CombatParser.FightRecord a, CombatParser.FightRecord b) =>
        ReferenceEquals(a, b) || (a.EndedAt == b.EndedAt && a.Label == b.Label);

    /// <summary>Jump to one fight (the Raid Kills window's "fight ↗" link).</summary>
    public void SelectFight(DateTime endedAt, string label)
    {
        Sync(force: true);
        foreach (FightItem item in FightsList.Items)
        {
            if (item.Entry.Rec.EndedAt != endedAt || item.Entry.Rec.Label != label) continue;
            FightsList.SelectedItems.Clear();
            FightsList.SelectedItems.Add(item);
            FightsList.ScrollIntoView(item);
            return;
        }
    }

    /// <summary>Who "you" and "your pet" are for one fight: the record's own
    /// stamps when it has them (new fights), the live parser's otherwise.</summary>
    private string SelfNameFor(CombatParser.FightRecord r) =>
        r.Character.Length > 0 ? r.Character : _parser.SelfName;

    private string PetNameFor(CombatParser.FightRecord r) =>
        r.Pet.Length > 0 ? r.Pet : _parser.PetName.Trim();

    private bool IsOtherPlayer(CombatParser.FightRecord r, CombatParser.Row row)
    {
        if (row.Enemy) return false;
        if (row.Name.Equals(SelfNameFor(r), StringComparison.OrdinalIgnoreCase)
            || row.Name.Equals("You", StringComparison.OrdinalIgnoreCase))
            return false;
        // Pets: the record's own list (one fight can hold several — each
        // re-summon gets a new name), the primary stamp, and every pet name
        // this character has EVER had (session + persisted).
        if (r.Pets.Any(p => p.Equals(row.Name, StringComparison.OrdinalIgnoreCase))) return false;
        if (PetNameFor(r) is { Length: > 0 } pet
            && row.Name.Equals(pet, StringComparison.OrdinalIgnoreCase)) return false;
        if (_parser.IsKnownPet(row.Name)) return false;
        // Legacy fights (before pet stamps): the pet's damage row totals
        // exactly what its ability drill-down sums to — a fingerprint no
        // groupmate matches.
        if (r.Pets.Count == 0 && r.Pet.Length == 0)
        {
            double petSum = r.PetAbilities.Sum(a => a.Total);
            if (petSum > 0 && Math.Abs(row.Total - petSum) < 0.5) return false;
        }
        return true;
    }

    /// <summary>A group fight = someone actually JOINED yours — hit one of
    /// your enemies or healed you. Recorded at capture (Allies); a bystander
    /// farming the next camp in logging range never counts. Legacy records
    /// (before the Character stamp) fall back to the row heuristic.</summary>
    private bool IsGroupFight(CombatParser.FightRecord r) =>
        r.Allies.Count > 0
        || (r.Character.Length == 0
            && (r.Damage.Any(x => IsOtherPlayer(r, x)) || r.Healing.Any(x => IsOtherPlayer(r, x))));

    /// <summary>Row = time + mob only; the day lives in the group header and
    /// duration/dps/zone in the tooltip (and the details card).</summary>
    private string RowText(Entry e) =>
        $"{(e.Saved ? "★ " : "")}{e.Rec.EndedAt:HH:mm}  {e.Rec.Label}{(IsGroupFight(e.Rec) ? "   · group" : "")}";

    private static string DayText(DateTime d) =>
        d.Date == DateTime.Today ? "Today"
        : d.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : d.Year == DateTime.Today.Year ? d.ToString("dd MMM")
        : d.ToString("dd MMM yyyy");

    private string TipText(Entry e)
    {
        var r = e.Rec;
        string zone = r.Zone.Length > 0 ? $" · {r.Zone}" : "";
        return $"{r.EndedAt:dd MMM HH:mm} · {r.Label} · {FormatDuration(r.DurationSeconds)} · " +
               $"{FormatDps(r.TotalDps)} dps · {(IsGroupFight(r) ? "group" : "solo")}{zone}";
    }

    private bool _shownSolo;

    private void BuildColumns()
    {
        _shownSolo = _soloMode();
        var entries = FightsList.SelectedItems.Cast<FightItem>()
            .Select(x => x.Entry)
            .OrderBy(e => _shown.IndexOf(e))
            .Take(MaxCompare)
            .ToList();

        SheetsHost.Children.Clear();
        foreach (var e in entries)
        {
            var sheet = new FightSheetView { Margin = new Thickness(0, 0, 0, 12) };
            var r = e.Rec;
            sheet.Show(
                $"{r.Label}   ·   {r.EndedAt:dd MMM HH:mm} · {FormatDuration(r.DurationSeconds)}",
                showTitle: entries.Count > 1,
                BuildSheetTabs(r));
            SheetsHost.Children.Add(sheet);
        }

        // The report (highlights + timelines) always shows the first selected
        // fight; comparisons stack their fact sheets below it.
        if (entries.Count > 0)
            TimelinePane.ShowFight(entries[0].Rec, DropNames(entries[0].Rec));
        else TimelinePane.Clear();
    }

    // ---- the fact sheet's datasets --------------------------------------------

    private static readonly Brush PetFg = Freeze(Color.FromRgb(0xA1, 0x88, 0x7F));
    private static readonly Brush OtherFg = Freeze(Color.FromRgb(0xAE, 0xB8, 0xC4));
    private static readonly Brush HealFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));

    private List<FightSheetView.SheetTab> BuildSheetTabs(CombatParser.FightRecord r)
    {
        double dur = Math.Max(1, r.DurationSeconds);
        bool solo = _soloMode();
        var tabs = new List<FightSheetView.SheetTab>
        {
            new("you", $"You · {SelfNameFor(r)}", SelfFg, FullRows(r.SelfAbilities, dur)),
            new("pet", "Pet(s)", PetFg, FullRows(r.PetAbilities, dur)),
        };
        if (!solo)
            tabs.Add(new("oth", "Others", OtherFg,
                r.Damage.Where(x => IsOtherPlayer(r, x))
                    .Select(x => SlimRow(x)).ToList(), Slim: true));
        tabs.Add(new("inc", "What hit you", IncomingFg,
            FullRows(r.IncomingSelfAbilities, dur, critTracked: false)));
        tabs.Add(new("heal", "Healing", HealFg,
            r.Healing.Where(x => !x.Enemy && (!solo || !IsOtherPlayer(r, x)))
                .Select(x => SlimRow(x)).ToList(), Slim: true));
        return tabs;
    }

    private static FightSheetView.SheetRow SlimRow(CombatParser.Row x) =>
        new(x.Name, x.Dps, x.Total, x.Percent, 0, null, null, null, null, null, null);

    /// <summary>Full drill-down rows. Nulls are honest gaps: a spell with no
    /// miss-tracking must not claim a 100% hit rate, and incoming rows carry
    /// no crit bookkeeping.</summary>
    private static List<FightSheetView.SheetRow> FullRows(
        List<CombatParser.Row> rows, double dur, bool critTracked = true)
    {
        return rows.Select(a =>
        {
            int attempts = a.Hits + a.Misses + a.Resists;
            return new FightSheetView.SheetRow(
                a.Name, a.Dps, a.Total, a.Percent, a.Hits,
                HitPct: attempts > 0 && a.Misses + a.Resists > 0
                    ? 100.0 * a.Hits / attempts : null,
                Min: a.Hits > 0 ? a.Min : null,
                Avg: a.Hits > 0 ? a.Total / a.Hits : null,
                Max: a.Hits > 0 ? a.Max : null,
                CritPct: critTracked && a.Hits > 0 ? 100.0 * a.Crits / a.Hits : null,
                PerMin: dur >= 10 && attempts > 0 ? attempts * 60.0 / dur : null);
        }).ToList();
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

    /// <summary>What the corpse gave — the loot log joined on the fight's own
    /// enemies within a window after the kill (looting takes a while).</summary>
    private List<string> DropNames(CombatParser.FightRecord r)
    {
        var enemies = r.Damage.Where(x => x.Enemy).Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enemies.Count == 0) return new();
        var from = r.EndedAt.AddSeconds(-Math.Max(60, r.DurationSeconds));
        var to = r.EndedAt.AddMinutes(10);
        return _loot.Entries
            .Where(l => l.When >= from && l.When <= to && enemies.Contains(l.Mob))
            .OrderBy(l => l.When)
            .Select(l => l.Count > 1 ? $"{l.Item} ×{l.Count}" : l.Item)
            .ToList();
    }
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
