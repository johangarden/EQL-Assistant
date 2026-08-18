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
/// </summary>
public partial class InventoryWindow : Window
{
    private sealed record RowVm(string Name, string Location, string? LocationTip, string CountText);
    private sealed record PillVm(string Label, Brush Bg, Brush Border, Brush Fg, string Tip);
    private sealed record FocusVm(string Family, string Kind, string? FamilyTip, Brush StatusBrush,
        List<PillVm> Pills, string BestText, string? BestTip, string PlaceText, Brush PlaceBrush,
        Visibility PlaceVis = Visibility.Visible,
        Visibility HeaderVis = Visibility.Collapsed, Visibility RowVis = Visibility.Visible,
        bool IsFoldToggle = false);

    /// <summary>A section header row ("Spells", "Songs & instruments").</summary>
    private static FocusVm FocusHeader(string title, bool foldToggle = false) => new(title, "", null,
        Brushes.Transparent, new List<PillVm>(), "", null, "", Brushes.Transparent,
        Visibility.Collapsed, HeaderVis: Visibility.Visible, RowVis: Visibility.Collapsed,
        IsFoldToggle: foldToggle);

    private bool _summonedOpen; // the summoned-charm section starts folded

    private static readonly (string Id, string Label)[] Tabs =
    {
        ("items", "Items"),
        ("exalt", "Exaltations"),
        ("focus", "Focus effects"),
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

    // Lane chips wear their family: a cool wash for what's ON you (worn,
    // bags, the keyring collections), a warm wash for what's STASHED (bank,
    // depot, hoard). The active chip keeps the accent look either way.
    private static readonly Brush CarryTint = Freeze("#144FC3F7");
    private static readonly Brush StashTint = Freeze("#14FFB74D");

    private readonly string _eqRoot;
    private readonly string _charName;
    private readonly string _server;
    private FileSystemWatcher? _fsWatcher;
    private readonly DispatcherTimer _reloadDebounce;
    private readonly DispatcherTimer _freshnessTick;

    private readonly FocusEffects _focus = new();
    private List<InventoryStore.CarryRow> _rows = new();
    private List<FocusEffects.AuditRow> _audit = new();
    private InventoryStore.Dump? _dump;
    private string _tab = "items";
    private string? _lane;            // null = All
    private string? _dumpPath;
    private DateTime _dumpMtime;
    private DateTime? _parsedAt;      // set on watcher-triggered reloads only

    public InventoryWindow(string eqRoot, string charName, string server)
    {
        InitializeComponent();
        _eqRoot = eqRoot;
        _charName = charName;
        _server = server;

        // The game rewrites the file in place; wait for the write to settle.
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); Reload(fromWatch: true); };

        _freshnessTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _freshnessTick.Tick += (_, _) => UpdateFreshness();

        Loaded += (_, _) => { Reload(); StartWatching(); _freshnessTick.Start(); };
        Closed += (_, _) =>
        {
            _reloadDebounce.Stop();
            _freshnessTick.Stop();
            _fsWatcher?.Dispose();
            _fsWatcher = null;
        };
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
            TabPanel.Children.Clear();
            LanePanel.Children.Clear();
            ParsedText.Visibility = Visibility.Collapsed;
            WarnText.Visibility = Visibility.Collapsed;
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
        if (fromWatch) _parsedAt = DateTime.Now;

        NoDumpPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;

        BuildTabs();
        BuildLaneChips();
        UpdateCoverage();
        UpdateFreshness();
        ApplyFilters();
    }

    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        foreach (var (id, label) in Tabs)
        {
            // The focus tab counts green families over the total — the gap
            // report in two digits. Summoned charms are conjured temporaries
            // and stay out of the score. Row tabs count their rows.
            var scored = _audit.Where(a => a.Family.Group != "summoned").ToList();
            string text = id == "focus"
                ? $"{label}  {scored.Count(a => a.Status == 2)}/{scored.Count}"
                : $"{label}  {_rows.Count(r => InventoryStore.TabOf(r) == id)}";
            AddPill(TabPanel, text, id, id == _tab, picked =>
            {
                _tab = picked!;
                RepaintPills(TabPanel, _tab);
                _lane = null; // a lane picked on one tab means nothing on another
                BuildLaneChips();
                ApplyFilters();
            });
        }
    }

    /// <summary>The gold coverage story: what this dump does NOT speak for.
    /// A missing storage is "the dump does not say", never "empty".</summary>
    private void UpdateCoverage()
    {
        if (_dump is null) return;
        var missing = InventoryStore.MissingStorages(_dump);
        if (missing.Count == 0)
        {
            WarnText.Visibility = Visibility.Collapsed;
        }
        else
        {
            WarnText.Text = "⚠  Not in this dump: " + string.Join(" · ", missing)
                + ".  The game only writes a storage while its window is open — open them in game, then re-type /outputfile inventory.";
            WarnText.Visibility = Visibility.Visible;
        }
    }

    private void BuildLaneChips()
    {
        // The audit is not row-backed; place is spelled per family instead.
        if (_tab == "focus")
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
        AddPill(LanePanel, "All", null, _lane is null, Pick);
        string prevGroup = "";
        foreach (var (id, label) in lanes)
        {
            string group = InventoryStore.LaneGroup(id);
            // A breath of air where one family ends and the next begins.
            bool gap = prevGroup.Length > 0 && group != prevGroup;
            AddPill(LanePanel, label, id, _lane == id, Pick, GroupTint(group), gap);
            prevGroup = group;
        }
    }

    private static Brush? GroupTint(string group) => group switch
    {
        "carry" => CarryTint,
        "stash" => StashTint,
        _ => null,
    };

    private void AddPill(WrapPanel panel, string label, string? id, bool on, Action<string?> onPick,
        Brush? offTint = null, bool gapBefore = false)
    {
        var text = new TextBlock { Text = label, FontSize = 11 };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(gapBefore ? 14 : 0, 0, 5, 0),
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

    private static void RepaintPills(WrapPanel panel, string? activeId)
    {
        foreach (Border b in panel.Children)
        {
            var (id, offTint) = ((string?, Brush?))b.Tag;
            bool isOn = Equals(id, activeId);
            b.Background = isOn ? SegOnBg : offTint ?? Brushes.Transparent;
            ((TextBlock)b.Child).Foreground = isOn ? SegOnFg : SegOffFg;
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
        string q = SearchBox.Text.Trim().ToLowerInvariant();
        bool focus = _tab == "focus";
        FocusList.Visibility = focus ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
        if (focus) { ApplyFocusFilter(q); return; }

        var tabRows = _rows.Where(r => InventoryStore.TabOf(r) == _tab).ToList();
        var matched = tabRows
            .Where(r => (_lane is null || r.Lane == _lane)
                        && (q.Length == 0 || r.SearchKey.Contains(q, StringComparison.Ordinal)))
            .Select(MakeRowVm)
            .ToList();
        ResultsList.ItemsSource = matched;
        CountText.Text = $"{matched.Count} of {tabRows.Count}";
        EmptyTabText.Text = _tab == "exalt" ? "No exaltation sockets in this dump." : "Nothing in this dump.";
        EmptyTabText.Visibility = tabRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                || t.Items.Any(i => i.Contains(q, StringComparison.OrdinalIgnoreCase)));

        // Within a section: what you WEAR first, then what you own in
        // storage, the gaps last — alphabetical inside each bucket.
        static int OwnBucket(FocusEffects.AuditRow a) =>
            a.WornTier > 0 ? 0 : a.BestTier > 0 ? 1 : 2;
        List<FocusEffects.AuditRow> Section(Func<FocusEffects.AuditRow, bool> pick) => _audit
            .Where(a => pick(a) && Match(a))
            .OrderBy(OwnBucket)
            .ThenBy(a => a.Family.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var spells = Section(a => a.Family.Group is not ("song" or "summoned"));
        var songs = Section(a => a.Family.Group == "song");
        var summoned = Section(a => a.Family.Group == "summoned");

        var shown = new List<FocusVm>();
        if (spells.Count > 0)
        {
            shown.Add(FocusHeader("Spells"));
            shown.AddRange(spells.Select(MakeFocusVm));
        }
        if (songs.Count > 0)
        {
            shown.Add(FocusHeader("Songs & instruments"));
            shown.AddRange(songs.Select(MakeFocusVm));
        }
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
        CountText.Text = $"{spells.Count + songs.Count + summoned.Count} of {_audit.Count} effects";
        EmptyTabText.Visibility = Visibility.Collapsed;
    }

    private void FocusList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FocusVm { IsFoldToggle: true })
        {
            _summonedOpen = !_summonedOpen;
            ApplyFocusFilter(SearchBox.Text.Trim().ToLowerInvariant());
        }
    }

    private static readonly Brush WornPillFg = Freeze("#0F1620");

    private FocusVm MakeFocusVm(FocusEffects.AuditRow a)
    {
        // "Minor Improved Damage I" and "Improved Damage I" share a trailing
        // numeral — when labels collide inside a family, number them 1..N.
        var labels = a.Family.Tiers.Select(t => TierLabel(t.Effect, t.TierNum)).ToList();
        if (labels.Distinct().Count() != labels.Count)
            labels = a.Family.Tiers.Select(t => t.TierNum.ToString()).ToList();

        var pills = new List<PillVm>();
        for (int i = 0; i < a.Family.Tiers.Count; i++)
        {
            var tier = a.Family.Tiers[i];
            string? lane = a.TierLanes[i];
            string items = tier.Items.Count == 0
                ? "No item is known to carry this tier."
                : "Items: " + string.Join(", ", tier.Items);
            string tip = tier.Effect
                + (tier.Description.Length > 0 ? "\n" + tier.Description : "")
                + "\n" + items;
            string label = labels[i];
            // Three states: WORN fills solid, owned-but-stored keeps the
            // translucent wash, unowned stays a gray outline.
            pills.Add(lane switch
            {
                "worn" => new PillVm(label, StatusFg[a.Status], StatusFg[a.Status], WornPillFg,
                    tip + "\n(worn)"),
                not null => new PillVm(label, StatusBg[a.Status], StatusFg[a.Status], StatusFg[a.Status],
                    tip + "\n(owned, not worn)"),
                null => new PillVm(label, Brushes.Transparent, PillOffBorder, PillOffFg, tip),
            });
        }

        bool wornIsBest = a.BestTier > 0 && a.WornTier == a.BestTier;
        string best = a.BestTier == 0
            ? "none owned"
            : a.WornTier > 0 && !wornIsBest
                ? $"{a.BestItem} (wearing {a.WornTier})"
                : a.BestItem;
        string place = a.BestTier == 0 ? "" : wornIsBest ? "WORN" : a.BestPlace.ToUpperInvariant();
        return new FocusVm(a.Family.Name, a.Family.Kind, a.Family.Tiers[0].Description,
            StatusFg[a.Status], pills, best, a.BestTier == 0 ? null : a.BestEffect,
            place, wornIsBest ? StatusFg[2] : StatusFg[1],
            a.BestTier == 0 ? Visibility.Collapsed : Visibility.Visible);
    }

    /// <summary>Selftest hook: front the audit board so its template
    /// actually instantiates (a collapsed list renders nothing and would
    /// hide a binding typo).</summary>
    public void ShowFocusTabForTest()
    {
        _tab = "focus";
        RepaintPills(TabPanel, _tab);
        BuildLaneChips();
        ApplyFilters();
        UpdateLayout();
    }

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

    /// <summary>An exaltation's interesting "where" is the item wearing it,
    /// not the socket path — the verbatim spelling stays on the tooltip.</summary>
    private RowVm MakeRowVm(InventoryStore.CarryRow r)
    {
        string count = r.Count > 1 ? $"{r.Count}×" : "";
        if (_tab == "exalt" && r.Host.Length > 0)
            return new RowVm(r.Name, "in " + r.Host, r.Location, count);
        return new RowVm(r.Name, r.Location,
            r.Host.Length > 0 ? "inside " + r.Host : null, count);
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
