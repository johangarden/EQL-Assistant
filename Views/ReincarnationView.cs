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
/// The Tools window's Reincarnation page (26 Nov, ahead of Kunark): what matters to you on the left
/// (a mode, the priority trio, classes and races dragged most → least important), the computed road
/// in the middle (one row per run: race, trio, XP penalty and what sets it — tick a run when it is
/// done), and the rank pips on the right (click one to say what was finished before you started
/// tracking). Every change saves and recomputes. Themed by hand; no stock controls.
/// </summary>
public sealed class ReincarnationView : DockPanel
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Surface = F("#1B2130"), Line = F("#2A3347"), Edge = F("#3A4560"), RowLine = F("#212A3C"), NextBg = F("#1A2436"), ChipBg = F("#1A2130"),
        Text = F("#E6ECF5"), Dim = F("#C9D4E3"), Hint = F("#7F93AD"), Faint = F("#5C6B82"),
        Gold = F("#E8C15A"), GoldEdge = F("#7A5B22"), GoldBg = F("#2A2210"), Teal = F("#4FC3F7"), TealBg = F("#16283E"), TealEdge = F("#2B5C7C"),
        Green = F("#7BD88F"), GreenEdge = F("#2C5A3A"), Amber = F("#E8A75A"), AmberEdge = F("#7A4E22"), Red = F("#FF8A80"), RedEdge = F("#7A2E33"), NoteFg = F("#E3CF9A");
    private const string DragFormat = "eql-reincarnation-rank";
    // The left card's width is the picker's: four chips and their gaps, the picker's own frame, the
    // card's frame and gap, and room for the card's scrollbar when the window is short.
    private const double TrioChip = 54, ScrollbarRoom = 18;
    internal const double LeftWidth = 4 * (TrioChip + 5) + 22 + 26 + 10 + ScrollbarRoom;

    private Reincarnation? _store;
    private Reincarnation.State? _s;
    private HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _who = new();
    private readonly StackPanel _top = new();
    private readonly Grid _cols = new();
    private ScrollViewer? _planScroll;
    private Segmented? _seg;
    private ClassPicker? _trio;

    public ReincarnationView()
    {
        SetDock(_top, Dock.Top);
        Children.Add(_top);
        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LeftWidth) });
        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(338) });
        Children.Add(_cols);
    }

    /// <summary>Attach the character's plan: /who's classes lead a first visit, Race unlocks says
    /// which races wait.</summary>
    public void Init(Reincarnation store, string charKey, IEnumerable<string> whoClasses, RaceBook? races)
    {
        _store = store;
        _who = whoClasses.Select(c => c.ToUpperInvariant()).ToList();
        var (locked, unlocked) = Reincarnation.RacesFrom(races);
        _locked = locked;
        _s = store.For(charKey, _who, unlocked);
        Build();
    }

    // ---- selftest ---------------------------------------------------------------------
    internal Reincarnation.Plan? PlanForTest { get; private set; }
    internal List<string> TileLines { get; } = new();
    internal int RowCountForTest { get; private set; }
    internal void ClickRunForTest(int number) { if (PlanForTest?.Runs.FirstOrDefault(r => r.Number == number) is { } r) Toggle(r); }
    internal void SetModeForTest(string mode) { if (_s is null) return; _s.Mode = mode; Changed(); }
    internal void MoveForTest(string code, int toIndex) => MoveRank(code, toIndex);
    internal void PickTrioForTest(params string[] trio) { if (_s is null) return; Reincarnation.LeadWith(_s.ClassOrder, trio); Changed(); }
    internal void ClickPipForTest(string code, int n) => Pip(code, n);
    /// <summary>What a picker needs across, and the room the left card leaves it with its scrollbar showing
    /// (card gap 10, frame 2, padding 24, the themed scrollbar 11).</summary>
    internal static double TrioRoom => LeftWidth - 10 - 2 - 24 - 11;
    internal static double NeedsAcross(ClassPicker picker) { picker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return picker.DesiredSize.Width - picker.Margin.Left - picker.Margin.Right; }
    internal double TrioNeedsForTest => _trio is null ? double.MaxValue : NeedsAcross(_trio);

    private void Changed() { _store?.Save(); Build(); }

    // ---- what a class or race gains: the mouse-over on every chip (owner, 26 Nov) -------
    /// <summary>Hang the gains card on a chip. Built when it opens — a page holds a few hundred chips.</summary>
    private void Gains(FrameworkElement el, string code, Func<string>? lead = null)
    {
        el.ToolTip = " "; // something to open; the card replaces it on the way up
        ToolTipService.SetShowDuration(el, 60000);
        ToolTipService.SetInitialShowDelay(el, 250);
        el.ToolTipOpening += (_, _) => el.ToolTip = GainsCard(code, lead?.Invoke() ?? "", out _);
    }

    /// <summary>Selftest: the card's words for a class or race, top to bottom.</summary>
    internal string TipTextForTest(string code, string lead = "") { GainsCard(code, lead, out string text); return text; }
    internal UIElement GainsCardForTest(string code, string lead = "") => GainsCard(code, lead, out _);
    private const double CardWidth = 340;

    /// <summary>Selftest: laid out at the card's width, nothing on the card is wider than the card —
    /// every line wraps and every rank cell sits whole on a row of its own if it must.</summary>
    internal bool GainsFitForTest(string code)
    {
        if (GainsCard(code, "", out _) is not StackPanel card) return false;
        card.Measure(new Size(CardWidth, double.PositiveInfinity));
        card.Arrange(new Rect(0, 0, CardWidth, card.DesiredSize.Height));
        bool Fits(UIElement el) => el.DesiredSize.Width <= CardWidth + 0.5;
        return card.Children.OfType<UIElement>().All(Fits)
            && card.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<FrameworkElement>())
                .All(c => c.DesiredSize.Width <= CardWidth + 0.5 && c.TranslatePoint(new Point(c.ActualWidth, 0), card).X <= CardWidth + 0.5);
    }

    private UIElement GainsCard(string code, string lead, out string text)
    {
        var said = new System.Text.StringBuilder();
        var sp = new StackPanel { MaxWidth = CardWidth };
        TextBlock Line(string t, Brush fg, double size = 11.5, bool bold = false, double top = 0)
        {
            said.AppendLine(t);
            var tb = new TextBlock { Text = t, Foreground = fg, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            sp.Children.Add(tb);
            return tb;
        }
        var a = Reincarnation.AbilityOf(code);
        bool isRace = Reincarnation.Races.Contains(code, StringComparer.OrdinalIgnoreCase);
        int have = _s?.Count(code) ?? 0;
        Line(a?.Name ?? (isRace ? Reincarnation.RaceName(code) : code), Text, 13, bold: true);
        if (lead.Length > 0) Line(lead, Dim, 11.5, top: 1);
        if (isRace && _locked.Contains(code)) Line("Not unlocked yet — see Race unlocks.", Red, 11, top: 1);
        if (a is null)
        {
            Line("What its reincarnation ranks give is not in the data yet.", Hint, 11, top: 6);
            text = said.ToString();
            return sp;
        }
        Line("PASSIVE — GROWS WITH EACH RANK", Faint, 9.5, bold: true, top: 8);
        Line(a.Passive, Dim, top: 2);
        // the three ranks side by side, the ones you have in gold — WRAPPING: a race's ranks can be long
        // ("+2 HP Gen +5 AC"), and three of those in one row ran off the card (owner's screenshots, 26 Nov)
        var ranks = new WrapPanel { Margin = new Thickness(0, 5, 0, -4) };
        string[] roman = { "I", "II", "III" };
        for (int i = 0; i < Math.Min(Reincarnation.Ranks, a.Ranks.Count); i++)
        {
            bool got = have > i;
            said.AppendLine($"Rank {roman[i]}: {a.Ranks[i]}{(got ? " (earned)" : "")}");
            var cell = new TextBlock { FontSize = 11 };
            cell.Inlines.Add(new Run(roman[i] + "  ") { Foreground = got ? Gold : Faint, FontSize = 9.5, FontWeight = FontWeights.Bold });
            cell.Inlines.Add(new Run(a.Ranks[i]) { Foreground = got ? Gold : Dim, FontWeight = FontWeights.SemiBold });
            ranks.Children.Add(new Border { Child = cell, Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 0, 5, 4), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = got ? GoldEdge : Edge, Background = got ? GoldBg : Brushes.Transparent });
        }
        sp.Children.Add(ranks);
        if (a.IsClass && a.Active.Length > 0)
        {
            Line("RANK III ALSO GRANTS — ACTIVATED", Faint, 9.5, bold: true, top: 9);
            Line("★ " + a.Active, Gold, 12, bold: true, top: 2);
            if (a.ActiveText.Length > 0) Line(a.ActiveText, Dim, top: 1);
            if (Reincarnation.Abilities.SharedCooldownSec > 0)
                Line($"All activated reincarnation abilities share one {Reincarnation.Abilities.SharedCooldownSec} s timer.", Hint, 11, top: 3);
        }
        else if (!a.IsClass) Line("Races have no activated ability — the passive is the whole gain.", Hint, 11, top: 7);
        Line(have == 0 ? "You have none of its ranks yet." : have >= Reincarnation.Ranks ? "You have all three ranks." : $"You have {have} of its 3 ranks.", have >= Reincarnation.Ranks ? Gold : Hint, 11, top: 7);
        if (Reincarnation.Abilities.Notice.Length > 0) Line(Reincarnation.Abilities.Notice, Faint, 10.5, top: 2);
        text = said.ToString();
        return sp;
    }

    private void Toggle(Reincarnation.PlanRun run)
    {
        if (_s is null) return;
        if (run.Done) { if (run.Number - 1 < _s.Done.Count) _s.Done.RemoveAt(run.Number - 1); }
        else _s.Done.Add(new Reincarnation.DoneRun { Race = run.Race, Classes = run.Classes.ToList(), At = DateTime.Now });
        Changed();
    }

    private void MoveRank(string code, int toIndex)
    {
        if (_s is null) return;
        var order = Reincarnation.Classes.Contains(code, StringComparer.OrdinalIgnoreCase) ? _s.ClassOrder : _s.RaceOrder;
        Reincarnation.Move(order, code, toIndex);
        Changed();
    }

    private void Pip(string code, int n)
    {
        if (_s is null) return;
        _s.SetCount(code, _s.Count(code) == n ? n - 1 : n);
        Changed();
    }

    // ---- the page ---------------------------------------------------------------------
    public void Build()
    {
        double keep = _planScroll?.VerticalOffset ?? 0;
        _top.Children.Clear(); _cols.Children.Clear(); TileLines.Clear(); RowCountForTest = 0;
        if (_s is null) return;
        var plan = Reincarnation.Build(_s, _locked);
        PlanForTest = plan;

        var note = new TextBlock { Foreground = NoteFg, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        note.Inlines.Add(new Run("Before Kunark (8 Dec): ") { FontWeight = FontWeights.Bold });
        note.Inlines.Add(new Run("the rule as community planners report it from the Kunark FAQ — each finished reincarnation adds "));
        note.Inlines.Add(new Run("+10% XP needed") { FontWeight = FontWeights.Bold });
        note.Inlines.Add(new Run(" to that class or race, the "));
        note.Inlines.Add(new Run("highest of the four") { FontWeight = FontWeights.Bold });
        note.Inlines.Add(new Run(" sets your rate (cap +300%), AA XP is never touched. Unverified in game; runs are ticked by hand until the log shows a line to read."));
        _top.Children.Add(new Border { Background = GoldBg, BorderBrush = GoldEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 7), Margin = new Thickness(0, 0, 0, 10), Child = note });

        // ---- tiles ----
        int doneRuns = plan.Runs.Count(r => r.Done);
        int classRanks = Reincarnation.Classes.Sum(c => Math.Min(Reincarnation.Ranks, _s.Count(c))), raceRanks = Reincarnation.Races.Sum(r => Math.Min(Reincarnation.Ranks, _s.Count(r)));
        int classesDone = Reincarnation.Classes.Count(c => _s.Count(c) >= Reincarnation.Ranks), racesDone = Reincarnation.Races.Count(r => _s.Count(r) >= Reincarnation.Ranks);
        var next = plan.Next;
        var sheet = Reincarnation.SheetOrder();
        var tiles = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, -10, 10) };
        tiles.Children.Add(Tile("RUNS DONE", $"{doneRuns}", $" / {plan.Runs.Count}", $"{plan.Runs.Count - doneRuns} to go"));
        tiles.Children.Add(Tile("CLASS RANKS", $"{classRanks}", " / 48", $"{classesDone} of 16 classes finished"));
        tiles.Children.Add(Tile("RACE RANKS", $"{raceRanks}", " / 48", $"{racesDone} of 16 races finished"));
        tiles.Children.Add(next is null ? Tile("NEXT RUN", "Done", "", "Every class and race at rank 3")
            : Tile("NEXT RUN", $"+{next.Penalty}%", " XP", $"{next.Race} · {string.Join(" / ", next.Classes)}"));
        tiles.Children.Add(Tile("THIS PLAN", $"+{plan.Average:0}%", " average", $"peak +{plan.Peak}% · the community sheet: +{sheet.Average:0}%, peak +{sheet.Peak}%"));
        _top.Children.Add(tiles);

        var left = LeftCard(); Grid.SetColumn(left, 0); _cols.Children.Add(left);
        var mid = PlanCard(plan); Grid.SetColumn(mid, 1); _cols.Children.Add(mid);
        var right = ProgressCard(plan); Grid.SetColumn(right, 2); _cols.Children.Add(right);
        if (keep > 0) Dispatcher.BeginInvoke(new Action(() => _planScroll?.ScrollToVerticalOffset(keep)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private Border Tile(string label, string big, string unit, string sub)
    {
        TileLines.Add($"{label}: {big}{unit} — {sub}");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        var b = new TextBlock { Margin = new Thickness(0, 1, 0, 0) };
        b.Inlines.Add(new Run(big) { Foreground = Text, FontSize = 20, FontWeight = FontWeights.SemiBold });
        b.Inlines.Add(new Run(unit) { Foreground = Hint, FontSize = 12 });
        sp.Children.Add(b);
        sp.Children.Add(new TextBlock { Text = sub, Foreground = Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 0, 10, 0), Child = sp };
    }

    private static Border CardOf(UIElement child, Thickness margin) =>
        new() { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 10, 12, 10), Margin = margin, Child = child };

    private static TextBlock Head(string caps, string tail = "")
    {
        var t = new TextBlock { Margin = new Thickness(0, 0, 0, 7), TextWrapping = TextWrapping.Wrap };
        t.Inlines.Add(new Run(caps) { Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        if (tail.Length > 0) t.Inlines.Add(new Run("  " + tail) { Foreground = Hint, FontSize = 11 });
        return t;
    }

    private static TextBlock Note(string text, Brush? fg = null) =>
        new() { Text = text, Foreground = fg ?? Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

    // ---- what matters to you ----------------------------------------------------------
    private UIElement LeftCard()
    {
        var s = _s!;
        var sp = new StackPanel();
        sp.Children.Add(Head("WHAT MATTERS TO YOU"));
        _seg = new Segmented(new[]
        {
            new Segmented.Option("low", "Lowest XP", "Always the cheapest run next; your priorities only break ties."),
            new Segmented.Option("balanced", "Balanced", "Your top picks get their ranks early unless staying on them costs clearly more."),
            new Segmented.Option("mine", "My picks first", "Your most important classes and races to rank 3 first, whatever the run costs."),
        }, s.Mode) { Margin = new Thickness(0, 0, 0, 6) };
        _seg.Changed += id => { s.Mode = id; Changed(); };
        sp.Children.Add(_seg);
        sp.Children.Add(Note(s.Mode switch
        {
            "low" => "Lowest XP: always the cheapest run next. Your order only decides between runs that cost the same.",
            "mine" => "My picks first: your most important classes and races reach rank 3 first, whatever each run costs.",
            _ => "Balanced: your top picks get their ranks early, unless staying on them costs clearly more than the cheapest run.",
        }));

        _trio = new ClassPicker("PRIORITY TRIO", chipWidth: TrioChip) { Margin = new Thickness(0, 0, 0, 10) };
        _trio.Set(s.ClassOrder.Take(3));
        _trio.Hint = _who.Count == 3 && s.ClassOrder.Take(3).All(_who.Contains) ? "Your /who trio leads." : "The first three of the list below.";
        _trio.Changed += combo => { if (combo.Count == 3) { Reincarnation.LeadWith(s.ClassOrder, combo); Changed(); } };
        _trio.ForEachChip((chip, cls) => Gains(chip, cls));
        sp.Children.Add(_trio);

        sp.Children.Add(Head("CLASSES, MOST → LEAST", "drag to reorder"));
        sp.Children.Add(RankChips(s.ClassOrder, isRace: false));
        sp.Children.Add(Head("RACES, MOST → LEAST", "drag to reorder"));
        sp.Children.Add(RankChips(s.RaceOrder, isRace: true));
        if (_locked.Count > 0)
        {
            var red = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            red.Inlines.Add(new Run("Red") { Foreground = Red, FontWeight = FontWeights.SemiBold });
            red.Inlines.Add(new Run(" = not unlocked yet (Race unlocks): planned last.") { Foreground = Hint });
            sp.Children.Add(red);
        }
        return CardOf(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = sp }, new Thickness(0, 0, 10, 0));
    }

    private WrapPanel RankChips(List<string> order, bool isRace)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        for (int i = 0; i < order.Count; i++)
        {
            string code = order[i];
            bool locked = isRace && _locked.Contains(code), lead = !isRace ? i < 3 : i == 0 && !locked;
            var tb = new TextBlock { FontSize = 10.5 };
            tb.Inlines.Add(new Run($"{i + 1} ") { Foreground = Faint, FontSize = 9.5 });
            tb.Inlines.Add(new Run(code) { Foreground = locked ? Red : lead ? Gold : Hint, FontWeight = FontWeights.SemiBold });
            var chip = new Border
            {
                Child = tb, Padding = new Thickness(5, 1, 7, 2), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                Background = lead ? GoldBg : ChipBg, BorderBrush = locked ? RedEdge : lead ? GoldEdge : Edge, Cursor = Cursors.SizeAll, AllowDrop = true,
            };
            int place = i + 1;
            Gains(chip, code, () => $"Priority {place} of {order.Count} — drag onto another to take its place.");
            Point? down = null;
            chip.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(this);
            chip.PreviewMouseLeftButtonUp += (_, _) => down = null;
            chip.PreviewMouseMove += (_, e) =>
            {
                if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - d.X) < 4 && Math.Abs(p.Y - d.Y) < 4) return;
                down = null;
                DragDrop.DoDragDrop(chip, new DataObject(DragFormat, (isRace ? "r:" : "c:") + code), DragDropEffects.Move);
            };
            bool Mine(DragEventArgs e) => e.Data.GetData(DragFormat) is string src && src.StartsWith(isRace ? "r:" : "c:", StringComparison.Ordinal) && src[2..] != code;
            var restEdge = chip.BorderBrush;
            chip.DragEnter += (_, e) => { if (Mine(e)) chip.BorderBrush = Teal; };
            chip.DragLeave += (_, _) => chip.BorderBrush = restEdge;
            chip.DragOver += (_, e) => { e.Effects = Mine(e) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            chip.Drop += (_, e) =>
            {
                e.Handled = true;
                if (!Mine(e)) return;
                string src = ((string)e.Data.GetData(DragFormat))[2..];
                int to = order.IndexOf(code);
                // the page is rebuilt once the drag's own loop has let go of the chips
                Dispatcher.BeginInvoke(new Action(() => MoveRank(src, to)));
            };
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    // ---- the plan ---------------------------------------------------------------------
    private static readonly double[] PlanCols = { 22, 26, 52, 158, 62 };

    private static Grid PlanRow()
    {
        var g = new Grid();
        foreach (double w in PlanCols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    private static T At<T>(T el, int col) where T : UIElement { Grid.SetColumn(el, col); return el; }

    private UIElement PlanCard(Reincarnation.Plan plan)
    {
        var dp = new DockPanel();
        var head = Head("THE PLAN", "tick a run when it is done · ★ = third rank, the class's activated ability");
        SetDock(head, Dock.Top); dp.Children.Add(head);

        var sum = new TextBlock { FontSize = 11, Foreground = Hint, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        void Bit(string a, string b, string c = "") { sum.Inlines.Add(new Run(a)); sum.Inlines.Add(new Run(b) { Foreground = Dim, FontWeight = FontWeights.SemiBold }); sum.Inlines.Add(new Run(c + "     ")); }
        Bit("", $"{plan.Runs.Count}", " runs");
        if (plan.TrioAt > 0) Bit("your trio at rank 3 after run ", $"{plan.TrioAt}");
        if (plan.AllClassesAt > 0) Bit("all 16 classes at rank 3 after run ", $"{plan.AllClassesAt}");
        SetDock(sum, Dock.Top); dp.Children.Add(sum);

        var titles = PlanRow();
        string[] names = { "", "#", "RACE", "TRIO", "XP PENALTY", "SET BY" };
        for (int i = 0; i < names.Length; i++)
            titles.Children.Add(At(new TextBlock { Text = names[i], Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, Margin = new Thickness(i == 1 ? 0 : 4, 0, 0, 5), TextAlignment = i == 1 ? TextAlignment.Right : TextAlignment.Left }, i));
        var titleBox = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = titles };
        SetDock(titleBox, Dock.Top); dp.Children.Add(titleBox);

        var rows = new StackPanel();
        var next = plan.Next;
        foreach (var run in plan.Runs)
        {
            var g = PlanRow();
            bool isNext = ReferenceEquals(run, next);
            var r = run;

            var box = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Background = run.Done ? GoldBg : Brushes.Transparent, BorderBrush = run.Done ? GoldEdge : Edge,
                ToolTip = run.Done ? "Done — click to take the tick back" : "Tick when this reincarnation is done",
                Child = run.Done ? new TextBlock { Text = "✓", Foreground = Gold, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) } : null,
            };
            box.MouseLeftButtonDown += (_, e) => { e.Handled = true; Toggle(r); };
            g.Children.Add(At(box, 0));
            g.Children.Add(At(new TextBlock { Text = run.Number.ToString(), Foreground = Faint, FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 1));

            var race = Chip(run.Race, run.RaceUse <= Reincarnation.Ranks ? run.RaceUse.ToString() : "×" + run.RaceUse, run.Locked ? 3 : run.RaceUse <= Reincarnation.Ranks ? 1 : 0);
            Gains(race, run.Race, () => r.RaceUse <= Reincarnation.Ranks ? $"Run {r.Number} earns its rank {r.RaceUse}." : $"Run {r.Number} is a repeat: use {r.RaceUse}, no new rank.");
            race.Margin = new Thickness(4, 0, 0, 0);
            g.Children.Add(At(race, 2));

            var trio = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < run.Classes.Count; i++)
            {
                int n = run.ClassUse[i];
                var c = Chip(run.Classes[i], n < Reincarnation.Ranks ? n.ToString() : n == Reincarnation.Ranks ? "★" : "×" + n, n <= Reincarnation.Ranks ? 1 : 0);
                Gains(c, run.Classes[i], () => n < Reincarnation.Ranks ? $"Run {r.Number} earns its rank {n}." : n == Reincarnation.Ranks ? $"Run {r.Number} earns its rank 3 — the activated ability." : $"Run {r.Number} is a repeat: use {n}, no new rank.");
                c.Margin = new Thickness(0, 0, 4, 0);
                trio.Children.Add(c);
            }
            g.Children.Add(At(trio, 3));

            var (penFg, penEdge) = run.Penalty == 0 ? (Green, GreenEdge) : run.Penalty <= 30 ? (Gold, GoldEdge) : run.Penalty <= 60 ? (Amber, AmberEdge) : (Red, RedEdge);
            g.Children.Add(At(new Border
            {
                MinWidth = 46, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(9),
                BorderBrush = penEdge, BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 2),
                Child = new TextBlock { Text = $"+{run.Penalty}%", Foreground = penFg, FontSize = 10.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center },
            }, 4));

            string why = run.Locked ? $"Unlock {Reincarnation.RaceName(run.Race)} first" : run.SetBy.Count > 0 ? string.Join(" · ", run.SetBy) : "—";
            var last = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
            if (isNext) { var tag = Tag("NEXT", Gold, GoldEdge); SetDock(tag, Dock.Right); last.Children.Add(tag); }
            last.Children.Add(new TextBlock { Text = why, Foreground = run.Locked ? Red : Faint, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            g.Children.Add(At(last, 5));

            var line = new Border
            {
                Child = g, Padding = new Thickness(0, 4, 0, 4), BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 1), Background = isNext ? NextBg : Brushes.Transparent, Opacity = run.Done ? 0.5 : 1,
                ToolTip = $"Run {run.Number}: {Reincarnation.RaceName(run.Race)} · {string.Join(" / ", run.Classes)} — +{run.Penalty}% XP"
                    + (run.SetBy.Count > 0 ? $" (set by {string.Join(", ", run.SetBy)})" : "") + (run.Earns.Any() ? $"\nEarns {string.Join(" · ", run.Earns)}" : "\nEarns no new rank"),
            };
            rows.Children.Add(line);
            RowCountForTest++;
        }
        if (plan.Runs.Count == 0) rows.Children.Add(Note("Nothing to plan."));
        _planScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = rows };
        dp.Children.Add(_planScroll);
        return CardOf(dp, new Thickness(0, 0, 10, 0));
    }

    /// <summary>A code with a small raised figure: 1 teal (earns a rank), 0 quiet (a repeat), 3 red (locked race), 2 plain.</summary>
    private static Border Chip(string code, string sup, int kind)
    {
        var (fg, edge, bg) = kind switch { 1 => (Teal, TealEdge, TealBg), 3 => (Red, RedEdge, (Brush)Brushes.Transparent), 2 => (Dim, Edge, ChipBg), _ => (Faint, Line, (Brush)Brushes.Transparent) };
        var tb = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        tb.Children.Add(new TextBlock { Text = code, FontSize = 10.5, Foreground = fg, FontWeight = FontWeights.SemiBold });
        if (sup.Length > 0) tb.Children.Add(new TextBlock { Text = sup, FontSize = 8.5, Foreground = fg, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, -1, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        return new Border { Child = tb, Padding = new Thickness(6, 1, 6, 2), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = edge, Background = bg, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Border Tag(string text, Brush fg, Brush edge) =>
        new() { Child = new TextBlock { Text = text, Foreground = fg, FontSize = 9, FontWeight = FontWeights.Bold }, Padding = new Thickness(5, 0, 5, 1), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = edge, VerticalAlignment = VerticalAlignment.Center };

    // ---- progress ---------------------------------------------------------------------
    private UIElement ProgressCard(Reincarnation.Plan plan)
    {
        var s = _s!;
        var sp = new StackPanel();
        sp.Children.Add(Head("PROGRESS", "★ n = the run that earns rank 3 · hover for the gains"));
        sp.Children.Add(Note("Click a pip to set what a class or race had finished before you started tracking."));
        int classRanks = Reincarnation.Classes.Sum(c => Math.Min(Reincarnation.Ranks, s.Count(c))), raceRanks = Reincarnation.Races.Sum(r => Math.Min(Reincarnation.Ranks, s.Count(r)));
        sp.Children.Add(Head("CLASSES", $"{classRanks} / 48 ranks"));
        var cg = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -12, 10) };
        foreach (string c in s.ClassOrder) cg.Children.Add(ProgressRow(c, plan, isRace: false));
        sp.Children.Add(cg);
        sp.Children.Add(Head("RACES", $"{raceRanks} / 48 ranks"));
        var rg = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -12, 10) };
        foreach (string r in s.RaceOrder) rg.Children.Add(ProgressRow(r, plan, isRace: true));
        sp.Children.Add(rg);

        if (Reincarnation.Abilities.Completionist.Length > 0)
            sp.Children.Add(Note("Completionist (" + string.Join(" / ", Reincarnation.Abilities.CompletionistRanks) + "): " + Reincarnation.Abilities.Completionist.Replace("This passive ability i", "I"), Hint));
        var src = new TextBlock { FontSize = 10.5, Foreground = Faint, TextWrapping = TextWrapping.Wrap };
        src.Inlines.Add(new Run("Hover a class or race for what its ranks give (eqlwiki). Rules: "));
        var link = new Run("Witty Waypoint's reincarnation planner ↗") { Foreground = Teal, Cursor = Cursors.Hand };
        link.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Reincarnation.RulesUrl) { UseShellExecute = true }); } catch { /* no browser */ }
        };
        src.Inlines.Add(link);
        src.Inlines.Add(new Run(" (checked against the Kunark FAQ). The comparison is the community's 48-run sheet."));
        sp.Children.Add(src);
        return CardOf(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = sp }, new Thickness(0));
    }

    private UIElement ProgressRow(string code, Reincarnation.Plan plan, bool isRace)
    {
        var s = _s!;
        int have = s.Count(code);
        bool locked = isRace && _locked.Contains(code);
        var dp = new DockPanel { LastChildFill = true };
        var chip = Chip(code, "", locked ? 3 : 2);
        chip.Width = 42; chip.Padding = new Thickness(0, 1, 0, 2);
        Gains(chip, code, () => plan.RankThreeAt.TryGetValue(code, out int at) && have < Reincarnation.Ranks ? $"Rank 3 comes with run {at} of this plan." : "");
        dp.Children.Add(chip);

        var pips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        for (int n = 1; n <= Reincarnation.Ranks; n++)
        {
            int at = n;
            var pip = new Ellipse
            {
                Width = 10, Height = 10, Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand, StrokeThickness = 1,
                Fill = have >= n ? Gold : Brushes.Transparent, Stroke = have >= n ? Gold : TealEdge,
                ToolTip = have >= n ? $"Rank {n} earned — click to take it back" : $"Click if {code} already has {n} finished reincarnation{(n == 1 ? "" : "s")}",
            };
            pip.MouseLeftButtonDown += (_, e) => { e.Handled = true; Pip(code, at); };
            pips.Children.Add(pip);
        }
        dp.Children.Add(pips);

        var tail = new TextBlock { FontSize = 10.5, Foreground = Hint, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (have > 0) { tail.Inlines.Add(new Run($"+{Math.Min(Reincarnation.CapPercent, have * Reincarnation.StepPercent)}%") { Foreground = Dim, FontWeight = FontWeights.SemiBold }); tail.Inlines.Add(new Run(" · ")); }
        if (have >= Reincarnation.Ranks) tail.Inlines.Add(new Run(have > Reincarnation.Ranks ? $"done ×{have}" : "done") { Foreground = Gold });
        else if (plan.RankThreeAt.TryGetValue(code, out int run)) { tail.Inlines.Add(new Run("★ ")); tail.Inlines.Add(new Run(run.ToString()) { Foreground = Dim, FontWeight = FontWeights.SemiBold }); }
        tail.ToolTip = (plan.RankThreeAt.TryGetValue(code, out int r3) && have < Reincarnation.Ranks ? $"Rank 3 comes with run {r3}. " : "") + (have > 0 ? $"{have} finished: +{Math.Min(Reincarnation.CapPercent, have * Reincarnation.StepPercent)}% XP needed whenever {code} is in the run" : "Untouched: no XP penalty from this one");
        dp.Children.Add(tail);
        return new Border { Child = dp, Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 12, 0), BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 1) };
    }
}
