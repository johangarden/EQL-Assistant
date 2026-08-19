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
/// wildcards · weapons), each cell wearing its slot title above, the item's
/// +N tier as a gold corner pill and typed socket pills (O·F·C·W·P). A
/// detail pane follows the selected slot with three tabs: Sockets (the
/// game's own item-window layout), Focus (the audit's view of the slot) and
/// Stats (wiki base values — the +N uplift is stated nowhere observable).
/// The footer rides the session panel's ding//who level machinery.
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
    private static readonly Brush CardBg = Freeze("#141A24");
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
    private string _paneTab = "sockets";
    private readonly List<Border> _cells = new();

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
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        CharName.Text = _charName.Length > 0 ? _charName : "Character";

        string? path = InventoryStore.FindDumpFile(_eqRoot, _charName, _server);
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
        if (_selected is null || !_worn.ContainsKey(_selected.Value))
            _selected = DollLayout.SelectMany(r => r).FirstOrDefault(s => _worn.ContainsKey(s));
        RefreshPane();
        RefreshFooter();
    }

    private void RefreshFooter()
    {
        if (_session is null) { FootLevel.Text = ""; FootClasses.Text = ""; FootWho.Text = ""; return; }
        var (text, tip) = _session.LevelInfo(DateTime.Now);
        // "lvl 50 /who 2m" → footer splits it: LEVEL 50 | SHM/SHD/ROG | cue
        FootLevel.Text = text.Length > 0 ? text.ToUpperInvariant().Replace("LVL", "LEVEL ") : "LEVEL —";
        FootLevel.ToolTip = tip.Length > 0 ? tip : null;
        FootClasses.Text = _session.WhoClasses.Replace("/", " / ");
        FootWho.Text = _session.WhoClasses.Length == 0 ? "type /who in game for classes + level" : "";
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
        wrap.Children.Add(new TextBlock
        {
            Text = slot.Token.ToUpperInvariant(),
            Foreground = SlotFg,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(4, 0, 0, 2),
        });

        _worn.TryGetValue(slot, out var entry);
        var inner = new Grid();
        var cell = new Border
        {
            Background = CellBg,
            BorderBrush = CellBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            MinHeight = 54,
            Cursor = Cursors.Hand,
            Child = inner,
            Tag = slot,
        };
        cell.MouseLeftButtonUp += (_, _) => { _selected = slot; RefreshPane(); };
        _cells.Add(cell);

        var body = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
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
            body.Children.Add(new TextBlock
            {
                Text = baseName,
                Foreground = TextFg,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 30,
                Margin = new Thickness(0, 0, tier.Length > 0 ? 26 : 0, 0),
            });
            if (entry.Children.Count > 0)
            {
                var pills = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                foreach (var child in entry.Children)
                {
                    var (label, slotName) = SlotTypeOf(child.Location);
                    bool on = !child.Empty;
                    pills.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(3, 0, 3, 1),
                        Margin = new Thickness(0, 0, 2, 0),
                        Background = on ? GreenFg : Brushes.Transparent,
                        BorderBrush = on ? GreenFg : PillOffBorder,
                        BorderThickness = new Thickness(1),
                        ToolTip = on ? $"{slotName} — {child.Name}" : $"{slotName} — empty",
                        Child = new TextBlock
                        {
                            Text = label,
                            FontSize = 8.5,
                            FontWeight = FontWeights.Bold,
                            Foreground = on ? WornPillFg : DimFg,
                        },
                    });
                }
                body.Children.Add(pills);
            }
        }
        inner.Children.Add(body);

        if (entry is not null && !entry.Empty)
        {
            var (_, tier) = SplitTier(entry.Name);
            if (tier.Length > 0)
                inner.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 4, 0),
                    Background = CardBg,
                    BorderBrush = GoldFg,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5, 0, 5, 1),
                    Child = new TextBlock
                    {
                        Text = tier,
                        Foreground = GoldFg,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                    },
                });
        }

        wrap.Children.Add(cell);
        return wrap;
    }

    /// <summary>"Wicked Sallet +5" → ("Wicked Sallet", "+5").</summary>
    private static (string Name, string Tier) SplitTier(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(?<n>.+?) (?<t>\+\d+)$");
        return m.Success ? (m.Groups["n"].Value, m.Groups["t"].Value) : (name, "");
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
            tab.MouseLeftButtonUp += (_, _) => { _paneTab = captured; RefreshPane(); };
            PaneTabs.Children.Add(tab);
        }

        var lines = new List<PaneLineVm>();
        PaneHint.Visibility = Visibility.Collapsed;

        if (_selected is not { } sel2 || !_worn.TryGetValue(sel2, out var entry) || entry.Empty)
        {
            PaneTitle.Text = _selected is { } s2 ? s2.Token : "";
            PaneSub.Text = "empty slot";
            PaneLines.ItemsSource = lines;
            return;
        }

        PaneTitle.Text = entry.Name;
        PaneSub.Text = sel2.Token;

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
        if (rec.Flags.Length > 0) lines.Add(new PaneLineVm("FLAGS", rec.Flags, TextFg, Visibility.Visible));
        if (rec.Ac is { } ac) lines.Add(new PaneLineVm("AC", ac.ToString(), TextFg, Visibility.Visible));
        if (rec.Stats.Length > 0) lines.Add(new PaneLineVm("STATS", rec.Stats, TextFg, Visibility.Visible));
        if (rec.Saves.Length > 0) lines.Add(new PaneLineVm("SAVES", rec.Saves, TextFg, Visibility.Visible));
        if (rec.Effects.Length > 0) lines.Add(new PaneLineVm("EFFECTS", rec.Effects, GreenFg, Visibility.Visible));
        if (rec.Weight.Length > 0 || rec.Size.Length > 0)
            lines.Add(new PaneLineVm("WEIGHT / SIZE", $"{rec.Weight} · {rec.Size}".Trim(' ', '·'), TextFg, Visibility.Visible));
        if (rec.Classes.Length > 0 || rec.Races.Length > 0)
            lines.Add(new PaneLineVm("CLASSES / RACES", $"{rec.Classes} · {rec.Races}".Trim(' ', '·'), TextFg, Visibility.Visible));
        if (rec.Extras.Length > 0) lines.Add(new PaneLineVm("MORE", rec.Extras, DimFg, Visibility.Visible));
        PaneHint.Text = "Wiki base values — the game states nowhere what a +N uplift changes.";
        PaneHint.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
