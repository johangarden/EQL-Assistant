using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Plane of Sky quest browser: class filter + search, quests sorted
/// closest-to-done, have/need chips per turn-in item with dropper tooltips,
/// reward stats on hover, and a manual/automatic completion checkmark.
/// </summary>
public partial class SkyWindow : Window
{
    private readonly SkyQuests _sky;
    private bool _initializing = true;

    private static readonly Brush HaveBg = Freeze(Color.FromRgb(0x1F, 0x6B, 0x2E));
    private static readonly Brush HaveFg = Freeze(Color.FromRgb(0xC9, 0xF0, 0xD2));
    private static readonly Brush PartBg = Freeze(Color.FromRgb(0x5A, 0x46, 0x1B));
    private static readonly Brush PartFg = Freeze(Color.FromRgb(0xFF, 0xE0, 0x82));
    private static readonly Brush NeedBg = Freeze(Color.FromRgb(0x20, 0x29, 0x3A));
    private static readonly Brush NeedFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush DoneFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush OpenFg = Freeze(Color.FromRgb(0xDC, 0xE6, 0xF5));

    public sealed record ChipVm(string Name, string CountText, string Sub, Brush Bg, Brush Fg,
        string Tip, string Url);

    // The REWARD wears its wiki icon (the same embedded set the Character
    // window draws) — the drop rows stay text-only, by owner taste.
    private static readonly Lazy<ItemStats> SharedItemStats = new(() => new ItemStats());

    /// <summary>A chip is a door to the item's wiki page.</summary>
    private void Chip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChipVm { Url.Length: > 0 } vm) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(vm.Url) { UseShellExecute = true });
        }
        catch { /* no browser is not our problem to solve */ }
        e.Handled = true;
    }

    /// <summary>The eqlwiki page for a name — pages live at the site ROOT,
    /// spaces spelled as underscores.</summary>
    private static string WikiUrl(string name) =>
        "https://eqlwiki.com/" + Uri.EscapeDataString(name.Trim().Replace(' ', '_'));

    /// <summary>One class badge: glyph (or ALL-text) ring + completion arc.</summary>
    public sealed record BadgeVm(string ClassName, string Abbr, string CountText, string Tip,
        Brush Ring, Brush Ink, Brush SelBg, Geometry? Arc, Visibility DoneVisibility,
        Geometry? Glyph)
    {
        public Visibility AbbrVisibility => Glyph is null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Class abbreviation over the icon (these aren't the game's
        /// icons, so the name rides along); ALL names itself in the ring.</summary>
        public string TopLabel => Glyph is null ? "" : Abbr;
    }

    // The 16 Sky test classes, each with a fixed badge tint (decoration — the
    // abbreviation is the identifier; tooltips carry the full name).
    private static readonly (string Name, string Abbr, string Hex)[] ClassBadges =
    {
        ("Warrior", "WAR", "#C97C5D"), ("Cleric", "CLR", "#EFE3B0"),
        ("Paladin", "PAL", "#EFA9C4"), ("Ranger", "RNG", "#6FBF73"),
        ("Shadow Knight", "SHD", "#A883D9"), ("Druid", "DRU", "#C9A15C"),
        ("Monk", "MNK", "#9FD9C3"), ("Bard", "BRD", "#E8C15A"),
        ("Rogue", "ROG", "#F2E063"), ("Shaman", "SHM", "#6FD3D3"),
        ("Necromancer", "NEC", "#86E0B0"), ("Wizard", "WIZ", "#6FA8F0"),
        ("Magician", "MAG", "#E06060"), ("Enchanter", "ENC", "#C883E8"),
        ("Beastlord", "BST", "#D9A06F"), ("Berserker", "BER", "#E07F7F"),
    };

    /// <summary>The selected class filter; "" = all quests.</summary>
    private string _selectedClass = "";

    public sealed record QuestVm(SkyQuests.SkyQuest Quest, string Title, string Subtitle,
        string Reward, string RewardStats, string Slot, string ProgressText, Brush ProgressBrush,
        bool Done, double CardOpacity, List<ChipVm> Chips, bool Tracked, ImageSource? RewardIcon)
    {
        public Visibility SlotVisibility => Slot.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RewardIconVis => RewardIcon is null ? Visibility.Collapsed : Visibility.Visible;
        public string TrackText => Tracked ? "★" : "☆";
        // Done cards keep a ghost star so titles stay aligned; it does nothing.
        public Brush TrackFg => Done ? TrackGhostFg : Tracked ? TrackOnFg : TrackOffFg;
    }

    private static readonly Brush TrackOnFg = Freeze(Color.FromRgb(0xE8, 0xC1, 0x5A));
    private static readonly Brush TrackOffFg = Freeze(Color.FromRgb(0x9F, 0xB4, 0xD0));
    private static readonly Brush TrackGhostFg = Freeze(Color.FromRgb(0x3A, 0x45, 0x60));

    private void Track_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuestVm { Done: false } vm)
            _sky.SetTracked(vm.Quest, !vm.Tracked);
    }

    /// <summary>Your /who class abbreviations ("SHD/SHM/NEC"), for the MINE badge.</summary>
    private readonly Func<string>? _classesProvider;

    /// <summary>The MINE filter: your /who classes as quest class names joined
    /// with '|' — "" when no /who is known yet.</summary>
    private string MineFilter()
    {
        string abbrs = _classesProvider?.Invoke() ?? "";
        var names = abbrs.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => ClassBadges.FirstOrDefault(b => b.Abbr.Equals(a.Trim(), StringComparison.OrdinalIgnoreCase)).Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
        return string.Join("|", names);
    }

    private string _pack = "sky";
    private readonly QuestLines? _lines;

    public SkyWindow(SkyQuests sky, Func<string?>? inventoryDumpFile = null, Func<string>? classesProvider = null,
        QuestLines? lines = null)
    {
        InitializeComponent();
        _lines = lines;
        RenderMenu();
        DialogPlacement.Persist(this, "sky");
        TitleRow.Children.Insert(0, DialogPlacement.Pin(this, "sky"));
        WindowTheme.ApplyDark(this);
        _sky = sky;
        _dumpFile = inventoryDumpFile;
        _classesProvider = classesProvider;

        // Slots split into tokens — "FACE BACK" filters under both FACE and BACK.
        SlotBox.ItemsSource = new[] { "All slots" }
            .Concat(_sky.Quests.SelectMany(q => q.Slot.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s))
            .ToList();
        SlotBox.SelectedIndex = 0;

        _sky.Changed += OnSkyChanged;
        Closed += (_, _) => _sky.Changed -= OnSkyChanged;

        _initializing = false;
        Refresh();
    }

    private void OnSkyChanged() => Dispatcher.BeginInvoke(Refresh);

    private void RenderMenu()
    {
        var items = new List<MenuTabs.Item> { new("sky", "Plane of Sky · class quests") };
        if (_lines is not null && _lines.Quests.Count > 0)
            items.Add(new MenuTabs.Item("lines", "Notable quests", Tip: "Multi-step weapon quests from eqlwiki, tracked from your log"));
        items.Add(new MenuTabs.Item("epics", "Epics · when the game gets them", Soon: true, Tip: "EQ Legends has no epic quests yet"));
        MenuTabs.Render(MenuRow, items, _pack, ShowPack);
    }

    /// <summary>Switch the window between its packs: the Sky pieces or the
    /// Notable quests view — one shows, the other collapses.</summary>
    public void ShowPack(string pack)
    {
        if (pack == "lines" && _lines is null) return;
        _pack = pack;
        RenderMenu();
        bool sky = pack == "sky";
        var v = sky ? Visibility.Visible : Visibility.Collapsed;
        SkyHint.Visibility = v;
        BadgesControl.Visibility = v;
        SkyPills.Visibility = v;
        FiltersRow.Visibility = v;
        if (sky)
        {
            LinesView.Visibility = Visibility.Collapsed;
            Refresh();
        }
        else
        {
            QuestsScroll.Visibility = IsleScroll.Visibility = HouseScroll.Visibility = Visibility.Collapsed;
            LinesView.Visibility = Visibility.Visible;
            LinesView.Attach(_lines!, _dumpFile, _classesProvider);
            int active = _lines!.Quests.Count(q => !_lines.IsComplete(q) && _lines.DoneCount(q) > 0);
            SummaryText.Text = active > 0 ? $"{active} line{(active == 1 ? "" : "s")} in progress" : $"{_lines.Quests.Count} quest lines";
        }
    }

    public string Pack => _pack;

    private void Filters_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Done_Checked(object sender, RoutedEventArgs e) => SetDone(sender, true);
    private void Done_Unchecked(object sender, RoutedEventArgs e) => SetDone(sender, false);

    private void SetDone(object sender, bool done)
    {
        if (_initializing) return;
        if ((sender as FrameworkElement)?.DataContext is not QuestVm vm) return;
        if (done && !ConfirmDialog.Show(this, "Mark quest complete",
            $"Mark '{vm.Quest.Name}' as done?\n\nIt leaves the open list (and drops its ★ hunt, "
            + "if tracked). The reward line in the log normally checks this off by itself.",
            "Mark done", "Cancel"))
        {
            Refresh(); // snap the checkbox back — nothing changed
            return;
        }
        _sky.SetCompleted(vm.Quest, done);
    }

    private void Badge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BadgeVm vm) return;
        _selectedClass = vm.ClassName;
        Refresh();
    }

    /// <summary>A completion arc: from 12 o'clock, clockwise, done/total of the
    /// ring (radius 17 in the badge's 38px box). Full = the whole circle.</summary>
    private static Geometry? BuildArc(int done, int total)
    {
        if (done <= 0 || total <= 0) return null;
        const double cx = 19, cy = 19, r = 17;
        double f = Math.Min(1.0, (double)done / total);
        if (f >= 1.0) return new EllipseGeometry(new Point(cx, cy), r, r);
        double angle = -Math.PI / 2 + f * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0,
            isLargeArc: f > 0.5, SweepDirection.Clockwise, isStroked: true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    private void RefreshBadges()
    {
        var badges = new List<BadgeVm>();

        void Add(string className, string abbr, Color tint, string? tipName = null)
        {
            var quests = className.Length == 0
                ? (IReadOnlyList<SkyQuests.SkyQuest>)_sky.Quests
                : _sky.Quests.Where(q => SkyQuests.ClassMatches(q.Class, className)).ToList();
            int done = quests.Count(_sky.IsCompleted);
            bool complete = quests.Count > 0 && done == quests.Count;
            bool selected = _selectedClass.Equals(className, StringComparison.OrdinalIgnoreCase);
            badges.Add(new BadgeVm(className, abbr, $"{done}/{quests.Count}",
                $"{tipName ?? (className.Length == 0 ? "All quests" : className)} — {done} of {quests.Count} complete",
                complete ? Freeze(Color.FromRgb(0x81, 0xC7, 0x84)) : Freeze(tint),
                selected ? Brushes.White : Freeze(Color.FromArgb(0xD8, tint.R, tint.G, tint.B)),
                selected ? Freeze(Color.FromArgb(0x50, tint.R, tint.G, tint.B)) : Brushes.Transparent,
                BuildArc(done, quests.Count),
                complete ? Visibility.Visible : Visibility.Collapsed,
                className.Length == 0 ? null : ClassGlyphs.For(className)));
        }

        Add("", "ALL", Color.FromRgb(0xFF, 0xC1, 0x2E));
        // MINE: the classes /who says you are — an opt-in lens, never a lock.
        if (MineFilter() is { Length: > 0 } mine)
            Add(mine, "MINE", Color.FromRgb(0x4F, 0xC3, 0xF7),
                "Your classes (" + (_classesProvider?.Invoke() ?? "") + ")");
        foreach (var (name, abbr, hex) in ClassBadges)
            Add(name, abbr, (Color)ColorConverter.ConvertFromString(hex));

        BadgesControl.ItemsSource = badges;
    }

    /// <summary>Isle/housekeeping row VMs (simple text rows).</summary>
    public sealed record LineVm(string Main, string Count, string Sub = "", Brush? CountFg = null)
    {
        /// <summary>Red for "need N"; green once the isle's item is in hand.</summary>
        public Brush CountBrush => CountFg ?? IsleNeedFg;
    }

    private static readonly Brush IsleNeedFg = Freeze(Color.FromRgb(0xFF, 0x8A, 0x80));
    /// <summary>Housekeeping row: the spare item, foldable to WHERE it sits
    /// (per the last /outputfile inventory snapshot).</summary>
    /// <summary>A storage section of the housekeeping view (bags, bank, …).</summary>
    public sealed record HouseSectionVm(string Key, string Title, string CountText, bool Open,
        List<HouseVm> Items)
    {
        public string Arrow => Open ? "▾" : "▸";
        public Visibility RowsVisibility => Open ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed record HouseVm(string Key, string Main, string Count, bool Open, List<string> Locations)
    {
        public string Arrow => Open ? "▾" : "▸";
        public Visibility RowsVisibility => Open ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed record IsleVm(string Isle, string NeedText, bool Open, List<LineVm> Rows)
    {
        public string Arrow => Open ? "▾" : "▸";
        public Visibility RowsVisibility => Open ? Visibility.Visible : Visibility.Collapsed;
    }

    // Folded by default — a glance gives the per-isle tallies, expand to farm.
    private readonly HashSet<string> _isleOpen = new(StringComparer.OrdinalIgnoreCase);

    // ---- the view pills -------------------------------------------------------

    private string _view = "quests";

    private static readonly Brush PillOnBg = Freeze(Color.FromRgb(0x23, 0x2B, 0x40));
    private static readonly Brush PillOnLine = Freeze(Color.FromRgb(0x5A, 0x6B, 0x8C));
    private static readonly Brush PillOnFg = Freeze(Color.FromRgb(0xE8, 0xC1, 0x5A));
    private static readonly Brush PillOffBg = Freeze(Color.FromRgb(0x1B, 0x21, 0x30));
    private static readonly Brush PillOffLine = Freeze(Color.FromRgb(0x3A, 0x45, 0x60));
    private static readonly Brush PillOffFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));

    private void View_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string v || v == _view) return;
        _view = v;
        Refresh();
    }

    private void StylePills()
    {
        foreach (var (pill, key) in new[]
                 { (QuestsPill, "quests"), (IslePill, "isle"), (HousePill, "house") })
        {
            bool on = key == _view;
            pill.Background = on ? PillOnBg : PillOffBg;
            pill.BorderBrush = on ? PillOnLine : PillOffLine;
            if (pill.Child is TextBlock tb) tb.Foreground = on ? PillOnFg : PillOffFg;
        }
    }

    private void Isle_Toggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string isle) return;
        if (!_isleOpen.Add(isle)) _isleOpen.Remove(isle);
        RefreshIsles();
    }

    private void Refresh()
    {
        if (_initializing || QuestsControl is null) return;

        RefreshBadges();
        StylePills();
        QuestsScroll.Visibility = _view == "quests" ? Visibility.Visible : Visibility.Collapsed;
        IsleScroll.Visibility = _view == "isle" ? Visibility.Visible : Visibility.Collapsed;
        HouseScroll.Visibility = _view == "house" ? Visibility.Visible : Visibility.Collapsed;

        // Filters belong to the Quests view; search also serves the isle list.
        FiltersRow.Visibility = _view == "house" ? Visibility.Collapsed : Visibility.Visible;
        StatusBox.Visibility = _view == "quests" ? Visibility.Visible : Visibility.Collapsed;
        SlotBox.Visibility = _view == "quests" ? Visibility.Visible : Visibility.Collapsed;

        if (_view == "isle") { RefreshIsles(); return; }
        if (_view == "house") { RefreshHousekeeping(); return; }
        string cls = _selectedClass;
        string slot = SlotBox.SelectedIndex > 0 ? (string)SlotBox.SelectedItem : "";
        string search = SearchBox.Text.Trim();
        string status = StatusBox.SelectedValue as string ?? "ready";

        var vms = new List<QuestVm>();
        foreach (var q in _sky.Quests)
        {
            if (!SkyQuests.ClassMatches(q.Class, cls)) continue;
            if (slot.Length > 0 && !q.Slot.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(slot, StringComparer.OrdinalIgnoreCase)) continue;
            bool done = _sky.IsCompleted(q);
            var (have, need) = _sky.Progress(q);
            bool keep = status switch
            {
                "all" => true,
                "done" => done,
                "ready" => !done && need > 0 && have >= need,
                "partly" => !done && have > 0 && have < need,
                _ => !done, // active
            };
            if (!keep) continue;
            if (search.Length > 0
                && !q.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !q.Reward.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !q.Items.Any(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                continue;

            vms.Add(ToVm(q, done));
        }

        // Closest-to-done first; completed sink to the bottom.
        vms.Sort((a, b) =>
        {
            if (a.Done != b.Done) return a.Done ? 1 : -1;
            var (ah, an) = _sky.Progress(a.Quest);
            var (bh, bn) = _sky.Progress(b.Quest);
            int cmp = (bn > 0 ? (double)bh / bn : 0).CompareTo(an > 0 ? (double)ah / an : 0);
            return cmp != 0 ? cmp : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        QuestsControl.ItemsSource = vms;
        SummaryText.Text = $"{_sky.CompletedCount} / {_sky.Quests.Count} complete";
    }

    /// <summary>The shopping list — everything ACTIVE quests still need,
    /// grouped by isle; the class badge and search narrow it.</summary>
    private void RefreshIsles()
    {
        string search = SearchBox.Text.Trim();
        // Held items ride along (Missing 0): the isle is a CHECKLIST, and
        // "need 6 of 7" must count the Gorgon Head you're carrying.
        var rows = _sky.MissingByIsle(_selectedClass, includeHeld: true)
            .Where(r => search.Length == 0
                        || r.Item.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || r.Quests.Any(q => q.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var isles = rows.GroupBy(r => r.Isle)
            .Select(g => new IsleVm(
                g.Key,
                g.Sum(r => r.Missing) == 0
                    ? $"all {g.Sum(r => r.Needed)} in hand — hand in"
                    : $"need {g.Sum(r => r.Missing)} of {g.Sum(r => r.Needed)}",
                _isleOpen.Contains(g.Key),
                g.Select(r => new LineVm(
                    r.Item,
                    r.Missing > 0
                        ? $"need {r.Missing}"
                        : $"have {Math.Min(r.Held, r.Needed)} of {r.Needed} — hand in",
                    (r.Who.Length > 0 ? $"drops: {r.Who} · " : "") +
                    $"for: {string.Join(", ", r.Quests.Take(4))}{(r.Quests.Count > 4 ? $" +{r.Quests.Count - 4}" : "")}",
                    r.Missing > 0 ? null : DoneFg))
                    .ToList()))
            .ToList();

        IsleControl.ItemsSource = isles;
        SummaryText.Text = $"{rows.Sum(r => r.Missing)} items still to collect · {isles.Count} isles";
    }

    // ---- where a spare actually sits: the inventory snapshot ------------------

    private readonly Func<string?>? _dumpFile;
    private readonly HashSet<string> _houseOpen = new(StringComparer.OrdinalIgnoreCase);
    private (string Path, DateTime Stamp, List<InventoryStore.CarryRow> Rows)? _dumpCache;

    /// <summary>The parsed /outputfile inventory snapshot, re-read only when
    /// the file on disk changed. Null = no dump found.</summary>
    private List<InventoryStore.CarryRow>? DumpRows(out string stamp)
    {
        stamp = "";
        string? path = _dumpFile?.Invoke();
        if (path is null || !System.IO.File.Exists(path)) return null;
        var t = System.IO.File.GetLastWriteTime(path);
        if (_dumpCache is not { } c || c.Path != path || c.Stamp != t)
        {
            try
            {
                var dump = InventoryStore.Parse(System.IO.File.ReadAllText(path));
                _dumpCache = (path, t, InventoryStore.CarryAll(dump).Rows);
            }
            catch { return null; }
        }
        stamp = _dumpCache!.Value.Stamp.ToString("dd MMM HH:mm");
        return _dumpCache.Value.Rows;
    }

    private readonly HashSet<string> _houseSectionOpen = new(StringComparer.OrdinalIgnoreCase);

    private void HouseSection_Toggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        if (!_houseSectionOpen.Add(key)) _houseSectionOpen.Remove(key);
        RefreshHousekeeping();
    }

    private void House_Toggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string item) return;
        if (!_houseOpen.Add(item)) _houseOpen.Remove(item);
        RefreshHousekeeping();
    }

    private List<string> HouseLocations(string item,
        List<InventoryStore.CarryRow>? inv, string stamp)
    {
        if (inv is null)
            return new List<string>
            { "Where it sits: no inventory snapshot found — run /outputfile inventory in game." };
        // The dump spells tiers into names ("Golden Efreeti Boots +3") and
        // the ledger doesn't always — match on the tier-stripped key, and
        // skip the nested exaltation rows (they'd echo the parent's slot).
        string key = FocusEffects.ItemKey(item);
        var hits = inv.Where(r => FocusEffects.ItemKey(r.Name) == key
                                  && !r.Name.EndsWith("(Exaltation)", StringComparison.Ordinal))
            .Select(r => (r.Count > 1 ? $"{r.Location} · ×{r.Count}" : r.Location)
                         + (r.Name.Equals(item, StringComparison.OrdinalIgnoreCase) ? "" : $"  ({r.Name})"))
            .ToList();
        if (hits.Count == 0)
            hits.Add(item.StartsWith("Wind Rune", StringComparison.OrdinalIgnoreCase)
                ? "Currency tab (runes never appear in the inventory dump)."
                : "Not in the inventory snapshot — looted after it, or already gone.");
        hits.Add($"— snapshot from {(stamp.Length > 0 ? stamp : "?")}");
        return hits;
    }

    // Storage order for the sections: what's on you, then what's stashed.
    private static readonly string[] HouseLaneOrder = { "worn", "bags", "bank", "depot", "hoard" };

    /// <summary>The ledger's reasoning for one spare, plus the dump's
    /// cross-check — "spare" must never mean "safe to destroy" on a stale
    /// count (owner, 6 Sep: "don't want to delete anything if it is in fact
    /// needed").</summary>
    private string LedgerLine(string item, int spare, int dumpCopies, bool haveDump)
    {
        var (held, needed) = _sky.LedgerFor(item);
        string line = $"Ledger: held {held} · active quests still need {needed} → {spare} spare";
        if (!haveDump) return line + ".";
        if (dumpCopies < held)
            return line + $" · ⚠ the snapshot shows only {dumpCopies} — the ledger may be stale (something left without a log line); trust your bags.";
        if (dumpCopies > held)
            return line + $" · the snapshot shows {dumpCopies} copies (more than the ledger knows) — even safer.";
        return line + $" · the snapshot agrees ({dumpCopies} copies).";
    }

    private void RefreshHousekeeping()
    {
        var inv = DumpRows(out string stamp);
        var spares = _sky.Surplus();
        var sections = new List<HouseSectionVm>();

        // Per item: its dump copies (tier-tolerant, exaltation rows skipped),
        // grouped by the storage they sit in.
        var byLane = new Dictionary<string, List<(SkyQuests.SurplusItem Spare, List<InventoryStore.CarryRow> Rows)>>(StringComparer.Ordinal);
        var notInDump = new List<SkyQuests.SurplusItem>();
        var copiesOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in spares)
        {
            string key = FocusEffects.ItemKey(s.Item);
            var hits = inv?.Where(r => FocusEffects.ItemKey(r.Name) == key
                                       && !r.Name.EndsWith("(Exaltation)", StringComparison.Ordinal))
                .ToList() ?? new List<InventoryStore.CarryRow>();
            copiesOf[s.Item] = hits.Sum(r => Math.Max(1, r.Count));
            if (hits.Count == 0) { notInDump.Add(s); continue; }
            foreach (var g in hits.GroupBy(r => r.Lane, StringComparer.Ordinal))
            {
                if (!byLane.TryGetValue(g.Key, out var list)) byLane[g.Key] = list = new();
                list.Add((s, g.ToList()));
            }
        }

        HouseVm Item(SkyQuests.SurplusItem s, string sectionKey, IEnumerable<string> slotLines)
        {
            string key = s.Item + "|" + sectionKey;
            bool open = _houseOpen.Contains(key);
            var lines = new List<string>();
            if (open)
            {
                lines.AddRange(slotLines);
                lines.Add(LedgerLine(s.Item, s.Surplus, copiesOf.GetValueOrDefault(s.Item), inv is not null));
                lines.Add($"— snapshot from {(stamp.Length > 0 ? stamp : "?")}");
            }
            return new HouseVm(key, s.Item, $"×{s.Surplus} spare", open, lines);
        }

        foreach (var lane in HouseLaneOrder.Concat(byLane.Keys.Except(HouseLaneOrder, StringComparer.Ordinal).OrderBy(k => k)))
        {
            if (!byLane.TryGetValue(lane, out var list)) continue;
            string title = InventoryStore.LaneLabels.GetValueOrDefault(lane, lane);
            var items = list.Select(x => Item(x.Spare, lane, x.Rows.Select(r =>
                    (r.Count > 1 ? $"{r.Location} · ×{r.Count}" : r.Location)
                    + (r.Name.Equals(x.Spare.Item, StringComparison.OrdinalIgnoreCase) ? "" : $"  ({r.Name})"))))
                .ToList();
            sections.Add(new HouseSectionVm(lane, title,
                $"{items.Count} item(s) · {list.Sum(x => x.Spare.Surplus)} spare",
                _houseSectionOpen.Contains(lane), items));
        }
        if (notInDump.Count > 0)
        {
            string title = inv is null ? "No inventory snapshot" : "Not in the snapshot";
            var items = notInDump.Select(s => Item(s, "none", new[]
            {
                inv is null
                    ? "Run /outputfile inventory in game to see where it sits."
                    : "Not in the inventory snapshot — looted after it, or already gone.",
            })).ToList();
            sections.Add(new HouseSectionVm("none", title,
                $"{items.Count} item(s) · {notInDump.Sum(s => s.Surplus)} spare",
                _houseSectionOpen.Contains("none"), items));
        }

        HouseControl.ItemsSource = sections;
        var rows = spares;
        HouseHint.Text = rows.Count > 0
            ? "Per the loot ledger (looted minus turned in): spare copies no ACTIVE Plane of Sky quest still needs — every quest wanting them is done, or you hold extras. Grouped by where they sit; fold an item out for the ledger's reasoning and the snapshot's cross-check before you clear anything out. Wind runes stay out: they stack in the currency tab and take no space."
            : "Nothing to clear out — everything you hold is still wanted by an open quest (per the loot ledger).";
        SummaryText.Text = $"{rows.Count} item(s) with spares";
    }

    private QuestVm ToVm(SkyQuests.SkyQuest q, bool done)
    {
        var (have, need) = _sky.Progress(q);
        var chips = q.Items.Select(it =>
        {
            int held = _sky.AllocatedHeld(q, it); // one copy serves one quest
            var (bg, fg) = held >= it.Count ? (HaveBg, HaveFg)
                : held > 0 ? (PartBg, PartFg)
                : (NeedBg, NeedFg);
            // The dropper rides ON the chip (full log names when the library
            // has them, the wiki shorthand otherwise); wind runes say their
            // "random drop" piece and skip the redundant zone.
            string sub = it.Mobs.Count > 0
                ? string.Join(" / ", it.Mobs) + (it.Where.Length > 0 ? $" · {it.Where}" : "")
                : it.Who;
            return new ChipVm(it.Name, $"{held}/{it.Count}", sub, bg, fg,
                $"{it.Name} — drops from {it.Who} ({it.Where}). Click for the wiki page."
                + (it.Stats is null ? "" : "\n\n" + it.Stats),
                WikiUrl(it.Name));
        }).ToList();

        return new QuestVm(q,
            $"{q.Name}",
            $"{q.Class} · turn in to {q.Giver}",
            q.Reward, q.RewardStats, q.Slot,
            done ? "✓ done" : $"{have}/{need}",
            done ? DoneFg : OpenFg,
            done, done ? 0.55 : 1.0, chips, _sky.IsTracked(q),
            ItemIcons.Get(SharedItemStats.Value.Lookup(q.Reward)?.Icon));
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
