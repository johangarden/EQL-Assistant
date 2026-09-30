using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Tools window's Slot finder (30 Sep): pick a slot, see every item that
/// fits it wherever it sits — worn, bags, bank, depot, hoard, storage — grouped
/// by who can wear it: your /who combo (gold), a combo you compare with (teal),
/// neither (folded). A class strip per row shows every class the item allows.
/// Themed by hand; no stock controls.
/// </summary>
public sealed class SlotFinderView : DockPanel
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Surface = F("#1B2130"), Card = F("#232B3D"), Line = F("#2A3347"), Edge = F("#3A4560"), Row = F("#1F2637"), GroupBg = F("#171D2A"),
        Text = F("#E6ECF5"), Dim = F("#C9D4E3"), Hint = F("#7F93AD"), Faint = F("#5C6B82"),
        Gold = F("#E8C15A"), GoldBg = F("#2A2210"), GoldEdge = F("#7A5B22"), GoldDim = F("#B89A4A"),
        Teal = F("#4DD0C8"), TealBg = F("#10292A"), TealEdge = F("#2A6E6A"),
        Green = F("#81C784"), GreenEdge = F("#2E5A36"), GreenBg = F("#15261A"), Blue = F("#4FC3F7"), Violet = F("#B39DDB"),
        ChipOn = F("#26304A"), ChipOnEdge = F("#4A5A7A"), StripOff = F("#262E42"), StripOn = F("#52627F");

    private ItemStats? _stats;
    private ToolPrefs? _prefs;
    private string _charKey = "";
    private List<InventoryStore.CarryRow>? _rows;
    private List<SlotFinder.SlotList> _index = new();
    private readonly List<string> _you = new();
    private readonly List<string> _compare = new();
    private readonly HashSet<string> _lanes = new(SlotFinder.Lanes, StringComparer.Ordinal);
    private readonly HashSet<SlotFinder.Fit> _folded = new() { SlotFinder.Fit.Neither };
    private string _slot = "WRIST";
    private string _stamp = "";
    private readonly StackPanel _top = new();
    private readonly StackPanel _body = new();
    private Popup? _picker;

    public SlotFinderView()
    {
        SetDock(_top, Dock.Top);
        Children.Add(_top);
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body });
    }

    public void Init(ItemStats stats, ToolPrefs? prefs, string charKey)
    {
        _stats = stats; _prefs = prefs; _charKey = charKey;
        _compare.Clear();
        _compare.AddRange((prefs?.Get($"compare:{charKey}") ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Take(3));
        string slot = prefs?.Get($"slot:{charKey}") ?? "";
        if (slot.Length > 0 && BisFinder.Slots.Any(s => s.Key == slot)) _slot = slot;
    }

    /// <summary>New dump rows and the /who combo (the you-side always follows /who).</summary>
    public void Update(List<InventoryStore.CarryRow> rows, string whoClasses, string stamp)
    {
        _rows = rows; _stamp = stamp;
        _you.Clear();
        _you.AddRange(whoClasses.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        if (_stats is not null) _index = SlotFinder.Build(rows, _stats);
        Build();
    }

    /// <summary>Selftest: the slot showing and the rows painted per fit group.</summary>
    internal string SlotShown => _slot;
    internal Dictionary<SlotFinder.Fit, int> GroupCountsForTest { get; } = new();
    internal List<string> RowNamesForTest { get; } = new();

    private void Save() { _prefs?.Set($"compare:{_charKey}", string.Join("/", _compare)); _prefs?.Set($"slot:{_charKey}", _slot); }

    private void Build() { BuildTop(); BuildBody(); }

    // The popup's anchor lives in the bar: a pick redraws the body only, and the
    // bar follows once the popup closes (owner, 30 Sep: "the class picker closes immediately").
    private void BuildTop()
    {
        _top.Children.Clear();
        if (_rows is null || _stats is null || _index.Count == 0) return;
        var current = _index.FirstOrDefault(s => s.Key == _slot) ?? _index[0];

        // ---- the bar: combos, lanes ----
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var combos = Group("COMBOS");
        combos.Children.Add(Chip(_you.Count > 0 ? string.Join("/", _you) : "no /who yet", _you.Count > 0 ? "you · /who" : "type /who in game", Gold, GoldBg, GoldEdge, GoldDim, dot: Gold));
        var cmp = Chip(_compare.Count > 0 ? string.Join("/", _compare) : "pick a combo", "compare ▾", Teal, TealBg, TealEdge, F("#3E9F99"), dot: Teal);
        cmp.Cursor = Cursors.Hand;
        cmp.ToolTip = "A combo you might switch to — its items light teal";
        // Opened on the RELEASE: an auto-close popup opened on the press reads the
        // release that follows (over the chip, outside the popup) as a click away.
        cmp.MouseLeftButtonDown += (_, e) => e.Handled = true;
        cmp.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenPicker(cmp); };
        combos.Children.Add(cmp);
        bar.Children.Add(combos);
        var where = Group("WHERE");
        foreach (var lane in SlotFinder.Lanes)
        {
            bool on = _lanes.Contains(lane);
            // On = gold with a tick; off = dimmed, so the filter reads at a glance (owner, 30 Sep).
            var c = new Border
            {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 4), Cursor = Cursors.Hand,
                Background = on ? GoldBg : Brushes.Transparent, BorderBrush = on ? GoldEdge : Line,
                Child = new TextBlock { Text = (on ? "✓ " : "") + SlotFinder.LaneLabel(lane), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = on ? Gold : Faint },
                ToolTip = on ? $"Listing {SlotFinder.LaneLabel(lane).ToLowerInvariant()} — click to leave it out" : $"Not listing {SlotFinder.LaneLabel(lane).ToLowerInvariant()} — click to include it",
            };
            string l = lane;
            c.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_lanes.Remove(l)) _lanes.Add(l); Build(); };
            where.Children.Add(c);
        }
        bar.Children.Add(where);
        _top.Children.Add(bar);

        // ---- the slots ----
        var slots = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var s in _index)
        {
            var items = s.Items.Where(i => i.Copies.Any(c => _lanes.Contains(c.Lane))).ToList();
            int yours = items.Count(i => SlotFinder.FitOf(i, _you, _compare) == SlotFinder.Fit.You);
            bool on = s.Key == current.Key;
            var tb = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
            tb.Inlines.Add(new Run(s.Label) { Foreground = on ? Gold : Hint });
            tb.Inlines.Add(new Run($"  {items.Count}") { Foreground = on ? GoldDim : Faint, FontSize = 10.5 });
            if (yours > 0) tb.Inlines.Add(new Run($"  {yours}✓") { Foreground = Gold, FontSize = 10.5 });
            var b = new Border
            {
                Child = tb, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(9, 4, 9, 5), Margin = new Thickness(0, 0, 5, 5), Cursor = Cursors.Hand,
                Background = on ? GoldBg : Surface, BorderBrush = on ? GoldEdge : Line,
                ToolTip = $"{items.Count} item{(items.Count == 1 ? "" : "s")} fit {s.Label} · {yours} your combo can wear",
            };
            string key = s.Key;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; _slot = key; Save(); Build(); };
            b.MouseEnter += (_, _) => { if (key != current.Key) b.BorderBrush = Edge; };
            b.MouseLeave += (_, _) => { if (key != current.Key) b.BorderBrush = Line; };
            slots.Children.Add(b);
        }
        _top.Children.Add(slots);
    }

    private void BuildBody()
    {
        _body.Children.Clear(); GroupCountsForTest.Clear(); RowNamesForTest.Clear();
        if (_rows is null || _stats is null || _index.Count == 0) return;
        var current = _index.FirstOrDefault(s => s.Key == _slot) ?? _index[0];

        // ---- the summary ----
        var shown = current.Items.Where(i => i.Copies.Any(c => _lanes.Contains(c.Lane))).ToList();
        var groups = new Dictionary<SlotFinder.Fit, List<SlotFinder.Item>>();
        foreach (var f in new[] { SlotFinder.Fit.You, SlotFinder.Fit.Compare, SlotFinder.Fit.Unknown, SlotFinder.Fit.Neither }) groups[f] = new();
        foreach (var it in shown) groups[SlotFinder.FitOf(it, _you, _compare)].Add(it);
        foreach (var kv in groups) GroupCountsForTest[kv.Key] = kv.Value.Sum(i => i.Count);
        var sum = new WrapPanel();
        sum.Children.Add(new TextBlock { Text = current.Label.ToUpperInvariant(), Foreground = Text, FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center });
        void Sum(string n, string label, Brush fg) { var t = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; t.Inlines.Add(new Run(n) { Foreground = fg, FontWeight = FontWeights.SemiBold }); t.Inlines.Add(new Run(" " + label) { Foreground = Hint }); sum.Children.Add(t); }
        Sum(shown.Sum(i => i.Count).ToString(), "items", Dim);
        Sum(shown.Count(i => i.Worn).ToString(), "worn", Dim);
        Sum(groups[SlotFinder.Fit.You].Sum(i => i.Count).ToString(), _you.Count > 0 ? "your combo can wear" : "for you (no /who yet)", Gold);
        if (_compare.Count > 0) Sum(groups[SlotFinder.Fit.Compare].Sum(i => i.Count).ToString(), $"more for {string.Join("/", _compare)}", Teal);
        Sum(groups[SlotFinder.Fit.Neither].Sum(i => i.Count).ToString(), "for classes in neither", Dim);
        if (_stamp.Length > 0) Sum("", $"snapshot {_stamp}", Faint);
        _body.Children.Add(new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Child = sum });

        // ---- the table ----
        var grid = new Grid();
        foreach (var w in new[] { new GridLength(1.4, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        grid.RowDefinitions.Add(new RowDefinition());
        string[] heads = { "ITEM", "AC", "STATS", "WHERE", "FOR", "" };
        for (int c = 0; c < heads.Length; c++)
            Cell(grid, c == 5 ? ClassHeader() : new TextBlock { Text = heads[c], Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold }, 0, c, right: c == 1);
        int row = 1;
        foreach (var (fit, title) in new[] {
            (SlotFinder.Fit.You, _you.Count > 0 ? $"WEARABLE BY YOU · {string.Join("/", _you)}" : "WEARABLE BY YOU"),
            (SlotFinder.Fit.Compare, _compare.Count > 0 ? $"WITH {string.Join("/", _compare)}" : ""),
            (SlotFinder.Fit.Unknown, "CLASSES UNKNOWN TO THE WIKI"),
            (SlotFinder.Fit.Neither, "NEITHER COMBO") })
        {
            var list = groups[fit];
            if (title.Length == 0 || list.Count == 0) continue;
            bool folded = _folded.Contains(fit);
            grid.RowDefinitions.Add(new RowDefinition());
            var head = new TextBlock { FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Faint, Margin = new Thickness(0, 2, 0, 2) };
            head.Inlines.Add(new Run(folded ? "▸  " : "▾  ") { Foreground = Hint });
            head.Inlines.Add(new Run(title));
            head.Inlines.Add(new Run($"   {list.Sum(i => i.Count)} item{(list.Sum(i => i.Count) == 1 ? "" : "s")}") { Foreground = Hint, FontWeight = FontWeights.SemiBold });
            var hb = new Border { Background = GroupBg, Padding = new Thickness(8, 5, 8, 5), Child = head, Cursor = Cursors.Hand };
            var ff = fit;
            hb.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_folded.Remove(ff)) _folded.Add(ff); Build(); };
            Grid.SetRow(hb, row); Grid.SetColumnSpan(hb, 6); grid.Children.Add(hb);
            row++;
            if (folded) continue;
            foreach (var it in list)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                RowNamesForTest.Add(it.Name);
                Brush edge = fit == SlotFinder.Fit.You ? Gold : fit == SlotFinder.Fit.Compare ? Teal : Brushes.Transparent;
                Brush nameFg = fit is SlotFinder.Fit.Neither or SlotFinder.Fit.Unknown ? Hint : Text;
                Cell(grid, ItemCell(it, current.Key, nameFg, edge), row, 0);
                var ac = it.Stats.GetValueOrDefault("AC");
                Cell(grid, new TextBlock { Text = ac > 0 ? ac.ToString() : "—", Foreground = ac > 0 ? Dim : Faint, FontSize = 12 }, row, 1, right: true);
                Cell(grid, new TextBlock { Text = SlotFinder.StatLine(it), Foreground = fit is SlotFinder.Fit.Neither ? Hint : Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap }, row, 2);
                Cell(grid, WhereCell(it), row, 3);
                Cell(grid, ForCell(fit, it), row, 4);
                Cell(grid, ClassStrip(it), row, 5);
                row++;
            }
        }
        _body.Children.Add(new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 6), Child = grid });
        if (shown.Count == 0)
            _body.Children.Add(new TextBlock { Text = $"Nothing that fits {current.Label} in the places ticked.", Foreground = Hint, FontSize = 12, Margin = new Thickness(2, 8, 0, 0) });

        // ---- who could use the rest ----
        var tally = SlotFinder.Tally(groups[SlotFinder.Fit.Neither]);
        if (tally.Count > 0)
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            wp.Children.Add(new TextBlock { Text = $"The {groups[SlotFinder.Fit.Neither].Sum(i => i.Count)} in neither would suit:  ", Foreground = Hint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            foreach (var (cls, n) in tally)
            {
                var c = new Border
                {
                    CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = Edge, Background = Card, Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 5, 4), Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = $"{cls} {n}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Dim }, ToolTip = $"Put {cls} in the combo you compare",
                };
                string cc = cls;
                c.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_compare.Contains(cc)) { _compare.Add(cc); while (_compare.Count > 3) _compare.RemoveAt(0); } Save(); Build(); };
                wp.Children.Add(c);
            }
            _body.Children.Add(wp);
        }
        _body.Children.Add(new TextBlock
        {
            Foreground = Faint, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Text = "Stats are scaled to the best copy's +N the way the BiS finder scales them. Gold edge: your combo can wear it; teal: only the combo you compare; none: neither. "
                 + "The class strip lights every class the wiki allows and outlines yours. An item counts for a combo when ANY of its classes may wear it. Exaltations sitting in sockets aren't items — the Focus planner covers those.",
        });
    }

    private UIElement ItemCell(SlotFinder.Item it, string slotKey, Brush nameFg, Brush edge)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Border { Width = 3, Background = edge, Margin = new Thickness(0, 0, 7, 0), CornerRadius = new CornerRadius(1) });
        var img = ItemIcons.Get(it.Rec.Icon);
        sp.Children.Add(img is not null
            ? new Image { Source = img, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }
            : new Border { Width = 22, Height = 22, Background = Card, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0) });
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new Run(it.Name) { Foreground = nameFg, FontSize = 12, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(tb);
        sp.Children.Add(new Border
        {
            Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(4, 0, 4, 1), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = GreenEdge, Background = GreenBg, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = $"+{it.BestTier}", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Green },
        });
        if (it.Copies.Count > 1)
            sp.Children.Add(new TextBlock { Text = $"{it.Copies.Count} copies ({string.Join(", ", it.Copies.Select(c => "+" + c.Tier))})", Foreground = Faint, FontSize = 10.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (it.WornIn(slotKey) || it.WornIn("ANY"))
            sp.Children.Add(new Border { Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(4, 1, 4, 1), CornerRadius = new CornerRadius(3), Background = Gold, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "WORN", FontSize = 9, FontWeight = FontWeights.ExtraBold, Foreground = F("#10151E") } });
        return sp;
    }

    private UIElement WhereCell(SlotFinder.Item it)
    {
        var sp = new StackPanel();
        foreach (var c in it.Copies.Where(c => _lanes.Contains(c.Lane)))
        {
            var tb = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 0, 0, 1) };
            Brush laneFg = c.Lane == "worn" ? Gold : c.Lane == "bank" ? Blue : c.Lane == "hoard" ? Violet : Hint;
            var lane = new Border { BorderBrush = laneFg, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0), Child = new TextBlock { Text = SlotFinder.LaneLabel(c.Lane).ToUpperInvariant(), FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = laneFg } };
            tb.Inlines.Add(new InlineUIContainer(lane) { BaselineAlignment = BaselineAlignment.Center });
            tb.Inlines.Add(new Run("  " + SlotFinder.PrettyLocation(c.Location, c.Lane)) { Foreground = Dim, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run($"  +{c.Tier}") { Foreground = Faint });
            if (c.Count > 1) tb.Inlines.Add(new Run($"  ×{c.Count}") { Foreground = Faint });
            sp.Children.Add(tb);
        }
        return sp;
    }

    private UIElement ForCell(SlotFinder.Fit fit, SlotFinder.Item it)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = $"{(_you.Count > 0 ? string.Join("/", _you) : "you")} · {(_compare.Count > 0 ? string.Join("/", _compare) : "the combo you compare")}" };
        bool you = fit == SlotFinder.Fit.You;
        bool cmp = _compare.Count > 0 && !string.IsNullOrWhiteSpace(it.Rec.Classes) && BisFinder.ClassAllowed(it.Rec.Classes, _compare);
        sp.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = you ? Gold : Brushes.Transparent, Stroke = you ? Gold : Edge, StrokeThickness = 1.5, Margin = new Thickness(0, 0, 5, 0) });
        sp.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = cmp ? Teal : Brushes.Transparent, Stroke = cmp ? Teal : Edge, StrokeThickness = 1.5 });
        return sp;
    }

    private UIElement ClassHeader()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var cls in BisFinder.AllClasses)
        {
            Brush fg = _you.Contains(cls) ? Gold : _compare.Contains(cls) ? Teal : Faint;
            sp.Children.Add(new TextBlock { Text = cls, FontSize = 7, Foreground = fg, Width = 22, LayoutTransform = new RotateTransform(-90), Margin = new Thickness(0, 0, 2, 0), HorizontalAlignment = HorizontalAlignment.Left });
        }
        return sp;
    }

    private UIElement ClassStrip(SlotFinder.Item it)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool unknown = string.IsNullOrWhiteSpace(it.Rec.Classes);
        foreach (var cls in BisFinder.AllClasses)
        {
            bool on = !unknown && SlotFinder.ClassCan(it.Rec, cls);
            bool mine = _you.Contains(cls), theirs = _compare.Contains(cls);
            sp.Children.Add(new Border
            {
                Width = 12, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 2, 0),
                Background = on ? (mine ? Gold : theirs ? Teal : StripOn) : StripOff,
                BorderBrush = mine ? GoldEdge : theirs ? TealEdge : Brushes.Transparent, BorderThickness = new Thickness(mine || theirs ? 1.5 : 0),
                ToolTip = cls + (unknown ? " — classes unknown" : on ? " can wear it" : ""),
            });
        }
        return sp;
    }

    private void OpenPicker(UIElement at)
    {
        if (_picker is not null) _picker.IsOpen = false;
        var grid = new UniformGrid { Columns = 4 };
        void Paint()
        {
            foreach (var child in grid.Children)
                if (child is Border b && b.Tag is string cls)
                {
                    bool on = _compare.Contains(cls);
                    b.Background = on ? TealBg : Card; b.BorderBrush = on ? TealEdge : Edge;
                    if (b.Child is TextBlock t) t.Foreground = on ? Teal : Hint;
                }
        }
        foreach (var cls in BisFinder.AllClasses)
        {
            var b = new Border { Tag = cls, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(0, 3, 0, 4), Margin = new Thickness(0, 0, 5, 5), Width = 54, Cursor = Cursors.Hand, Child = new TextBlock { Text = cls, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center } };
            string c = cls;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_compare.Remove(c)) { _compare.Add(c); while (_compare.Count > 3) _compare.RemoveAt(0); } Paint(); Save(); BuildBody(); };
            grid.Children.Add(b);
        }
        Paint();
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "COMPARE WITH · PICK UP TO 3", Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 7) });
        sp.Children.Add(grid);
        sp.Children.Add(new TextBlock { Text = "Remembered. An item counts for a combo when ANY of its classes may wear it.", Foreground = Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 236 });
        _picker = new Popup
        {
            PlacementTarget = at, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 6,
            Child = new Border { Background = Surface, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(10), Child = sp },
        };
        _picker.Closed += (_, _) => BuildTop(); // the chip's words catch up
        _picker.IsOpen = true;
    }

    /// <summary>Selftest: open the compare picker, click a class in it, and say whether it is still open.</summary>
    internal bool PickInPopupForTest(string cls)
    {
        var anchor = _top.Children.OfType<WrapPanel>().FirstOrDefault()?.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<Border>().Skip(1).FirstOrDefault();
        if (anchor is null) return false;
        OpenPicker(anchor);
        var chip = ((_picker!.Child as Border)!.Child as StackPanel)!.Children.OfType<UniformGrid>().First().Children.OfType<Border>().First(b => (string)b.Tag == cls);
        chip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
        bool open = _picker.IsOpen && _compare.Contains(cls);
        _picker.IsOpen = false;
        return open;
    }

    // ---- bits ----

    private static StackPanel Group(string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 4) };
        sp.Children.Add(new TextBlock { Text = label, Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static Border Chip(string text, string small, Brush fg, Brush bg, Brush edge, Brush smallFg, Brush? dot = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot is not null) sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dot, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = small, FontSize = 10.5, Foreground = smallFg, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = edge, Background = bg, Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 4), Child = sp };
    }

    private static void Cell(Grid g, UIElement el, int row, int col, bool right = false)
    {
        var b = new Border { BorderBrush = Row, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(col == 0 ? 0 : 10, 5, 2, 5), Child = el, VerticalAlignment = VerticalAlignment.Stretch };
        if (el is FrameworkElement fe) { fe.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left; fe.VerticalAlignment = VerticalAlignment.Center; }
        Grid.SetRow(b, row); Grid.SetColumn(b, col); g.Children.Add(b);
    }
}
