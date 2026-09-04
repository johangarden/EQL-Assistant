using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The searchable carry ledger: every non-empty row of the game's inventory
/// dump in file order — Item | Location | Count — split over three tabs
/// (Items / Exaltations / Focus effects, i.e. the keyring table), one search
/// box for WHAT (item name only; folding the location in makes "ring" match
/// every keyring row) and lane chips for WHERE, rebuilt per tab. A gold
/// coverage line names every storage the dump left unsaid. Reloads itself
/// when the game rewrites the dump. Read-only by design: the dump is the
/// record, this is the reader.
/// A PANEL since 2 Sep (owner ruling): the Character window hosts the
/// analysis tabs (Sheet · Focus board · Best in slot), the Loot window
/// hosts the browser tabs (All items · Exaltations · Armor sets) — each
/// host attaches its own instance with its tab subset.
/// </summary>
public partial class InventoryPanel : UserControl
{
    private sealed record RowVm(string Name, string Location, string? LocationTip, string CountText,
        List<PillVm>? Pills = null, string Chevron = "", List<DetailVm>? Details = null,
        Visibility DetailsVis = Visibility.Collapsed, string RowKey = "",
        Visibility RuleVis = Visibility.Collapsed,
        ImageSource? Icon = null, Visibility IconVis = Visibility.Collapsed);
    private sealed record PillVm(string Label, Brush Bg, Brush Border, Brush Fg, string Tip);
    private sealed record DetailVm(string Text, Brush Fg, FontWeight Weight, Thickness Margin = default,
        string? Url = null, double FontSize = 11)
    {
        public bool IsLink => Url is not null;
    }

    private static readonly Thickness DetailTab = new(16, 1, 0, 0);      // sub-lines tab in
    private static readonly Thickness DetailHeadPad = new(0, 4, 0, 0);   // headers breathe
    private sealed record FocusVm(string Family, string Kind, string? FamilyTip, Brush StatusBrush,
        List<PillVm> Pills, string BestText, string? BestTip, string PlaceText, Brush PlaceBrush,
        Visibility PlaceVis = Visibility.Visible,
        Visibility HeaderVis = Visibility.Collapsed, Visibility RowVis = Visibility.Visible,
        bool IsFoldToggle = false, string? StatusTip = null, Brush? RowBg = null,
        string Chevron = "▸", List<DetailVm>? Details = null,
        Visibility DetailsVis = Visibility.Collapsed,
        string VerdictText = "");

    /// <summary>A section header row ("Spells", "Songs & instruments").</summary>
    private static FocusVm FocusHeader(string title, bool foldToggle = false) => new(title, "", null,
        Brushes.Transparent, new List<PillVm>(), "", null, "", Brushes.Transparent,
        Visibility.Collapsed, HeaderVis: Visibility.Visible, RowVis: Visibility.Collapsed,
        IsFoldToggle: foldToggle);

    private bool _summonedOpen; // the summoned-charm section starts folded
    private string? _openFamily; // accordion: at most one family unfolded
    private string? _openItem;   // same accordion rule on the Items tab
    private Dictionary<string, InventoryStore.Entry> _entryByLoc = new(StringComparer.Ordinal);
    private Dictionary<string, string> _ownedByKey = new(StringComparer.Ordinal);

    private static readonly (string Id, string Label)[] Tabs =
    {
        ("sheet", "Character sheet"),
        ("items", "All items"),
        ("exalt", "Exaltations"),
        ("focus", "Focus effects"),
        ("sets", "Armor sets"),
        ("bis", "BiS Finder"),
    };

    private static readonly Brush SegOnBg = Freeze("#16283E");
    private static readonly Brush SegOnFg = Freeze("#4FC3F7");
    private static readonly Brush SegOffFg = Freeze("#7F93AD");

    // Audit stoplight: green = best tier that exists, orange = below it,
    // red = none owned. Same tints the Enemy DoTs panel uses.
    private static readonly Brush[] StatusFg =
        { Freeze("#E57373"), Freeze("#FFB74D"), Freeze("#66BB6A") };
    private static readonly Brush[] StatusBg =
        { Freeze("#2DE57373"), Freeze("#2DFFB74D"), Freeze("#2D66BB6A") };
    private static readonly Brush PillOffBorder = Freeze("#3A4560");
    private static readonly Brush PillOffFg = Freeze("#5F7189");
    // A summoned-only tier can't be hunted — it ghosts instead of nagging.
    private static readonly Brush PillGhostBorder = Freeze("#232C3E");
    private static readonly Brush PillGhostFg = Freeze("#404E63");
    // A whisper of the verdict across the whole row.
    private static readonly Brush[] RowWash =
        { Freeze("#10E57373"), Freeze("#10FFB74D"), Freeze("#1066BB6A") };

    // Lane chips wear their family: a cool wash for what's ON you (worn,
    // bags, the keyring collections), a warm wash for what's STASHED (bank,
    // depot, hoard). The active chip keeps the accent look either way.
    private static readonly Brush CarryTint = Freeze("#144FC3F7");
    private static readonly Brush StashTint = Freeze("#14FFB74D");

    private string _eqRoot = "";
    private string _charName = "";
    private string _server = "";
    private FileSystemWatcher? _fsWatcher;
    private readonly DispatcherTimer _reloadDebounce;
    private readonly DispatcherTimer _freshnessTick;
    private string[] _tabs = Tabs.Select(t => t.Id).ToArray();
    private bool _showTabRow = true;
    private bool _attached, _started;

    private readonly FocusEffects _focus = new();
    private readonly ItemStats _itemStats = new(); // wiki icons + stat table
    private List<InventoryStore.CarryRow> _rows = new();
    private List<FocusEffects.AuditRow> _audit = new();
    private InventoryStore.Dump? _dump;
    private string _tab = "items";
    private string? _lane;            // null = All
    private string? _dumpPath;
    private DateTime _dumpMtime;
    private DateTime? _parsedAt;      // set on watcher-triggered reloads only

    // per-storage last-captured times (persisted per character)
    private static readonly Lazy<ConfigService> SharedConfig = new(() => new ConfigService());
    private Dictionary<string, DateTime> _sectionTimes = new(StringComparer.Ordinal);
    private string CharKey => $"{_charName}_{_server}".ToLowerInvariant();

    private SessionStats? _session;

    /// <summary>The sheet's drill drawer wants the HOST window wider — the
    /// host decides how (animated width on the Character window).</summary>
    public Action<bool>? DrawerExtendRequested { get; set; }

    public InventoryPanel()
    {
        InitializeComponent();
        UpdateDupChip();
        SheetView.Init(_focus, _itemStats);
        SheetView.FocusBoardRequested = () => ShowTab("focus");
        SheetView.DrawerExtendRequested = extend => DrawerExtendRequested?.Invoke(extend);

        // The game rewrites the file in place; wait for the write to settle.
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); Reload(fromWatch: true); };

        _freshnessTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _freshnessTick.Tick += (_, _) => UpdateFreshness();

        Loaded += (_, _) => { if (_attached && !_started) Start(); };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Point the panel at a character's dump and choose which tabs
    /// this host shows. The first tab listed opens.</summary>
    public void Attach(string eqRoot, string charName, string server, SessionStats? session,
        string[] tabs, bool showHeader = true, bool showTabRow = true)
    {
        _eqRoot = eqRoot;
        _charName = charName;
        _server = server;
        _session = session;
        _tabs = tabs.Length > 0 ? tabs : Tabs.Select(t => t.Id).ToArray();
        _tab = _tabs[0];
        _showTabRow = showTabRow;
        HeaderRow.Visibility = showHeader ? Visibility.Visible : Visibility.Collapsed;
        BisView.Init(_itemStats, SharedConfig.Value, CharKey);
        RefreshCharHeader();
        _attached = true;
        if (IsLoaded && !_started) Start();
    }

    private void Start()
    {
        _started = true;
        Reload();
        StartWatching();
        _freshnessTick.Start();
    }

    private void Stop()
    {
        _reloadDebounce.Stop();
        _freshnessTick.Stop();
        _fsWatcher?.Dispose();
        _fsWatcher = null;
        _started = false;
    }

    private void StartWatching()
    {
        if (_eqRoot.Length == 0 || !Directory.Exists(_eqRoot)) return;
        try
        {
            _fsWatcher = new FileSystemWatcher(_eqRoot, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            void OnAny(object s, FileSystemEventArgs e)
            {
                if (!e.Name?.EndsWith("-Inventory.txt", StringComparison.OrdinalIgnoreCase) ?? true)
                    return;
                Dispatcher.BeginInvoke(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });
            }
            _fsWatcher.Changed += OnAny;
            _fsWatcher.Created += OnAny;
            _fsWatcher.Renamed += (s, e) => OnAny(s, e);
            _fsWatcher.EnableRaisingEvents = true;
        }
        catch { /* watching is a convenience; manual reopen still works */ }
    }

    /// <summary>Find, read and re-render the freshest dump. Public so a test
    /// or the host can force it.</summary>
    public void Reload(bool fromWatch = false)
    {
        _dumpPath = InventoryStore.FindDumpFile(_eqRoot, _charName, _server);
        if (_dumpPath is null)
        {
            _rows = new List<InventoryStore.CarryRow>();
            _audit = new List<FocusEffects.AuditRow>();
            _dump = null;
            NoDumpPanel.Visibility = Visibility.Visible;
            ResultsList.Visibility = Visibility.Collapsed;
            FocusList.Visibility = Visibility.Collapsed;
            SheetView.Visibility = Visibility.Collapsed;
            BisView.Visibility = Visibility.Collapsed;
            TabPanel.Children.Clear();
            LanePanel.Children.Clear();
            ParsedText.Visibility = Visibility.Collapsed;
            WarnText.Visibility = Visibility.Collapsed;
            SectionPanel.Children.Clear();
            EmptyTabText.Visibility = Visibility.Collapsed;
            FreshnessText.Text = "not yet run";
            CountText.Text = "";
            return;
        }

        string text;
        try
        {
            using var fs = new FileStream(_dumpPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
            _dumpMtime = File.GetLastWriteTime(_dumpPath);
        }
        catch
        {
            return; // mid-write; the debounce will bring us back
        }

        _dump = InventoryStore.Parse(text);
        (_rows, _) = InventoryStore.CarryAll(_dump);
        _audit = _focus.Audit(_rows);
        // Location → tree entry, for the Items tab's socket pills.
        _entryByLoc = new Dictionary<string, InventoryStore.Entry>(StringComparer.Ordinal);
        void WalkEntries(InventoryStore.Entry e)
        {
            _entryByLoc[e.Location] = e;
            foreach (var c in e.Children) WalkEntries(c);
        }
        foreach (var e in _dump.Items) WalkEntries(e);
        // Best place per item key, for the fold-out's "you: worn/in bank".
        _ownedByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            string key = FocusEffects.ItemKey(row.Name);
            if (!_ownedByKey.TryGetValue(key, out string? cur)
                || OwnRank(row.Lane) < OwnRank(cur))
                _ownedByKey[key] = row.Lane;
        }
        if (fromWatch) _parsedAt = DateTime.Now;

        // Every storage THIS dump covers was captured at the dump's mtime —
        // remember it, so an absent storage can say how old its last look is.
        _sectionTimes = SharedConfig.Value.LoadSectionTimes(CharKey);
        foreach (var (key, _) in InventoryStore.StorageDefs)
        {
            bool covered = _dump.Covered.Contains(key)
                || (key == "hoard" && _dump.HasExtraItemSection);
            if (covered) _sectionTimes[key] = _dumpMtime;
        }
        SharedConfig.Value.SaveSectionTimes(CharKey, _sectionTimes);

        NoDumpPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;

        // The Sheet tab renders from THIS parse — the dump is read once.
        SheetView.Update(_dump, _rows, _audit);
        BisView.Update(_rows, KnownClasses(), _dumpMtime.ToString("dd MMM HH:mm"));

        BuildTabs();
        BuildLaneChips();
        UpdateFreshness(); // also paints the coverage banner + section pills
        ApplyFilters();
    }

    /// <summary>Per-storage freshness, EXCEPTIONS ONLY — status must never
    /// out-shout the tab selector. All storages in the current dump = one dim
    /// line; only a stale or never-captured storage earns a pill (amber with
    /// its last-captured age, dim "never").</summary>
    private void BuildSectionPills()
    {
        SectionPanel.Children.Clear();
        if (_dump is null) return;

        var problems = new List<(string Label, DateTime Seen)>();
        int total = InventoryStore.StorageDefs.Length;
        foreach (var (key, label) in InventoryStore.StorageDefs)
        {
            bool current = _dump.Covered.Contains(key)
                || (key == "hoard" && _dump.HasExtraItemSection);
            if (current) continue;
            _sectionTimes.TryGetValue(key, out DateTime seen);
            problems.Add((label, seen));
        }

        string summary = problems.Count == 0
            ? $"all {total} storages in this dump"
            : $"{total - problems.Count} of {total} storages in this dump —";
        SectionPanel.Children.Add(new TextBlock
        {
            Text = summary,
            Foreground = (Brush)FindResource("Brush.TextHint"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Worn, bags, bank, shared bank, depot, the Dragon Hoard and the "
                + "exaltation & storage collections — the game only writes a storage "
                + "while its window is open. Re-type /outputfile inventory in game "
                + "whenever your gear or bags change.",
        });

        foreach (var (label, seen) in problems)
        {
            string age = seen == default ? "never" : ShortAge(DateTime.Now - seen);
            Brush fg = seen == default ? PillOffFg : StatusFg[1];
            Brush bg = seen == default ? Brushes.Transparent : StatusBg[1];
            string tip = seen == default
                ? $"{label} — never captured. Open it in game, then re-type /outputfile inventory."
                : $"{label} — last captured {seen:d MMM HH:mm}; the current dump doesn't carry it. Open it in game, then re-dump.";
            SectionPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderBrush = fg,
                BorderThickness = new Thickness(1),
                Background = bg,
                Padding = new Thickness(7, 0, 7, 1),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = tip,
                Child = new TextBlock
                {
                    FontSize = 10,
                    Foreground = fg,
                    Text = $"{label} · {age}",
                },
            });
        }
    }

    private static string ShortAge(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 60) return $"{Math.Max(0, (int)t.TotalMinutes)}m";
        if (t.TotalHours < 48) return $"{(int)t.TotalHours}h";
        return $"{(int)t.TotalDays}d";
    }

    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        TabPanel.Visibility = _showTabRow ? Visibility.Visible : Visibility.Collapsed;
        // Title → Menu → Pills (owner ruling, 4 Sep): the sections are
        // MENU tabs; pills live inside a section (BiS Finder's Armor | Weapons).
        var menu = new List<MenuTabs.Item>();
        foreach (var (id, label) in Tabs)
        {
            if (!_tabs.Contains(id)) continue; // this host's subset only
            // The focus tab counts green families over the total — the gap
            // report in two digits. Summoned charms are conjured temporaries
            // and stay out of the score. Row tabs count their rows.
            var scored = _audit.Where(a => a.Family.Group != "summoned").ToList();
            string text = id switch
            {
                "sheet" or "bis" => label,
                "focus" => $"{label}  {scored.Count(a => a.Status == 2)}/{scored.Count}",
                _ => $"{label}  {_rows.Count(r => InventoryStore.TabOf(r) == id)}",
            };
            menu.Add(new MenuTabs.Item(id, text));
        }
        MenuTabs.Render(TabPanel, menu, _tab, ShowTab);
    }

    /// <summary>Switch to a tab by id (sheet · items · exalt · focus) — the
    /// toolbar's helm and chest are two doors into this one window.</summary>
    public void ShowTab(string id)
    {
        if (id != "sheet") SheetView.CloseDrawer(); // the extension is sheet-only
        if (id == "bis") BisView.ResetCombo(KnownClasses()); // start from who you are
        _tab = id;
        BuildTabs();
        _lane = null; // a lane picked on one tab means nothing on another
        BuildLaneChips();
        ApplyFilters();
    }

    /// <summary>The gold coverage story: what this dump does NOT speak for.
    /// A missing storage is "the dump does not say", never "empty".</summary>
    private void UpdateCoverage()
    {
        if (_dump is null) return;
        var parts = new List<string>();
        int days = (int)(DateTime.Now - _dumpMtime).TotalDays;
        if (days >= InventoryStore.DumpStaleDays)
            parts.Add($"⚠  This dump is {days} days old — gear may have moved since. Re-type /outputfile inventory in game.");
        var missing = InventoryStore.MissingStorages(_dump);
        if (missing.Count > 0)
            parts.Add("⚠  Not in this dump: " + string.Join(" · ", missing)
                + ".  The game only writes a storage while its window is open — open them in game, then re-type /outputfile inventory.");
        WarnText.Text = string.Join("\n", parts);
        WarnText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildLaneChips()
    {
        // The audit boards are not row-backed (and the sheet is not
        // list-backed); place is spelled per family / per slot instead.
        if (_tab is "focus" or "sheet" or "sets" or "bis")
        {
            LanePanel.Children.Clear();
            LanePanel.Visibility = Visibility.Collapsed;
            return;
        }
        var tabRows = _rows.Where(r => InventoryStore.TabOf(r) == _tab).ToList();
        var lanes = _dump is null
            ? new List<(string Id, string Label)>()
            : InventoryStore.LanesOf(tabRows, _dump);

        // A vanished lane (tab switch, different dump) degrades the pick to
        // All rather than filtering to nothing.
        if (_lane is not null && lanes.All(l => l.Id != _lane)) _lane = null;

        LanePanel.Children.Clear();
        // One lane needs no chooser (the Focus tab is all keyring).
        LanePanel.Visibility = lanes.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (lanes.Count < 2) return;

        void Pick(string? picked)
        {
            _lane = picked;
            RepaintPills(LanePanel, _lane);
            ApplyFilters();
        }

        // The chips read as labeled families: All | Carried (on you) |
        // Stored (away) | anything else — vertical rules between them.
        StackPanel ChipGroup(string subtitle, IEnumerable<(string Id, string Label)> chips, Brush? tint)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (id, label) in chips)
                AddPill(row, label, id, _lane == id, Pick, tint);
            var group = new StackPanel();
            group.Children.Add(row);
            group.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 9,
                Foreground = (Brush)FindResource("Brush.TextHint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 5, 0),
            });
            return group;
        }
        Border Rule() => new()
        {
            Width = 1,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Freeze("#2A3648"),
            Margin = new Thickness(2, 2, 9, 0),
        };

        var carry = lanes.Where(l => InventoryStore.LaneGroup(l.Id) == "carry").ToList();
        var stash = lanes.Where(l => InventoryStore.LaneGroup(l.Id) == "stash").ToList();
        var other = lanes.Where(l => InventoryStore.LaneGroup(l.Id) == "").ToList();

        var all = new StackPanel();
        var allRow = new StackPanel { Orientation = Orientation.Horizontal };
        AddPill(allRow, "All", null, _lane is null, Pick);
        all.Children.Add(allRow);
        LanePanel.Children.Add(all);
        if (carry.Count > 0) { LanePanel.Children.Add(Rule()); LanePanel.Children.Add(ChipGroup("Carried", carry, CarryTint)); }
        if (stash.Count > 0) { LanePanel.Children.Add(Rule()); LanePanel.Children.Add(ChipGroup("Stored", stash, StashTint)); }
        if (other.Count > 0) { LanePanel.Children.Add(Rule()); LanePanel.Children.Add(ChipGroup("", other, null)); }
    }

    private static Brush? GroupTint(string group) => group switch
    {
        "carry" => CarryTint,
        "stash" => StashTint,
        _ => null,
    };

    private void AddPill(Panel panel, string label, string? id, bool on, Action<string?> onPick,
        Brush? offTint = null)
    {
        var text = new TextBlock { Text = label, FontSize = 11 };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(0, 0, 5, 0),
            BorderBrush = Freeze("#3A4560"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = text,
            Tag = (id, offTint),
        };
        chip.Background = on ? SegOnBg : offTint ?? Brushes.Transparent;
        text.Foreground = on ? SegOnFg : SegOffFg;
        chip.MouseLeftButtonUp += (_, _) => onPick(id);
        panel.Children.Add(chip);
    }

    /// <summary>Repaint every pill under the panel — chips may sit inside
    /// group stacks, so this walks the tree.</summary>
    private static void RepaintPills(Panel panel, string? activeId)
    {
        foreach (object child in panel.Children)
        {
            if (child is Border b && b.Tag is ValueTuple<string?, Brush?> tag)
            {
                bool isOn = Equals(tag.Item1, activeId);
                b.Background = isOn ? SegOnBg : tag.Item2 ?? Brushes.Transparent;
                ((TextBlock)b.Child).Foreground = isOn ? SegOnFg : SegOffFg;
            }
            else if (child is Panel p)
            {
                RepaintPills(p, activeId);
            }
        }
    }

    private void UpdateFreshness()
    {
        if (_dumpPath is null) return;
        var age = DateTime.Now - _dumpMtime;
        FreshnessText.Text = "updated " + AgoText(age) + " ago";
        FreshnessText.ToolTip = _dumpPath;
        // A day-old dump answers yesterday's questions — say so in gold.
        FreshnessText.Foreground = age > TimeSpan.FromHours(24)
            ? (Brush)FindResource("Brush.Gold")
            : (Brush)FindResource("Brush.TextHint");
        // The pill ages, the stale banner and the header's level age all
        // drift with the clock too.
        BuildSectionPills();
        UpdateCoverage();
        RefreshCharHeader();

        // The watcher just caught a rewrite: celebrate briefly, then let the
        // ordinary freshness line carry it.
        bool justParsed = _parsedAt is not null && DateTime.Now - _parsedAt < TimeSpan.FromSeconds(90);
        ParsedText.Text = justParsed ? $"✓  Parsed {Path.GetFileName(_dumpPath)} just now." : "";
        ParsedText.Visibility = justParsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string AgoText(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalHours >= 48) return $"{(int)age.TotalDays}d";
        if (age.TotalMinutes >= 60) return $"{(int)age.TotalHours}h {age.Minutes}m";
        if (age.TotalSeconds >= 60) return $"{(int)age.TotalMinutes}m";
        return $"{(int)age.TotalSeconds}s";
    }

    private void Filters_Changed(object sender, TextChangedEventArgs e) => ApplyFilters();

    private void ApplyFilters()
    {
        // The Sheet and Best-in-slot tabs are their own surfaces — no list,
        // no search, no lanes.
        bool sheet = _tab == "sheet" && _dump is not null;
        bool bis = _tab == "bis" && _dump is not null;
        SheetView.Visibility = sheet ? Visibility.Visible : Visibility.Collapsed;
        BisView.Visibility = bis ? Visibility.Visible : Visibility.Collapsed;
        SearchRow.Visibility = sheet || bis ? Visibility.Collapsed : Visibility.Visible;
        if (sheet || bis)
        {
            ResultsList.Visibility = Visibility.Collapsed;
            FocusList.Visibility = Visibility.Collapsed;
            EmptyTabText.Visibility = Visibility.Collapsed;
            return;
        }

        string q = SearchBox.Text.Trim().ToLowerInvariant();
        bool board = _tab is "focus" or "sets"; // both render in FocusList
        FocusList.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = board ? Visibility.Collapsed : Visibility.Visible;
        DupChip.Visibility = board ? Visibility.Collapsed : Visibility.Visible;
        if (_tab == "sets") { ApplySetsFilter(q); return; }
        if (board) { ApplyFocusFilter(q); return; }

        var tabRows = _rows.Where(r => InventoryStore.TabOf(r) == _tab).ToList();

        if (_dupsOnly)
        {
            // The duplicate finder: only names owned as ≥2 physical copies,
            // copies of one name kept adjacent (worst offenders first), a thin
            // rule between names. Lanes don't apply — the POINT is the spread
            // across storages — so the chips step aside.
            LanePanel.Visibility = Visibility.Collapsed;
            // Bags never count — the dump's own container reading, plus the
            // wiki's ("Capacity:" flag) for all-empty 10-slot bags (Kavruul's)
            // the dump can't tell from sockets.
            var dupCandidates = tabRows
                .Where(r => !r.IsContainer && !_itemStats.IsContainer(r.Name))
                .ToList();
            var dupKeys = InventoryStore.DuplicateKeys(dupCandidates);
            var dupRows = dupCandidates
                .Where(r => dupKeys.Contains(FocusEffects.ItemKey(r.Name))
                            && (q.Length == 0 || r.SearchKey.Contains(q, StringComparison.Ordinal)))
                .ToList();
            var copies = dupRows
                .GroupBy(r => FocusEffects.ItemKey(r.Name), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var dupSorted = dupRows
                .OrderByDescending(r => copies[FocusEffects.ItemKey(r.Name)])
                .ThenBy(r => FocusEffects.ItemKey(r.Name), StringComparer.Ordinal)
                .ThenBy(r => r.Line)
                .ToList();
            ResultsList.ItemsSource = dupSorted.Select((r, i) =>
            {
                bool rule = i > 0 && FocusEffects.ItemKey(dupSorted[i - 1].Name)
                    != FocusEffects.ItemKey(r.Name);
                return MakeRowVm(r) with { RuleVis = rule ? Visibility.Visible : Visibility.Collapsed };
            }).ToList();
            int names = dupSorted.Select(r => FocusEffects.ItemKey(r.Name)).Distinct().Count();
            CountText.Text = $"{names} duplicated · {dupSorted.Count} copies";
            EmptyTabText.Text = q.Length > 0
                ? "No duplicates match the search."
                : "No duplicates — every name here is a single copy.";
            EmptyTabText.Visibility = dupSorted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        LanePanel.Visibility = LanePanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var matched = tabRows
            .Where(r => (_lane is null || r.Lane == _lane)
                        && (q.Length == 0 || r.SearchKey.Contains(q, StringComparison.Ordinal)))
            // Worn gear sorts armor → jewelry → weapons (the dump's own order
            // is the client's enumeration); everything else keeps file order.
            .OrderBy(r => r.Lane == "worn" ? 0 : 1)
            .ThenBy(r => r.Lane == "worn" ? InventoryStore.WornRank(r.Location) : 0)
            .ThenBy(r => r.Line)
            .ToList()
            is var sorted ? sorted.Select((r, i) =>
            {
                // A thin rule where one worn band ends and the next begins
                // (armor | jewelry | weapons | wildcards | everything else).
                static int Band(InventoryStore.CarryRow x) =>
                    x.Lane == "worn" ? InventoryStore.WornBand(x.Location) : 99;
                bool rule = i > 0 && Band(sorted[i - 1]) != Band(r)
                    && (Band(sorted[i - 1]) != 99 || Band(r) != 99);
                return MakeRowVm(r) with { RuleVis = rule ? Visibility.Visible : Visibility.Collapsed };
            }).ToList() : new List<RowVm>();
        ResultsList.ItemsSource = matched;
        CountText.Text = $"{matched.Count} of {tabRows.Count}";
        EmptyTabText.Text = _tab == "exalt" ? "No exaltation sockets in this dump." : "Nothing in this dump.";
        EmptyTabText.Visibility = tabRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- the duplicate finder toggle ----------------------------------------

    private bool _dupsOnly;

    private void DupChip_Click(object sender, MouseButtonEventArgs e)
    {
        _dupsOnly = !_dupsOnly;
        UpdateDupChip();
        ApplyFilters();
    }

    private void UpdateDupChip()
    {
        DupChip.Background = _dupsOnly ? SegOnBg : Brushes.Transparent;
        DupChipText.Foreground = _dupsOnly ? SegOnFg : SegOffFg;
    }

    /// <summary>The audit board: every family always renders (the gaps ARE
    /// the content), spells first, the bard instrument resonances under
    /// their own header. Search narrows by family, effect, kind or carrier
    /// item name.</summary>
    private void ApplyFocusFilter(string q)
    {
        bool Match(FocusEffects.AuditRow a) => q.Length == 0
            || a.Family.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Family.Kind.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Family.Tiers.Any(t => t.Effect.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Items.Any(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));

        // Grouped by PLACE, the character sheet's language: what's on your
        // body, what's owned but stored, what's missing — alpha inside.
        List<FocusEffects.AuditRow> Section(Func<FocusEffects.AuditRow, bool> pick) => _audit
            .Where(a => pick(a) && Match(a))
            .OrderBy(a => a.Family.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var worn = Section(a => a.Family.Group != "summoned" && a.WornTier > 0);
        var stored = Section(a => a.Family.Group != "summoned" && a.WornTier == 0 && a.BestTier > 0);
        var missing = Section(a => a.Family.Group != "summoned" && a.BestTier == 0);
        var summoned = Section(a => a.Family.Group == "summoned");

        var shown = new List<FocusVm>();
        void Group(string title, List<FocusEffects.AuditRow> rows)
        {
            if (rows.Count == 0) return;
            shown.Add(FocusHeader(title));
            shown.AddRange(rows.Select(MakeFocusVm));
        }
        Group("Worn", worn);
        Group("Stored", stored);
        Group("Missing", missing);
        if (summoned.Count > 0)
        {
            // Folded by default — these are caster-conjured temporaries, not
            // gear you hunt. A live search always sees through the fold.
            bool open = _summonedOpen || q.Length > 0;
            shown.Add(FocusHeader((open ? "▾  " : "▸  ") + $"Summoned charms ({summoned.Count}) — conjured temporaries",
                foldToggle: true));
            if (open) shown.AddRange(summoned.Select(MakeFocusVm));
        }
        FocusList.ItemsSource = shown;
        CountText.Text = $"{worn.Count + stored.Count + missing.Count + summoned.Count} of {_audit.Count} effects";
        EmptyTabText.Visibility = Visibility.Collapsed;
    }

    // ---- the armor-set board (planar class sets vs the dump) -----------------

    private readonly ArmorSets _armorSets = new();
    private string? _openSet;    // accordion, same rule as the focus board
    private bool _otherSetsOpen; // the other-classes section starts folded

    /// <summary>The set board: your classes' sets first (from /who), everyone
    /// else's behind a fold. Ownership is the dump's word via the same item
    /// key the focus board uses — +N tiers count as the piece.</summary>
    private void ApplySetsFilter(string q)
    {
        var classes = (_session?.WhoClasses ?? "")
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool Match(ArmorSets.Set s) => q.Length == 0
            || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Kind.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Zones.Any(z => z.Contains(q, StringComparison.OrdinalIgnoreCase))
            || s.Pieces.Any(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));

        // Real piece lists first, multi-class after the class's own set,
        // pieces-unknown pages last — alpha inside each band.
        static int Band(ArmorSets.Set s) => s.Pieces.Count == 0 ? 2 : s.Multiclass ? 1 : 0;
        List<ArmorSets.Set> Section(Func<ArmorSets.Set, bool> pick) => _armorSets.Sets
            .Where(s => pick(s) && Match(s))
            .OrderBy(Band)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mine = Section(s => ArmorSets.Relevant(s, classes));
        var others = classes.Length == 0
            ? new List<ArmorSets.Set>()
            : Section(s => !ArmorSets.Relevant(s, classes));

        var shown = new List<FocusVm>();
        shown.Add(FocusHeader(classes.Length > 0
            ? $"Your classes — {string.Join(" / ", classes)}"
            : "All sets — type /who in game and yours sort first"));
        shown.AddRange(mine.Select(s => MakeSetVm(s, classes)));
        if (others.Count > 0)
        {
            bool open = _otherSetsOpen || q.Length > 0;
            shown.Add(FocusHeader((open ? "▾  " : "▸  ") + $"Other classes ({others.Count})",
                foldToggle: true));
            if (open) shown.AddRange(others.Select(s => MakeSetVm(s, classes)));
        }
        FocusList.ItemsSource = shown;

        int pieceTotal = mine.Sum(s => s.Pieces.Count);
        int pieceOwned = mine.Sum(s => s.Pieces.Count(p =>
            _ownedByKey.ContainsKey(FocusEffects.ItemKey(p.Name))));
        CountText.Text = pieceTotal > 0
            ? $"{pieceOwned} of {pieceTotal} pieces · {mine.Count} sets"
            : $"{mine.Count + others.Count} sets";
        EmptyTabText.Visibility = Visibility.Collapsed;
    }

    private FocusVm MakeSetVm(ArmorSets.Set s, IReadOnlyCollection<string> classes)
    {
        int total = s.Pieces.Count;
        int worn = 0, stored = 0;
        foreach (var p in s.Pieces)
        {
            if (!_ownedByKey.TryGetValue(FocusEffects.ItemKey(p.Name), out string? lane)) continue;
            if (lane == "worn") worn++; else stored++;
        }
        int owned = worn + stored;
        int status = total == 0 ? 0 : owned == total ? 2 : owned > 0 ? 1 : 0;

        string verdict;
        if (total == 0)
        {
            verdict = "pieces unknown";
        }
        else
        {
            verdict = $"{owned}/{total}";
            var bits = new List<string>();
            if (worn > 0) bits.Add($"{worn} worn");
            if (stored > 0) bits.Add($"{stored} stored");
            if (bits.Count > 0) verdict += " — " + string.Join(" · ", bits);
        }

        bool open = _openSet == s.Name;
        return new FocusVm(s.Name, $"{s.Kind} · {string.Join(", ", s.Zones)}", null,
            total == 0 ? PillOffFg : StatusFg[status], new List<PillVm>(), "", null,
            s.RaceNote.Length > 0 ? "IKSAR" : "", StatusFg[1],
            s.RaceNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            StatusTip: total == 0 ? "The wiki page doesn't list the pieces yet."
                : status switch { 2 => "Full set owned.", 1 => "Partial set.", _ => "None owned." },
            RowBg: RowWash[status],
            Chevron: open ? "▾" : "▸",
            Details: open ? MakeSetDetails(s, classes) : null,
            DetailsVis: open ? Visibility.Visible : Visibility.Collapsed,
            VerdictText: verdict);
    }

    /// <summary>The fold-out: every piece head-to-toe with where YOUR copy
    /// sits — and for missing pieces, who drops it (wiki links throughout).</summary>
    private List<DetailVm> MakeSetDetails(ArmorSets.Set s, IReadOnlyCollection<string> classes)
    {
        var details = new List<DetailVm>();
        if (s.RaceNote.Length > 0)
            details.Add(new DetailVm(s.RaceNote, DetailDimFg, FontWeights.Normal, DetailTab));
        if (s.Pieces.Count == 0)
        {
            details.Add(new DetailVm(s.Note.Length > 0 ? s.Note : "The wiki page doesn't list the pieces yet.",
                DetailDimFg, FontWeights.Normal, DetailTab));
            details.Add(new DetailVm($"↳  {s.Name} on the wiki", DetailDimFg, FontWeights.Normal,
                DetailDropTab, Url: WikiUrl(s.Name)));
            return details;
        }

        foreach (var p in s.Pieces.OrderBy(x => ArmorSets.SlotRank(x.Slot))
                     .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool owned = _ownedByKey.TryGetValue(FocusEffects.ItemKey(p.Name), out string? lane);
            // Multi-class sets admit classes per piece (Lustrous Russet's
            // breastplate takes BER, its helm doesn't).
            bool wearable = classes.Count == 0 || p.Classes.Count == 0
                || p.Classes.Any(c => classes.Contains(c, StringComparer.OrdinalIgnoreCase));

            var parts = new List<string> { SlotTitle(p.Slot), p.Name };
            parts.Add(owned ? "you: " + OwnLabel(lane!) : "missing");
            if (!wearable) parts.Add("not your classes");
            Brush fg = !owned ? DetailDimFg : lane == "worn" ? StatusFg[2] : StatusFg[1];
            details.Add(new DetailVm(string.Join("  ·  ", parts), fg, FontWeights.Normal,
                DetailTab, Url: WikiUrl(p.Name)));

            if (owned || !wearable) continue;
            var drops = _itemStats.Lookup(p.Name)?.Drops;
            if (drops is not { Count: > 0 }) continue;
            foreach (var d in drops.Take(3))
            {
                string mob = d.Length > 0 ? d[0] : "";
                string zone = d.Length > 1 ? d[1] : "";
                if (mob.Length == 0) continue;
                details.Add(new DetailVm($"↳  {mob}{(zone.Length > 0 ? $" — {zone}" : "")}",
                    DetailDimFg, FontWeights.Normal, DetailDropTab, Url: WikiUrl(mob)));
            }
            if (drops.Count > 3)
                details.Add(new DetailVm($"↳  …and {drops.Count - 3} more droppers (wiki page has the full list)",
                    DetailDimFg, FontWeights.Normal, DetailDropTab, Url: WikiUrl(p.Name)));
        }
        return details;
    }

    private static string SlotTitle(string slot) => slot.Length == 0 ? slot
        : char.ToUpperInvariant(slot[0]) + slot[1..].ToLowerInvariant();

    private void ResultsList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not RowVm vm) return;
        if (vm.Chevron.Length == 0) return; // nothing to unfold
        _openItem = _openItem == vm.RowKey ? null : vm.RowKey;
        ApplyFilters();
    }

    private void FocusList_Click(object sender, MouseButtonEventArgs e)
    {
        // A dropper line opens its wiki page — and must NOT toggle the fold.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is DetailVm { Url: { } url })
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { /* no browser is not our problem to solve */ }
            e.Handled = true;
            return;
        }
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FocusVm vm) return;
        bool sets = _tab == "sets"; // the set board shares this list + its folds
        if (vm.IsFoldToggle)
        {
            if (sets) _otherSetsOpen = !_otherSetsOpen;
            else _summonedOpen = !_summonedOpen;
        }
        else if (vm.RowVis == Visibility.Visible)
        {
            // Accordion: a click opens this family and closes whichever was
            // open; clicking the open one folds it away.
            if (sets) _openSet = _openSet == vm.Family ? null : vm.Family;
            else _openFamily = _openFamily == vm.Family ? null : vm.Family;
        }
        else return;
        string q = SearchBox.Text.Trim().ToLowerInvariant();
        if (sets) ApplySetsFilter(q);
        else ApplyFocusFilter(q);
    }

    /// <summary>Where you keep an item, for the fold-out: lower = closer to
    /// being used.</summary>
    private static int OwnRank(string lane) => lane switch
    {
        "worn" => 0,
        "activated" => 1,
        "storage" or "keyring" => 2,
        _ => 3,
    };

    // Mirrors FocusEffects.PlaceLabel — in-game vocabulary, never the dump's
    // legacy "keyring" word.
    private static string OwnLabel(string lane) => lane switch
    {
        "worn" => "worn",
        "activated" => "activated items",
        "storage" => "in storage",
        "keyring" => "exaltations",
        "bags" => "in bags",
        "bank" => "in bank",
        "depot" => "in depot",
        "hoard" => "dragon hoard",
        _ => lane,
    };

    private static readonly Brush WornPillFg = Freeze("#0F1620");

    private FocusVm MakeFocusVm(FocusEffects.AuditRow a)
    {
        // Pills speak the game's numerals ("Improved Damage II" wears a II).
        // "Minor Improved Damage I" collides with "Improved Damage I" — the
        // variant that doesn't start with the family name keeps its first
        // word: Minor I · I · II · III.
        var labels = a.Family.Tiers.Select(t => TierLabel(t.Effect, t.TierNum)).ToList();
        if (labels.Distinct().Count() != labels.Count)
            for (int i = 0; i < labels.Count; i++)
                if (!a.Family.Tiers[i].Effect.StartsWith(a.Family.Name, StringComparison.Ordinal))
                    labels[i] = a.Family.Tiers[i].Effect.Split(' ')[0] + " " + labels[i];

        var pills = new List<PillVm>();
        for (int i = 0; i < a.Family.Tiers.Count; i++)
        {
            var tier = a.Family.Tiers[i];
            string? lane = a.TierLanes[i];
            string items = tier.Items.Count == 0
                ? "No item is known to carry this tier."
                : tier.Items.Count == 1
                    ? "Item: " + tier.Items[0].Name
                    : $"{tier.Items.Count} items — click the row for details.";
            string tip = tier.Effect
                + (tier.Description.Length > 0 ? "\n" + tier.Description : "")
                + "\n" + items
                + (tier.SummonedOnly ? "\n(summoned only — a conjured temporary, not huntable gear)" : "");
            string label = labels[i];
            // Pills answer one question: do you OWN it. Green filled = owned
            // and worn, green outline = owned in storage (the sort and the
            // place badge tell them apart), gray outline = missing. In a
            // MIXED family an unowned summoned-only tier doesn't render at
            // all — it can't be hunted, so it isn't a gap (a conjured one you
            // HOLD still shows). Inside the Summoned fold the tiers are the
            // content.
            if (lane is null && tier.SummonedOnly && a.Family.Group != "summoned") continue;
            pills.Add(lane switch
            {
                "worn" => new PillVm(label, StatusFg[2], StatusFg[2], WornPillFg,
                    tip + "\n(worn)"),
                not null => new PillVm(label, Brushes.Transparent, StatusFg[2], StatusFg[2],
                    tip + "\n(owned, not worn)"),
                null => new PillVm(label, Brushes.Transparent, PillOffBorder, PillOffFg, tip),
            });
        }

        bool wornIsBest = a.BestTier > 0 && a.WornTier == a.BestTier;
        // The verdict carries the "wearing II → III out there" story now, so
        // the item cell shows just the best owned carrier.
        string best = a.BestTier == 0 ? "none owned" : a.BestItem;
        string statusTip = a.Status switch
        {
            2 => "Wearing the best.",
            1 => "Upgrade available.",
            _ => "None owned.",
        };
        string place = a.BestTier == 0 ? "" : wornIsBest ? "WORN" : a.BestPlace.ToUpperInvariant();
        bool open = _openFamily == a.Family.Name;
        return new FocusVm(a.Family.Name, a.Family.Kind, a.Family.Tiers[0].Description,
            StatusFg[a.Status], pills, best, a.BestTier == 0 ? null : a.BestEffect,
            place, wornIsBest ? StatusFg[2] : StatusFg[1],
            a.BestTier == 0 ? Visibility.Collapsed : Visibility.Visible,
            StatusTip: statusTip,
            // The wash follows the section bands — worn / stored / missing —
            // so the background and the grouping always agree.
            RowBg: RowWash[a.WornTier > 0 ? 2 : a.BestTier > 0 ? 1 : 0],
            Chevron: open ? "▾" : "▸",
            Details: open ? MakeDetails(a) : null,
            DetailsVis: open ? Visibility.Visible : Visibility.Collapsed,
            // Summoned charms skip the verdict — "huntable" would lie about
            // a conjured temporary.
            VerdictText: a.Family.Group == "summoned" ? "" : FocusEffects.VerdictText(a));
    }

    private static readonly Brush DetailHeaderFg = Freeze("#9FB4D0");
    private static readonly Brush DetailDimFg = Freeze("#5F7189");

    /// <summary>The fold-out: every tier with its effect text and every known
    /// carrier item — slot, classes, and where YOUR copy sits, if anywhere.</summary>
    private List<DetailVm> MakeDetails(FocusEffects.AuditRow a)
    {
        var details = new List<DetailVm>();
        for (int i = 0; i < a.Family.Tiers.Count; i++)
        {
            var tier = a.Family.Tiers[i];
            string cap = tier.LevelCap is { } c ? $" · decays over lvl {c}" : "";
            string summoned = tier.SummonedOnly ? " · summoned only" : "";
            // The tier title wears its pill's color (green when you own it),
            // a marker and a size step up — it anchors the whole block below.
            Brush headerFg = a.TierLanes[i] is not null ? StatusFg[2] : DetailHeaderFg;
            details.Add(new DetailVm($"▸  {tier.Effect}{cap}{summoned}", headerFg,
                FontWeights.SemiBold, DetailHeadPad, FontSize: 12.5));
            if (tier.Description.Length > 0)
                details.Add(new DetailVm(tier.Description, DetailDimFg, FontWeights.Normal, DetailTab));
            if (tier.Items.Count == 0)
                details.Add(new DetailVm("no item is known to carry this tier", DetailDimFg, FontWeights.Normal, DetailTab));
            foreach (var item in tier.Items)
            {
                var parts = new List<string> { item.Name };
                if (item.Slot.Length > 0) parts.Add(item.Slot);
                if (item.Classes.Length > 0) parts.Add(item.Classes);
                bool owned = _ownedByKey.TryGetValue(FocusEffects.ItemKey(item.Name), out string? lane);
                if (owned) parts.Add("you: " + OwnLabel(lane!));
                Brush fg = !owned ? DetailDimFg : lane == "worn" ? StatusFg[2] : StatusFg[1];
                details.Add(new DetailVm(string.Join("  ·  ", parts), fg, FontWeights.Normal, DetailTab));

                // Who drops it, and where — from the wiki's own table; the
                // mob line links to its wiki page.
                var drops = _itemStats.Lookup(item.Name)?.Drops;
                if (drops is not { Count: > 0 }) continue;
                foreach (var d in drops.Take(3))
                {
                    string mob = d.Length > 0 ? d[0] : "";
                    string zone = d.Length > 1 ? d[1] : "";
                    if (mob.Length == 0) continue;
                    details.Add(new DetailVm(
                        $"↳  {mob}{(zone.Length > 0 ? $" — {zone}" : "")}",
                        DetailDimFg, FontWeights.Normal, DetailDropTab,
                        Url: WikiUrl(mob)));
                }
                if (drops.Count > 3)
                    details.Add(new DetailVm($"↳  …and {drops.Count - 3} more droppers (wiki page has the full list)",
                        DetailDimFg, FontWeights.Normal, DetailDropTab, Url: WikiUrl(item.Name)));
            }
        }
        return details;
    }

    private static readonly Thickness DetailDropTab = new(30, 1, 0, 0);

    /// <summary>The eqlwiki page for a mob or item name — pages live at the
    /// site ROOT (eqlwiki.com/Chief_Goonda, no /wiki/ segment), spaces spelled
    /// as underscores.</summary>
    private static string WikiUrl(string name) =>
        "https://eqlwiki.com/" + Uri.EscapeDataString(name.Trim().Replace(' ', '_'));

    /// <summary>Front the audit board (also the selftest hook — a collapsed
    /// list renders nothing and would hide a binding typo).</summary>
    public void ShowFocusTab()
    {
        ShowTab("focus");
        UpdateLayout();
    }

    // ---- the character header (name · level · classes — the session
    // panel's ding//who machinery, shared by every tab) ----------------------

    private static readonly Brush HdrText = Freeze("#C9D4E3");
    private static readonly Brush HdrDim = Freeze("#5C6B82");
    private static readonly Brush HdrBorder = Freeze("#3A4560");

    private void RefreshCharHeader()
    {
        CharName.Text = _charName.Length > 0 ? _charName : "Character";
        ServerText.Text = _server;
        ClassChips.Children.Clear();
        LevelText.Text = "";
        LevelAge.Text = "";
        LevelAge.ToolTip = null;
        WhoHint.Text = "";

        var stmt = _session?.LevelStatement;
        if (stmt is { } s)
        {
            LevelText.Text = "Level " + s.Level;
            LevelAge.Text = AgoText(DateTime.Now - s.Ts) + " ago";
            LevelAge.ToolTip = _session!.LevelInfo(DateTime.Now).Tip;
        }
        // This session's /who first; otherwise the combo the parser SAVED
        // from your last /who (char-state.json) — a restart must not forget
        // who you are (Johan, 2 Sep).
        string classes = _session?.WhoClasses ?? "";
        bool saved = false;
        if (classes.Length == 0)
        {
            var (savedClasses, savedLevel) = SharedConfig.Value.LoadLastClasses(_charName);
            if (savedClasses.Length > 0)
            {
                classes = savedClasses;
                saved = true;
                if (stmt is null && savedLevel > 0) LevelText.Text = "Level " + savedLevel;
            }
        }
        foreach (var cls in classes.Split('/', StringSplitOptions.RemoveEmptyEntries))
            ClassChips.Children.Add(HeaderChip(cls, HdrText, HdrBorder));
        if (stmt is { FromWho: true })
            ClassChips.Children.Add(HeaderChip("stated by /who", StatusFg[2], StatusFg[2]));
        else if (stmt is not null)
            ClassChips.Children.Add(HeaderChip("from your last ding", HdrDim, HdrBorder));
        else if (saved)
            ClassChips.Children.Add(HeaderChip("saved from your last /who", HdrDim, HdrBorder));
        if (stmt is null && classes.Length == 0)
            WhoHint.Text = "type /who in game for classes + level";
    }

    /// <summary>The class combo we know — this session's /who, else the saved one.</summary>
    private string KnownClasses()
    {
        string live = _session?.WhoClasses ?? "";
        return live.Length > 0 ? live : SharedConfig.Value.LoadLastClasses(_charName).Classes;
    }

    private static UIElement HeaderChip(string text, Brush fg, Brush border) => new Border
    {
        CornerRadius = new CornerRadius(9),
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 1, 8, 2),
        Margin = new Thickness(0, 0, 5, 0),
        Child = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
        },
    };

    /// <summary>What a tier pill says: the effect's own trailing token when
    /// it's a numeral (III, 14 — the resonances' numbers are the mod
    /// strength), the plain tier number otherwise ("Jolum's Minor Abatement"
    /// → 1; the exact name lives on the tooltip).</summary>
    private static string TierLabel(string effect, int tierNum)
    {
        string last = effect.Split(' ')[^1];
        if (System.Text.RegularExpressions.Regex.IsMatch(last, @"^([IVX]{1,4}|\d{1,2})$")) return last;
        return tierNum.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex SlotNumRx =
        new(@"-Slot(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>An exaltation's interesting "where" is the item wearing it,
    /// not the socket path — the verbatim spelling stays on the tooltip.
    /// Items with sockets wear a pill per slot (green = occupied, gray =
    /// empty; numbers are the dump's spelling — the game's slot TYPES are
    /// not stated anywhere observable) and unfold for the details.</summary>
    private RowVm MakeRowVm(InventoryStore.CarryRow r)
    {
        string count = r.Count > 1 ? $"{r.Count}×" : "";
        var icon = ItemIcons.Get(_itemStats.Lookup(r.Name)?.Icon);
        var iconVis = icon is null ? Visibility.Collapsed : Visibility.Visible;
        if (_tab == "exalt" && r.Host.Length > 0)
            return new RowVm(r.Name, "in " + r.Host, r.Location, count,
                Icon: icon, IconVis: iconVis);

        List<PillVm>? pills = null;
        var ownEffects = _focus.EffectsOf(r.Name);
        _entryByLoc.TryGetValue(r.Location, out var entry);
        // A bag's children are its contents, not sockets — no pills for bags.
        if (entry is not null && InventoryStore.IsContainer(entry)) entry = null;
        if (entry is { Children.Count: > 0 })
        {
            pills = new List<PillVm>();
            foreach (var child in entry.Children)
            {
                var (label, slotName) = SlotTypeOf(child.Location);
                // Occupied pills wear their socket TYPE's color (SocketColors,
                // shared with the character sheet); empty stays a gray outline.
                pills.Add(child.Empty
                    ? new PillVm(label, Brushes.Transparent, PillOffBorder, PillOffFg,
                        $"{slotName} — empty")
                    : new PillVm(label, SocketColors.Fill(label), SocketColors.Fill(label),
                        SocketColors.Ink, $"{slotName} — {child.Name}"));
            }
        }

        bool foldable = pills is not null || ownEffects.Count > 0;
        bool open = foldable && _openItem == r.Location;
        return new RowVm(r.Name, r.Location,
            r.Host.Length > 0 ? "inside " + r.Host : null, count,
            pills,
            Chevron: foldable ? (open ? "▾" : "▸") : "",
            Details: open ? MakeItemDetails(r, entry, ownEffects) : null,
            DetailsVis: open ? Visibility.Visible : Visibility.Collapsed,
            RowKey: r.Location,
            Icon: icon, IconVis: iconVis);
    }

    /// <summary>The item fold-out: what the item itself grants, then each
    /// socket with its occupant and anything we can PROVE about it (its
    /// focus effect via the audit table — slot types are the game's secret).</summary>
    private List<DetailVm> MakeItemDetails(InventoryStore.CarryRow r,
        InventoryStore.Entry? entry, IReadOnlyList<(FocusEffects.Family Fam, FocusEffects.Tier Tier)> ownEffects)
    {
        var details = new List<DetailVm>();
        foreach (var (fam, tier) in ownEffects)
            details.Add(new DetailVm($"focus: {tier.Effect} · {fam.Kind}", StatusFg[2], FontWeights.SemiBold));
        if (entry is not null)
            foreach (var child in entry.Children)
            {
                var (_, slotName) = SlotTypeOf(child.Location);
                if (child.Empty)
                {
                    details.Add(new DetailVm($"{slotName}: empty", DetailDimFg, FontWeights.Normal, DetailTab));
                    continue;
                }
                var socketFx = _focus.EffectsOf(child.Name);
                string fx = socketFx.Count > 0
                    ? " — " + string.Join(", ", socketFx.Select(e => $"{e.Tier.Effect} ({e.Fam.Kind})"))
                    : "";
                details.Add(new DetailVm($"{slotName}: {child.Name}{fx}",
                    socketFx.Count > 0 ? StatusFg[2] : DetailHeaderFg, FontWeights.Normal, DetailTab));
            }
        return details;
    }

    /// <summary>A child location's slot type ("Head-Slot7" → F / Focus
    /// Exaltation), unmapped numbers verbatim.</summary>
    private static (string Label, string Name) SlotTypeOf(string location)
    {
        var m = SlotNumRx.Match(location);
        return m.Success
            ? InventoryStore.SlotType(int.Parse(m.Groups[1].Value))
            : ("?", "slot ?");
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
