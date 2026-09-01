using System.Windows;
using System.Windows.Controls;
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

        // The report consults the live pet ledger: a fresh summon is a
        // stranger until it first speaks, and fights archived in that gap
        // need the render-time correction.
        TimelinePane.IsOwnPet = name => _parser.IsKnownPet(name);
        TimelinePane.SiblingsLookup = SiblingsFor;
        StylePills();

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

        bool listChanged = !entries.SequenceEqual(_shown);

        var selected = FightsList.SelectedItems.Cast<FightItem>().Select(x => x.Entry.Rec).ToHashSet();

        _shown = entries;
        var view = new ListCollectionView(
            entries.Select(e => new FightItem(e, RowText(e), DayText(e.Rec.EndedAt), TipText(e))).ToList());
        view.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(FightItem.Day)));
        FightsList.ItemsSource = view;

        foreach (FightItem item in FightsList.Items)
            if (selected.Contains(item.Entry.Rec))
                FightsList.SelectedItems.Add(item);

        if (listChanged && _view == "parses") BuildParses();
    }

    /// <summary>Sibling fights for the trend card: same mob, same character
    /// (a legacy record without a stamp matches anyone), oldest first,
    /// including the fight itself.</summary>
    private IReadOnlyList<CombatParser.FightRecord> SiblingsFor(CombatParser.FightRecord rec) =>
        _shown.Select(e => e.Rec)
            .Where(r => r.Label.Equals(rec.Label, StringComparison.OrdinalIgnoreCase)
                && (r.Character.Length == 0 || rec.Character.Length == 0
                    || r.Character.Equals(rec.Character, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.EndedAt)
            .ToList();

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
        // An "ally" the ledger NOW knows was your own pet (it hadn't spoken
        // yet when the fight archived) doesn't make a group.
        r.Allies.Any(a => !_parser.IsKnownPet(a))
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
                BuildSheetActors(r));
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

    private List<FightSheetView.SheetActor> BuildSheetActors(CombatParser.FightRecord r)
    {
        double dur = Math.Max(1, r.DurationSeconds);
        bool solo = _soloMode();

        // Legacy fights predate the per-spell heal capture — fall back to the
        // actor's per-source Healing row so the aspect isn't silently empty.
        List<FightSheetView.SheetRow> HealFallback(Func<CombatParser.Row, bool> mine) =>
            r.Healing.Where(x => !x.Enemy && mine(x)).Select(SlimRow).ToList();

        var youHeal = r.SelfHealAbilities.Count > 0
            ? new FightSheetView.SheetAspect("heal", "Healing done", FullRows(r.SelfHealAbilities, dur))
            : new FightSheetView.SheetAspect("heal", "Healing done",
                HealFallback(x => !IsOtherPlayer(r, x) && !PetNameFor(r).Equals(x.Name, StringComparison.OrdinalIgnoreCase)), Slim: true);
        var petHeal = r.PetHealAbilities.Count > 0
            ? new FightSheetView.SheetAspect("heal", "Healing done", FullRows(r.PetHealAbilities, dur))
            : new FightSheetView.SheetAspect("heal", "Healing done",
                HealFallback(x => r.Pets.Any(p => p.Equals(x.Name, StringComparison.OrdinalIgnoreCase))
                                  || PetNameFor(r).Equals(x.Name, StringComparison.OrdinalIgnoreCase)), Slim: true);

        var actors = new List<FightSheetView.SheetActor>
        {
            new("you", $"You · {SelfNameFor(r)}", SelfFg, new List<FightSheetView.SheetAspect>
            {
                new("dealt", "Damage dealt", FullRows(r.SelfAbilities, dur)),
                new("taken", "Damage taken", FullRows(r.IncomingSelfAbilities, dur, critTracked: false)),
                youHeal,
            }),
            new("pet", "Pet(s)", PetFg, new List<FightSheetView.SheetAspect>
            {
                new("dealt", "Damage dealt", FullRows(r.PetAbilities, dur)),
                new("taken", "Damage taken", FullRows(r.IncomingPetAbilities, dur, critTracked: false)),
                petHeal,
            }),
        };
        if (!solo)
            actors.Add(new("oth", "Others", OtherFg, new List<FightSheetView.SheetAspect>
            {
                new("dealt", "Damage dealt",
                    r.Damage.Where(x => IsOtherPlayer(r, x)).Select(SlimRow).ToList(), Slim: true),
                new("heal", "Healing done",
                    r.Healing.Where(x => !x.Enemy && IsOtherPlayer(r, x)).Select(SlimRow).ToList(), Slim: true),
            }));
        return actors;
    }

    private static FightSheetView.SheetRow SlimRow(CombatParser.Row x) =>
        new(x.Name, x.Dps, x.Total, x.Percent, 0, null, null, null, null, null, null, null);

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
                // Resists only exist for spells/procs — melee shows the honest
                // dash instead of a meaningless zero.
                Resists: CombatParser.IsMeleeAbility(a.Name) ? null : a.Resists,
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

    // ---- the Parses view: every recorded fight grouped by mob -----------------

    private string _view = "fights";

    private static readonly Brush PillOnBg = Freeze(Color.FromRgb(0x23, 0x2B, 0x40));
    private static readonly Brush PillOnLine = Freeze(Color.FromRgb(0x5A, 0x6B, 0x8C));
    private static readonly Brush PillOnFg = Freeze(Color.FromRgb(0xE8, 0xC1, 0x5A));
    private static readonly Brush PillOffBg = Freeze(Color.FromRgb(0x1B, 0x21, 0x30));
    private static readonly Brush PillOffLine = Freeze(Color.FromRgb(0x3A, 0x45, 0x60));
    private static readonly Brush PillOffFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush ParseHeadFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));
    private static readonly Brush ParseGroupFg = Freeze(Color.FromRgb(0xE8, 0xC1, 0x5A));
    private static readonly Brush ParseValFg = Freeze(Color.FromRgb(0xC9, 0xD4, 0xE3));
    private static readonly Brush ParseDimFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush ParseLine = Freeze(Color.FromRgb(0x1F, 0x26, 0x37));
    private static readonly Brush ParseBest = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush ParseWorse = Freeze(Color.FromRgb(0xE5, 0x73, 0x73));
    private static readonly Brush ParseCombo = Freeze(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly Brush ParseHover = Freeze(Color.FromRgb(0x1A, 0x20, 0x30));

    private void View_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string v || v == _view) return;
        _view = v;
        StylePills();
        if (_view == "parses") BuildParses();
    }

    private void StylePills()
    {
        bool parses = _view == "parses";
        foreach (var (pill, on) in new[] { (FightsPill, !parses), (ParsesPill, parses) })
        {
            pill.Background = on ? PillOnBg : PillOffBg;
            pill.BorderBrush = on ? PillOnLine : PillOffLine;
            if (pill.Child is System.Windows.Controls.TextBlock tb)
                tb.Foreground = on ? PillOnFg : PillOffFg;
        }
        FightsView.Visibility = parses ? Visibility.Collapsed : Visibility.Visible;
        ParsesView.Visibility = parses ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = parses
            ? "Every recorded fight (kept + this session) grouped by mob, newest first. Δ compares each row to the one before it on the same mob — DPS for target dummies, kill time for everything else; the best value per mob reads green. Click a row to open that fight's report."
            : "A fight lands here ~10 seconds after combat goes quiet (the session keeps the last 50). Select one for details — the breakdown, the timeline and ⚡ Analyse. Ctrl-click more to compare side by side. ★ Keep saves a fight permanently, so you can compare this week's kill against last week's.";
    }

    private void ParseSearch_Changed(object sender, RoutedEventArgs e)
    {
        if (_view == "parses") BuildParses();
    }

    private double SelfDps(CombatParser.FightRecord r) =>
        r.SelfAbilities.Sum(a => a.Total) / Math.Max(1, r.DurationSeconds);

    private double PetDps(CombatParser.FightRecord r) =>
        r.PetAbilities.Sum(a => a.Total) / Math.Max(1, r.DurationSeconds);

    private static string ComboText(CombatParser.FightRecord r) =>
        r.Classes.Length > 0
            ? (r.Level > 0 ? $"{r.Level} {r.Classes}" : r.Classes)
            : "";

    private void BuildParses()
    {
        ParsesHost.Children.Clear();
        string q = ParseSearch.Text.Trim();

        var groups = _shown
            .GroupBy(e => e.Rec.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => q.Length == 0 || g.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Count() > 1) // mobs with history first
            .ThenByDescending(g => g.Max(e => e.Rec.EndedAt))
            .ToList();

        if (groups.Count == 0)
        {
            ParsesHost.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = q.Length > 0 ? "No mob matches the filter."
                    : "No fights recorded yet.",
                Foreground = ParseDimFg, FontSize = 12, Margin = new Thickness(2, 6, 0, 0),
            });
            return;
        }

        foreach (var g in groups)
        {
            // Oldest→newest for the Δ chain; the table shows newest first.
            var asc = g.Select(e => e.Rec).OrderBy(r => r.EndedAt).ToList();
            bool dummy = g.Key.Contains("dummy", StringComparison.OrdinalIgnoreCase);
            double Metric(CombatParser.FightRecord r) =>
                dummy ? SelfDps(r) + PetDps(r) : r.DurationSeconds;
            double best = dummy ? asc.Max(Metric) : asc.Min(Metric);

            var deltas = new Dictionary<CombatParser.FightRecord, double?>();
            for (int i = 0; i < asc.Count; i++)
            {
                double? d = null;
                if (i > 0 && Metric(asc[i - 1]) > 0)
                {
                    double cur = Metric(asc[i]), prev = Metric(asc[i - 1]);
                    d = dummy ? (cur - prev) / prev : (prev - cur) / prev; // + = better
                    if (Math.Abs(d.Value) < 0.01) d = null;
                }
                deltas[asc[i]] = d;
            }

            var head = new System.Windows.Controls.TextBlock
            {
                FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 12, 0, 3),
            };
            head.Inlines.Add(new System.Windows.Documents.Run(g.Key.ToUpperInvariant())
            { Foreground = ParseGroupFg });
            head.Inlines.Add(new System.Windows.Documents.Run(
                $"   {asc.Count} fight(s) · best " + (dummy
                    ? $"{FormatDps(best)} dps"
                    : FormatDuration(best)))
            { Foreground = ParseHeadFg, FontWeight = FontWeights.Normal });
            ParsesHost.Children.Add(head);

            var grid = new System.Windows.Controls.Grid();
            double[] widths = { 110, 62, 76, 72, 76, 58, 110, 0 };
            foreach (double w in widths)
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });

            string[] heads = { "WHEN", "DUR", "YOU DPS", "PET DPS", "TOTAL", "Δ", "CLASSES", "LOADOUT" };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
            for (int c = 0; c < heads.Length; c++)
                PCell(grid, heads[c], 0, c, ParseHeadFg, size: 9.5, bold: true,
                    right: c is > 0 and < 6);

            int row = 1;
            foreach (var r in asc.AsEnumerable().Reverse())
            {
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
                double you = SelfDps(r), pet = PetDps(r), total = you + pet;
                bool isBest = asc.Count > 1 && Math.Abs(Metric(r) - best) < 1e-9;

                PCell(grid, r.EndedAt.ToString("dd MMM HH:mm"), row, 0, ParseDimFg);
                PCell(grid, FormatDuration(r.DurationSeconds), row, 1,
                    !dummy && isBest ? ParseBest : ParseValFg, right: true, bold: !dummy && isBest);
                PCell(grid, FormatDps(you), row, 2, ParseValFg, right: true);
                PCell(grid, pet > 0 ? FormatDps(pet) : "—", row, 3,
                    pet > 0 ? ParseValFg : ParseDimFg, right: true);
                PCell(grid, FormatDps(total), row, 4,
                    dummy && isBest ? ParseBest : ParseValFg, right: true, bold: dummy && isBest);
                double? delta = deltas[r];
                PCell(grid, delta is { } dv ? $"{(dv >= 0 ? "+" : "−")}{Math.Abs(dv) * 100:0}%" : "",
                    row, 5, delta is { } d2 && d2 >= 0 ? ParseBest : ParseWorse,
                    right: true, size: 10.5);
                string combo = ComboText(r);
                PCell(grid, combo.Length > 0 ? combo : "—", row, 6,
                    combo.Length > 0 ? ParseCombo : ParseDimFg);
                PCell(grid, r.Loadout.Length > 0 ? r.Loadout : "—", row, 7,
                    r.Loadout.Length > 0 ? ParseDimFg : ParseDimFg);

                // The whole row is a link to the fight's report.
                var hit = new Border
                {
                    Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Open this fight's report",
                };
                var rec = r;
                hit.MouseEnter += (_, _) => hit.Background = ParseHover;
                hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
                hit.MouseLeftButtonDown += (_, _) => JumpToFight(rec);
                System.Windows.Controls.Grid.SetRow(hit, row);
                System.Windows.Controls.Grid.SetColumnSpan(hit, heads.Length);
                grid.Children.Insert(0, hit); // under the text cells
                row++;
            }
            ParsesHost.Children.Add(grid);
        }
    }

    private static void PCell(System.Windows.Controls.Grid grid, string text, int row, int col,
        Brush fg, bool right = false, double size = 12, bool bold = false)
    {
        var border = new Border
        {
            BorderBrush = ParseLine,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(col == 0 ? 2 : 10, 3, 2, 3),
            IsHitTestVisible = false, // clicks fall through to the row link
            Child = new System.Windows.Controls.TextBlock
            {
                Text = text, FontSize = size, Foreground = fg,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        System.Windows.Controls.Grid.SetRow(border, row);
        System.Windows.Controls.Grid.SetColumn(border, col);
        grid.Children.Add(border);
    }

    private void JumpToFight(CombatParser.FightRecord rec)
    {
        _view = "fights";
        StylePills();
        SelectFight(rec.EndedAt, rec.Label);
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
