using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Tools window's Slot finder (30 Sep): pick a slot, see every item that
/// fits it wherever it sits — worn, bags, bank, depot, hoard, storage — your
/// combo's items first (gold), the rest folded away with a tally of the classes
/// they'd suit. A class strip per row shows every class the item allows. One
/// class picker, prefilled from /who or your last pick (owner, 30 Sep: no
/// compare combo, no place filter — the place on each row is enough).
/// Themed by hand; no stock controls.
/// </summary>
public sealed class SlotFinderView : DockPanel
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Surface = F("#1B2130"), Card = F("#232B3D"), Line = F("#2A3347"), Edge = F("#3A4560"), Row = F("#1F2637"), GroupBg = F("#171D2A"),
        Text = F("#E6ECF5"), Dim = F("#C9D4E3"), Hint = F("#7F93AD"), Faint = F("#5C6B82"),
        Gold = F("#E8C15A"), GoldBg = F("#2A2210"), GoldEdge = F("#7A5B22"), GoldDim = F("#B89A4A"),
        Green = F("#81C784"), GreenEdge = F("#2E5A36"), GreenBg = F("#15261A"), Blue = F("#4FC3F7"), Violet = F("#B39DDB"),
        StripOff = F("#262E42"), StripOn = F("#52627F");

    private ItemStats? _stats;
    private ToolPrefs? _prefs;
    private string _charKey = "";
    private List<InventoryStore.CarryRow>? _rows;
    private List<SlotFinder.SlotList> _index = new();
    private readonly List<string> _you = new();
    private string _who = "";
    private readonly ClassPicker _pick = new("CLASS COMBO · PICK UP TO 3", "#E8C15A");
    private readonly HashSet<SlotFinder.Fit> _folded = new() { SlotFinder.Fit.Neither };
    private string _slot = "WRIST";
    private string _stamp = "";
    private readonly StackPanel _top = new();
    private readonly StackPanel _body = new();

    public SlotFinderView()
    {
        SetDock(_top, Dock.Top);
        Children.Add(_top);
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body });
        // The picker lives outside the rebuilt bar, so a pick never rebuilds it under the mouse.
        _pick.Changed += c => { _you.Clear(); _you.AddRange(c); Save(); BuildTop(); BuildBody(); };
    }

    public void Init(ItemStats stats, ToolPrefs? prefs, string charKey)
    {
        _stats = stats; _prefs = prefs; _charKey = charKey;
        _you.Clear();
        _you.AddRange((prefs?.Get($"you:{charKey}") ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Take(3));
        string slot = prefs?.Get($"slot:{charKey}") ?? "";
        if (slot.Length > 0 && BisFinder.Slots.Any(s => s.Key == slot)) _slot = slot;
    }

    /// <summary>New dump rows and the /who combo: /who prefills when the game has
    /// said; otherwise your last hand pick stands.</summary>
    public void Update(List<InventoryStore.CarryRow> rows, string whoClasses, string stamp)
    {
        _rows = rows; _stamp = stamp; _who = whoClasses;
        var who = whoClasses.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).ToList();
        if (who.Count > 0) { _you.Clear(); _you.AddRange(who); }
        if (_stats is not null) _index = SlotFinder.Build(rows, _stats);
        BuildTop(); BuildBody();
    }

    /// <summary>Selftest: the slot showing, the rows painted per fit group, the picker.</summary>
    internal string SlotShown => _slot;
    internal Dictionary<SlotFinder.Fit, int> GroupCountsForTest { get; } = new();
    internal List<string> RowNamesForTest { get; } = new();
    internal ClassPicker YouPickerForTest => _pick;

    private void Save() { _prefs?.Set($"you:{_charKey}", string.Join("/", _you)); _prefs?.Set($"slot:{_charKey}", _slot); }

    private SlotFinder.SlotList? Current => _index.Count == 0 ? null : _index.FirstOrDefault(s => s.Key == _slot) ?? _index[0];

    private void BuildTop()
    {
        _top.Children.Clear();
        if (_rows is null || _stats is null || Current is not { } current) return;

        // ---- the picker ----
        _pick.Set(_you);
        _pick.Hint = _who.Length > 0 ? $"Prefilled from /who ({_who}) — change it here for a what-if." : _you.Count > 0 ? "No /who yet — your last pick; type /who in game to prefill." : "No /who yet — pick your classes, or type /who in game.";
        if (_pick.Parent is Panel pp) pp.Children.Remove(_pick);
        _top.Children.Add(_pick);

        // ---- the slots ----
        var slots = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var s in _index)
        {
            int yours = s.Items.Count(i => SlotFinder.FitOf(i, _you, Array.Empty<string>()) == SlotFinder.Fit.You);
            bool on = s.Key == current.Key;
            var tb = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
            tb.Inlines.Add(new Run(s.Label) { Foreground = on ? Gold : Hint });
            tb.Inlines.Add(new Run($"  {s.Items.Count}") { Foreground = on ? GoldDim : Faint, FontSize = 10.5 });
            if (yours > 0) tb.Inlines.Add(new Run($"  {yours}✓") { Foreground = Gold, FontSize = 10.5 });
            var b = new Border
            {
                Child = tb, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(9, 4, 9, 5), Margin = new Thickness(0, 0, 5, 5), Cursor = Cursors.Hand,
                Background = on ? GoldBg : Surface, BorderBrush = on ? GoldEdge : Line,
                ToolTip = $"{s.Items.Count} item{(s.Items.Count == 1 ? "" : "s")} fit {s.Label} · {yours} your combo can wear",
            };
            string key = s.Key;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; _slot = key; Save(); BuildTop(); BuildBody(); };
            b.MouseEnter += (_, _) => { if (key != current.Key) b.BorderBrush = Edge; };
            b.MouseLeave += (_, _) => { if (key != current.Key) b.BorderBrush = Line; };
            slots.Children.Add(b);
        }
        _top.Children.Add(slots);
    }

    private void BuildBody()
    {
        _body.Children.Clear(); GroupCountsForTest.Clear(); RowNamesForTest.Clear();
        if (_rows is null || _stats is null || Current is not { } current) return;

        // ---- the summary ----
        var shown = current.Items;
        var groups = new Dictionary<SlotFinder.Fit, List<SlotFinder.Item>>();
        foreach (var f in new[] { SlotFinder.Fit.You, SlotFinder.Fit.Unknown, SlotFinder.Fit.Neither }) groups[f] = new();
        foreach (var it in shown) groups[SlotFinder.FitOf(it, _you, Array.Empty<string>())].Add(it);
        foreach (var kv in groups) GroupCountsForTest[kv.Key] = kv.Value.Sum(i => i.Count);
        var sum = new WrapPanel();
        sum.Children.Add(new TextBlock { Text = current.Label.ToUpperInvariant(), Foreground = Text, FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center });
        void Sum(string n, string label, Brush fg) { var t = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; t.Inlines.Add(new Run(n) { Foreground = fg, FontWeight = FontWeights.SemiBold }); t.Inlines.Add(new Run(" " + label) { Foreground = Hint }); sum.Children.Add(t); }
        Sum(shown.Sum(i => i.Count).ToString(), "items", Dim);
        Sum(shown.Count(i => i.Worn).ToString(), "worn", Dim);
        Sum(groups[SlotFinder.Fit.You].Sum(i => i.Count).ToString(), _you.Count > 0 ? $"{string.Join("/", _you)} can wear" : "for you (pick your classes above)", Gold);
        Sum(groups[SlotFinder.Fit.Neither].Sum(i => i.Count).ToString(), "for other classes", Dim);
        if (_stamp.Length > 0) Sum("", $"snapshot {_stamp}", Faint);
        _body.Children.Add(new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Child = sum });

        // ---- the table ----
        var grid = new Grid();
        foreach (var w in new[] { new GridLength(1.4, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        grid.RowDefinitions.Add(new RowDefinition());
        string[] heads = { "ITEM", "AC", "STATS", "WHERE", "" };
        for (int c = 0; c < heads.Length; c++)
            Cell(grid, c == 4 ? ClassHeader() : new TextBlock { Text = heads[c], Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold }, 0, c, right: c == 1);
        int row = 1;
        foreach (var (fit, title) in new[] {
            (SlotFinder.Fit.You, _you.Count > 0 ? $"WEARABLE BY YOU · {string.Join("/", _you)}" : ""),
            (SlotFinder.Fit.Unknown, "CLASSES UNKNOWN TO THE WIKI"),
            (SlotFinder.Fit.Neither, _you.Count > 0 ? "NOT FOR YOUR COMBO" : "EVERYTHING THAT FITS") })
        {
            var list = groups[fit];
            if (title.Length == 0 || list.Count == 0) continue;
            bool folded = _folded.Contains(fit) && _you.Count > 0;
            grid.RowDefinitions.Add(new RowDefinition());
            var head = new TextBlock { FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Faint, Margin = new Thickness(0, 2, 0, 2) };
            head.Inlines.Add(new Run(folded ? "▸  " : "▾  ") { Foreground = Hint });
            head.Inlines.Add(new Run(title));
            head.Inlines.Add(new Run($"   {list.Sum(i => i.Count)} item{(list.Sum(i => i.Count) == 1 ? "" : "s")}") { Foreground = Hint, FontWeight = FontWeights.SemiBold });
            var hb = new Border { Background = GroupBg, Padding = new Thickness(8, 5, 8, 5), Child = head, Cursor = Cursors.Hand };
            var ff = fit;
            hb.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_folded.Remove(ff)) _folded.Add(ff); BuildBody(); };
            Grid.SetRow(hb, row); Grid.SetColumnSpan(hb, 5); grid.Children.Add(hb);
            row++;
            if (folded) continue;
            foreach (var it in list)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                RowNamesForTest.Add(it.Name);
                Brush edge = fit == SlotFinder.Fit.You ? Gold : Brushes.Transparent;
                Brush nameFg = fit == SlotFinder.Fit.You || _you.Count == 0 ? Text : Hint;
                Cell(grid, ItemCell(it, current.Key, nameFg, edge), row, 0);
                var ac = it.Stats.GetValueOrDefault("AC");
                Cell(grid, new TextBlock { Text = ac > 0 ? ac.ToString() : "—", Foreground = ac > 0 ? Dim : Faint, FontSize = 12 }, row, 1, right: true);
                Cell(grid, new TextBlock { Text = SlotFinder.StatLine(it), Foreground = fit == SlotFinder.Fit.Neither && _you.Count > 0 ? Hint : Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap }, row, 2);
                Cell(grid, WhereCell(it), row, 3);
                Cell(grid, ClassStrip(it), row, 4);
                row++;
            }
        }
        _body.Children.Add(new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 6), Child = grid });
        if (shown.Count == 0)
            _body.Children.Add(new TextBlock { Text = $"Nothing you own fits {current.Label}.", Foreground = Hint, FontSize = 12, Margin = new Thickness(2, 8, 0, 0) });

        // ---- who could use the rest ----
        var tally = _you.Count > 0 ? SlotFinder.Tally(groups[SlotFinder.Fit.Neither]) : new();
        if (tally.Count > 0)
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            wp.Children.Add(new TextBlock { Text = $"The {groups[SlotFinder.Fit.Neither].Sum(i => i.Count)} for other classes would suit:  ", Foreground = Hint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            foreach (var (cls, n) in tally)
                wp.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = Edge, Background = Card, Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 5, 4),
                    Child = new TextBlock { Text = $"{cls} {n}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Dim }, ToolTip = $"{n} of them {(n == 1 ? "is" : "are")} wearable by a {cls}",
                });
            _body.Children.Add(wp);
        }
        var unknown = SlotFinder.Unknown(_rows, _stats);
        if (unknown.Count > 0)
            _body.Children.Add(new TextBlock
            {
                Foreground = Hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                Text = $"{unknown.Count} item{(unknown.Count == 1 ? "" : "s")} the wiki table doesn't know, so {(unknown.Count == 1 ? "it isn't" : "they aren't")} under any slot: {string.Join(", ", unknown.Take(8))}{(unknown.Count > 8 ? ", …" : "")}. Bags, food, quest pieces and brand-new items land here.",
            });
        _body.Children.Add(new TextBlock
        {
            Foreground = Faint, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Text = "Stats are scaled to the best copy's +N the way the BiS finder scales them. An item counts for your combo when ANY of its classes may wear it; the class strip lights every class the wiki allows and outlines yours. "
                 + "Exaltations sitting in sockets aren't items — the Focus planner covers those.",
        });
    }

    private UIElement ItemCell(SlotFinder.Item it, string slotKey, Brush nameFg, Brush edge)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Border { Width = 3, Background = edge, Margin = new Thickness(0, 0, 7, 0), CornerRadius = new CornerRadius(1) });
        var line = ItemChips.Name(it.Name, it.BestTier, it.Rec.Icon, nameFg, 1, it.WornIn(slotKey) || it.WornIn("ANY") ? new[] { ItemChips.Tag.Worn } : Array.Empty<ItemChips.Tag>());
        if (it.Copies.Count > 1)
            line.Children.Insert(3, new TextBlock { Text = $"{it.Copies.Count} copies ({string.Join(", ", it.Copies.Select(c => "+" + c.Tier))})", Foreground = Faint, FontSize = 10.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(line);
        return sp;
    }

    private static UIElement WhereCell(SlotFinder.Item it)
    {
        var sp = new StackPanel();
        // Which copy is which only matters when the copies differ in tier (owner, 1 Oct: "the +3?").
        bool tiersDiffer = it.Copies.Select(c => c.Tier).Distinct().Count() > 1;
        foreach (var c in it.Copies)
        {
            var tb = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 0, 0, 1) };
            Brush laneFg = c.Lane == "worn" ? Gold : c.Lane == "bank" ? Blue : c.Lane == "hoard" ? Violet : Hint;
            var lane = new Border { BorderBrush = laneFg, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0), Child = new TextBlock { Text = SlotFinder.LaneLabel(c.Lane).ToUpperInvariant(), FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = laneFg } };
            tb.Inlines.Add(new InlineUIContainer(lane) { BaselineAlignment = BaselineAlignment.Center });
            tb.Inlines.Add(new Run("  " + SlotFinder.PrettyLocation(c.Location, c.Lane)) { Foreground = Dim, FontWeight = FontWeights.SemiBold });
            if (tiersDiffer) tb.Inlines.Add(new Run($"  — the +{c.Tier}") { Foreground = Faint });
            if (c.Count > 1) tb.Inlines.Add(new Run($"  ×{c.Count}") { Foreground = Faint });
            sp.Children.Add(tb);
        }
        return sp;
    }

    private UIElement ClassHeader()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var cls in BisFinder.AllClasses)
            sp.Children.Add(new TextBlock { Text = cls, FontSize = 7, Foreground = _you.Contains(cls) ? Gold : Faint, Width = 22, LayoutTransform = new RotateTransform(-90), Margin = new Thickness(0, 0, 2, 0), HorizontalAlignment = HorizontalAlignment.Left });
        return sp;
    }

    private UIElement ClassStrip(SlotFinder.Item it)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool unknown = string.IsNullOrWhiteSpace(it.Rec.Classes);
        foreach (var cls in BisFinder.AllClasses)
        {
            bool on = !unknown && SlotFinder.ClassCan(it.Rec, cls);
            bool mine = _you.Contains(cls);
            sp.Children.Add(new Border
            {
                Width = 12, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 2, 0),
                Background = on ? (mine ? Gold : StripOn) : StripOff,
                BorderBrush = mine ? GoldEdge : Brushes.Transparent, BorderThickness = new Thickness(mine ? 1.5 : 0),
                ToolTip = cls + (unknown ? " — classes unknown" : on ? " can wear it" : ""),
            });
        }
        return sp;
    }

    private static void Cell(Grid g, UIElement el, int row, int col, bool right = false)
    {
        var b = new Border { BorderBrush = Row, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(col == 0 ? 0 : 10, 5, 2, 5), Child = el, VerticalAlignment = VerticalAlignment.Stretch };
        if (el is FrameworkElement fe) { fe.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left; fe.VerticalAlignment = VerticalAlignment.Center; }
        Grid.SetRow(b, row); Grid.SetColumn(b, col); g.Children.Add(b);
    }
}
