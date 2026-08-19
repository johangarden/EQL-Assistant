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
    private string _charTab = "totals";  // totals | focusall | clickies
    private string _itemTab = "sockets"; // sockets | focus | stats
    private List<InventoryStore.CarryRow> _rows = new();

    /// <summary>Wired by MainWindow: the compact focus list links to the
    /// full board (the Inventory window's Focus effects tab).</summary>
    public Action? FocusBoardRequested { get; set; }
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
        _rows = rows;
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

    // Fixed square cells: every slot identical, filled or empty — short rows
    // center for free because nothing stretches.
    private const double CellSize = 98;

    private void BuildDoll()
    {
        DollRows.Children.Clear();
        _cells.Clear();
        foreach (var row in DollLayout)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 9),
            };
            foreach (var slot in row) sp.Children.Add(BuildCellWrap(slot));
            DollRows.Children.Add(sp);
        }
    }

    private UIElement BuildCellWrap((string Token, int Nth) slot)
    {
        var wrap = new StackPanel { Width = CellSize, Margin = new Thickness(3, 0, 4, 0) };
        _worn.TryGetValue(slot, out var entry);

        wrap.Children.Add(new TextBlock
        {
            Text = slot.Token.ToUpperInvariant(),
            Foreground = SlotFg,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(4, 0, 0, 2),
        });
        var inner = new Grid();
        var cell = new Border
        {
            Background = CellBg,
            BorderBrush = CellBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Width = CellSize,
            Height = CellSize, // square, fixed: every cell identical
            Cursor = Cursors.Hand,
            Child = inner,
            Tag = slot,
        };
        // Click a slot → its item tabs; click it again → back to the
        // character-wide tabs. Each mode remembers its own last tab.
        cell.MouseLeftButtonUp += (_, _) =>
        {
            _selected = _selected == slot ? null : slot;
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
            var (baseName, tier) = SplitTier(entry.Name);
            // The cell floor: occupied-socket pills left, the +N tier gold on
            // the right — down here it can never be eaten by a long name.
            // Thin gold outline on the climb (+1..+9); solid gold at the +10 cap.
            var floor = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 3, 0, 0) };
            if (tier.Length > 0)
            {
                bool maxed = TierOf(entry.Name) >= ItemUpgrade.MaxTier;
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    BorderBrush = GoldFg,
                    BorderThickness = new Thickness(1),
                    Background = maxed ? GoldFg : Brushes.Transparent,
                    Padding = new Thickness(4, 0, 4, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = maxed ? "item level 10 — maxed" : $"item level {TierOf(entry.Name)} of 10",
                    Child = new TextBlock
                    {
                        Text = tier,
                        Foreground = maxed ? SocketColors.Ink : GoldFg,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                    },
                };
                DockPanel.SetDock(badge, Dock.Right);
                floor.Children.Add(badge);
            }
            if (BuildPillTrack(entry) is { } track)
            {
                DockPanel.SetDock(track, Dock.Left);
                floor.Children.Add(track);
            }
            if (floor.Children.Count > 0)
            {
                DockPanel.SetDock(floor, Dock.Bottom);
                body.Children.Add(floor);
            }
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
                MaxHeight = 56, // the square gives four narrow lines of room
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = entry.Name,
            });
        }
        inner.Children.Add(body);

        wrap.Children.Add(cell);
        return wrap;
    }

    // Canonical socket order — pills render in this order, but ONLY for
    // occupied sockets: a pill on the doll always means "something is
    // slotted here". Empty and absent sockets live on the Sockets tab.
    private static readonly (string Label, string Name)[] PillTrack =
    {
        ("O", "Ornamentation"),
        ("F", "Focus Exaltation"),
        ("C", "Click Exaltation"),
        ("W", "Worn Exaltation"),
        ("P", "Proc Exaltation"),
    };

    private UIElement? BuildPillTrack(InventoryStore.Entry entry)
    {
        // label -> the item's socket child of that type, if the dump lists one
        var byType = new Dictionary<string, InventoryStore.Entry>(StringComparer.Ordinal);
        foreach (var child in entry.Children)
            byType.TryAdd(SlotTypeOf(child.Location).Label, child);

        var pills = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, name) in PillTrack)
        {
            if (!byType.TryGetValue(label, out var child) || child.Empty) continue;
            // Each socket TYPE owns a color (SocketColors — shared with the
            // Inventory window's pills).
            var fill = SocketColors.Fill(label);
            pills.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(3, 0, 3, 1),
                Margin = new Thickness(0, 0, 2, 0),
                Background = fill,
                BorderBrush = fill,
                BorderThickness = new Thickness(1),
                ToolTip = $"{name} — {child.Name}",
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = SocketColors.Ink,
                },
            });
        }
        return pills.Children.Count > 0 ? pills : null;
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

    // Two tab sets: the pane speaks for the whole CHARACTER until a slot is
    // selected, then for that ITEM. Each mode remembers its own last tab.
    private static readonly (string Id, string Label)[] CharTabDefs =
    {
        ("totals", "Total stats"),
        ("focusall", "Focus effects"),
        ("clickies", "Clickies"),
    };
    private static readonly (string Id, string Label)[] ItemTabDefs =
    {
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

        bool itemMode = _selected is not null;
        string active = itemMode ? _itemTab : _charTab;
        PaneTabs.Children.Clear();
        foreach (var (id, label) in itemMode ? ItemTabDefs : CharTabDefs)
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
            bool on = active == id;
            tab.Background = on ? TabOnBg : Brushes.Transparent;
            text.Foreground = on ? TabOnFg : SlotFg;
            string captured = id;
            bool capturedItemMode = itemMode;
            tab.MouseLeftButtonUp += (_, _) =>
            {
                if (capturedItemMode) _itemTab = captured;
                else _charTab = captured;
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

        if (!itemMode)
        {
            switch (_charTab)
            {
                case "focusall": BuildFocusAllPane(); break;
                case "clickies": BuildClickiesPane(); break;
                default:
                    PaneTitle.Text = "Stats from gear";
                    BuildTotalsPane(lines);
                    break;
            }
            PaneLines.ItemsSource = lines;
            return;
        }

        if (!_worn.TryGetValue(_selected!.Value, out var entry) || entry.Empty)
        {
            PaneTitle.Text = _selected.Value.Token;
            PaneSub.Text = "empty slot";
            PaneLines.ItemsSource = lines;
            return;
        }

        PaneTitle.Text = entry.Name;
        PaneSub.Text = _selected.Value.Token;
        if (ItemIcons.Get(_stats.Lookup(entry.Name)?.Icon) is { } icon)
        {
            PaneIcon.Source = icon;
            PaneIcon.Visibility = Visibility.Visible;
        }

        switch (_itemTab)
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
    private static UIElement StatRowEl(string label, string baseText, string scaledText,
        string? tip = null, Brush? plainFg = null)
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
            val.Inlines.Add(new System.Windows.Documents.Run(baseText) { Foreground = plainFg ?? TextFg });
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

    /// <summary>Total stats — the same item-window grid the per-item Stats
    /// tab wears, over the whole doll.</summary>
    private void BuildTotalsPane(List<PaneLineVm> lines)
    {
        var worn = _worn.Values.Where(e => !e.Empty).ToList();
        int counted = 0, unknown = 0, acTier = 0, voidGrant = 0;
        // normalized key -> at-tier sum; percents listed, never added.
        var sums = new Dictionary<string, (string Label, int Tier)>(StringComparer.Ordinal);
        var order = new List<string>();
        var percents = new List<(string Label, string Value)>();

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
                    percents.Add((ItemStats.StatLabel(p[0]),
                        ItemUpgrade.ScaleValueText(p[0], p[1], tier)));
                }
            }
            if (SynthVoid(rec, tier)) voidGrant += tier;
        }

        PaneSub.Text = $"what the {worn.Count} worn items grant at their current tiers";
        if (counted == 0)
        {
            lines.Add(new PaneLineVm("", "no worn item is in the wiki table yet", DimFg, Visibility.Collapsed));
            return;
        }

        static string Fmt(int n) => n > 0 ? "+" + n : n.ToString();
        List<UIElement> Rows(string[] orderKeys, bool stripSv = false)
        {
            var ordered = order.Where(orderKeys.Contains)
                .OrderBy(k => Array.IndexOf(orderKeys, k)).ToList();
            return ordered.Select(k =>
            {
                string label = sums[k].Label;
                if (stripSv && label.StartsWith("SV ", StringComparison.Ordinal)) label = label[3..];
                return StatRowEl(label, Fmt(sums[k].Tier), Fmt(sums[k].Tier));
            }).ToList();
        }

        // Top: AC + the unsummed percents left, pools right — then the same
        // attribute / save / other columns as an item.
        var topA = new List<UIElement>();
        if (acTier > 0) topA.Add(StatRowEl("AC", acTier.ToString(), acTier.ToString()));
        foreach (var (label, value) in percents)
            topA.Add(StatRowEl(label, value, value,
                "listed, never summed — whether worn percents stack is stated nowhere", AmberFg));
        var topB = Rows(PoolKeys);

        string[] attrOrder = { "STR", "STA", "INT", "WIS", "AGI", "DEX", "CHA" };
        var attrCol = Rows(attrOrder);
        string[] saveOrder = { "SV_MAGIC", "SV_FIRE", "SV_COLD", "SV_DISEASE", "SV_POISON" };
        var saveKeys = saveOrder.Concat(order.Where(k =>
            k.StartsWith("SV_", StringComparison.Ordinal) && !saveOrder.Contains(k))).ToArray();
        var saveCol = Rows(saveKeys, stripSv: true);
        if (voidGrant > 0)
            saveCol.Add(StatRowEl("Void", "", "+" + voidGrant,
                "granted by the upgrades themselves — every upgraded item with two attributes gains SV Void"));
        var otherKeys = order.Where(k => !attrOrder.Contains(k) && !PoolKeys.Contains(k)
            && !k.StartsWith("SV_", StringComparison.Ordinal)).ToArray();
        var otherCol = Rows(otherKeys);

        var root = new StackPanel();
        if (topA.Count > 0 || topB.Count > 0) root.Children.Add(Columns(topA, topB));
        if (attrCol.Count > 0 || saveCol.Count > 0 || otherCol.Count > 0)
        {
            var section = Columns(attrCol, saveCol, otherCol);
            section.Margin = new Thickness(0, 9, 0, 0);
            root.Children.Add(section);
        }
        PaneGrid.Content = root;
        PaneGrid.Visibility = Visibility.Visible;

        PaneHint.Text = (unknown > 0 ? $"{unknown} worn item(s) missing from the wiki table count toward nothing. " : "")
            + "Sums of the worn items' wiki blocks, each scaled to its +N tier by the wiki's own item-level rules. Amber = listed, never summed. Per-item numbers live on each slot's Stats tab.";
        PaneHint.Visibility = Visibility.Visible;
    }

    // ---- Focus effects: the Inventory audit, compacted ---------------------------

    private static readonly Brush[] StatusFgs = { Freeze("#E57373"), Freeze("#FFB74D"), Freeze("#66BB6A") };
    private static readonly Brush[] StatusWash = { Freeze("#10E57373"), Freeze("#10FFB74D"), Freeze("#1066BB6A") };

    private static TextBlock SectHeader(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Foreground = SlotFg,
        FontSize = 9.5,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 9, 0, 4),
    };

    /// <summary>"Improved Damage" at tier 3 → "III" (the ladder's own effect
    /// spelling, family prefix stripped); an unnamed tier reads "T3".</summary>
    private static string RomanOf(FocusEffects.Family fam, int tierNum)
    {
        var tier = fam.Tiers.FirstOrDefault(t => t.TierNum == tierNum);
        if (tier is null) return "T" + tierNum;
        return tier.Effect.StartsWith(fam.Name + " ", StringComparison.Ordinal)
            ? tier.Effect[(fam.Name.Length + 1)..]
            : tier.Effect;
    }

    private void BuildFocusAllPane()
    {
        PaneTitle.Text = "Focus effects";
        var rows = _audit.Where(a => a.Family.Group != "summoned")
            .OrderBy(a => a.Family.Name, StringComparer.Ordinal).ToList();
        int green = rows.Count(a => a.Status == 2);
        int upg = rows.Count(a => a.Status == 1);
        int missing = rows.Count(a => a.Status == 0);
        PaneSub.Text = $"{green} worn best · {upg} upgradable · {missing} missing — the audit's verdicts";

        var root = new StackPanel();
        void Section(string title, int status)
        {
            var inGroup = rows.Where(a => a.Status == status).ToList();
            if (inGroup.Count == 0) return;
            root.Children.Add(SectHeader(title));
            foreach (var a in inGroup)
            {
                string verdict = status switch
                {
                    2 => $"{RomanOf(a.Family, a.WornTier)} — worn best",
                    1 => a.WornTier > 0
                        ? $"{RomanOf(a.Family, a.WornTier)} worn → {RomanOf(a.Family, a.HuntableMax)} huntable"
                        : "stored, not worn",
                    _ => "none owned",
                };
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 1) };
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = StatusFgs[status],
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0),
                };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                var st = new TextBlock
                {
                    Text = verdict,
                    Foreground = StatusFgs[status],
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                DockPanel.SetDock(st, Dock.Right);
                row.Children.Add(st);
                var name = new TextBlock
                {
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                name.Inlines.Add(new System.Windows.Documents.Run(a.Family.Name)
                    { Foreground = TextFg, FontWeight = FontWeights.SemiBold });
                name.Inlines.Add(new System.Windows.Documents.Run("  " + a.Family.Kind)
                    { Foreground = DimFg, FontSize = 10 });
                row.Children.Add(name);
                root.Children.Add(new Border
                {
                    Background = StatusWash[status],
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(6, 2, 6, 3),
                    Margin = new Thickness(-6, 0, -6, 1),
                    Child = row,
                    ToolTip = status == 1 && a.BestItem.Length > 0
                        ? $"best owned: {a.BestEffect} ({a.BestItem}, {a.BestPlace})"
                        : null,
                });
            }
        }
        Section("Wearing the best", 2);
        Section("Upgrade available", 1);
        Section("Missing", 0);

        // The shortcut to the full board.
        var link = new TextBlock
        {
            Text = "Open the full board — Inventory → Focus effects ↗",
            Foreground = TabOnFg,
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 10, 0, 0),
        };
        link.MouseLeftButtonUp += (_, _) => FocusBoardRequested?.Invoke();
        root.Children.Add(link);

        PaneGrid.Content = root;
        PaneGrid.Visibility = Visibility.Visible;
    }

    // ---- Clickies: every click effect you're carrying ----------------------------

    /// <summary>The "click:" effect names a wiki record states (detail
    /// parentheses dropped for the compact list).</summary>
    private static List<string> ClickLines(ItemStats.Record? rec)
    {
        var found = new List<string>();
        if (rec is null || rec.Effects.Length == 0) return found;
        foreach (var line in rec.Effects.Split('\n'))
        {
            if (!line.StartsWith("click:", StringComparison.OrdinalIgnoreCase)) continue;
            string name = line["click:".Length..].Trim();
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) name = name[..paren];
            if (name.Length > 0) found.Add(name);
        }
        return found;
    }

    private UIElement ClickyRow(int? iconId, string effect, string via, bool known = true)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        if (ItemIcons.Get(iconId) is { } icon)
        {
            var img = new System.Windows.Controls.Image
            {
                Source = icon,
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 7, 0),
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
            DockPanel.SetDock(img, Dock.Left);
            row.Children.Add(img);
        }
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = effect,
            Foreground = known ? GreenFg : TextFg,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = via,
            Foreground = DimFg,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
        });
        row.Children.Add(body);
        return row;
    }

    private void BuildClickiesPane()
    {
        PaneTitle.Text = "Clickies";
        PaneSub.Text = "every click effect you're carrying";
        var root = new StackPanel();

        // Worn: the item's own click lines, then any Click-Exaltation socket.
        var wornRows = new List<UIElement>();
        foreach (var ((token, _), e) in _worn.OrderBy(kv => kv.Key.Token, StringComparer.Ordinal))
        {
            if (e.Empty) continue;
            var rec = _stats.Lookup(e.Name);
            foreach (var fx in ClickLines(rec))
                wornRows.Add(ClickyRow(rec?.Icon, fx, $"{e.Name} · {token}"));
            foreach (var child in e.Children.Where(c => !c.Empty && SlotTypeOf(c.Location).Label == "C"))
            {
                var crec = _stats.Lookup(child.Name);
                var fxs = ClickLines(crec);
                var (childBase, _) = SplitTier(child.Name.Replace(" (Exaltation)", ""));
                if (fxs.Count == 0)
                    wornRows.Add(ClickyRow(crec?.Icon, childBase,
                        $"socketed in {e.Name} · {token} — click effect not in the wiki table", known: false));
                foreach (var fx in fxs)
                    wornRows.Add(ClickyRow(crec?.Icon, fx, $"{childBase}, socketed in {e.Name} · {token}"));
            }
        }
        if (wornRows.Count > 0)
        {
            root.Children.Add(SectHeader("Worn"));
            foreach (var r in wornRows) root.Children.Add(r);
        }

        // The game's own clicky collection: the Activated-Items keyring.
        var keyringRows = new List<UIElement>();
        foreach (var r in _rows.Where(r => r.Lane == "activated"))
        {
            var rec = _stats.Lookup(r.Name);
            var fxs = ClickLines(rec);
            var (baseName, _) = SplitTier(r.Name);
            if (fxs.Count == 0)
                keyringRows.Add(ClickyRow(rec?.Icon, baseName,
                    "Activated keyring — click effect not in the wiki table", known: false));
            foreach (var fx in fxs)
                keyringRows.Add(ClickyRow(rec?.Icon, fx, $"{r.Name} · Activated keyring"));
        }
        if (keyringRows.Count > 0)
        {
            root.Children.Add(SectHeader("Activated keyring"));
            foreach (var r in keyringRows) root.Children.Add(r);
        }

        if (wornRows.Count == 0 && keyringRows.Count == 0)
            root.Children.Add(new TextBlock
            {
                Text = "no clickies found in the dump",
                Foreground = DimFg,
                FontSize = 11.5,
                FontStyle = FontStyles.Italic,
            });

        PaneGrid.Content = root;
        PaneGrid.Visibility = Visibility.Visible;
        PaneHint.Text = "Effect names come from the wiki's click lines — an item whose click the wiki doesn't state shows the item alone. Worn gear first, then the game's Activated-Items keyring.";
        PaneHint.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
