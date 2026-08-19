using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The paper-doll: worn gear in anatomical rows (ears–face–head–neck ·
/// shoulders–chest–back–arms · wrists–fingers–hands · waist–legs–feet ·
/// wildcards · weapons), each cell wearing the wiki's own item art, its slot
/// title above, the item's +N tier as a gold corner pill and typed socket
/// pills (O·F·C·W·P). A detail pane follows the selected slot with four
/// tabs: Totals (stats-from-gear sums, base → at worn tier), Sockets (the
/// game's own item-window layout), Focus (the audit's view of the slot) and
/// Stats (wiki values scaled to the worn tier by eqlwiki's own slider
/// rules — see Services/ItemUpgrade). The footer rides the session panel's
/// ding//who level machinery.
/// </summary>
public partial class CharacterSheetWindow : Window
{
    private sealed record PaneLineVm(string Key, string Value, Brush Fg, Visibility KeyVis);

    // The doll's rows: base token + which occurrence of it (two Ears, two
    // Wrists, two Fingers, two Any Slots — file order decides first/second).
    private static readonly (string Token, int Nth)[][] DollLayout =
    {
        new[] { ("Ear", 0), ("Face", 0), ("Head", 0), ("Neck", 0), ("Ear", 1) },
        new[] { ("Shoulders", 0), ("Chest", 0), ("Back", 0), ("Arms", 0) },
        new[] { ("Wrist", 0), ("Fingers", 0), ("Hands", 0), ("Fingers", 1), ("Wrist", 1) },
        new[] { ("Waist", 0), ("Legs", 0), ("Feet", 0) },
        new[] { ("Any Slot", 0), ("Any Slot", 1) },
        new[] { ("Primary", 0), ("Secondary", 0), ("Range", 0), ("Ammo", 0), ("Held", 0) },
    };

    private static readonly Brush CellBg = Freeze("#1A2230");
    private static readonly Brush CellBorder = Freeze("#3A4560");
    private static readonly Brush CellSelBorder = Freeze("#4FC3F7");
    private static readonly Brush CellSelBg = Freeze("#1F2A3C");
    private static readonly Brush SlotFg = Freeze("#7F93AD");
    private static readonly Brush DimFg = Freeze("#5C6B82");
    private static readonly Brush TextFg = Freeze("#C9D4E3");
    private static readonly Brush GoldFg = Freeze("#E8C15A");
    private static readonly Brush GreenFg = Freeze("#66BB6A");
    private static readonly Brush AmberFg = Freeze("#FFB74D");
    private static readonly Brush PillOffBorder = Freeze("#232C3E");
    private static readonly Brush WornPillFg = Freeze("#0F1620");
    private static readonly Brush TabOnBg = Freeze("#16283E");
    private static readonly Brush TabOnFg = Freeze("#4FC3F7");

    private readonly string _eqRoot;
    private readonly string _charName;
    private readonly string _server;
    private readonly FocusEffects _focus;
    private readonly ItemStats _stats;
    private readonly SessionStats? _session;

    private InventoryStore.Dump? _dump;
    private List<FocusEffects.AuditRow> _audit = new();
    private readonly Dictionary<(string Token, int Nth), InventoryStore.Entry> _worn = new();
    private (string Token, int Nth)? _selected;
    private string _paneTab = "totals";
    private readonly List<Border> _cells = new();

    private string? _dumpPath;
    private System.IO.FileSystemWatcher? _fsWatcher;
    private readonly System.Windows.Threading.DispatcherTimer _ageTick;
    private readonly System.Windows.Threading.DispatcherTimer _reloadDebounce;

    public CharacterSheetWindow(string eqRoot, string charName, string server,
        FocusEffects focus, ItemStats stats, SessionStats? session)
    {
        InitializeComponent();
        Interop.WindowTheme.ApplyDark(this);
        DialogPlacement.Persist(this, "charsheet");
        _eqRoot = eqRoot;
        _charName = charName;
        _server = server;
        _focus = focus;
        _stats = stats;
        _session = session;
        // "updated 28h ago" must not fossilize while the window sits open.
        _ageTick = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMinutes(1) };
        _ageTick.Tick += (_, _) => RefreshHeader();
        // The game rewrites the dump in place — settle before re-reading.
        _reloadDebounce = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(600) };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); Reload(); };
        Loaded += (_, _) => { Reload(); _ageTick.Start(); };
        Closed += (_, _) =>
        {
            _ageTick.Stop();
            _reloadDebounce.Stop();
            _fsWatcher?.Dispose();
            _fsWatcher = null;
        };
    }

    public void Reload()
    {
        CharName.Text = _charName.Length > 0 ? _charName : "Character";

        string? path = InventoryStore.FindDumpFile(_eqRoot, _charName, _server);
        _dumpPath = path;
        RefreshHeader();
        WatchDump(path);
        if (path is null)
        {
            NoDumpText.Visibility = Visibility.Visible;
            return;
        }
        NoDumpText.Visibility = Visibility.Collapsed;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            _dump = InventoryStore.Parse(reader.ReadToEnd());
        }
        catch
        {
            return;
        }

        var (rows, _) = InventoryStore.CarryAll(_dump);
        _audit = _focus.Audit(rows);
        int green = _audit.Count(a => a.Family.Group != "summoned" && a.Status == 2);
        int upgradable = _audit.Count(a => a.Family.Group != "summoned" && a.Status == 1);
        int missing = _audit.Count(a => a.Family.Group != "summoned" && a.Status == 0);
        FocusSummary.Text = $"FOCUS  {green} worn best · {upgradable} upgradable · {missing} missing";

        // Worn entries by (token, occurrence) in file order.
        _worn.Clear();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in _dump.Items)
        {
            if (InventoryStore.LaneOfBase(e.Base) != "worn") continue;
            int nth = counts.TryGetValue(e.Base, out int c) ? c : 0;
            counts[e.Base] = nth + 1;
            _worn[(e.Base, nth)] = e;
        }

        BuildDoll();
        // Nothing preselected: the pane opens on the whole character (Totals)
        // and a highlighted cell would claim otherwise.
        if (_selected is { } sel && !_worn.ContainsKey(sel)) _selected = null;
        RefreshPane();
    }

    // ---- the header (Companion-style: level · age · classes · source) -----------

    private static string AgeText(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return "just now";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m ago";
        if (t.TotalHours < 24) return $"{(int)t.TotalHours}h {t.Minutes}m ago";
        return $"{(int)t.TotalDays}d {t.Hours}h ago";
    }

    private void RefreshHeader()
    {
        ServerText.Text = _server;
        ClassChips.Children.Clear();
        LevelText.Text = "";
        LevelAge.Text = "";
        WhoHint.Text = "";

        var stmt = _session?.LevelStatement;
        if (stmt is { } s)
        {
            LevelText.Text = "Level " + s.Level;
            LevelAge.Text = AgeText(DateTime.Now - s.Ts);
            LevelAge.ToolTip = _session!.LevelInfo(DateTime.Now).Tip;
        }
        string classes = _session?.WhoClasses ?? "";
        foreach (var cls in classes.Split('/', StringSplitOptions.RemoveEmptyEntries))
            ClassChips.Children.Add(Chip(cls, TextFg, CellBorder));
        if (stmt is { FromWho: true })
            ClassChips.Children.Add(Chip("stated by /who", GreenFg, GreenFg));
        else if (stmt is not null)
            ClassChips.Children.Add(Chip("from your last ding", DimFg, CellBorder));
        if (stmt is null && classes.Length == 0)
            WhoHint.Text = "type /who in game for classes + level";

        if (_dumpPath is { } path && File.Exists(path))
        {
            DumpStrip.Visibility = Visibility.Visible;
            DumpAge.Text = "updated " + AgeText(DateTime.Now - File.GetLastWriteTime(path));
            DumpAge.ToolTip = path;
            DumpHow.ToolTip = $"In game, type /outputfile inventory — the game writes "
                + $"{_charName}_{_server}-Inventory.txt into its own folder and this sheet "
                + "re-reads it the moment it changes. The Inventory window (the chest on "
                + "the toolbar) has the full how-to.";
        }
        else
        {
            DumpStrip.Visibility = Visibility.Collapsed;
        }
    }

    private static UIElement Chip(string text, Brush fg, Brush border) => new Border
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

    /// <summary>Re-read the sheet whenever the game rewrites the dump — the
    /// strip promises "the sheet follows the dump" and this keeps it true.</summary>
    private void WatchDump(string? path)
    {
        string? dir = path is null ? null : Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return;
        if (_fsWatcher is not null &&
            string.Equals(_fsWatcher.Path, dir, StringComparison.OrdinalIgnoreCase)) return;
        _fsWatcher?.Dispose();
        _fsWatcher = new System.IO.FileSystemWatcher(dir, Path.GetFileName(path!))
        {
            NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        void Poke(object? _, System.IO.FileSystemEventArgs __) => Dispatcher.BeginInvoke(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
        _fsWatcher.Changed += Poke;
        _fsWatcher.Created += Poke;
        _fsWatcher.Renamed += (s, e) => Poke(s, e);
    }

    // ---- the doll ---------------------------------------------------------------

    private void BuildDoll()
    {
        DollRows.Children.Clear();
        _cells.Clear();
        foreach (var row in DollLayout)
        {
            // Short rows keep the 5-wide cell size and center via side margins
            // (a 4-in-5 pad can't center on a uniform grid).
            double inset = row.Length switch { 4 => 0.1, 3 => 0.2, 2 => 0.3, _ => 0 };
            var grid = new UniformGrid { Rows = 1, Columns = row.Length, Margin = new Thickness(0, 0, 0, 9) };
            foreach (var slot in row) grid.Children.Add(BuildCellWrap(slot));
            var host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(inset, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - 2 * inset, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(inset, GridUnitType.Star) });
            Grid.SetColumn(grid, 1);
            host.Children.Add(grid);
            DollRows.Children.Add(host);
        }
    }

    private UIElement BuildCellWrap((string Token, int Nth) slot)
    {
        var wrap = new StackPanel { Margin = new Thickness(3, 0, 4, 0) };
        _worn.TryGetValue(slot, out var entry);

        // Slot title line, with the item's +N riding it in gold — outside the
        // cell, where it can't crowd the name.
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 2) };
        title.Children.Add(new TextBlock
        {
            Text = slot.Token.ToUpperInvariant(),
            Foreground = SlotFg,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
        });
        if (entry is not null && !entry.Empty && SplitTier(entry.Name).Tier is { Length: > 0 } tierText)
            title.Children.Add(new TextBlock
            {
                Text = tierText,
                Foreground = GoldFg,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 0, 0, 0),
            });
        wrap.Children.Add(title);
        var inner = new Grid();
        var cell = new Border
        {
            Background = CellBg,
            BorderBrush = CellBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Height = 66, // static: every cell identical, filled or empty
            Cursor = Cursors.Hand,
            Child = inner,
            Tag = slot,
        };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (_selected == slot)
            {
                // Clicking the selected slot again deselects — back to the
                // whole character.
                _selected = null;
                _paneTab = "totals";
            }
            else
            {
                _selected = slot;
                // Clicking an item means "show me THIS" — leave the
                // character-wide Totals tab for the item's own sockets.
                if (_paneTab == "totals") _paneTab = "sockets";
            }
            RefreshPane();
        };
        _cells.Add(cell);

        var body = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        if (entry is null || entry.Empty)
        {
            body.Children.Add(new TextBlock
            {
                Text = "empty",
                Foreground = DimFg,
                FontStyle = FontStyles.Italic,
                FontSize = 11.5,
            });
        }
        else
        {
            var (baseName, _) = SplitTier(entry.Name);
            // Pills anchor to the cell floor in a STATIC five-position track
            // (O F C W P) so the same socket type aligns across every cell;
            // a position the item does not have ghosts out.
            body.Children.Add(BuildPillTrack(entry));
            DockPanel.SetDock(body.Children[0] as UIElement, Dock.Bottom);
            // The wiki's own item art, when the table carries it.
            if (ItemIcons.Get(_stats.Lookup(entry.Name)?.Icon) is { } icon)
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = icon,
                    Width = 21,
                    Height = 21,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 5, 0),
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
                DockPanel.SetDock(img, Dock.Left);
                body.Children.Add(img);
            }
            body.Children.Add(new TextBlock
            {
                Text = baseName,
                Foreground = TextFg,
                FontWeight = FontWeights.SemiBold,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 28,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }
        inner.Children.Add(body);

        wrap.Children.Add(cell);
        return wrap;
    }

    // Canonical socket order — the same position means the same type on
    // every cell. Ghosted borders/text for a socket the item doesn't have
    // (the dump enumerates each item's sockets: armor carries Ornamentation,
    // weapons carry Proc, everything carries Focus/Click/Worn).
    private static readonly (string Label, string Name)[] PillTrack =
    {
        ("O", "Ornamentation"),
        ("F", "Focus Exaltation"),
        ("C", "Click Exaltation"),
        ("W", "Worn Exaltation"),
        ("P", "Proc Exaltation"),
    };
    private static readonly Brush PillGhostBorder = Freeze("#1D2534");
    private static readonly Brush PillGhostFg = Freeze("#38455C");

    private UIElement BuildPillTrack(InventoryStore.Entry entry)
    {
        // label -> the item's socket child of that type, if the dump lists one
        var byType = new Dictionary<string, InventoryStore.Entry>(StringComparer.Ordinal);
        foreach (var child in entry.Children)
            byType.TryAdd(SlotTypeOf(child.Location).Label, child);

        int tier = TierOf(entry.Name);
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        for (int i = 0; i < PillTrack.Length; i++)
        {
            var (label, name) = PillTrack[i];
            byType.TryGetValue(label, out var child);
            bool has = child is not null;
            bool on = has && !child!.Empty;
            // Socket types unlock by item level (wiki "Exaltations"):
            // Ornamentation at +0, Focus +1, Click +2, Worn +3, Proc +4 —
            // the track's own order. A ghost can say WHY it's missing.
            string ghost = tier < i
                ? $"{name} — unlocks at +{i}"
                : $"{name} — this item has no such slot";
            pills.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(3, 0, 3, 1),
                Margin = new Thickness(0, 0, 2, 0),
                Background = on ? GreenFg : Brushes.Transparent,
                BorderBrush = on ? GreenFg : has ? PillOffBorder : PillGhostBorder,
                BorderThickness = new Thickness(1),
                ToolTip = on ? $"{name} — {child!.Name}"
                    : has ? $"{name} — empty"
                    : ghost,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = on ? WornPillFg : has ? DimFg : PillGhostFg,
                },
            });
        }
        return pills;
    }

    /// <summary>"Wicked Sallet +5" → ("Wicked Sallet", "+5").</summary>
    private static (string Name, string Tier) SplitTier(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(?<n>.+?) (?<t>\+\d+)$");
        return m.Success ? (m.Groups["n"].Value, m.Groups["t"].Value) : (name, "");
    }

    /// <summary>The +N tier stated by the item's name; a bare name is 0 for
    /// scaling (fraction inside the tier is never stated anywhere).</summary>
    private static int TierOf(string name)
    {
        var (_, tier) = SplitTier(name);
        return tier.Length > 0 && int.TryParse(tier.AsSpan(1), out int n) ? n : 0;
    }

    private static readonly System.Text.RegularExpressions.Regex SlotNumRx =
        new(@"-Slot(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static (string Label, string Name) SlotTypeOf(string location)
    {
        var m = SlotNumRx.Match(location);
        return m.Success
            ? InventoryStore.SlotType(int.Parse(m.Groups[1].Value))
            : ("?", "slot ?");
    }

    // ---- the detail pane --------------------------------------------------------

    private static readonly (string Id, string Label)[] PaneTabDefs =
    {
        ("totals", "Totals"),
        ("sockets", "Sockets"),
        ("focus", "Focus"),
        ("stats", "Stats"),
    };

    private void RefreshPane()
    {
        foreach (var c in _cells)
        {
            bool sel = _selected is { } s && c.Tag is ValueTuple<string, int> t && (t.Item1, t.Item2) == s;
            c.BorderBrush = sel ? CellSelBorder : CellBorder;
            c.Background = sel ? CellSelBg : CellBg;
        }

        PaneTabs.Children.Clear();
        foreach (var (id, label) in PaneTabDefs)
        {
            var text = new TextBlock { Text = label, FontSize = 10.5 };
            var tab = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 1, 10, 2),
                Margin = new Thickness(0, 0, 5, 0),
                BorderBrush = CellBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = text,
            };
            bool on = _paneTab == id;
            tab.Background = on ? TabOnBg : Brushes.Transparent;
            text.Foreground = on ? TabOnFg : SlotFg;
            string captured = id;
            tab.MouseLeftButtonUp += (_, _) =>
            {
                _paneTab = captured;
                // Totals is the whole character — drop the cell highlight so
                // the doll agrees with the pane.
                if (captured == "totals") _selected = null;
                RefreshPane();
            };
            PaneTabs.Children.Add(tab);
        }

        var lines = new List<PaneLineVm>();
        PaneHint.Visibility = Visibility.Collapsed;
        PaneIcon.Visibility = Visibility.Collapsed;
        PaneGrid.Visibility = Visibility.Collapsed;
        PaneGrid.Content = null;
        PaneLinesBelow.Visibility = Visibility.Collapsed;
        PaneLinesBelow.ItemsSource = null;

        if (_paneTab == "totals")
        {
            PaneTitle.Text = "Stats from gear";
            BuildTotalsLines(lines);
            PaneLines.ItemsSource = lines;
            return;
        }

        if (_selected is not { } sel2 || !_worn.TryGetValue(sel2, out var entry) || entry.Empty)
        {
            PaneTitle.Text = _selected is { } s2 ? s2.Token : "";
            PaneSub.Text = _selected is null ? "click a slot on the doll" : "empty slot";
            PaneLines.ItemsSource = lines;
            return;
        }

        PaneTitle.Text = entry.Name;
        PaneSub.Text = sel2.Token;
        if (ItemIcons.Get(_stats.Lookup(entry.Name)?.Icon) is { } icon)
        {
            PaneIcon.Source = icon;
            PaneIcon.Visibility = Visibility.Visible;
        }

        switch (_paneTab)
        {
            case "sockets": BuildSocketLines(entry, lines); break;
            case "focus": BuildFocusLines(entry, lines); break;
            default: BuildStatLines(entry, lines); break;
        }
        PaneLines.ItemsSource = lines;
    }

    private void BuildSocketLines(InventoryStore.Entry entry, List<PaneLineVm> lines)
    {
        if (entry.Children.Count == 0)
        {
            lines.Add(new PaneLineVm("", "no sockets on this item", DimFg, Visibility.Collapsed));
            return;
        }
        foreach (var child in entry.Children)
        {
            var (_, slotName) = SlotTypeOf(child.Location);
            if (child.Empty)
            {
                lines.Add(new PaneLineVm(slotName.ToUpperInvariant(), "empty", DimFg, Visibility.Visible));
                continue;
            }
            var fx = _focus.EffectsOf(child.Name);
            string val = child.Name + (fx.Count > 0
                ? " — " + string.Join(", ", fx.Select(e => e.Tier.Effect))
                : "");
            lines.Add(new PaneLineVm(slotName.ToUpperInvariant(), val, GreenFg, Visibility.Visible));
        }
    }

    private void BuildFocusLines(InventoryStore.Entry entry, List<PaneLineVm> lines)
    {
        // The item's own focus, then every socketed focus, then the audit's
        // verdict for those families.
        var all = new List<(FocusEffects.Family Fam, FocusEffects.Tier Tier, string Via)>();
        foreach (var (fam, tier) in _focus.EffectsOf(entry.Name))
            all.Add((fam, tier, "the item itself"));
        foreach (var child in entry.Children.Where(c => !c.Empty))
            foreach (var (fam, tier) in _focus.EffectsOf(child.Name))
                all.Add((fam, tier, child.Name));

        if (all.Count == 0)
        {
            lines.Add(new PaneLineVm("", "no known focus effect on this slot", DimFg, Visibility.Collapsed));
            return;
        }
        foreach (var (fam, tier, via) in all)
        {
            var auditRow = _audit.FirstOrDefault(a => a.Family == fam);
            string verdict = auditRow switch
            {
                { Status: 2 } => "wearing the best",
                { Status: 1 } => "upgrade available",
                _ => "",
            };
            Brush fg = auditRow?.Status == 2 ? GreenFg : AmberFg;
            lines.Add(new PaneLineVm(fam.Name.ToUpperInvariant() + " · " + fam.Kind,
                $"{tier.Effect} — via {via}" + (verdict.Length > 0 ? $" · {verdict}" : ""),
                fg, Visibility.Visible));
        }
    }

    private void BuildStatLines(InventoryStore.Entry entry, List<PaneLineVm> lines)
    {
        var rec = _stats.Lookup(entry.Name);
        if (rec is null)
        {
            lines.Add(new PaneLineVm("", "the wiki has no page for this item", DimFg, Visibility.Collapsed));
            return;
        }
        int tier = TierOf(entry.Name);

        // The game's own window order: flags line, class/race, then the grid.
        if (rec.Flags.Length > 0)
            lines.Add(new PaneLineVm("", rec.Flags, TextFg, Visibility.Collapsed));
        string classRace = ((rec.Classes.Length > 0 ? "Class: " + rec.Classes : "")
            + (rec.Races.Length > 0 ? "   Race: " + rec.Races : "")).Trim();
        if (classRace.Length > 0)
            lines.Add(new PaneLineVm("", classRace, SlotFg, Visibility.Collapsed));

        PaneGrid.Content = BuildStatGrid(rec, tier);
        PaneGrid.Visibility = Visibility.Visible;

        var below = new List<PaneLineVm>();
        if (rec.Effects.Length > 0) below.Add(new PaneLineVm("EFFECTS", rec.Effects, GreenFg, Visibility.Visible));
        if (rec.Extras.Length > 0) below.Add(new PaneLineVm("MORE", rec.Extras, DimFg, Visibility.Visible));
        if (below.Count > 0)
        {
            PaneLinesBelow.ItemsSource = below;
            PaneLinesBelow.Visibility = Visibility.Visible;
        }

        PaneHint.Text = tier > 0
            ? $"Gold = at +{tier} by the wiki's own item-level rules · (brackets) = wiki base. Merge exp banked inside a tier isn't in the dump, so live numbers can read a touch higher."
            : "Wiki base values — this item carries no +N tier.";
        PaneHint.Visibility = Visibility.Visible;
    }

    /// <summary>One "Label:   value" grid row — the scaled value in gold with
    /// the wiki base in dim brackets when the tier changes it.</summary>
    private static UIElement StatRowEl(string label, string baseText, string scaledText, string? tip = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 3), ToolTip = tip };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label + ":", Foreground = SlotFg, FontSize = 11.5 });
        var val = new TextBlock
        {
            FontSize = 11.5,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap, // "+37% (+36%)" folds, never clips
            Margin = new Thickness(8, 0, 0, 0),
        };
        if (scaledText != baseText)
        {
            val.Inlines.Add(new System.Windows.Documents.Run(scaledText)
                { Foreground = GoldFg, FontWeight = FontWeights.SemiBold });
            if (baseText.Length > 0)
                val.Inlines.Add(new System.Windows.Documents.Run($" ({baseText})") { Foreground = DimFg });
        }
        else
        {
            val.Inlines.Add(new System.Windows.Documents.Run(baseText) { Foreground = TextFg });
        }
        Grid.SetColumn(val, 1);
        g.Children.Add(val);
        return g;
    }

    /// <summary>Side-by-side equal-width columns, empty ones dropped.</summary>
    private static FrameworkElement Columns(params List<UIElement>[] cols)
    {
        var filled = cols.Where(c => c.Count > 0).ToList();
        var grid = new UniformGrid { Rows = 1, Columns = filled.Count };
        foreach (var col in filled)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
            foreach (var row in col) sp.Children.Add(row);
            grid.Children.Add(sp);
        }
        return grid;
    }

    private static UIElement BuildStatGrid(ItemStats.Record rec, int tier)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var topA = new List<UIElement>();
        var topB = new List<UIElement>();
        var attrs = new List<(int Order, UIElement El)>();
        var saves = new List<UIElement>();
        var other = new List<UIElement>();

        // Top-left: size/weight/levels (+ weapon skill/delay), like the game.
        if (rec.Size.Length > 0) topA.Add(StatRowEl("Size", rec.Size, rec.Size));
        if (rec.Weight.Length > 0)
        {
            string scaledW = rec.Weight;
            if (double.TryParse(rec.Weight, inv, out double w))
            {
                double sw = ItemUpgrade.ScaleWeight(w, tier);
                if (sw != w) scaledW = sw.ToString("0.0", inv);
            }
            topA.Add(StatRowEl("Weight", rec.Weight, scaledW));
        }
        if (rec.Skill.Length > 0) topA.Add(StatRowEl("Skill", rec.Skill, rec.Skill));
        if (rec.Delay is { } delay)
            topA.Add(StatRowEl("Atk Delay", delay.ToString(), delay.ToString(),
                "Delay never scales with the tier — that's why the ratio improves"));

        // Top-right: AC, pools and the weapon numbers.
        if (rec.Ac is { } ac)
            topB.Add(StatRowEl("AC", ac.ToString(), ItemUpgrade.ScalePrimary(ac, tier).ToString()));
        if (rec.Dmg is { } dmg)
        {
            int sd = ItemUpgrade.ScaleDamage(dmg, tier);
            topB.Add(StatRowEl("DMG", dmg.ToString(), sd.ToString()));
            if (rec.Delay is { } d2)
                topB.Add(StatRowEl("Ratio", ((double)dmg / d2).ToString("0.00", inv),
                    ((double)sd / d2).ToString("0.00", inv)));
            if (rec.DmgBonus is { } bon) topB.Add(StatRowEl("Dmg Bon", bon.ToString(), bon.ToString()));
            if (rec.Backstab is { } bs) topB.Add(StatRowEl("Backstab", bs.ToString(), bs.ToString()));
        }

        string[] attrOrder = { "STR", "STA", "INT", "WIS", "AGI", "DEX", "CHA" };
        foreach (var p in rec.Stats)
        {
            string key = ItemUpgrade.NormalizeKey(p[0]);
            string scaled = ItemUpgrade.ScaleValueText(p[0], p[1], tier);
            int ai = Array.IndexOf(attrOrder, key);
            if (ai >= 0) attrs.Add((ai, StatRowEl(ItemStats.StatLabel(p[0]), p[1], scaled)));
            else if (PoolKeys.Contains(key)) topB.Add(StatRowEl(ItemStats.StatLabel(p[0]), p[1], scaled));
            else if (key is "REC_LEVEL" or "RECOMMENDED_LEVEL") topA.Add(StatRowEl("Rec Level", p[1], p[1]));
            else if (key is "REQ_LEVEL" or "REQUIRED_LEVEL") topA.Add(StatRowEl("Req Level", p[1], p[1]));
            else other.Add(StatRowEl(ItemStats.StatLabel(p[0]), p[1], scaled));
        }

        string[] saveOrder = { "SV_MAGIC", "SV_FIRE", "SV_COLD", "SV_DISEASE", "SV_POISON" };
        foreach (var p in rec.Saves.OrderBy(p =>
        {
            int i = Array.IndexOf(saveOrder, ItemUpgrade.NormalizeKey(p[0]));
            return i < 0 ? 99 : i;
        }))
        {
            string label = ItemStats.StatLabel(p[0]);
            if (label.StartsWith("SV ", StringComparison.Ordinal)) label = label[3..];
            saves.Add(StatRowEl(label, p[1], ItemUpgrade.ScaleValueText(p[0], p[1], tier)));
        }
        if (SynthVoid(rec, tier))
            saves.Add(StatRowEl("Void", "", "+" + tier,
                "granted by the upgrade itself — any upgraded item with two attributes gains SV Void"));

        var root = new StackPanel();
        if (topA.Count > 0 || topB.Count > 0)
            root.Children.Add(Columns(topA, topB));
        var attrCol = attrs.OrderBy(a => a.Order).Select(a => a.El).ToList();
        if (attrCol.Count > 0 || saves.Count > 0 || other.Count > 0)
        {
            var section = Columns(attrCol, saves, other);
            section.Margin = new Thickness(0, 9, 0, 0);
            root.Children.Add(section);
        }
        return root;
    }

    private static bool SynthVoid(ItemStats.Record rec, int tier) =>
        ItemUpgrade.SynthesizesVoid(rec.Stats.Concat(rec.Saves).Select(p => p[0]), tier);

    // ---- the gear sum (Companion's characterSheet.ts semantics) -------------------

    private static readonly string[] AttrKeys = { "STR", "STA", "AGI", "DEX", "WIS", "INT", "CHA" };
    private static readonly string[] PoolKeys = { "HP", "MP", "END" };

    // Requirements and metadata a sum would only lie about — never totalled.
    private static readonly string[] NoSumKeys =
        { "REQ_LEVEL", "REC_LEVEL", "REQUIRED_LEVEL", "RECOMMENDED_LEVEL",
          "CHARGES", "CAST_TIME", "COOLDOWN", "RECAST" };

    private void BuildTotalsLines(List<PaneLineVm> lines)
    {
        var worn = _worn.Values.Where(e => !e.Empty).ToList();
        int counted = 0, unknown = 0, acTier = 0, voidGrant = 0;
        // normalized key -> at-tier sum; percents listed, never added.
        var sums = new Dictionary<string, (string Label, int Tier)>(StringComparer.Ordinal);
        var order = new List<string>();
        var percents = new List<string>();

        foreach (var e in worn)
        {
            var rec = _stats.Lookup(e.Name);
            if (rec is null) { unknown++; continue; }
            counted++;
            int tier = TierOf(e.Name);
            if (rec.Ac is { } ac) acTier += ItemUpgrade.ScalePrimary(ac, tier);
            foreach (var p in rec.Stats.Concat(rec.Saves))
            {
                string key = ItemUpgrade.NormalizeKey(p[0]);
                if (NoSumKeys.Contains(key)) continue;
                if (ItemUpgrade.StatInteger(p[1]) is { } n)
                {
                    int scaled = ItemUpgrade.ClassOf(p[0]) switch
                    {
                        ItemUpgrade.StatClass.Primary => ItemUpgrade.ScalePrimary(n, tier),
                        ItemUpgrade.StatClass.Flat => ItemUpgrade.ScaleFlat(n, tier),
                        _ => n,
                    };
                    if (!sums.TryGetValue(key, out var held))
                    {
                        held = (ItemStats.StatLabel(p[0]), 0);
                        order.Add(key);
                    }
                    sums[key] = (held.Label, held.Tier + scaled);
                }
                else
                {
                    // "36%" — whether worn percents stack is stated nowhere,
                    // so they're listed, never summed (Companion's rule).
                    percents.Add($"{ItemStats.StatLabel(p[0])} {ItemUpgrade.ScaleValueText(p[0], p[1], tier)}");
                }
            }
            if (SynthVoid(rec, tier)) voidGrant += tier;
        }

        PaneSub.Text = $"what the {worn.Count} worn items grant at their current tiers";

        string SumText(Func<string, bool> pick) => string.Join(" · ", order
            .Where(pick)
            .Select(k => sums[k])
            .Select(s => $"{s.Label} {Fmt(s.Tier)}"));
        static string Fmt(int n) => n > 0 ? "+" + n : n.ToString();

        if (acTier > 0)
            lines.Add(new PaneLineVm("AC", acTier.ToString(), TextFg, Visibility.Visible));
        string attrs = SumText(k => AttrKeys.Contains(k));
        if (attrs.Length > 0) lines.Add(new PaneLineVm("ATTRIBUTES", attrs, TextFg, Visibility.Visible));
        string pools = SumText(k => PoolKeys.Contains(k));
        if (pools.Length > 0) lines.Add(new PaneLineVm("POOLS", pools, TextFg, Visibility.Visible));
        var savesParts = order.Where(k => k.StartsWith("SV_", StringComparison.Ordinal))
            .Select(k => sums[k]).Select(s => $"{s.Label} {Fmt(s.Tier)}").ToList();
        if (voidGrant > 0) savesParts.Add($"SV Void +{voidGrant} (upgrade grants)");
        if (savesParts.Count > 0)
            lines.Add(new PaneLineVm("SAVES", string.Join(" · ", savesParts), TextFg, Visibility.Visible));
        string other = SumText(k => !AttrKeys.Contains(k) && !PoolKeys.Contains(k)
            && !k.StartsWith("SV_", StringComparison.Ordinal));
        if (other.Length > 0) lines.Add(new PaneLineVm("OTHER", other, TextFg, Visibility.Visible));
        if (percents.Count > 0)
            lines.Add(new PaneLineVm("NOT SUMMED", string.Join(" · ", percents), AmberFg, Visibility.Visible));

        if (counted == 0)
        {
            lines.Add(new PaneLineVm("", "no worn item is in the wiki table yet", DimFg, Visibility.Collapsed));
            return;
        }
        PaneHint.Text = (unknown > 0 ? $"{unknown} worn item(s) missing from the wiki table count toward nothing. " : "")
            + "Sums of the worn items' wiki blocks, each scaled to its +N tier by the wiki's own item-level rules. Percents are listed, never summed — stacking is stated nowhere. Per-item base numbers live on the Stats tab.";
        PaneHint.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
