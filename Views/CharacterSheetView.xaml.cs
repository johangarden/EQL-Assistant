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
/// title above, its +N tier as a gold badge (solid at the +10 cap) and a
/// colored pill per OCCUPIED socket (SocketColors). The detail pane holds
/// three character-wide tabs — Total stats, Focus effects, Clickies — and a
/// selected slot swaps in ONE combined item view: wiki stats scaled to the
/// worn tier (eqlwiki's own slider rules, see Services/ItemUpgrade), the
/// sockets as the dump lists them, and the focus audit's verdicts.
///
/// A VIEW, not a window: the Character window (InventoryWindow) parses the
/// dump once and feeds it in through <see cref="Update"/> — header, dump
/// watcher and freshness live with the host.
/// </summary>
public partial class CharacterSheetView : UserControl
{
    private sealed record PaneLineVm(string Key, string Value, Brush Fg, Visibility KeyVis,
        Visibility RuleVis = Visibility.Collapsed, bool BoardLink = false,
        string LinkText = "")
    {
        public System.Windows.Input.Cursor RowCursor =>
            BoardLink ? Cursors.Hand : Cursors.Arrow;
    }

    /// <summary>Where an available upgrade lives, from the audit row: a
    /// better tier OWNED but stored, a better tier still out there to hunt,
    /// or both.</summary>
    private static string UpgradeWhere(FocusEffects.AuditRow a)
    {
        bool stored = a.BestTier > a.WornTier;
        bool huntable = a.HuntableMax > a.BestTier;
        return stored && huntable ? "stored & huntable" : stored ? "stored" : "huntable";
    }

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

    private FocusEffects _focus = null!;
    private ItemStats _stats = null!;

    private List<FocusEffects.AuditRow> _audit = new();
    private readonly Dictionary<(string Token, int Nth), InventoryStore.Entry> _worn = new();
    private (string Token, int Nth)? _selected;
    private List<InventoryStore.CarryRow> _rows = new();
    // A clicked totals stat drills into "which items grant it" (normalized
    // key + display label); null = the ordinary landing pane.
    private string? _drillKey;
    private string _drillLabel = "";

    /// <summary>Wired by the host: the compact focus list links to the full
    /// board (the Character window's Focus board tab).</summary>
    public Action? FocusBoardRequested { get; set; }
    private readonly List<Border> _cells = new();

    public CharacterSheetView()
    {
        InitializeComponent();
    }

    /// <summary>An amber "upgrade … → focus board" line is a link — clicking
    /// anywhere on it jumps to the board.</summary>
    private void PaneLinesBelow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaneLineVm { BoardLink: true })
            FocusBoardRequested?.Invoke();
    }

    /// <summary>Give the view its lookup tables — once, before the first
    /// <see cref="Update"/>.</summary>
    public void Init(FocusEffects focus, ItemStats stats)
    {
        _focus = focus;
        _stats = stats;
    }

    /// <summary>The host parsed the dump — render it. Called on every reload
    /// (the host owns the file watcher), any number of times.</summary>
    public void Update(InventoryStore.Dump dump,
        List<InventoryStore.CarryRow> rows, List<FocusEffects.AuditRow> audit)
    {
        _rows = rows;
        _audit = audit;

        // Worn entries by (token, occurrence) in file order.
        _worn.Clear();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in dump.Items)
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
        // Click a slot → its item view; click it again → back to the landing
        // pane (any open stat drill closes with it).
        cell.MouseLeftButtonUp += (_, _) =>
        {
            _selected = _selected == slot ? null : slot;
            _drillKey = null;
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
    // No tabs: the landing view stacks totals, focus verdicts and clickies in
    // one scroll; a selected slot swaps in the combined item view, and
    // clicking the slot again comes back.

    private void RefreshPane()
    {
        foreach (var c in _cells)
        {
            bool sel = _selected is { } s && c.Tag is ValueTuple<string, int> t && (t.Item1, t.Item2) == s;
            c.BorderBrush = sel ? CellSelBorder : CellBorder;
            c.Background = sel ? CellSelBg : CellBg;
        }

        bool itemMode = _selected is not null;

        var lines = new List<PaneLineVm>();
        PaneIcon.Visibility = Visibility.Collapsed;
        PaneGrid.Visibility = Visibility.Collapsed;
        PaneGrid.Content = null;
        PaneLinesBelow.Visibility = Visibility.Collapsed;
        PaneLinesBelow.ItemsSource = null;
        PaneTitle.ToolTip = null;

        if (!itemMode)
        {
            if (_drillKey is { } drill)
            {
                // A clicked stat: which worn items grant it, best first.
                BuildStatDrill(drill);
                PaneLines.ItemsSource = lines;
                return;
            }
            // ONE combined landing view: totals grid, focus verdicts,
            // clickies — no tabs, click a slot for the item view.
            PaneTitle.Text = "Stats from gear";
            BuildLandingPane(lines);
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

        // Title with the +N in gold, same voice as the doll's badges.
        var (titleBase, titleTier) = SplitTier(entry.Name);
        PaneTitle.Text = "";
        PaneTitle.Inlines.Clear();
        PaneTitle.Inlines.Add(new System.Windows.Documents.Run(titleBase));
        if (titleTier.Length > 0)
            PaneTitle.Inlines.Add(new System.Windows.Documents.Run(" " + titleTier) { Foreground = GoldFg });
        PaneTitle.ToolTip = entry.Name;
        PaneSub.Text = _selected.Value.Token;
        if (ItemIcons.Get(_stats.Lookup(entry.Name)?.Icon) is { } icon)
        {
            PaneIcon.Source = icon;
            PaneIcon.Visibility = Visibility.Visible;
        }

        // Everything about the item in one scroll — STABLE zones first: the
        // fixed stats grid, the always-five-row socket ladder (each socketed
        // focus carrying the audit's verdict on its own line), and only then
        // the variable tail (effects, wiki leftovers) where movement can't
        // shove anything around.
        var below = new List<PaneLineVm>();
        var tail = new List<PaneLineVm>();
        BuildStatLines(entry, lines, tail);
        BuildSocketLines(entry, below);
        // A rule between the socket ladder and whatever the item adds after.
        if (tail.Count > 0)
            tail[0] = tail[0] with { RuleVis = Visibility.Visible };
        below.AddRange(tail);
        if (below.Count > 0)
        {
            PaneLinesBelow.ItemsSource = below;
            PaneLinesBelow.Visibility = Visibility.Visible;
        }
        PaneLines.ItemsSource = lines;
    }

    private void BuildSocketLines(InventoryStore.Entry entry, List<PaneLineVm> lines)
    {
        // label -> the item's socket child of that type, if the dump lists one
        var byType = new Dictionary<string, InventoryStore.Entry>(StringComparer.Ordinal);
        foreach (var child in entry.Children)
            byType.TryAdd(SlotTypeOf(child.Location).Label, child);

        // Every item shows the full five-socket ladder, three honest states:
        // occupant / empty / why-there-is-no-socket (locked by tier, or the
        // type doesn't exist on this kind of item).
        int tier = TierOf(entry.Name);
        for (int i = 0; i < PillTrack.Length; i++)
        {
            var (label, name) = PillTrack[i];
            string key = name.ToUpperInvariant();
            if (byType.TryGetValue(label, out var child) && !child.Empty)
            {
                // Effect first, carrier second, the audit's verdict riding
                // the same line — this IS the focus section for sockets.
                var fx = _focus.EffectsOf(child.Name);
                string val;
                Brush fg = GreenFg;
                bool link = false;
                if (fx.Count > 0)
                {
                    string effects = string.Join(", ", fx.Select(e => e.Tier.Effect));
                    var arow = _audit.FirstOrDefault(a => fx.Any(e => e.Fam == a.Family));
                    string verdict = arow switch
                    {
                        { Status: 2 } => " · wearing the best",
                        { Status: 1 } => $" · upgrade {UpgradeWhere(arow)}",
                        _ => "",
                    };
                    if (arow is { Status: 1 }) { fg = AmberFg; link = true; }
                    val = $"{effects} — {child.Name}{verdict}";
                }
                else
                {
                    val = child.Name;
                }
                lines.Add(new PaneLineVm(key, val, fg, Visibility.Visible, BoardLink: link,
                    LinkText: link ? " → focus board" : ""));
            }
            else if (child is not null)
            {
                lines.Add(new PaneLineVm(key, "empty", DimFg, Visibility.Visible));
            }
            else
            {
                // Socket types unlock by item level (wiki "Exaltations"):
                // O +0, F +1, C +2, W +3, P +4 — the ladder's own order.
                lines.Add(new PaneLineVm(key, tier < i
                    ? $"locked — unlocks at +{i}"
                    : "not available on this item", DimFg, Visibility.Visible));
            }
        }
    }

    private void BuildStatLines(InventoryStore.Entry entry, List<PaneLineVm> lines, List<PaneLineVm> below)
    {
        var rec = _stats.Lookup(entry.Name);
        if (rec is null)
        {
            lines.Add(new PaneLineVm("", "the wiki has no page for this item", DimFg, Visibility.Collapsed));
            return;
        }
        int tier = TierOf(entry.Name);

        // The game's own window order: flags line, class/race, then the grid.
        // Both lines ALWAYS render (blank when the item states nothing) so
        // the grid below starts at the same height for every item.
        lines.Add(new PaneLineVm("", rec.Flags.Length > 0 ? rec.Flags : " ", TextFg, Visibility.Collapsed));
        string classRace = ((rec.Classes.Length > 0 ? "Class: " + rec.Classes : "")
            + (rec.Races.Length > 0 ? "   Race: " + rec.Races : "")).Trim();
        lines.Add(new PaneLineVm("", classRace.Length > 0 ? classRace : " ", SlotFg, Visibility.Collapsed));

        PaneGrid.Content = BuildStatGrid(rec, tier);
        PaneGrid.Visibility = Visibility.Visible;

        if (rec.Effects.Length > 0)
        {
            // The item's OWN focus lines carry the audit's verdict inline —
            // and a focus a SOCKET line already states (usually the item's
            // own exaltation copy socketed into itself) is not said twice.
            var ownFx = _focus.EffectsOf(entry.Name);
            bool anyUpgrade = false;
            var socketed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in entry.Children.Where(c => !c.Empty))
                foreach (var (_, fxTier) in _focus.EffectsOf(child.Name))
                    socketed.Add(fxTier.Effect);

            var fxLines = new List<string>();
            foreach (var line in rec.Effects.Split('\n'))
            {
                if (!line.StartsWith("focus:", StringComparison.OrdinalIgnoreCase))
                {
                    fxLines.Add(line);
                    continue;
                }
                string name = line["focus:".Length..].Trim();
                int paren = name.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 0) name = name[..paren];
                if (socketed.Contains(name)) continue; // the socket line owns it
                var match = ownFx.FirstOrDefault(e =>
                    string.Equals(e.Tier.Effect, name, StringComparison.OrdinalIgnoreCase));
                if (match.Fam is null)
                {
                    fxLines.Add(line);
                    continue;
                }
                var arow = _audit.FirstOrDefault(a => a.Family == match.Fam);
                fxLines.Add(line + (arow switch
                {
                    { Status: 2 } => " · wearing the best",
                    { Status: 1 } => $" · upgrade {UpgradeWhere(arow)}",
                    _ => "",
                }));
                if (arow is { Status: 1 }) anyUpgrade = true;
            }
            if (fxLines.Count > 0)
                below.Add(new PaneLineVm("EFFECTS", string.Join("\n", fxLines),
                    anyUpgrade ? AmberFg : GreenFg, Visibility.Visible, BoardLink: anyUpgrade,
                    LinkText: anyUpgrade ? " → focus board" : ""));
        }
        if (rec.Extras.Length > 0) below.Add(new PaneLineVm("MORE", rec.Extras, DimFg, Visibility.Visible));
        // No footer lecture — gold-vs-brackets reads on its own; the rules
        // live in the Totals hint for whoever wants them.
    }

    /// <summary>One "Label: value" cell — the scaled value in gold with the
    /// wiki base in dim brackets when the tier changes it.</summary>
    private sealed record StatCell(TextBlock Label, TextBlock Value);

    private static StatCell StatCellOf(string label, string baseText, string scaledText,
        string? tip = null, Brush? plainFg = null)
    {
        var lab = new TextBlock
        {
            Text = label + ":",
            Foreground = SlotFg,
            FontSize = 11.5,
            ToolTip = tip,
            Margin = new Thickness(0, 0, 0, 3),
        };
        var val = new TextBlock
        {
            FontSize = 11.5,
            TextAlignment = TextAlignment.Right,
            ToolTip = tip,
            Margin = new Thickness(8, 0, 0, 3),
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
        return new StatCell(lab, val);
    }

    /// <summary>True tabular columns: every label column and every value
    /// column auto-sizes to its widest member, so nothing ever wraps or
    /// staggers — the item window's own alignment. Empty columns drop.</summary>
    private static FrameworkElement Columns(params List<StatCell>[] cols)
    {
        var filled = cols.Where(c => c.Count > 0).ToList();
        var grid = new Grid();
        int rows = filled.Max(c => c.Count);
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int ci = 0; ci < filled.Count; ci++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // gutter between column pairs (none after the last)
            grid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(ci < filled.Count - 1 ? 18 : 0) });
            for (int ri = 0; ri < filled[ci].Count; ri++)
            {
                var cell = filled[ci][ri];
                Grid.SetRow(cell.Label, ri);
                Grid.SetColumn(cell.Label, ci * 3);
                Grid.SetRow(cell.Value, ri);
                Grid.SetColumn(cell.Value, ci * 3 + 1);
                grid.Children.Add(cell.Label);
                grid.Children.Add(cell.Value);
            }
        }
        grid.HorizontalAlignment = HorizontalAlignment.Left;
        return grid;
    }

    /// <summary>A stat this item doesn't state: the label stays (the grid
    /// keeps the SAME rows for every item, so nothing dances on click), the
    /// value cell simply stays blank.</summary>
    private static StatCell DashCell(string label) => StatCellOf(label, " ", " ", null, DimFg);

    private static UIElement BuildStatGrid(ItemStats.Record rec, int tier)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // normalized key -> raw value, for the fixed rows below.
        var vals = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in rec.Stats.Concat(rec.Saves))
        {
            string key = ItemUpgrade.NormalizeKey(p[0]);
            vals.TryAdd(key, p[1]);
            rawLabel.TryAdd(key, p[0]);
        }
        var used = new HashSet<string>(StringComparer.Ordinal);

        StatCell Cell(string label, params string[] keys)
        {
            foreach (var key in keys)
            {
                used.Add(key);
                if (vals.TryGetValue(key, out var v))
                    return StatCellOf(label, v, ItemUpgrade.ScaleValueText(key, v, tier));
            }
            return DashCell(label);
        }
        StatCell Fixed(string label, string? text, string? tip = null) =>
            string.IsNullOrEmpty(text) ? DashCell(label) : StatCellOf(label, text, text, tip);

        // ---- the FIXED template: identical rows for every item ----
        string scaledW = rec.Weight;
        if (double.TryParse(rec.Weight, inv, out double w))
        {
            double sw = ItemUpgrade.ScaleWeight(w, tier);
            if (sw != w) scaledW = sw.ToString("0.0", inv);
        }
        var topA = new List<StatCell>
        {
            Fixed("Size", rec.Size),
            rec.Weight.Length > 0 ? StatCellOf("Weight", rec.Weight, scaledW) : DashCell("Weight"),
            Cell("Rec Level", "REC_LEVEL", "RECOMMENDED_LEVEL"),
            Cell("Req Level", "REQ_LEVEL", "REQUIRED_LEVEL"),
            Fixed("Skill", rec.Skill),
            Fixed("Atk Delay", rec.Delay?.ToString(),
                "Delay never scales with the tier — that's why the ratio improves"),
        };

        var topB = new List<StatCell>
        {
            rec.Ac is { } ac
                ? StatCellOf("AC", ac.ToString(), ItemUpgrade.ScalePrimary(ac, tier).ToString())
                : DashCell("AC"),
            Cell("HP", "HP"),
            Cell("Mana", "MP"),
            Cell("Endurance", "END"),
            rec.Dmg is { } dmg
                ? StatCellOf("DMG", dmg.ToString(), ItemUpgrade.ScaleDamage(dmg, tier).ToString())
                : DashCell("DMG"),
            rec is { Dmg: { } d1, Delay: { } d2 }
                ? StatCellOf("Ratio", ((double)d1 / d2).ToString("0.00", inv),
                    ((double)ItemUpgrade.ScaleDamage(d1, tier) / d2).ToString("0.00", inv))
                : DashCell("Ratio"),
        };
        if (rec.DmgBonus is { } bon) topB.Add(StatCellOf("Dmg Bon", bon.ToString(), bon.ToString()));
        if (rec.Backstab is { } bs) topB.Add(StatCellOf("Backstab", bs.ToString(), bs.ToString()));

        var attrCol = new List<StatCell>
        {
            Cell("Strength", "STR"),
            Cell("Stamina", "STA"),
            Cell("Intelligence", "INT"),
            Cell("Wisdom", "WIS"),
            Cell("Agility", "AGI"),
            Cell("Dexterity", "DEX"),
            Cell("Charisma", "CHA"),
        };

        var saveCol = new List<StatCell>
        {
            Cell("Magic", "SV_MAGIC"),
            Cell("Fire", "SV_FIRE"),
            Cell("Cold", "SV_COLD"),
            Cell("Disease", "SV_DISEASE"),
            Cell("Poison", "SV_POISON"),
        };
        used.Add("SV_VOID");
        saveCol.Add(vals.TryGetValue("SV_VOID", out var voidVal)
            ? StatCellOf("Void", voidVal, ItemUpgrade.ScaleValueText("SV_VOID", voidVal, tier))
            : SynthVoid(rec, tier)
                ? StatCellOf("Void", "", "+" + tier,
                    "granted by the upgrade itself — any upgraded item with two attributes gains SV Void")
                : DashCell("Void"));

        var otherCol = new List<StatCell>
        {
            Cell("Haste", "HASTE"),
            Cell("Attack", "ATTACK"),
            Cell("Regen", "HP_REGEN"),
            Cell("Mana Regen", "MANA_REGEN"),
            Cell("End Regen", "END_REGEN"),
        };
        // Anything the template doesn't name still shows — appended, verbatim.
        foreach (var key in vals.Keys.Where(k => !used.Contains(k)))
            otherCol.Add(StatCellOf(ItemStats.StatLabel(rawLabel[key]), vals[key],
                ItemUpgrade.ScaleValueText(key, vals[key], tier)));

        var root = new StackPanel();
        root.Children.Add(Columns(topA, topB));
        var section = Columns(attrCol, saveCol, otherCol);
        section.Margin = new Thickness(0, 9, 0, 0);
        root.Children.Add(section);
        // divider before the socket ladder
        root.Children.Add(new Border
        {
            Height = 1,
            Background = Freeze("#232C3E"),
            Margin = new Thickness(0, 10, 0, 3),
        });
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
    /// <summary>The stats-from-gear grid, returned for the combined landing
    /// pane (null when no worn item is in the wiki table).</summary>
    private UIElement? BuildTotalsRoot(List<PaneLineVm> lines)
    {
        var worn = _worn.Values.Where(e => !e.Empty).ToList();
        int counted = 0, unknown = 0, acTier = 0, voidGrant = 0;
        // normalized key -> at-tier sum; percents listed, never added.
        var sums = new Dictionary<string, (string Label, int Tier)>(StringComparer.Ordinal);
        var order = new List<string>();
        var percents = new List<(string Key, string Label, string Value)>();

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
                    percents.Add((key, ItemStats.StatLabel(p[0]),
                        ItemUpgrade.ScaleValueText(p[0], p[1], tier)));
                }
            }
            if (SynthVoid(rec, tier)) voidGrant += tier;
        }

        PaneSub.Text = $"what the {worn.Count} worn items grant at their current tiers";
        if (counted == 0)
        {
            lines.Add(new PaneLineVm("", "no worn item is in the wiki table yet", DimFg, Visibility.Collapsed));
            return null;
        }

        static string Fmt(int n) => n > 0 ? "+" + n : n.ToString();
        List<StatCell> Rows(string[] orderKeys, bool stripSv = false)
        {
            var ordered = order.Where(orderKeys.Contains)
                .OrderBy(k => Array.IndexOf(orderKeys, k)).ToList();
            return ordered.Select(k =>
            {
                string label = sums[k].Label;
                if (stripSv && label.StartsWith("SV ", StringComparison.Ordinal)) label = label[3..];
                var cell = StatCellOf(label, Fmt(sums[k].Tier), Fmt(sums[k].Tier));
                MakeDrillable(cell, k, label);
                return cell;
            }).ToList();
        }

        // Top: AC + the unsummed percents left, pools right — then the same
        // attribute / save / other columns as an item.
        var topA = new List<StatCell>();
        if (acTier > 0)
        {
            var acCell = StatCellOf("AC", acTier.ToString(), acTier.ToString());
            MakeDrillable(acCell, "AC", "AC");
            topA.Add(acCell);
        }
        foreach (var (pkey, label, value) in percents)
        {
            var pCell = StatCellOf(label, value, value,
                "listed, never summed — whether worn percents stack is stated nowhere", AmberFg);
            MakeDrillable(pCell, pkey, label);
            topA.Add(pCell);
        }
        var topB = Rows(PoolKeys);

        string[] attrOrder = { "STR", "STA", "INT", "WIS", "AGI", "DEX", "CHA" };
        var attrCol = Rows(attrOrder);
        string[] saveOrder = { "SV_MAGIC", "SV_FIRE", "SV_COLD", "SV_DISEASE", "SV_POISON" };
        var saveKeys = saveOrder.Concat(order.Where(k =>
            k.StartsWith("SV_", StringComparison.Ordinal) && !saveOrder.Contains(k))).ToArray();
        var saveCol = Rows(saveKeys, stripSv: true);
        if (voidGrant > 0)
        {
            var voidCell = StatCellOf(order.Contains("SV_VOID") ? "Void grants" : "Void",
                "", "+" + voidGrant,
                "granted by the upgrades themselves — every upgraded item with two attributes gains SV Void");
            MakeDrillable(voidCell, "SV_VOID", "SV Void");
            saveCol.Add(voidCell);
        }
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
        // The explanation retires to a tooltip — the combined pane has no
        // room for lectures. (Unknown items count toward nothing; amber =
        // listed, never summed; the math is eqlwiki's own slider rules.)
        root.ToolTip = (unknown > 0 ? $"{unknown} worn item(s) missing from the wiki table count toward nothing. " : "")
            + "Sums of the worn items' wiki blocks, each scaled to its +N tier by the wiki's own item-level rules. Amber percents are listed, never summed.";
        return root;
    }

    /// <summary>Every totals row is a question — clicking it answers "which
    /// items grant this?" with the ranked drill view.</summary>
    private void MakeDrillable(StatCell cell, string key, string label)
    {
        foreach (var tb in new[] { cell.Label, cell.Value })
        {
            tb.Cursor = Cursors.Hand;
            tb.ToolTip = $"click — which items grant {label}";
            tb.MouseLeftButtonUp += (_, _) =>
            {
                _drillKey = key;
                _drillLabel = label;
                _selected = null;
                RefreshPane();
            };
        }
    }

    /// <summary>The drill: every worn item granting the clicked stat, ranked
    /// by its at-tier contribution, each row clicking through to the item.</summary>
    private void BuildStatDrill(string key)
    {
        PaneTitle.Text = _drillLabel;
        PaneSub.Text = "worn items granting it — at worn tier, best first";

        var root = new StackPanel();
        var back = new TextBlock
        {
            Text = "← all stats",
            Foreground = SlotFg,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 8),
        };
        back.MouseLeftButtonUp += (_, _) => { _drillKey = null; RefreshPane(); };
        root.Children.Add(back);

        var found = new List<(double Rank, UIElement Row)>();
        var without = new List<(string Sort, UIElement Row)>();
        foreach (var (slotKey, e) in _worn)
        {
            if (e.Empty) continue;
            var rec = _stats.Lookup(e.Name);
            int tier = TierOf(e.Name);

            string? baseText = null, atText = null, note = null;
            if (rec is not null)
            {
                if (key == "AC" && rec.Ac is { } ac)
                {
                    baseText = ac.ToString();
                    atText = ItemUpgrade.ScalePrimary(ac, tier).ToString();
                }
                else if (key != "AC")
                {
                    var pair = rec.Stats.Concat(rec.Saves)
                        .FirstOrDefault(p => ItemUpgrade.NormalizeKey(p[0]) == key);
                    if (pair is not null)
                    {
                        baseText = pair[1];
                        atText = ItemUpgrade.ScaleValueText(pair[0], pair[1], tier);
                    }
                    else if (key == "SV_VOID" && SynthVoid(rec, tier))
                    {
                        baseText = atText = "+" + tier;
                        note = "upgrade grant";
                    }
                }
            }
            if (atText is not null)
            {
                double rank = ItemUpgrade.StatInteger(atText)
                    ?? ItemUpgrade.PercentInteger(atText) ?? 0;
                found.Add((rank, DrillRow(slotKey, e, rec!, baseText!, atText, note)));
            }
            else
            {
                // The deficit list: worn gear granting NOTHING here — the
                // "ok, I need to upgrade x, y, z" view.
                without.Add((slotKey.Token, DimDrillRow(slotKey, e, rec)));
            }
        }

        if (found.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "no worn item grants this",
                Foreground = DimFg,
                FontSize = 11.5,
                FontStyle = FontStyles.Italic,
            });
        }
        foreach (var (_, row) in found.OrderByDescending(f => f.Rank))
            root.Children.Add(row);

        if (without.Count > 0)
        {
            root.Children.Add(SectionRule());
            root.Children.Add(SectHeader($"granting no {_drillLabel}"));
            foreach (var (_, row) in without.OrderBy(w => w.Sort, StringComparer.Ordinal))
                root.Children.Add(row);
        }

        PaneGrid.Content = root;
        PaneGrid.Visibility = Visibility.Visible;
    }

    /// <summary>A deficit row: dim everything — this item gives none of the
    /// drilled stat (a "?" when the wiki has no record to judge by).</summary>
    private UIElement DimDrillRow((string Token, int Nth) slotKey, InventoryStore.Entry e,
        ItemStats.Record? rec)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand };
        if (ItemIcons.Get(rec?.Icon) is { } icon)
        {
            var img = new System.Windows.Controls.Image
            {
                Source = icon,
                Width = 18,
                Height = 18,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
            DockPanel.SetDock(img, Dock.Left);
            row.Children.Add(img);
        }
        var val = new TextBlock
        {
            Text = rec is null ? "?" : "—",
            Foreground = DimFg,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = rec is null ? "not in the wiki table — no stats to judge by" : null,
        };
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);

        var (baseName, tierText) = SplitTier(e.Name);
        var name = new TextBlock
        {
            FontSize = 11.5,
            Foreground = DimFg,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{e.Name} · {slotKey.Token} — click for the item",
        };
        name.Inlines.Add(new System.Windows.Documents.Run(baseName));
        if (tierText.Length > 0)
            name.Inlines.Add(new System.Windows.Documents.Run(" " + tierText));
        name.Inlines.Add(new System.Windows.Documents.Run("  " + slotKey.Token) { FontSize = 10 });
        row.Children.Add(name);

        row.MouseLeftButtonUp += (_, _) =>
        {
            _selected = slotKey;
            _drillKey = null;
            RefreshPane();
        };
        return row;
    }

    private UIElement DrillRow((string Token, int Nth) slotKey, InventoryStore.Entry e,
        ItemStats.Record rec, string baseText, string atText, string? note)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand };
        if (ItemIcons.Get(rec.Icon) is { } icon)
        {
            var img = new System.Windows.Controls.Image
            {
                Source = icon,
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
            DockPanel.SetDock(img, Dock.Left);
            row.Children.Add(img);
        }
        var val = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        val.Inlines.Add(new System.Windows.Documents.Run(atText)
            { Foreground = atText != baseText ? GoldFg : TextFg, FontWeight = FontWeights.SemiBold });
        if (atText != baseText)
            val.Inlines.Add(new System.Windows.Documents.Run($" ({baseText})") { Foreground = DimFg });
        if (note is not null)
            val.Inlines.Add(new System.Windows.Documents.Run(" · " + note)
                { Foreground = DimFg, FontSize = 10.5 });
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);

        var (baseName, tierText) = SplitTier(e.Name);
        var name = new TextBlock
        {
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{e.Name} · {slotKey.Token} — click for the item",
        };
        name.Inlines.Add(new System.Windows.Documents.Run(baseName) { Foreground = TextFg });
        if (tierText.Length > 0)
            name.Inlines.Add(new System.Windows.Documents.Run(" " + tierText) { Foreground = GoldFg });
        name.Inlines.Add(new System.Windows.Documents.Run("  " + slotKey.Token)
            { Foreground = DimFg, FontSize = 10 });
        row.Children.Add(name);

        row.MouseLeftButtonUp += (_, _) =>
        {
            _selected = slotKey;
            _drillKey = null;
            RefreshPane();
        };
        return row;
    }

    /// <summary>A rule between the landing pane's sections.</summary>
    private static Border SectionRule() => new()
    {
        Height = 1,
        Background = Freeze("#232C3E"),
        Margin = new Thickness(0, 12, 0, 2),
    };

    /// <summary>The combined landing pane: totals grid, focus verdicts,
    /// clickies — one scroll, no tabs, no lectures.</summary>
    private void BuildLandingPane(List<PaneLineVm> lines)
    {
        var root = new StackPanel();
        if (BuildTotalsRoot(lines) is { } totals) root.Children.Add(totals);
        root.Children.Add(SectionRule());
        AddFocusSection(root);
        root.Children.Add(SectionRule());
        AddClickiesSection(root);
        PaneGrid.Content = root;
        PaneGrid.Visibility = Visibility.Visible;
    }

    // ---- Focus effects: the Inventory audit, compacted ---------------------------

    private static readonly Brush[] StatusFgs = { Freeze("#E57373"), Freeze("#FFB74D"), Freeze("#66BB6A") };
    private static readonly Brush[] StatusWash = { Freeze("#10E57373"), Freeze("#10FFB74D"), Freeze("#1066BB6A") };

    /// <summary>Small-caps header; `strong` marks a top-level section of the
    /// combined landing pane (brighter than its group headers).</summary>
    private static TextBlock SectHeader(string text, bool strong = false) => new()
    {
        Text = text.ToUpperInvariant(),
        Foreground = strong ? TextFg : SlotFg,
        FontSize = strong ? 10.5 : 9.5,
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

    /// <summary>The compact focus audit, appended to the landing pane.</summary>
    private void AddFocusSection(StackPanel root)
    {
        var rows = _audit.Where(a => a.Family.Group != "summoned")
            .OrderBy(a => a.Family.Name, StringComparer.Ordinal).ToList();
        int green = rows.Count(a => a.Status == 2);
        int upg = rows.Count(a => a.Status == 1);
        int missing = rows.Count(a => a.Status == 0);
        root.Children.Add(SectHeader(
            $"Focus effects — {green} worn best · {upg} upgradable · {missing} missing", strong: true));

        // A row's verdict: green "worn best", or amber with WHERE the
        // upgrade lives — the stored tier, the huntable tier, or both.
        string Verdict(FocusEffects.AuditRow a)
        {
            if (a.Status == 2) return $"{RomanOf(a.Family, a.WornTier)} — worn best";
            if (a.BestTier == 0) return "none owned";
            bool stored = a.BestTier > a.WornTier;
            bool huntable = a.HuntableMax > a.BestTier;
            var parts = new List<string>();
            if (stored) parts.Add($"{RomanOf(a.Family, a.BestTier)} stored");
            if (huntable) parts.Add($"{RomanOf(a.Family, a.HuntableMax)} huntable");
            string prefix = a.WornTier > 0 ? $"{RomanOf(a.Family, a.WornTier)} worn → " : "";
            return prefix + string.Join(" · ", parts);
        }

        // Grouped by PLACE, not verdict: what's on your body (best or not,
        // the color still judges), what's owned but stored, what's missing.
        void Section(string title, Func<FocusEffects.AuditRow, bool> pick)
        {
            var inGroup = rows.Where(pick).ToList();
            if (inGroup.Count == 0) return;
            root.Children.Add(SectHeader(title));
            foreach (var a in inGroup)
            {
                int status = a.Status;
                string verdict = Verdict(a);
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
        Section("Worn", a => a.WornTier > 0);
        Section("Stored", a => a.WornTier == 0 && a.BestTier > 0);
        Section("Missing", a => a.BestTier == 0);
        // No shortcut line — the Focus board is one tab away in the window.
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

    /// <summary>One COMPACT line per clicky: icon, effect, carrier — the full
    /// story on the tooltip.</summary>
    private UIElement ClickyRow(int? iconId, string effect, string via, bool known = true)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        if (ItemIcons.Get(iconId) is { } icon)
        {
            var img = new System.Windows.Controls.Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
            DockPanel.SetDock(img, Dock.Left);
            row.Children.Add(img);
        }
        var text = new TextBlock
        {
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{effect} — {via}",
        };
        text.Inlines.Add(new System.Windows.Documents.Run(effect)
            { Foreground = known ? GreenFg : TextFg, FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run(" — " + via)
            { Foreground = DimFg, FontSize = 10.5 });
        row.Children.Add(text);
        return row;
    }

    /// <summary>The clickies list, appended to the landing pane.</summary>
    private void AddClickiesSection(StackPanel root)
    {
        root.Children.Add(SectHeader("Clickies", strong: true));

        // Worn: the item's own click lines, then any Click-Exaltation socket.
        var wornRows = new List<UIElement>();
        foreach (var ((token, _), e) in _worn.OrderBy(kv => kv.Key.Token, StringComparer.Ordinal))
        {
            if (e.Empty) continue;
            // One row per EFFECT per item: an item's own exaltation copy
            // socketed into itself would otherwise state its click twice.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rec = _stats.Lookup(e.Name);
            foreach (var fx in ClickLines(rec))
                if (seen.Add(fx))
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
                    if (seen.Add(fx))
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
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
