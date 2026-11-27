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
    // WHAT YOU HAVE NOW grows to some fifty rows (32 lines + 16 abilities) — on top of the progress it
    // pushed the pips out of sight (owner, 26 Nov: "it needs to be next to the summary"). Wide enough,
    // it is a fourth column; narrower, the right card switches between the two so the plan keeps its width.
    internal const double WideAt = 1380, NowWidth = 300;
    internal static bool IsWide(double pageWidth) => pageWidth >= WideAt;
    private bool _wide;
    private bool? _wideForced;
    private string _right = "progress";
    private int _nowCount;
    private Segmented? _seg;
    private ClassPicker? _trio;

    public ReincarnationView()
    {
        SetDock(_top, Dock.Top);
        Children.Add(_top);
        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LeftWidth) });
        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(338 + ScrollGap) });
        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) }); // "what you have now", when the page is wide
        Children.Add(_cols);
        SizeChanged += (_, e) =>
        {
            bool wide = IsWide(e.NewSize.Width);
            if (_wideForced is null && wide != _wide) { _wide = wide; Build(); }
        };
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
    /// <summary>"four" columns side by side, or "tabs" in the right card.</summary>
    internal string LayoutForTest => _wide ? "four" : "tabs";
    internal string RightShownForTest => _wide ? "both" : _right;
    internal void SetWideForTest(bool wide) { _wideForced = wide; _wide = wide; Build(); }
    internal void ShowRightForTest(string id) { _right = id; Build(); }
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
    /// <summary>Selftest: the band that leads a gains card — its tag and its name with their sizes.</summary>
    internal string NameBandForTest(string code)
    {
        if (GainsCard(code, "", out _) is not StackPanel card || card.Children.Count == 0) return "";
        return card.Children[0] is Border { Child: DockPanel d } ? string.Join(" | ", d.Children.OfType<TextBlock>().Select(t => t.Text + " " + t.FontSize.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))) : "";
    }
    internal UIElement SourcesCardForTest(string label) => SourcesCard(Reincarnation.Sheet(_s!).First(r => r.Def.Label == label));

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
        // WHO the card is about leads it as a band (owner, 27 Nov: "highlight the race/class name more") —
        // on a stat line's card, where several givers stack, the bands are what the eye finds first.
        string name = a?.Name ?? (isRace ? Reincarnation.RaceName(code) : code);
        said.AppendLine(name);
        var band = new DockPanel();
        var kind = new TextBlock { Text = (isRace ? "RACE" : "CLASS") + " · " + code.ToUpperInvariant(), Foreground = Teal, FontSize = 9.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0) };
        SetDock(kind, Dock.Right);
        band.Children.Add(kind);
        band.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new Border { Child = band, Background = TealBg, BorderBrush = TealEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 0, 0, 3) });
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

    // ---- a stat line lights its givers (owner, 27 Nov: "when i mouse over a stat in the side panel
    // it highlights the class/race pill related to it") -------------------------------------------
    // Every pill of a class or race on the page — its place in the order, its progress row, each run
    // of the road that holds it — is registered as it is built; a line of the sheet lights the pills
    // of everyone behind it the moment the mouse is on it (no waiting for the card) and lets go as it
    // leaves. Teal, never gold: gold says GAINED here.
    private static readonly Brush LitBg = F("#1F4E73");
    private readonly Dictionary<string, List<Border>> _pills = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Code, Border Pill, Brush Edge, Brush Bg, List<(DependencyObject El, DependencyProperty Prop, object Was)> Inks)> _lit = new();
    private readonly Dictionary<string, Border> _statLines = new(StringComparer.Ordinal);
    private Border? _hot;

    private Border Pill(string code, Border chip)
    {
        if (!_pills.TryGetValue(code, out var list)) _pills[code] = list = new List<Border>();
        list.Add(chip);
        return chip;
    }

    private void Glow(Border line, string key, IReadOnlyList<string> codes)
    {
        _statLines[key] = line;
        line.MouseEnter += (_, _) => Light(line, codes);
        line.MouseLeave += (_, _) => Unlight();
    }

    private static IEnumerable<TextBlock> TextsOf(Border pill) => pill.Child switch
    {
        TextBlock tb => new[] { tb },
        Panel p => p.Children.OfType<TextBlock>(),
        _ => Array.Empty<TextBlock>(),
    };

    private void Light(Border line, IReadOnlyList<string> codes)
    {
        Unlight();
        _hot = line;
        line.Background = NextBg;
        // the Completionist part stands for every class
        foreach (string code in codes.SelectMany(c => c == Reincarnation.EveryClass ? Reincarnation.Classes : new[] { c }).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_pills.TryGetValue(code, out var pills)) continue;
            foreach (var pill in pills)
            {
                var inks = new List<(DependencyObject, DependencyProperty, object)>();
                void Ink(DependencyObject el, DependencyProperty prop) { inks.Add((el, prop, el.ReadLocalValue(prop))); el.SetValue(prop, Brushes.White); }
                foreach (var tb in TextsOf(pill))
                {
                    Ink(tb, TextBlock.ForegroundProperty);
                    // a run with its own colour (the code in the order chips) goes white with it; small
                    // print with its own size (the place number) keeps its quiet ink
                    foreach (var run in tb.Inlines.OfType<Run>())
                        if (run.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue && run.ReadLocalValue(TextElement.FontSizeProperty) == DependencyProperty.UnsetValue)
                            Ink(run, TextElement.ForegroundProperty);
                }
                _lit.Add((code, pill, pill.BorderBrush, pill.Background, inks));
                pill.BorderBrush = Teal;
                pill.Background = LitBg;
            }
        }
    }

    private void Unlight()
    {
        foreach (var l in _lit)
        {
            l.Pill.BorderBrush = l.Edge;
            l.Pill.Background = l.Bg;
            foreach (var (el, prop, was) in l.Inks)
                if (was == DependencyProperty.UnsetValue) el.ClearValue(prop); else el.SetValue(prop, was);
        }
        _lit.Clear();
        if (_hot is not null) { _hot.Background = Brushes.Transparent; _hot = null; }
    }

    /// <summary>Selftest / render: put the mouse on a line of the sheet (its label, or "★ " + an ability) and take it off.</summary>
    internal void HoverStatForTest(string key) { if (_statLines.TryGetValue(key, out var line)) line.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent }); }
    internal void LeaveStatForTest() { if (_hot is { } line) line.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent }); }
    /// <summary>Selftest: the lit pills, counted per class or race.</summary>
    internal Dictionary<string, int> LitForTest => _lit.GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    /// <summary>Selftest: how the first pill of a code looks — edge, fill and the ink of its code.</summary>
    internal string PillLookForTest(string code)
    {
        if (!_pills.TryGetValue(code, out var list) || list.Count == 0) return "";
        var pill = list[0];
        string C(Brush? b) => b is SolidColorBrush sc ? sc.Color.ToString() : "-";
        var tb = TextsOf(pill).FirstOrDefault();
        var ink = tb?.Inlines.OfType<Run>().LastOrDefault()?.Foreground ?? tb?.Foreground;
        return $"{C(pill.BorderBrush)} {C(pill.Background)} {C(ink)}";
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
        _pills.Clear(); _lit.Clear(); _statLines.Clear(); _hot = null;
        if (_s is null) return;
        var plan = Reincarnation.Build(_s, _locked);
        PlanForTest = plan;

        var note = new TextBlock { Foreground = NoteFg, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        note.Inlines.Add(new Run("Rulers of Kunark rules (the official FAQ): ") { FontWeight = FontWeights.Bold });
        note.Inlines.Add(new Run("you reincarnate at the level cap, "));
        note.Inlines.Add(new Run(Reincarnation.LevelCap.ToString()) { FontWeight = FontWeights.Bold });
        note.Inlines.Add(new Run(". Each finished reincarnation adds "));
        note.Inlines.Add(new Run("+10% XP needed") { FontWeight = FontWeights.Bold });
        note.Inlines.Add(new Run(" to that class or race, the "));
        note.Inlines.Add(new Run("slowest of the four") { FontWeight = FontWeights.Bold });
        note.Inlines.Add(new Run(" sets your rate (cap +300%), AA XP is never touched. A loadout stands at its lowest class's level. What the ranks give is eqlwiki's table, partly datamined; runs are ticked by hand until the log shows a line to read."));
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
            : next.Ready ? Tile("NEXT RUN", "Ready", "", $"{next.Race} · {string.Join(" / ", next.Classes)} — nothing to level")
            : Tile("NEXT RUN", $"+{next.Penalty}%", " XP", $"{next.Race} · {string.Join(" / ", next.Classes)}" + (next.From > 1 ? $" · from level {next.From}" : "")));
        int fullRuns = plan.Runs.Count(r => r.From <= 1), headRuns = plan.Runs.Count - fullRuns;
        tiles.Children.Add(Tile("THIS PLAN", $"+{plan.Average:0}%", " average", headRuns == 0
            ? $"peak +{plan.Peak}% · the community sheet: +{sheet.Average:0}%, peak +{sheet.Peak}%"
            : $"over {fullRuns} levellings from level 1 · peak +{plan.Peak}% · {headRuns} run{(headRuns == 1 ? "" : "s")} start higher"));
        _top.Children.Add(tiles);

        var left = LeftCard(); Grid.SetColumn(left, 0); _cols.Children.Add(left);
        var mid = PlanCard(plan); Grid.SetColumn(mid, 1); _cols.Children.Add(mid);
        var now = new StackPanel { Margin = new Thickness(0, 0, ScrollGap, 0) }; // the figures stand clear of the scrollbar (owner, 26 Nov)
        NowBlock(now);
        _cols.ColumnDefinitions[3].Width = new GridLength(_wide ? NowWidth : 0);
        if (_wide)
        {
            var right = CardOf(Scroller(ProgressPanel(plan)), new Thickness(0, 0, 10, 0)); Grid.SetColumn(right, 2); _cols.Children.Add(right);
            var have = CardOf(Scroller(now), new Thickness(0)); Grid.SetColumn(have, 3); _cols.Children.Add(have);
        }
        else
        {
            var dp = new DockPanel();
            var seg = new Segmented(new[]
            {
                new Segmented.Option("progress", "Progress", "The rank pips of every class and race."),
                new Segmented.Option("now", _nowCount > 0 ? $"Have now · {_nowCount}" : "Have now", "What your finished reincarnations give you right now. Widen the window to see both side by side."),
            }, _right, "#E8C15A") { Margin = new Thickness(0, 0, 0, 8) };
            seg.Changed += id => { _right = id; Build(); };
            SetDock(seg, Dock.Top); dp.Children.Add(seg);
            dp.Children.Add(Scroller(_right == "now" ? now : ProgressPanel(plan)));
            var right = CardOf(dp, new Thickness(0)); Grid.SetColumn(right, 2); _cols.Children.Add(right);
        }
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

    /// <summary>Room between a scrolling card's content and its scrollbar.</summary>
    private const double ScrollGap = 10;

    private static ScrollViewer Scroller(UIElement content) =>
        new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content };

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

        sp.Children.Add(LevelsBlock(s));

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

    // ---- where the classes stand (owner, 27 Nov: "atm i have all the classes at lvl 50 … but the line
    // says 0%") — one folded line in the card; open, a level and the sixteen classes to light ----------
    private static readonly Brush GreenBg = F("#15261B");
    private bool _levelsOpen;
    private int _levelPick;
    /// <summary>Selftest: the folded line's words.</summary>
    internal string LevelsLineForTest { get; private set; } = "";
    internal void SetLevelsForTest(int level, params string[] only)
    {
        if (_s is null) return;
        foreach (string c in only.Length > 0 ? only : Reincarnation.Classes) { if (level <= 1) _s.Levels.Remove(c); else _s.Levels[c] = Math.Min(level, Reincarnation.LevelCap); }
        if (level > 1) _levelPick = Math.Min(level, Reincarnation.LevelCap);
        Changed();
    }
    internal void OpenLevelsForTest() { _levelsOpen = true; Build(); }

    private void SetLevel(string code, int level)
    {
        if (_s is null) return;
        if (level <= 1) _s.Levels.Remove(code); else _s.Levels[code] = Math.Min(level, Reincarnation.LevelCap);
        Changed();
    }

    /// <summary>The − / + beside the level: every class lit at the old level follows to the new one.</summary>
    private void StepLevel(int by)
    {
        if (_s is null) return;
        int old = _levelPick, now = Math.Clamp(old + by, 2, Reincarnation.LevelCap);
        if (now == old) return;
        foreach (string c in Reincarnation.Classes) if (_s.LevelOf(c) == old) _s.Levels[c] = now;
        _levelPick = now;
        Changed();
    }

    private UIElement LevelsBlock(Reincarnation.State s)
    {
        int cap = Reincarnation.LevelCap;
        var above = Reincarnation.Classes.Where(c => s.LevelOf(c) > 1).ToList();
        if (_levelPick < 2) _levelPick = above.Count > 0 ? above.GroupBy(s.LevelOf).OrderByDescending(g => g.Count()).First().Key : 50;
        string sum = above.Count == 0 ? "every class from level 1"
            : above.Count == Reincarnation.Classes.Length && above.All(c => s.LevelOf(c) == s.LevelOf(above[0])) ? $"all 16 classes at level {s.LevelOf(above[0])}"
            : $"{above.Count} of 16 classes above level 1";
        LevelsLineForTest = sum;

        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var head = new TextBlock { Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "Where your classes stand today — click to set it" };
        head.Inlines.Add(new Run("YOUR LEVELS NOW") { Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        head.Inlines.Add(new Run("  " + sum + "  ") { Foreground = above.Count > 0 ? Green : Hint, FontSize = 11 });
        head.Inlines.Add(new Run(_levelsOpen ? "▾ close" : "▸ set") { Foreground = Teal, FontSize = 11 });
        head.MouseLeftButtonDown += (_, e) => { e.Handled = true; _levelsOpen = !_levelsOpen; Build(); };
        sp.Children.Add(head);
        if (!_levelsOpen) return sp;

        sp.Children.Add(Note($"A class you have not reincarnated yet keeps its level. A loadout stands at its LOWEST class, so three such classes together only need the levels up to {cap} — and nothing at {cap}."));
        Border Step(string glyph, int by)
        {
            var b = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(5), BorderBrush = Edge, BorderThickness = new Thickness(1), Background = ChipBg, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = glyph, Foreground = Dim, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) },
            };
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; StepLevel(by); };
            return b;
        }
        TextBlock Link(string text, string tip, Action act)
        {
            var t = new TextBlock { Text = text, Foreground = Teal, FontSize = 11, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), ToolTip = tip, Background = Brushes.Transparent };
            t.MouseLeftButtonDown += (_, e) => { e.Handled = true; act(); };
            return t;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = "Lit = level", Foreground = Hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(Step("−", -1));
        row.Children.Add(new TextBlock { Text = _levelPick.ToString(), Foreground = Green, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Width = 26, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Step("+", 1));
        row.Children.Add(Link("all", $"Every class stands at level {_levelPick}", () => { foreach (string c in Reincarnation.Classes) s.Levels[c] = _levelPick; Changed(); }));
        row.Children.Add(Link("none", "Every class from level 1", () => { s.Levels.Clear(); Changed(); }));
        sp.Children.Add(row);

        var wrap = new WrapPanel();
        foreach (string code in Reincarnation.Classes)
        {
            string c = code;
            int lv = s.LevelOf(c);
            bool lit = lv > 1, spent = s.Done.Any(d => d.Classes.Contains(c, StringComparer.OrdinalIgnoreCase));
            var tb = new TextBlock { FontSize = 10.5 };
            tb.Inlines.Add(new Run(c) { Foreground = lit ? Green : Hint, FontWeight = FontWeights.SemiBold });
            if (lit) tb.Inlines.Add(new Run(" " + lv) { Foreground = Green, FontSize = 9.5 });
            var chip = new Border
            {
                Child = tb, Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                Background = lit ? GreenBg : ChipBg, BorderBrush = lit ? GreenEdge : Edge, Cursor = Cursors.Hand, Opacity = spent ? 0.5 : 1,
                ToolTip = spent ? $"{c} was reincarnated in a ticked run: it starts from level 1 now, whatever is set here."
                    : lit ? $"{c} stands at level {lv} — click to put it back to level 1" : $"Click: {c} stands at level {_levelPick} now",
            };
            chip.MouseLeftButtonDown += (_, e) => { e.Handled = true; SetLevel(c, lv == _levelPick ? 1 : _levelPick); };
            wrap.Children.Add(chip);
        }
        sp.Children.Add(wrap);
        return sp;
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
            wrap.Children.Add(Pill(code, chip));
        }
        return wrap;
    }

    // ---- the plan ---------------------------------------------------------------------
    private static readonly double[] PlanCols = { 20, 22, 52, 158, 56, 44 };
    /// <summary>Selftest: the LEVEL FIRST cell of every row — "ready", "50→55 +0%", "1→55 +10%" — the AFTER
    /// pill beside it ("+10%"), and how many rows are green.</summary>
    internal List<string> PlanCellsForTest { get; } = new();
    internal List<string> PlanAfterForTest { get; } = new();
    internal int GreenRowsForTest { get; private set; }

    /// <summary>What a run's four carry once it is done: the highest of them, as the next loadout would feel it.</summary>
    internal static int AfterOf(Reincarnation.PlanRun run) =>
        Math.Min(Reincarnation.CapPercent, Reincarnation.StepPercent * Math.Max(run.RaceUse, run.ClassUse.Count > 0 ? run.ClassUse.Max() : 0));
    internal string PlanRowTipForTest(int number) => PlanForTest?.Runs.FirstOrDefault(r => r.Number == number) is { } r ? RowTip(r) : "";

    /// <summary>A row's mouse-over: what the run asks before the reincarnation, what it earns, and what its
    /// four carry afterwards (owner, 27 Nov: "they would all be reincarnate lvl 1 and thereby have 10% penalty").</summary>
    private static string RowTip(Reincarnation.PlanRun run)
    {
        int cap = Reincarnation.LevelCap;
        string before = run.Ready ? $"Ready: all three classes stand at level {cap} — nothing to level before you reincarnate."
            : $"Before: level {run.From} → {cap} at +{run.Penalty}% XP" + (run.SetBy.Count > 0 ? $" (set by {string.Join(", ", run.SetBy)})" : "")
                + (run.From > 1 ? " — the loadout stands at its lowest class's level." : ".");
        var after = run.Classes.Select((c, i) => $"{c} +{Math.Min(Reincarnation.CapPercent, run.ClassUse[i] * Reincarnation.StepPercent)}%")
            .Append($"{run.Race} +{Math.Min(Reincarnation.CapPercent, run.RaceUse * Reincarnation.StepPercent)}%");
        return $"Run {run.Number}: {Reincarnation.RaceName(run.Race)} · {string.Join(" / ", run.Classes)}\n{before}"
            + (run.Earns.Any() ? $"\nEarns {string.Join(" · ", run.Earns)}" : "\nEarns no new rank")
            + $"\nAfterwards: {string.Join(" · ", after)} — a loadout levels at the slowest of its four.";
    }

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
        // What the two figures are, in words (owner, 27 Nov: "i still dont get the interface").
        var legend = new TextBlock { FontSize = 10.5, Foreground = Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -3, 0, 8) };
        legend.Inlines.Add(new Run("LEVEL FIRST") { Foreground = Hint, FontWeight = FontWeights.SemiBold });
        legend.Inlines.Add(new Run(" = what to level before you can reincarnate that row; a "));
        legend.Inlines.Add(new Run("green row") { Foreground = Green, FontWeight = FontWeights.SemiBold });
        legend.Inlines.Add(new Run(" is ready as it stands. "));
        legend.Inlines.Add(new Run("AFTER") { Foreground = Hint, FontWeight = FontWeights.SemiBold });
        legend.Inlines.Add(new Run(" = the XP penalty its classes and race carry once you have."));
        SetDock(legend, Dock.Top); dp.Children.Add(legend);

        string[] names = { "", "#", "RACE", "TRIO", "LEVEL FIRST", "AFTER", "SET BY" };
        PlanCellsForTest.Clear(); PlanAfterForTest.Clear(); GreenRowsForTest = 0;
        for (int i = 0; i < names.Length; i++)
            titles.Children.Add(At(new TextBlock { Text = names[i], Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, Margin = new Thickness(i == 1 ? 0 : 4, 0, 0, 5), TextAlignment = i == 1 ? TextAlignment.Right : TextAlignment.Left }, i));
        // a narrow page leaves the last column some 20 px: half a title reads as a fault, so it steps aside (its words are on the row's mouse-over)
        if (titles.Children[titles.Children.Count - 1] is TextBlock setBy)
            titles.SizeChanged += (_, e) => setBy.Visibility = e.NewSize.Width - PlanCols.Sum() >= 44 ? Visibility.Visible : Visibility.Collapsed;
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
            g.Children.Add(At(new TextBlock { Text = run.Number.ToString(), Foreground = isNext ? Gold : Faint, FontWeight = isNext ? FontWeights.Bold : FontWeights.Normal, FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 1));

            var race = Chip(run.Race, run.RaceUse <= Reincarnation.Ranks ? run.RaceUse.ToString() : "×" + run.RaceUse, run.Locked ? 3 : run.RaceUse <= Reincarnation.Ranks ? 1 : 0);
            Gains(race, run.Race, () => r.RaceUse <= Reincarnation.Ranks ? $"Run {r.Number} earns its rank {r.RaceUse}." : $"Run {r.Number} is a repeat: use {r.RaceUse}, no new rank.");
            race.Margin = new Thickness(4, 0, 0, 0);
            g.Children.Add(At(Pill(run.Race, race), 2));

            var trio = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < run.Classes.Count; i++)
            {
                int n = run.ClassUse[i];
                var c = Chip(run.Classes[i], n < Reincarnation.Ranks ? n.ToString() : n == Reincarnation.Ranks ? "★" : "×" + n, n <= Reincarnation.Ranks ? 1 : 0);
                Gains(c, run.Classes[i], () => n < Reincarnation.Ranks ? $"Run {r.Number} earns its rank {n}." : n == Reincarnation.Ranks ? $"Run {r.Number} earns its rank 3 — the activated ability." : $"Run {r.Number} is a repeat: use {n}, no new rank.");
                c.Margin = new Thickness(0, 0, 4, 0);
                trio.Children.Add(Pill(run.Classes[i], c));
            }
            g.Children.Add(At(trio, 3));

            // TWO FIGURES, EACH NAMED (owner, 27 Nov — one "XP PENALTY +0%" read as what comes after the reincarnation,
            // and a "ready" pill hid the +10% it earns). LEVEL FIRST, in words: what the run asks BEFORE it — the
            // loadout stands at its lowest class's level and levels to the cap at the rate of the slowest of its four;
            // three classes at the cap ask nothing, and the whole row is green. AFTER, the pill: what its four carry
            // once it is done — the penalty the reincarnation EARNS.
            static (Brush Fg, Brush Edge) Heat(int pct) => pct == 0 ? (Green, GreenEdge) : pct <= 30 ? (Gold, GoldEdge) : pct <= 60 ? (Amber, AmberEdge) : (Red, RedEdge);
            PlanCellsForTest.Add(run.Ready ? "ready" : $"{run.From}→{Reincarnation.LevelCap} +{run.Penalty}%");
            var first = new TextBlock { FontSize = 10, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (run.Ready) first.Inlines.Add(new Run("ready") { Foreground = Green, FontWeight = FontWeights.SemiBold });
            else
            {
                first.Inlines.Add(new Run($"{run.From}→{Reincarnation.LevelCap} ") { Foreground = run.From > 1 ? Green : Faint });
                first.Inlines.Add(new Run($"+{run.Penalty}%") { Foreground = Heat(run.Penalty).Fg, FontWeight = FontWeights.SemiBold });
            }
            g.Children.Add(At(first, 4));
            int after = AfterOf(run);
            PlanAfterForTest.Add($"+{after}%");
            var (aftFg, aftEdge) = Heat(after);
            g.Children.Add(At(new Border
            {
                MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(9),
                BorderBrush = aftEdge, BorderThickness = new Thickness(1), Padding = new Thickness(5, 1, 5, 2),
                Child = new TextBlock { Text = $"+{after}%", Foreground = aftFg, FontSize = 10.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center },
            }, 5));
            if (run.Ready) GreenRowsForTest++;

            string why = run.Locked ? $"Unlock {Reincarnation.RaceName(run.Race)} first" : run.SetBy.Count > 0 ? string.Join(" · ", run.SetBy) : "—";
            var last = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
            if (isNext)
            {
                // the next run's number is gold whatever the width; the tag joins it when the last column has the room (a narrow
                // page leaves it some 20 px — half a tag under the scrollbar, 27 Nov)
                var tag = Tag("NEXT", Gold, GoldEdge); SetDock(tag, Dock.Right); last.Children.Add(tag);
                last.SizeChanged += (_, e) => tag.Visibility = e.NewSize.Width >= 40 ? Visibility.Visible : Visibility.Collapsed;
            }
            last.Children.Add(new TextBlock { Text = why, Foreground = run.Locked ? Red : Faint, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            g.Children.Add(At(last, 6));

            var line = new Border
            {
                Child = g, Padding = new Thickness(0, 4, 0, 4), BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 1), Background = run.Ready ? GreenBg : isNext ? NextBg : Brushes.Transparent, Opacity = run.Done ? 0.5 : 1,
                ToolTip = RowTip(run),
            };
            rows.Children.Add(line);
            RowCountForTest++;
        }
        if (plan.Runs.Count == 0) rows.Children.Add(Note("Nothing to plan."));
        _planScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = rows };
        dp.Children.Add(_planScroll);
        return CardOf(dp, new Thickness(0, 0, 10, 0));
    }

    /// <summary>A code with a small raised figure: 1 teal (earns a rank), 0 quiet (a repeat), 3 red (locked race), 2 plain, 4 gold (all three ranks).</summary>
    private static Border Chip(string code, string sup, int kind)
    {
        var (fg, edge, bg) = kind switch { 1 => (Teal, TealEdge, TealBg), 3 => (Red, RedEdge, (Brush)Brushes.Transparent), 2 => (Dim, Edge, ChipBg), 4 => (Gold, GoldEdge, GoldBg), _ => (Faint, Line, (Brush)Brushes.Transparent) };
        var tb = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        tb.Children.Add(new TextBlock { Text = code, FontSize = 10.5, Foreground = fg, FontWeight = FontWeights.SemiBold });
        if (sup.Length > 0) tb.Children.Add(new TextBlock { Text = sup, FontSize = 8.5, Foreground = fg, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, -1, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        return new Border { Child = tb, Padding = new Thickness(6, 1, 6, 2), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = edge, Background = bg, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Border Tag(string text, Brush fg, Brush edge) =>
        new() { Child = new TextBlock { Text = text, Foreground = fg, FontSize = 9, FontWeight = FontWeights.Bold }, Padding = new Thickness(5, 0, 5, 1), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = edge, VerticalAlignment = VerticalAlignment.Center };

    // ---- progress ---------------------------------------------------------------------
    private StackPanel ProgressPanel(Reincarnation.Plan plan)
    {
        var s = _s!;
        var sp = new StackPanel { Margin = new Thickness(0, 0, ScrollGap, 0) };
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
        return sp;
    }

    /// <summary>Selftest: the "what you have now" block, line by line.</summary>
    internal List<string> NowLines { get; } = new();

    /// <summary>WHAT YOU HAVE NOW: every finished rank's passive at its current value, the activated
    /// abilities unlocked, the stats several races share added up — it moves with every tick and pip.
    /// Its own column on a wide page, the right card's second face on a narrow one.</summary>
    private void NowBlock(StackPanel sp)
    {
        NowLines.Clear(); GroupHeadsForTest.Clear();
        var st = _s!;
        var rows = Reincarnation.Sheet(st);
        int gained = rows.Count(r => r.Have > 0);
        int abilities = st.ClassOrder.Count(c => st.Count(c) >= Reincarnation.Ranks);
        _nowCount = gained + abilities;
        sp.Children.Add(Head("WHAT YOU HAVE NOW", $"{gained} of {rows.Count} bonuses · {abilities} of {Reincarnation.Classes.Length} abilities"));

        // EVERY REINCARNATION (the Kunark FAQ, 27 Nov): stat soft caps rise by a fifth of the levels given up —
        // 11 points at level 55. Counted per reincarnation; the FAQ leaves open whether per class, and which stats.
        {
            int runsDone = Reincarnation.RunsDone(st), soft = runsDone * Reincarnation.SoftcapPerRun;
            sp.Children.Add(GroupHead("EVERY REINCARNATION"));
            NowLines.Add($"Stat soft caps: {(soft > 0 ? "+" + soft : "—")} · {Reincarnation.SoftcapPerRun} per run");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 44 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            g.Children.Add(At(new TextBlock { Text = "Stat soft caps", Foreground = soft > 0 ? Text : Hint, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 0));
            g.Children.Add(At(new TextBlock { Text = soft > 0 ? "+" + soft : "—", Foreground = soft > 0 ? Gold : Faint, FontSize = 11.5, FontWeight = soft > 0 ? FontWeights.SemiBold : FontWeights.Normal, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 1));
            g.Children.Add(At(new TextBlock { Text = $"{Reincarnation.SoftcapPerRun} per run", Foreground = Faint, FontSize = 10, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 2));
            sp.Children.Add(new Border
            {
                Child = g, Background = Brushes.Transparent, Padding = new Thickness(0, 2, 0, 3), BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 1),
                ToolTip = $"The Kunark FAQ: stat soft caps rise by a fifth of the levels a reincarnation gives up — {Reincarnation.SoftcapPerRun} points at level {Reincarnation.LevelCap}.\n"
                    + $"Counted here per reincarnation ({runsDone} done). The FAQ does not say whether it counts per reincarnation or per class, nor which stats it lifts.",
            });
        }

        // A STAT SHEET (owner, 26 Nov): every bonus there is, one line per stat, its givers added up —
        // gold once gained, quiet until then. Where a line comes from is on the mouse-over.
        string group = "";
        foreach (var r in rows)
        {
            if (r.Def.Group != group) { group = r.Def.Group; sp.Children.Add(GroupHead(group)); }
            bool has = r.Have > 0, full = has && r.Have >= r.Max;
            NowLines.Add($"{r.Def.Label}: {(has ? r.HaveText : "—")} of {r.MaxText}");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 44 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            g.Children.Add(At(new TextBlock { Text = r.Def.Label, Foreground = has ? Text : Hint, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 0));
            g.Children.Add(At(new TextBlock { Text = has ? r.HaveText : "—", Foreground = has ? Gold : Faint, FontSize = 11.5, FontWeight = has ? FontWeights.SemiBold : FontWeights.Normal, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 1));
            g.Children.Add(At(new TextBlock { Text = full ? "max" : "of " + r.MaxText, Foreground = full ? GoldEdge : Faint, FontSize = 10, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 2));
            var line = new Border { Child = g, Background = Brushes.Transparent, Padding = new Thickness(0, 2, 0, 3), BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 1) };
            var row = r;
            Hang(line, () => SourcesCard(row));
            Glow(line, r.Def.Label, row.Parts.Select(p => p.Code).ToList());
            sp.Children.Add(line);
        }

        // The activated abilities: all sixteen, yours in gold, the rest with how far their class is.
        int cd = Reincarnation.Abilities.SharedCooldownSec;
        sp.Children.Add(GroupHead("ACTIVATED ABILITIES" + (cd > 0 ? $" · ONE SHARED {cd} S TIMER" : "")));
        foreach (string code in st.ClassOrder)
        {
            if (Reincarnation.AbilityOf(code) is not { Active.Length: > 0 } a) continue;
            int have = Math.Min(Reincarnation.Ranks, st.Count(code));
            bool yours = have >= Reincarnation.Ranks;
            NowLines.Add($"★ {a.Active}: {(yours ? "yours" : $"{have} / {Reincarnation.Ranks}")}");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 44 });
            var name = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Inlines.Add(new Run("★ ") { Foreground = yours ? Gold : Faint });
            name.Inlines.Add(new Run(a.Active) { Foreground = yours ? Gold : Hint, FontWeight = yours ? FontWeights.SemiBold : FontWeights.Normal });
            g.Children.Add(At(name, 0));
            g.Children.Add(At(new TextBlock { Text = yours ? "yours" : $"{have} / {Reincarnation.Ranks}", Foreground = yours ? Gold : Faint, FontSize = yours ? 11 : 10, FontWeight = yours ? FontWeights.SemiBold : FontWeights.Normal, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 1));
            var line = new Border { Child = g, Background = Brushes.Transparent, Padding = new Thickness(0, 2, 0, 3), BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 1) };
            Gains(line, code);
            Glow(line, "★ " + a.Active, new[] { code });
            sp.Children.Add(line);
        }
        sp.Children.Add(new TextBlock { Text = "Gold = gained. Hover a line: its classes and races light up on the page, the card says what each gives.", Foreground = Faint, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
    }

    /// <summary>Selftest: the sheet's section heads with their ink.</summary>
    internal List<string> GroupHeadsForTest { get; } = new();

    /// <summary>A section of the sheet (OFFENSE, DEFENSE…). Teal, a size up and ruled off (owner, 27 Nov:
    /// "should be clearer / other colour, easier to read" — it was the faintest ink on the card).</summary>
    private Border GroupHead(string caps)
    {
        GroupHeadsForTest.Add($"{caps} {((SolidColorBrush)Teal).Color}");
        return new Border
        {
            Child = new TextBlock { Text = caps, Foreground = Teal, FontSize = 10.5, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis },
            BorderBrush = TealEdge, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 3), Margin = new Thickness(0, 12, 0, 2),
        };
    }

    /// <summary>Hang a card on anything, built when it opens.</summary>
    private static void Hang(FrameworkElement el, Func<object> card)
    {
        el.ToolTip = " ";
        ToolTipService.SetShowDuration(el, 60000);
        ToolTipService.SetInitialShowDelay(el, 250);
        el.ToolTipOpening += (_, _) => el.ToolTip = card();
    }

    /// <summary>Selftest: a stat line's mouse-over, in words.</summary>
    internal string SourcesTextForTest(string label)
    {
        var row = Reincarnation.Sheet(_s!).First(r => r.Def.Label == label);
        SourcesCard(row, out string text);
        return text;
    }

    private UIElement SourcesCard(Reincarnation.StatRow row) => SourcesCard(row, out _);

    /// <summary>Who gives a stat: the gains card of every class or race behind the line, one under the
    /// other — and for the Completionist line, what it asks.</summary>
    private UIElement SourcesCard(Reincarnation.StatRow row, out string text)
    {
        var said = new System.Text.StringBuilder();
        var sp = new StackPanel { MaxWidth = CardWidth };
        string head = $"{row.Def.Label}: {(row.Have > 0 ? row.HaveText : "nothing yet")} of {row.MaxText}";
        said.AppendLine(head);
        sp.Children.Add(new TextBlock { Text = head, Foreground = row.Have > 0 ? Gold : Dim, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var part in row.Parts)
        {
            sp.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(0, 7, 0, 7) });
            if (part.Code == Reincarnation.EveryClass)
            {
                string[] roman = { "I", "II", "III" };
                string line = part.Rank > 0 ? $"Completionist {roman[part.Rank - 1]} — every class has reached rank {roman[part.Rank - 1]}." : "Completionist — not yet: it starts once EVERY class has a rank.";
                string how = $"{row.Def.Format(Reincarnation.RankNumbers(part.Code, 0)[0])} / {row.Def.Format(Reincarnation.RankNumbers(part.Code, 0)[1])} / {row.Def.Format(part.Max)} as all sixteen classes reach rank I / II / III (our reading of the wiki's \"completing all Class Reincarnations\").";
                said.AppendLine(line); said.AppendLine(how);
                sp.Children.Add(new TextBlock { Text = line, Foreground = part.Rank > 0 ? Gold : Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(new TextBlock { Text = how, Foreground = Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                continue;
            }
            sp.Children.Add(GainsCard(part.Code, row.Parts.Count > 1 ? $"Gives {row.Def.Format(part.Have)} of its {row.Def.Format(part.Max)} to this line." : "", out string one));
            said.Append(one);
        }
        text = said.ToString();
        return sp;
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
        dp.Children.Add(Pill(code, chip));

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
        if (have > 0) { tail.Inlines.Add(new Run($"+{Math.Min(Reincarnation.CapPercent, have * Reincarnation.StepPercent)}%") { Foreground = Dim, FontWeight = FontWeights.SemiBold }); tail.Inlines.Add(new Run("  ")); }
        if (have >= Reincarnation.Ranks) tail.Inlines.Add(new Run(have > Reincarnation.Ranks ? $"done ×{have}" : "done") { Foreground = Gold });
        else if (plan.RankThreeAt.TryGetValue(code, out int run)) { tail.Inlines.Add(new Run("★ ")); tail.Inlines.Add(new Run(run.ToString()) { Foreground = Dim, FontWeight = FontWeights.SemiBold }); }
        tail.ToolTip = (plan.RankThreeAt.TryGetValue(code, out int r3) && have < Reincarnation.Ranks ? $"Rank 3 comes with run {r3}. " : "") + (have > 0 ? $"{have} finished: +{Math.Min(Reincarnation.CapPercent, have * Reincarnation.StepPercent)}% XP needed whenever {code} is in the run" : "Untouched: no XP penalty from this one");
        dp.Children.Add(tail);
        return new Border { Child = dp, Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 12, 0), BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 1) };
    }
}
