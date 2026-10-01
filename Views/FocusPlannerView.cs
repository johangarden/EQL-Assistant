using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Tools window's Focus planner (30 Sep): which focus exaltation goes in
/// which of your worn items' focus sockets. Mark each family Need / Nice / Off;
/// the plan is the exact best assignment (<see cref="FocusPlanner"/>), shown
/// socket by socket with the move that gets you there, then who fought over a
/// socket and what's worth hunting. A what-if combo (the bard's instruments
/// appear only with BRD) has marks of its own. Themed by hand; no stock controls.
/// </summary>
public sealed class FocusPlannerView : DockPanel
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Surface = F("#1B2130"), Card = F("#232B3D"), Line = F("#2A3347"), Edge = F("#3A4560"), Row = F("#1F2637"), ChangeBg = F("#182418"),
        Text = F("#E6ECF5"), Dim = F("#C9D4E3"), Hint = F("#7F93AD"), Faint = F("#5C6B82"),
        Gold = F("#E8C15A"), GoldBg = F("#2A2210"), GoldEdge = F("#7A5B22"), Green = F("#81C784"), Warn = F("#FFB074"), Red = F("#FF7A7A"), Blue = F("#4FC3F7"),
        ChipOn = F("#26304A"), ChipOnEdge = F("#4A5A7A"), NiceDot = F("#7F93AD"), OffDot = F("#3A4560");

    private FocusEffects? _focus;
    private ItemStats? _stats;
    private ToolPrefs? _prefs;
    private string _charKey = "";
    private List<InventoryStore.CarryRow>? _rows;
    private readonly List<string> _combo = new();
    private string _who = "";
    private int _level;
    private readonly ClassPicker _pick = new("CLASS COMBO · PICK UP TO 3", "#E8C15A");
    private Dictionary<string, FocusPlanner.Want> _wants = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _top = new();
    private readonly StackPanel _body = new();
    private readonly ContentControl _headRight = new() { VerticalAlignment = VerticalAlignment.Top }; // the verdict, beside the picker

    public FocusPlannerView()
    {
        SetDock(_top, Dock.Top);
        Children.Add(_top);
        _pick.Changed += c => { _combo.Clear(); _combo.AddRange(c); _prefs?.Set($"focuscombo:{_charKey}", string.Join("/", _combo)); LoadWants(); BuildTop(); BuildBody(); };
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body });
    }

    public void Init(FocusEffects focus, ItemStats stats, ToolPrefs? prefs, string charKey)
    {
        _focus = focus; _stats = stats; _prefs = prefs; _charKey = charKey;
        _combo.Clear();
        _combo.AddRange((prefs?.Get($"focuscombo:{charKey}") ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Take(3));
    }

    public void Update(List<InventoryStore.CarryRow> rows, string whoClasses, int level)
    {
        _rows = rows; _level = level; _who = whoClasses;
        // /who prefills when the game has said; otherwise the last hand pick stands (owner, 30 Sep).
        var who = whoClasses.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).ToList();
        if (who.Count > 0) { _combo.Clear(); _combo.AddRange(who); }
        LoadWants();
        Build();
    }

    private IReadOnlyList<string> Combo => _combo;
    private string WantsKey => $"focus:{_charKey}:{string.Join("/", Combo)}";

    private void LoadWants() => _wants = FocusPlanner.Unpack(_prefs?.Get(WantsKey) ?? "", FocusPlanner.DefaultWants(Combo));

    /// <summary>Selftest: the plan painted last, and the move column's words.</summary>
    internal FocusPlanner.Plan? PlanForTest { get; private set; }
    internal List<string> MoveLinesForTest { get; } = new();
    internal void SetWantForTest(string family, FocusPlanner.Want w) { _wants[family] = w; Build(); }
    internal void SetComboForTest(params string[] combo) { _combo.Clear(); _combo.AddRange(combo); LoadWants(); Build(); }
    internal ClassPicker PickerForTest => _pick;

    private void Build() { BuildTop(); BuildBody(); }

    // The what-if popup anchors on a bar chip: a pick redraws the body only,
    // the bar follows once the popup closes (owner, 30 Sep).
    private void BuildTop()
    {
        _top.Children.Clear();
        if (_rows is null || _focus is null || _stats is null) return;

        // ---- the bar: the one picker ----
        _pick.Set(_combo);
        _pick.Hint = _who.Length > 0 ? $"Prefilled from /who ({_who}) — change it for a what-if; each combo keeps its own marks." : _combo.Count > 0 ? "No /who yet — your last pick; type /who in game to prefill." : "No /who yet — pick your classes, or type /who in game.";
        if (_pick.Parent is Panel pp) pp.Children.Remove(_pick);
        if (_headRight.Parent is Panel hp) hp.Children.Remove(_headRight);
        // The picker on the left, the verdict beside it (owner, 1 Oct: the free space next to the picker).
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.Children.Add(_pick);
        Grid.SetColumn(_headRight, 1);
        bar.Children.Add(_headRight);
        _top.Children.Add(bar);
    }

    private void BuildBody()
    {
        _body.Children.Clear(); MoveLinesForTest.Clear(); _headRight.Content = null;
        if (_rows is null || _focus is null || _stats is null) return;
        if (Combo.Count == 0)
        {
            _body.Children.Add(new TextBlock { Text = "Pick your classes above (or type /who in game) — the planner needs them to know which foci and exaltations are yours.", Foreground = Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            return;
        }
        var plan = FocusPlanner.Build(_rows, _focus, _stats, Combo, _level, _wants);
        PlanForTest = plan;

        // ---- the verdict ----
        var v = new StackPanel();
        v.Children.Add(new TextBlock { Text = $"{string.Join("/", Combo)} · LEVEL {(_level > 0 ? _level.ToString() : "?")} · {plan.Pool.Select(e => e.Line).Distinct().Count()} FOCUS EXALTATIONS OWNED", Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        var head = new TextBlock { Margin = new Thickness(0, 2, 0, 0) };
        head.Inlines.Add(new Run($"{plan.Moves} move{(plan.Moves == 1 ? "" : "s")}") { Foreground = Text, FontSize = 15, FontWeight = FontWeights.Bold });
        head.Inlines.Add(new Run($"  — needs {plan.NeedsPlaced} of {plan.Needs} socketed · nice-to-haves {plan.NicesPlaced} of {plan.Nices}") { Foreground = Hint, FontSize = 12 });
        v.Children.Add(head);
        var decayed = plan.Sockets.Where(p => p.Plan is not null && FocusPlanner.Strength(p.Plan.Tier, _level) < 1).Select(p => $"{p.Plan!.Family.Name} {p.Plan.TierLabel}").Distinct().ToList();
        string line = plan.Moves == 0 && plan.NeedsPlaced == plan.Needs ? "Your sockets already hold the best you own." : plan.Moves == 0 ? "Nothing to move — the needs you can't socket are in the notes below." : "The plan is below, socket by socket.";
        if (decayed.Count > 0) line += $" Running at part strength past its level cap: {string.Join(", ", decayed)} — the next tier is worth hunting (see below).";
        else if (_level > 0) line += " Every focus in the plan runs at full strength at your level.";
        if (!plan.Exact) line += " (Too many ways to lay this out to try them all — this is the greedy plan.)";
        v.Children.Add(new TextBlock { Text = line, Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        _headRight.Content = new Border { Background = Surface, BorderBrush = GoldEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = v };

        _body.Children.Add(Panel("WHAT YOU WANT", "Need beats any number of nice-to-haves · the best tier you own · a tier past its level cap counts at part strength", Families(plan)));
        _body.Children.Add(Panel("THE PLAN · SOCKET BY SOCKET", $"{plan.Sockets.Count(s => !s.Socket.Fixed)} open sockets · {plan.Moves} move{(plan.Moves == 1 ? "" : "s")}", PlanTable(plan)));
        foreach (var n in Notes(plan)) _body.Children.Add(n);
        _body.Children.Add(new TextBlock
        {
            Foreground = Faint, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "Rules (eqlwiki, Exaltations): a worn item's Focus socket opens at +1, one per item; an exaltation keeps its source item's slot — a Face item's focus fits a Face socket; the same focus never stacks, so only its best tier counts. "
                 + "The pool is every focus exaltation you own: in your worn items' sockets, loose on the key ring, and in stored items' sockets. "
                 + "Assumed, not stated: an exaltation keeps its source item's class restrictions; an Any Slot item's socket takes any exaltation (they're used last); a tier past its level cap fades toward 30% over 30 levels. "
                 + "Reciting the moves is on you — the plan doesn't know about copper.",
        });
    }

    private UIElement Families(FocusPlanner.Plan plan)
    {
        var wrap = new WrapPanel();
        var groups = new (string Kind, string Title, string[] Fams)[]
        {
            ("dmg", "DAMAGE", new[] { "Improved Damage", "Burning Affliction" }),
            ("heal", "HEALING", new[] { "Improved Healing" }),
            ("mana", "MANA", new[] { "Mana Preservation", "Affliction Efficiency" }),
            ("speed", "CAST SPEED", new[] { "Spell Haste", "Affliction Haste", "Enhancement Haste" }),
            ("util", "UTILITY", new[] { "Extended Enhancement", "Reagent Conservation", "Extended Range" }),
            ("pet", "PETS", new[] { "Summoning Efficiency", "Summoning Haste", "Reanimation Efficiency", "Reanimation Haste" }),
            ("song", "BARD INSTRUMENTS · ONLY WITH BRD", new[] { "String Resonance", "Percussion Resonance", "Brass Resonance", "Wind Resonance" }),
        };
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, title, fams) in groups)
        {
            var rows = fams.Select(n => plan.Families.FirstOrDefault(f => f.Family.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).Where(f => f is not null && f.Shown).ToList();
            foreach (var f in rows) placed.Add(f!.Family.Name);
            if (rows.Count == 0) continue;
            wrap.Children.Add(FamilyBlock(title, rows!));
        }
        var rest = plan.Families.Where(f => f.Shown && !placed.Contains(f.Family.Name)).ToList();
        if (rest.Count > 0) wrap.Children.Add(FamilyBlock("OTHER", rest));
        var sp = new StackPanel();
        sp.Children.Add(wrap);
        if (!Combo.Contains("BRD", StringComparer.OrdinalIgnoreCase))
            sp.Children.Add(new TextBlock { Text = "The four bard instrument foci stay hidden: this combo has no BRD.", Foreground = Faint, FontSize = 10.5, Margin = new Thickness(0, 6, 0, 0) });
        return sp;
    }

    private Border FamilyBlock(string title, List<FocusPlanner.FamilyRow> rows)
    {
        var sp = new StackPanel { Width = 300, Margin = new Thickness(0, 0, 18, 6) };
        sp.Children.Add(new TextBlock { Text = title, Foreground = Faint, FontSize = 9, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 2) });
        foreach (var f in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { TextWrapping = TextWrapping.Wrap };
            name.Inlines.Add(new Run(f.Family.Name) { Foreground = Text, FontSize = 12, FontWeight = FontWeights.SemiBold });
            name.Inlines.Add(new Run("  " + f.Family.Kind) { Foreground = Faint, FontSize = 10.5 });
            left.Children.Add(name);
            var own = new TextBlock { FontSize = 10.5, TextWrapping = TextWrapping.Wrap };
            if (f.Best is null) own.Inlines.Add(new Run("you own none") { Foreground = Faint });
            else
            {
                own.Inlines.Add(new Run("best: ") { Foreground = Hint });
                own.Inlines.Add(new Run(f.Best.TierLabel) { Foreground = Dim, FontWeight = FontWeights.SemiBold });
                own.Inlines.Add(new Run($" ({f.Best.Name.Replace(" (Exaltation)", "")}) · {(f.Best.Slots.Count > 0 ? string.Join("/", f.Best.Slots.Select(s => s.ToLowerInvariant())) : "slot unknown")}") { Foreground = Hint });
                if (FocusPlanner.Strength(f.Best.Tier, _level) < 1) own.Inlines.Add(new Run($" · decays past L{f.Best.Tier.LevelCap}") { Foreground = Warn });
                if (f.Want != FocusPlanner.Want.Off && f.Placed is null) own.Inlines.Add(new Run(" · no socket left") { Foreground = Warn });
            }
            left.Children.Add(own);
            g.Children.Add(left);
            var seg = new Segmented(new[] { new Segmented.Option("need", "Need", "Must be socketed — outweighs any number of nice-to-haves"), new Segmented.Option("nice", "Nice", "Fills a socket left over"), new Segmented.Option("off", "Off", "Not wanted for this combo") },
                f.Want.ToString().ToLowerInvariant(), "#E8C15A") { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            string fam = f.Family.Name;
            seg.Changed += id => { _wants[fam] = id == "need" ? FocusPlanner.Want.Need : id == "nice" ? FocusPlanner.Want.Nice : FocusPlanner.Want.Off; _prefs?.Set(WantsKey, FocusPlanner.Pack(_wants)); BuildBody(); };
            Grid.SetColumn(seg, 1); g.Children.Add(seg);
            sp.Children.Add(new Border { Child = g, Padding = new Thickness(0, 5, 0, 5), BorderBrush = Row, BorderThickness = new Thickness(0, 0, 0, 1) });
        }
        return new Border { Child = sp };
    }

    private UIElement PlanTable(FocusPlanner.Plan plan)
    {
        var g = new Grid();
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        g.RowDefinitions.Add(new RowDefinition());
        string[] heads = { "SOCKET · ITEM", "NOW", "PLAN", "WHAT TO DO" };
        for (int c = 0; c < heads.Length; c++) Cell(g, new TextBlock { Text = heads[c], Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold }, 0, c);
        int row = 1;
        var byFam = plan.Sockets.Where(p => p.Plan is not null).ToDictionary(p => p.Plan!.Family, p => p);
        var idle = new List<string>();
        foreach (var p in plan.Sockets)
        {
            if (p.Now is null && p.Plan is null) { idle.Add(p.Socket.Label); continue; }
            bool change = p.Plan is not null && (p.Now is null || p.Now.Line != p.Plan.Line);
            g.RowDefinitions.Add(new RowDefinition());
            if (change) { var band = new Border { Background = ChangeBg, BorderBrush = Green, BorderThickness = new Thickness(3, 0, 0, 0) }; Grid.SetRow(band, row); Grid.SetColumnSpan(band, 4); g.Children.Add(band); }
            var sock = new StackPanel();
            sock.Children.Add(new TextBlock { Text = p.Socket.Label, Foreground = Text, FontSize = 12, FontWeight = FontWeights.SemiBold });
            var itemLine = ItemChips.FromDumpName(p.Socket.Item, _stats!, Hint);
            itemLine.Margin = new Thickness(0, 2, 0, 0);
            foreach (var ch in itemLine.Children) if (ch is TextBlock t && t.FontSize > 11) t.FontSize = 11;
            sock.Children.Add(itemLine);
            Cell(g, sock, row, 0);
            bool shadow = p.Now is not null && p.Plan is null && byFam.TryGetValue(p.Now.Family, out var elsewhere) && elsewhere.Socket.Label != p.Socket.Label;
            Cell(g, Fx(p.Now, shadow), row, 1);
            Cell(g, p.Plan is null ? new TextBlock { Text = "—", Foreground = Faint, FontSize = 12 } : Fx(p.Plan, false), row, 2);
            var mv = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            string moveText;
            if (p.Socket.Fixed) { moveText = "fixed — a +0 item's own focus stays on it"; mv.Inlines.Add(new Run(moveText) { Foreground = Faint, FontStyle = FontStyles.Italic }); }
            else if (!change && p.Plan is not null) { moveText = "keep"; mv.Inlines.Add(new Run(moveText) { Foreground = Faint }); }
            else if (p.Plan is not null)
            {
                moveText = FocusPlanner.MoveText(p);
                if (p.Now is not null)
                {
                    // Two steps, two colours: the one coming out, then the one going in.
                    int cut = moveText.IndexOf(" Then: ", StringComparison.Ordinal);
                    mv.Inlines.Add(new Run(moveText[..cut]) { Foreground = Warn });
                    mv.Inlines.Add(new LineBreak());
                    string rest = moveText[(cut + " Then: ".Length)..];
                    mv.Inlines.Add(new Run("Then " + rest) { Foreground = Green });
                }
                else mv.Inlines.Add(new Run(moveText) { Foreground = Green });
            }
            else if (shadow)
            {
                var e = byFam[p.Now!.Family];
                moveText = $"shadowed by {e.Plan!.Family.Name} {e.Plan.TierLabel} in {e.Socket.Label} — nothing else you want fits {p.Socket.SlotKey.ToLowerInvariant()}";
                mv.Inlines.Add(new Run(moveText) { Foreground = Warn });
            }
            else
            {
                var want = plan.Families.FirstOrDefault(f => f.Family == p.Now!.Family)?.Want ?? FocusPlanner.Want.Off;
                moveText = want == FocusPlanner.Want.Off ? $"keep — {p.Now!.Family.Name} is Off; nothing wanted fits {p.Socket.SlotKey.ToLowerInvariant()}" : "keep";
                mv.Inlines.Add(new Run(moveText) { Foreground = Faint, FontStyle = want == FocusPlanner.Want.Off ? FontStyles.Italic : FontStyles.Normal });
            }
            MoveLinesForTest.Add($"{p.Socket.Label}: {moveText}");
            Cell(g, mv, row, 3);
            row++;
        }
        var sp = new StackPanel();
        sp.Children.Add(g);
        if (idle.Count > 0)
        {
            var t = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            t.Inlines.Add(new Run($"{idle.Count} more socket{(idle.Count == 1 ? " is" : "s are")} open, and nothing you own fits {(idle.Count == 1 ? "it" : "them")}: ") { Foreground = Hint, FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new Run(string.Join(" · ", idle) + ". The hunt list below names what would.") { Foreground = Faint });
            sp.Children.Add(t);
        }
        if (plan.Sockets.Count == 0)
            sp.Children.Add(new TextBlock { Text = "No open focus sockets — a worn item's Focus socket opens at +1.", Foreground = Hint, FontSize = 12 });
        else if (plan.Moves > 0)
            sp.Children.Add(new TextBlock { Text = "Green rows are the moves. In game: open the item, drag the exaltation out of its Focus socket (it lands on the key ring's Exaltations tab), then drag it into the other item's Focus socket.", Foreground = Faint, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        return sp;
    }

    private UIElement Fx(FocusPlanner.Exalt? e, bool shadow)
    {
        if (e is null) return new TextBlock { Text = "empty", Foreground = Faint, FontSize = 10.5 };
        var want = _wants.TryGetValue(e.Family.Name, out var w) ? w : FocusPlanner.Want.Off;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = want == FocusPlanner.Want.Need ? Gold : want == FocusPlanner.Want.Nice ? NiceDot : OffDot });
        var tb = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ToolTip = $"{e.Name} — {e.Tier.Description}" };
        tb.Inlines.Add(new Run($"{e.Family.Name} {e.TierLabel}") { Foreground = shadow || want == FocusPlanner.Want.Off ? Faint : Dim });
        if (FocusPlanner.Strength(e.Tier, _level) < 1) tb.Inlines.Add(new Run($"  decays past L{e.Tier.LevelCap}") { Foreground = Warn, FontSize = 10.5 });
        sp.Children.Add(tb);
        return sp;
    }

    private IEnumerable<UIElement> Notes(FocusPlanner.Plan plan)
    {
        var list = new List<UIElement>();
        if (plan.Conflicts.Count > 0)
        {
            var tb = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Dim };
            foreach (var c in plan.Conflicts)
            {
                if (tb.Inlines.Count > 0) tb.Inlines.Add(new LineBreak());
                tb.Inlines.Add(new Run(c.Socket.Label + ": ") { Foreground = Text, FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new Run(string.Join(", ", c.Wanting.Select(e => $"{e.Family.Name} {e.TierLabel} ({Want(e)})")) + $" all fit {c.Socket.SlotKey.ToLowerInvariant()}. "));
                tb.Inlines.Add(new Run(c.Winner is null ? "The socket went to nothing — every one of them fits somewhere better." : $"{c.Winner.Family.Name} {c.Winner.TierLabel} takes it") { Foreground = c.Winner is null ? Hint : Green });
                var losers = c.Wanting.Where(e => c.Winner is null || e.Family != c.Winner.Family).Where(e => plan.Families.FirstOrDefault(f => f.Family == e.Family)?.Placed is null).ToList();
                if (losers.Count > 0) tb.Inlines.Add(new Run($"; {string.Join(" and ", losers.Select(e => e.Family.Name))} go{(losers.Count == 1 ? "es" : "")} without. Mark {c.Winner?.Family.Name ?? "the winner"} Nice to see the other way round.") { Foreground = Hint });
                else tb.Inlines.Add(new Run(".") { Foreground = Hint });
            }
            list.Add(Note("WHO GETS THE SOCKET", tb, Warn));
        }
        if (plan.Hunts.Count > 0)
        {
            var tb = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Dim };
            foreach (var g in plan.Hunts.GroupBy(h => h.Family))
            {
                if (tb.Inlines.Count > 0) tb.Inlines.Add(new LineBreak());
                var first = g.First();
                tb.Inlines.Add(new Run($"{first.Tier.Effect}") { Foreground = Text, FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new Run($" ({first.Why}): {string.Join(", ", g.Select(h => $"{h.Item} · {h.Slot}"))}."));
                var open = g.FirstOrDefault(h => h.OpenSocket.Length > 0);
                if (open is not null) tb.Inlines.Add(new Run($" Your {open.OpenSocket} socket is open for it.") { Foreground = Green });
            }
            list.Add(Note("WORTH HUNTING", tb, Blue));
        }
        if (plan.Foreign.Count > 0)
            list.Add(Note("NOT FOR THIS COMBO", new TextBlock { Text = string.Join(" · ", plan.Foreign.Select(e => $"{e.Name.Replace(" (Exaltation)", "")} ({e.Family.Name} {e.TierLabel}, {e.Classes})")), FontSize = 12, Foreground = Hint, TextWrapping = TextWrapping.Wrap }, Faint));
        if (plan.Unplaceable.Count > 0)
            list.Add(Note("SLOT UNKNOWN", new TextBlock { Text = "The wiki doesn't say which slot these came from, so the planner can't place them: " + string.Join(" · ", plan.Unplaceable.Select(e => e.Name.Replace(" (Exaltation)", ""))), FontSize = 12, Foreground = Hint, TextWrapping = TextWrapping.Wrap }, Faint));
        return list;
    }

    private string Want(FocusPlanner.Exalt e) => (_wants.TryGetValue(e.Family.Name, out var w) ? w : FocusPlanner.Want.Off).ToString();

    // ---- bits ----

    private static Border Panel(string title, string note, UIElement body)
    {
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var n = new TextBlock { Text = note, Foreground = Hint, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(n, Dock.Right); head.Children.Add(n);
        head.Children.Add(new TextBlock { Text = title, Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        sp.Children.Add(head); sp.Children.Add(body);
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 10), Child = sp };
    }

    private static Border Note(string title, UIElement body, Brush titleFg)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Foreground = titleFg, FontSize = 9.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) });
        sp.Children.Add(body);
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 0, 0, 8), Child = sp };
    }

    private static void Cell(Grid g, UIElement el, int row, int col)
    {
        var b = new Border { BorderBrush = Row, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(col == 0 ? 6 : 12, 6, 6, 6), Child = el };
        if (el is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Left; fe.VerticalAlignment = VerticalAlignment.Center; }
        Grid.SetRow(b, row); Grid.SetColumn(b, col); g.Children.Add(b);
    }
}
