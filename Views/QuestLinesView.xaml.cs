using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Notable quests pack in the Quests window: quest lines on the left
/// (progress ring, class fit, prerequisites), the chosen line on the right
/// — reward and gates, an "Up next" card, then the step timeline with what
/// to bring (held counts from the dump and the ledger), what to say, what
/// the NPC answers, and the log evidence that ticked each step.
/// </summary>
public partial class QuestLinesView : UserControl
{
    private static readonly Brush Card = Freeze("#20293A");
    private static readonly Brush CardSel = Freeze("#242A36");
    private static readonly Brush Field = Freeze("#232B3D");
    private static readonly Brush Line = Freeze("#3A4560");
    private static readonly Brush Hair = Freeze("#1F2637");
    private static readonly Brush Text = Freeze("#E6ECF5");
    private static readonly Brush Dim = Freeze("#C9D4E3");
    private static readonly Brush Hint = Freeze("#7F93AD");
    private static readonly Brush Faint = Freeze("#5C6B82");
    private static readonly Brush Gold = Freeze("#E8C15A");
    private static readonly Brush Cyan = Freeze("#4FC3F7");
    private static readonly Brush Green = Freeze("#7CE07C");
    private static readonly Brush GreenLine = Freeze("#2E5A3A");
    private static readonly Brush GreenBg = Freeze("#1E2A22");
    private static readonly Brush Amber = Freeze("#FFB74D");
    private static readonly Brush AmberLine = Freeze("#5A4A2E");
    private static readonly Brush Red = Freeze("#FF5C5C");
    private static readonly Brush Violet = Freeze("#B39DDB");
    private static readonly Brush Quote = Freeze("#D1C4E9");
    private static readonly Brush GoldBg = Freeze("#2A2614");
    private static readonly Brush ChipOnBg = Freeze("#232B40");
    private static readonly Brush ChipOnLine = Freeze("#5A6B8C");
    private static readonly Brush PreLine = Freeze("#2C4E66");
    private static readonly Brush WinBg = Freeze("#141A26");

    private QuestLines? _lines;
    private Func<string?>? _dumpFile;
    private Func<string>? _classes;
    private string? _selected;
    private string _lens = "all";   // all · mine · tracked
    private bool _leftOnly;         // "What's left"
    private string _pack = "";      // "" = Notable quests · "epics" = the class epics (25 Nov)
    private bool _othersOpen;       // the folded list of classes outside your combo
    private readonly HashSet<string> _partFlips = new(StringComparer.Ordinal); // parts toggled away from their default fold
    private static readonly Brush Rule = Freeze("#2A3346");
    private static readonly Brush AmberBg = Freeze("#2A2414");
    private Segmented? _showSeg;
    private (string Path, DateTime Stamp, List<InventoryStore.CarryRow> Rows)? _dumpCache;

    public QuestLinesView()
    {
        InitializeComponent();
    }

    /// <summary>Which pack the view shows: "" = Notable quests, "epics" = the class epics. The epics
    /// open on "Fits my combo" — fifteen classes, three of them yours. Set it before Attach.</summary>
    public string Pack
    {
        get => _pack;
        set
        {
            if (_pack.Equals(value, StringComparison.OrdinalIgnoreCase)) return;
            _pack = value;
            _selected = null;
            _lens = value == "epics" ? "mine" : "all";
            _othersOpen = false;
            _partFlips.Clear();
        }
    }

    private IReadOnlyList<QuestLines.Quest> PackQuests() => _lines!.QuestsIn(_pack);

    public void Attach(QuestLines lines, Func<string?>? dumpFile, Func<string>? classesProvider)
    {
        if (_lines is not null) _lines.Changed -= OnChanged;
        _lines = lines;
        _dumpFile = dumpFile;
        _classes = classesProvider;
        _lines.Changed += OnChanged;
        Unloaded += (_, _) => { if (_lines is not null) _lines.Changed -= OnChanged; };
        BuildPills();
        Refresh();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

    /// <summary>Which quest the right side shows (selftest + deep links).</summary>
    public string? SelectedKey => _selected;
    public void Select(string key) { _selected = key; Refresh(); }

    // ---------------------------------------------------------------- pills

    private void BuildPills()
    {
        Pills.Children.Clear();
        foreach (var (id, label, tip) in new[]
        {
            ("all", _pack == "epics" && _lines is not null ? $"All {PackQuests().Count}" : "All", "Every quest line in the pack"),
            ("mine", "Fits my combo", "Lines a class in your /who combo can do (any-class lines always fit)"),
            ("tracked", "★ Tracked", "Lines you starred"),
        })
        {
            var pill = Chip(label, id);
            pill.ToolTip = tip;
            pill.Padding = new Thickness(10, 2, 10, 3);
            pill.MouseLeftButtonDown += (_, _) => { _lens = id; Refresh(); };
            Pills.Children.Add(pill);
        }
        ShowRow.Children.Clear();
        ShowRow.Children.Add(new TextBlock { Text = "Show:", Foreground = Hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _showSeg = new Segmented(new[]
        {
            new Segmented.Option("all", "Every step"),
            new Segmented.Option("left", "What's left", "Hide the steps already done"),
        }, _leftOnly ? "left" : "all", "#E8C15A");
        _showSeg.Margin = new Thickness(0);
        _showSeg.Changed += id => { _leftOnly = id == "left"; Refresh(); };
        ShowRow.Children.Add(_showSeg);
    }

    private void PaintPills()
    {
        foreach (var child in Pills.Children)
        {
            if (child is not Border pill || pill.Tag is not string id) continue;
            bool on = id == _lens;
            pill.Background = on ? Freeze("#16283E") : Field;
            pill.BorderBrush = on ? Cyan : Line;
            if (pill.Child is TextBlock tb) tb.Foreground = on ? Cyan : Hint;
        }
    }

    // ---------------------------------------------------------------- refresh

    private HashSet<string> MyClasses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in (_classes?.Invoke() ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(a);
        return set;
    }

    private static bool Fits(QuestLines.Quest q, HashSet<string> mine) =>
        q.Classes.Count == 0 || q.Classes.Any(mine.Contains);

    public void Refresh()
    {
        if (_lines is null) return;
        PaintPills();
        var mine = MyClasses();
        var all = PackQuests();
        string notice = _lines.NoticeOf(_pack);
        NoticeText.Text = notice;
        NoticeBox.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var quests = all.Where(q => _lens switch
        {
            "mine" => Fits(q, mine),
            "tracked" => _lines.IsTracked(q),
            _ => true,
        }).ToList();

        if (_selected is null || all.All(q => q.Key != _selected))
            _selected = (all.FirstOrDefault(q => _lines.IsTracked(q) && !_lines.IsComplete(q))
                         ?? all.FirstOrDefault(q => Fits(q, mine) && !_lines.IsComplete(q))
                         ?? all.FirstOrDefault())?.Key;

        BuildList(quests, mine, all);
        var sel = all.FirstOrDefault(q => q.Key == _selected);
        DetailHost.Children.Clear();
        if (sel is not null) BuildDetail(sel, mine);
    }

    // ---------------------------------------------------------------- the list

    private void BuildList(List<QuestLines.Quest> quests, HashSet<string> mine, IReadOnlyList<QuestLines.Quest> all)
    {
        ListHost.Children.Clear();
        foreach (var q in quests)
        {
            bool sel = q.Key == _selected;
            int done = _lines!.DoneCount(q), total = _lines.RequiredCount(q);
            bool complete = _lines.IsComplete(q);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock { Text = q.Name, FontWeight = FontWeights.Bold, Foreground = Brushes.White, FontSize = 13 };
            grid.Children.Add(name);

            var meta = new TextBlock { Foreground = Hint, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            meta.Inlines.Add(RewardLink(q.Reward));
            meta.Inlines.Add(new System.Windows.Documents.Run(" · " + RewardShort(q)));
            if (q.MinLevel > 1) meta.Inlines.Add(new System.Windows.Documents.Run($" · lvl {q.MinLevel}"));
            Grid.SetRow(meta, 1);
            grid.Children.Add(meta);

            var chips = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            Grid.SetRow(chips, 2); Grid.SetColumnSpan(chips, 2);
            if (q.Classes.Count == 0)
                chips.Children.Add(Badge("any class", Hint, Line));
            else
                foreach (var c in q.Classes)
                {
                    bool me = mine.Contains(c);
                    chips.Children.Add(me ? Badge(c + " · in your combo", Gold, ChipOnLine, ChipOnBg) : Badge(c, Hint, Line));
                }
            foreach (var p in q.Prereqs)
            {
                var pq = _lines.Quests.FirstOrDefault(x => x.Key == p.Quest);
                bool have = (pq is not null && _lines.IsComplete(pq)) || Holds(p.Item).Held;
                chips.Children.Add(Badge($"needs {p.Item}" + (have ? " ✓" : ""), have ? Green : Cyan, have ? GreenLine : PreLine));
            }
            int partCount = q.Steps.Select(x => x.Part).Where(x => x.Length > 0).Distinct().Count();
            if (partCount > 1) chips.Children.Add(Badge($"{partCount} parts", Hint, Line));
            grid.Children.Add(chips);

            // progress ring
            var ring = Ring(done, total, complete);
            Grid.SetColumn(ring, 1); Grid.SetRowSpan(ring, 2);
            ring.Margin = new Thickness(10, 0, 0, 0);
            ring.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(ring);

            var card = new Border
            {
                Background = sel ? CardSel : Card,
                BorderBrush = sel ? Gold : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 9, 11, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                Child = grid,
                Tag = q.Key,
            };
            string key = q.Key;
            card.MouseLeftButtonDown += (_, _) => { _selected = key; Refresh(); };
            ListHost.Children.Add(card);
        }
        // The classes outside your combo, folded to one line each (epics: fifteen classes, three yours).
        var others = _lens == "mine" ? all.Where(q => !quests.Contains(q)).ToList() : new List<QuestLines.Quest>();
        if (others.Count > 0)
        {
            bool open = _othersOpen || quests.Count == 0 || others.Any(q => q.Key == _selected);
            var head = new TextBlock
            {
                Text = $"{(open ? "▾" : "▸")}  {others.Count} other class{(others.Count == 1 ? "" : "es")}",
                Foreground = Hint, FontSize = 11.5, Cursor = Cursors.Hand, Margin = new Thickness(2, 2, 0, 6),
                ToolTip = "Epics for classes outside your /who combo",
            };
            head.MouseLeftButtonDown += (_, _) => { _othersOpen = !open; Refresh(); };
            ListHost.Children.Add(head);
            if (open)
                foreach (var q in others)
                {
                    bool sel = q.Key == _selected;
                    var row = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(2, 0, 0, 0) };
                    var count = new TextBlock { Text = $"{_lines!.DoneCount(q)}/{_lines.RequiredCount(q)}", Foreground = Faint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(count, Dock.Right);
                    row.Children.Add(count);
                    row.Children.Add(new TextBlock
                    {
                        Text = $"{q.Name.Replace(" Epic", "")} · {q.Reward}", Foreground = sel ? Gold : Dim, FontSize = 12,
                        FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 8, 3),
                    });
                    string key = q.Key;
                    row.MouseLeftButtonDown += (_, _) => { _selected = key; Refresh(); };
                    ListHost.Children.Add(row);
                }
        }
        string foot = _lines!.FooterOf(_pack);
        ListHost.Children.Add(new TextBlock
        {
            Text = quests.Count == 0 && others.Count == 0
                ? "Nothing under this lens — try All."
                : foot.Length > 0 ? foot
                : $"{all.Count} quest line{(all.Count == 1 ? "" : "s")} from eqlwiki so far. More join as they're written up.",
            Foreground = Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0),
        });
    }

    /// <summary>The reward's name as a link to its eqlwiki page (owner, 23 Sep).</summary>
    private static System.Windows.Documents.Run RewardLink(string reward)
    {
        var run = new System.Windows.Documents.Run(reward)
        {
            Foreground = Dim, FontWeight = FontWeights.SemiBold,
            TextDecorations = TextDecorations.Underline, Cursor = Cursors.Hand,
            ToolTip = "Open on eqlwiki",
        };
        string url = SkyWindow.WikiUrl(reward);
        run.MouseLeftButtonDown += (_, e) => { SkyWindow.OpenUrl(url); e.Handled = true; };
        return run;
    }

    private static string RewardShort(QuestLines.Quest q)
    {
        var st = q.RewardStats;
        string skill = System.Text.RegularExpressions.Regex.Match(st, @"Skill:\s*(?<s>[^\n]+?)\s+Atk Delay:\s*(?<d>\d+)").Groups["s"].Value;
        string dly = System.Text.RegularExpressions.Regex.Match(st, @"Atk Delay:\s*(?<d>\d+)").Groups["d"].Value;
        string dmg = System.Text.RegularExpressions.Regex.Match(st, @"DMG:\s*(?<d>\d+)").Groups["d"].Value;
        return skill.Length > 0 ? $"{skill} {dmg}/{dly}" : "quest reward";
    }

    private FrameworkElement Ring(int done, int total, bool complete)
    {
        const double size = 38, r = 16, stroke = 4;
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Width = r * 2 + stroke, Height = r * 2 + stroke, Stroke = Freeze("#2A3346"), StrokeThickness = stroke, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        double frac = total == 0 ? 0 : (double)done / total;
        if (frac >= 0.999)
            grid.Children.Add(new Ellipse { Width = r * 2 + stroke, Height = r * 2 + stroke, Stroke = Green, StrokeThickness = stroke, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        else if (frac > 0)
        {
            double c = size / 2, ang = frac * 2 * Math.PI;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(ang), c - r * Math.Cos(ang));
            var fig = new PathFigure { StartPoint = start };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, ang > Math.PI, SweepDirection.Clockwise, true));
            var geo = new PathGeometry(); geo.Figures.Add(fig);
            grid.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = Gold, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        grid.Children.Add(new TextBlock
        {
            Text = complete ? "✓" : $"{done}/{total}",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = complete ? Green : Dim,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }

    // ---------------------------------------------------------------- the detail

    private void BuildDetail(QuestLines.Quest q, HashSet<string> mine)
    {
        int done = _lines!.DoneCount(q), total = _lines.RequiredCount(q);

        // header
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleRow = new DockPanel();
        var star = new TextBlock
        {
            Text = _lines.IsTracked(q) ? "★" : "☆", FontSize = 15, Cursor = Cursors.Hand,
            Foreground = _lines.IsTracked(q) ? Gold : Faint, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Track this line — it leads the list and the Up next card follows it",
        };
        star.MouseLeftButtonDown += (_, _) => _lines.SetTracked(q, !_lines.IsTracked(q));
        DockPanel.SetDock(star, Dock.Left);
        titleRow.Children.Add(star);
        titleRow.Children.Add(new TextBlock { Text = q.Name, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(titleRow);

        var prog = new TextBlock { TextAlignment = TextAlignment.Right, Foreground = Dim, FontSize = 12 };
        prog.Inlines.Add(new System.Windows.Documents.Run(done.ToString()) { Foreground = Gold, FontSize = 18, FontWeight = FontWeights.Bold });
        prog.Inlines.Add(new System.Windows.Documents.Run($" of {total} steps"));
        var last = q.Steps.Select(s => _lines.MarkOf(q, s)).Where(m => m is not null && m.How != "implied").OrderByDescending(m => m!.When).FirstOrDefault();
        if (last is not null)
        {
            prog.Inlines.Add(new System.Windows.Documents.LineBreak());
            prog.Inlines.Add(new System.Windows.Documents.Run($"last progress {last.When:d MMM, HH:mm}") { Foreground = Hint, FontSize = 11 });
        }
        Grid.SetColumn(prog, 1); Grid.SetRowSpan(prog, 2);
        prog.Margin = new Thickness(14, 0, 0, 0);
        head.Children.Add(prog);

        var stats = new TextBlock { Foreground = Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        stats.Inlines.Add(RewardLink(q.Reward));
        stats.Inlines.Add(new System.Windows.Documents.Run(" · " + StatsLine(q.RewardStats)));
        Grid.SetRow(stats, 1);
        head.Children.Add(stats);

        var req = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(req, 2); Grid.SetColumnSpan(req, 2);
        if (q.MinLevel > 1) req.Children.Add(Badge($"Level {q.MinLevel}", Hint, Line));
        if (q.Classes.Count > 0)
        {
            bool fit = Fits(q, mine);
            req.Children.Add(Badge(string.Join(" / ", q.Classes) + (fit ? " · in your combo ✓" : " · not in your /who combo"), fit ? Green : Amber, fit ? GreenLine : AmberLine));
        }
        foreach (var p in q.Prereqs)
        {
            var pq = _lines.Quests.FirstOrDefault(x => x.Key == p.Quest);
            var h = Holds(p.Item);
            bool have = (pq is not null && _lines.IsComplete(pq)) || h.Held;
            req.Children.Add(Badge($"{p.Item} in hand" + (have ? " ✓" : " — " + (pq?.Name ?? "quest")), have ? Green : Amber, have ? GreenLine : AmberLine));
        }
        foreach (var r in q.Requires) req.Children.Add(Badge(r, Hint, Line));
        if (q.Start.Length > 0) req.Children.Add(Badge("starts with " + q.Start, Hint, Line));
        if (q.Wiki.Length > 0)
        {
            var wiki = Badge("eqlwiki ↗", Cyan, PreLine);
            wiki.Cursor = Cursors.Hand;
            wiki.ToolTip = q.Wiki;
            string url = q.Wiki;
            wiki.MouseLeftButtonDown += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ } };
            req.Children.Add(wiki);
        }
        head.Children.Add(req);

        DetailHost.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 12, 14, 12), Child = head });
        if (q.Note.Length > 0)
            DetailHost.Children.Add(new TextBlock { Text = q.Note, Foreground = Hint, FontSize = 11, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (q.Warning.Length > 0)
        {
            // The one thing that ruins the whole quest (the shadow knight's cons, the shaman's faction).
            var w = new TextBlock { Foreground = Amber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            w.Inlines.Add(new System.Windows.Documents.Run("Whole quest: ") { FontWeight = FontWeights.SemiBold });
            w.Inlines.Add(new System.Windows.Documents.Run(q.Warning));
            DetailHost.Children.Add(new Border { Background = AmberBg, BorderBrush = AmberLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 7, 12, 8), Margin = new Thickness(0, 10, 0, 0), Child = w });
        }

        // up next
        var next = _lines.NextStep(q);
        if (next is not null)
        {
            var g = new Grid { Margin = new Thickness(0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = _lines.AwaitsClick(q, next) ? "TICK IT" : "UP NEXT", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Green, Margin = new Thickness(0, 2, 14, 0) };
            g.Children.Add(k);
            var t = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
            string whereNext = string.Join(" · ", new[] { next.Npc, next.Zone }.Where(x => x.Length > 0));
            t.Text = whereNext.Length > 0 ? $"{next.Title} — {whereNext}" : next.Title;
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var d = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            Grid.SetColumn(d, 1); Grid.SetRow(d, 1);
            d.Inlines.Add(new System.Windows.Documents.Run(NextAdvice(q, next)));
            g.Children.Add(d);
            DetailHost.Children.Add(new Border { Background = GreenBg, BorderBrush = GreenLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 10, 0, 0), Child = g });
        }
        else
        {
            DetailHost.Children.Add(new Border
            {
                Background = GreenBg, BorderBrush = GreenLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock { Text = $"Done — {q.Reward} is yours.", Foreground = Green, FontWeight = FontWeights.Bold },
            });
        }

        // steps
        var steps = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        int hidden = 0;
        // An epic's steps come in named PARTS: a header each (its own count, OPTIONAL when nothing in
        // it is required), folded unless it holds the next step — a click flips it.
        bool parted = q.Steps.Any(x => x.Part.Length > 0);
        string? curPart = null;
        bool partOpen = true;
        int partNo = 0;
        for (int i = 0; i < q.Steps.Count; i++)
        {
            var s = q.Steps[i];
            bool isDone = _lines.IsDone(q, s);
            if (parted && s.Part != curPart)
            {
                curPart = s.Part;
                partNo++;
                var inPart = q.Steps.Where(x => x.Part == curPart).ToList();
                if (_leftOnly && inPart.All(x => _lines.IsDone(q, x))) { partOpen = false; hidden += inPart.Count; continue; }
                string flipKey = q.Key + "|" + curPart;
                partOpen = (next is not null && inPart.Contains(next)) ^ _partFlips.Contains(flipKey);
                steps.Children.Add(PartHeader(q, curPart, partNo, inPart, partOpen, flipKey));
            }
            if (parted && !partOpen) continue;
            if (_leftOnly && isDone) { hidden++; continue; }
            steps.Children.Add(StepCard(q, s, i + 1, isDone, ReferenceEquals(s, next), !parted && steps.Children.Count > 0));
        }
        if (hidden > 0)
            steps.Children.Insert(0, new TextBlock { Text = $"{hidden} step{(hidden == 1 ? "" : "s")} done — hidden by What's left", Foreground = Faint, FontSize = 11, Margin = new Thickness(44, 0, 0, 6) });
        DetailHost.Children.Add(steps);

        DetailHost.Children.Add(new TextBlock
        {
            Text = "Steps are read from your log the way the Sky quests are: loot lines, \"You have slain\", \"You offered … / You complete the trade with …\" and \"You say, '…'\". Reparsing or merging a log never double-counts. " + (q.Steps.Any(x => x.Part.Length > 0)
                ? "The parts run side by side: a hand-in or a combine marks the steps that made its items, and the last step marks the whole quest. Steps the log cannot see — a purchase, a ground spawn, an item the wiki does not name — wait for your tick."
                : "A later step proving itself marks the earlier ones done, so a quest you started before EQL Assistant still fills in."),
            Foreground = Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 8),
        });
    }

    /// <summary>A part's header line: chevron · PART n · its name · OPTIONAL · a rule · the count.</summary>
    private FrameworkElement PartHeader(QuestLines.Quest q, string part, int no, List<QuestLines.Step> inPart, bool open, string flipKey)
    {
        var required = inPart.Where(x => !x.Optional).ToList();
        bool optionalPart = required.Count == 0;
        var counted = optionalPart ? inPart : required;
        int done = counted.Count(x => _lines!.IsDone(q, x));
        bool complete = !optionalPart && done == counted.Count;

        var dock = new DockPanel { Margin = new Thickness(0, no == 1 ? 0 : 6, 0, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = open ? "Fold this part" : "Show this part's steps" };
        var count = new TextBlock
        {
            Text = (complete ? "✓ " : "") + $"{done} of {counted.Count}" + (!optionalPart && inPart.Count > required.Count ? $" · {inPart.Count - required.Count} optional" : ""),
            Foreground = complete ? Green : Hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        DockPanel.SetDock(count, Dock.Right);
        dock.Children.Add(count);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(left, Dock.Left);
        left.Children.Add(new TextBlock { Text = open ? "▾" : "▸", Foreground = Hint, FontSize = 11, Width = 14, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = $"PART {no}", Foreground = complete ? Green : Dim, FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = part, Foreground = Hint, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 8, 0) });
        if (optionalPart)
        {
            var opt = Badge("OPTIONAL", Hint, Line);
            opt.Margin = new Thickness(0);
            opt.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(opt);
        }
        dock.Children.Add(left);
        dock.Children.Add(new Border { Height = 1, Background = Rule, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0) });
        dock.MouseLeftButtonDown += (_, _) => { if (!_partFlips.Add(flipKey)) _partFlips.Remove(flipKey); Refresh(); };
        return dock;
    }

    private string NextAdvice(QuestLines.Quest q, QuestLines.Step s)
    {
        var parts = new List<string>();
        if (s.Part.Length > 0) parts.Add(s.Part + ".");
        if (_lines!.AwaitsClick(q, s)) parts.Add(s.ClickText.Length > 0
            ? $"The log saw the kill and the loot — right-click {s.Click}; its own line checks the step off (or flip the switch)."
            : $"The log saw the kill and the loot — right-click {s.Click}, then flip the switch on the step.");
        else
        {
            if (s.Kill.Count > 0) parts.Add("Kill " + string.Join(" / ", s.Kill) + ".");
            if (s.KillAny.Count > 0) parts.Add("Kill any one of " + JoinOr(s.KillAny) + ".");
            if (s.Make.Count > 0) parts.Add("Combine for " + Join(s.Make) + ".");
            if (s.Loot.Count > 0) parts.Add("Loot " + Join(s.Loot) + ".");
            if (s.Say.Length > 0) parts.Add($"Say \"{s.Say}\".");
            if (s.Handin.Count > 0)
            {
                var missing = s.Handin.Where(h => !Holds(h.Name).Held).Select(h => h.Name).ToList();
                parts.Add(missing.Count == 0
                    ? "Everything to hand in is in your bags."
                    : "Still missing: " + Join(missing) + ".");
            }
            if (s.Coins.Length > 0) parts.Add($"Bring {s.Coins}.");
            if (s.Click.Length > 0) parts.Add($"Then right-click {s.Click}.");
        }
        if (s.Faction.Length > 0) parts.Add($"Needs {s.Faction}.");
        if (s.Note.Length > 0) parts.Add(s.Note);
        return string.Join(" ", parts);
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
    private static string JoinOr(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1];

    private FrameworkElement StepCard(QuestLines.Quest q, QuestLines.Step s, int no, bool done, bool current, bool connect)
    {
        var mark = _lines!.MarkOf(q, s);
        var row = new Grid { Margin = new Thickness(0, connect ? 4 : 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var circle = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2),
            BorderBrush = done ? GreenLine : current ? Gold : Line,
            Background = done ? GreenBg : current ? GoldBg : WinBg,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = done ? "✓" : no.ToString(), FontWeight = FontWeights.Bold, FontSize = 12,
                Foreground = done ? Green : current ? Gold : Hint,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(circle);

        var body = new StackPanel();
        // title + kind
        var h = new DockPanel();
        var kind = new TextBlock { Text = KindLabel(s), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = KindBrush(s), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(kind, Dock.Right);
        h.Children.Add(kind);
        h.Children.Add(new TextBlock { Text = s.Title, FontWeight = FontWeights.Bold, Foreground = done ? Dim : Brushes.White, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(h);
        // where
        var where = new TextBlock { Foreground = Hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        if (s.Npc.Length > 0) where.Inlines.Add(new System.Windows.Documents.Run(s.Npc) { Foreground = Dim, FontWeight = FontWeights.SemiBold });
        if (s.Zone.Length > 0) where.Inlines.Add(new System.Windows.Documents.Run((s.Npc.Length > 0 ? " · " : "") + s.Zone));
        if (s.Faction.Length > 0) where.Inlines.Add(new System.Windows.Documents.Run(" · " + s.Faction));
        body.Children.Add(where);
        // bring / yields / get
        var bring = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        var partial = _lines.PartialOf(q, s);
        if (s.Handin.Count > 0 || s.Bring.Count > 0 || s.Coins.Length > 0)
        {
            bring.Children.Add(Label(s.Kind == "combine" ? "Use" : "Bring"));
            foreach (var n in s.Handin.Concat(s.Bring))
            {
                bool sealedOff = partial.Contains("offer:" + LootTracker.ItemKey(n.Name)) || done;
                var hold = Holds(n.Name);
                string text = n.Count > 1 ? $"{n.Count}× {n.Name}" : n.Name;
                if (sealedOff && s.Handin.Any(x => x.Name == n.Name)) bring.Children.Add(ItemChip(text, "handed in", Faint, Hair, strike: true));
                else if (hold.Held) bring.Children.Add(ItemChip(text, hold.Where, Green, GreenLine));
                else bring.Children.Add(ItemChip(text, "not in your bags", Amber, AmberLine));
            }
            if (s.Coins.Length > 0) bring.Children.Add(ItemChip(s.Coins, "", Dim, Line));
        }
        if (s.KillAny.Count > 0)
        {
            bring.Children.Add(Label("Any one of"));
            foreach (var k in s.KillAny)
            {
                bool slain = QuestLines.KillSeen(partial, k);
                bring.Children.Add(ItemChip(k, slain ? "slain" : "", slain ? Green : Dim, slain ? GreenLine : Line));
            }
        }
        if (s.Loot.Count > 0)
        {
            bring.Children.Add(Label("Yields"));
            foreach (var l in s.Loot)
            {
                bool got = done || partial.Contains("loot:" + LootTracker.ItemKey(l));
                var hold = Holds(l);
                bring.Children.Add(got
                    ? ItemChip(l, hold.Held ? hold.Where : "looted", Green, GreenLine)
                    : ItemChip(l, current ? "not looted yet" : "", current ? Amber : Dim, current ? AmberLine : Line));
            }
        }
        if (s.Get.Count > 0)
        {
            bring.Children.Add(Label("Get"));
            foreach (var g in s.Get)
            {
                var hold = Holds(g);
                bring.Children.Add(hold.Held ? ItemChip(g, hold.Where, Green, GreenLine) : ItemChip(g, "", Dim, Line));
            }
        }
        if (s.Make.Count > 0)
        {
            bring.Children.Add(Label("Makes"));
            foreach (var mk in s.Make)
            {
                var hold = Holds(mk);
                bring.Children.Add(hold.Held ? ItemChip(mk, hold.Where, Green, GreenLine) : ItemChip(mk, "", Dim, Line));
            }
        }
        if (bring.Children.Count > 0) body.Children.Add(bring);
        // say / reply
        if (s.Say.Length > 0 || s.Reply.Length > 0)
        {
            var say = new TextBlock { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
            if (s.Say.Length > 0)
            {
                say.Inlines.Add(new System.Windows.Documents.Run("You say: ") { Foreground = Hint });
                say.Inlines.Add(new System.Windows.Documents.Run($"“{s.Say}”") { Foreground = Quote, FontStyle = FontStyles.Italic });
                if (s.Reply.Length > 0) say.Inlines.Add(new System.Windows.Documents.Run("   "));
            }
            if (s.Reply.Length > 0)
            {
                say.Inlines.Add(new System.Windows.Documents.Run(FirstName(s.Npc) + ": ") { Foreground = Hint });
                say.Inlines.Add(new System.Windows.Documents.Run($"“{s.Reply}”") { Foreground = Quote, FontStyle = FontStyles.Italic });
            }
            body.Children.Add(say);
        }
        if (s.Note.Length > 0 && (s.Say.Length == 0 || s.Reply.Length == 0))
            body.Children.Add(new TextBlock { Text = s.Note, Foreground = Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (s.Caution.Length > 0)
            body.Children.Add(new TextBlock { Text = "⚠ " + s.Caution, Foreground = Amber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        // evidence + switch
        var ev = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var sw = new CheckBox
        {
            IsChecked = done,
            Content = s.Click.Length > 0 ? "right-clicked" : "done",
            FontSize = 11, Foreground = Hint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            ToolTip = s.Click.Length > 0
                ? (s.ClickText.Length > 0 ? $"Right-clicking {s.Click} prints a line the log reads — flip this only if it didn't" : $"The log can't see a right-click — flip this once you've clicked {s.Click}")
                : "Tick a step the log missed; a step the log proved itself stays ticked (Data → reset clears it)",
        };
        sw.Checked += (_, _) => { if (!done) _lines.Tick(q, s); };
        sw.Unchecked += (_, _) => { if (done && !_lines.Untick(q, s)) sw.IsChecked = true; };
        DockPanel.SetDock(sw, Dock.Right);
        ev.Children.Add(sw);
        var evText = new TextBlock { FontSize = 10.5, Foreground = Faint, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (mark is not null)
        {
            evText.Inlines.Add(EvTag(mark.How == "you" ? "you" : mark.How == "implied" ? "implied" : "auto",
                mark.How == "you" ? Gold : Green));
            evText.Inlines.Add(new System.Windows.Documents.Run(" " + (mark.How == "auto" ? mark.Evidence : mark.Evidence + $" · {mark.When:d MMM HH:mm}")));
        }
        else if (partial.Count > 0)
        {
            evText.Inlines.Add(EvTag("partly", Amber));
            evText.Inlines.Add(new System.Windows.Documents.Run(" " + string.Join(" · ", _lines.EvidenceOf(q, s))));
            if (_lines.AwaitsClick(q, s)) evText.Inlines.Add(new System.Windows.Documents.Run(s.ClickText.Length > 0 ? $" — now right-click {s.Click}; the log checks it off" : $" — now right-click {s.Click} and flip the switch"));
        }
        else
        {
            evText.Inlines.Add(EvTag(current ? "waiting" : "later", Faint));
            evText.Inlines.Add(new System.Windows.Documents.Run(" " + Proof(s)));
        }
        ev.Children.Add(evText);
        body.Children.Add(ev);

        var card = new Border
        {
            Background = Card, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 10),
            BorderBrush = current ? Gold : Brushes.Transparent, BorderThickness = new Thickness(1),
            Opacity = done || current ? 1 : 0.72,
            Child = body,
        };
        Grid.SetColumn(card, 2);
        row.Children.Add(card);
        return row;
    }

    private static string Proof(QuestLines.Step s)
    {
        var bits = new List<string>();
        foreach (var k in s.Kill) bits.Add($"\"You have slain {k}!\"");
        if (s.KillAny.Count > 0) bits.Add("the death of " + JoinOr(s.KillAny));
        if (s.Loot.Count > 0) bits.Add("the loot line");
        if (s.Make.Count > 0) bits.Add($"the game's combine line naming {Join(s.Make)}");
        if (s.Say.Length > 0) bits.Add($"\"You say, '{s.Say}'\"");
        if (s.HasTrade) bits.Add($"\"You complete the trade with {s.NpcNames.First()}\"");
        if (s.Click.Length > 0) bits.Add("your switch for the right-click");
        if (bits.Count == 0)
            return s.Kind switch
            {
                "combine" => "the wiki does not name what this makes — tick it once the combine is done",
                "buy" or "forage" or "ground" or "pickpocket" or "talk" => "the log prints nothing it could be told by — tick it yourself",
                _ => "tick it yourself",
            };
        return "checks itself off on " + Join(bits) + (s.Trade && s.Handin.Count == 0 ? " (the wiki does not name the item)" : "");
    }

    private static string KindLabel(QuestLines.Step s)
    {
        var bits = new List<string>();
        if (s.Optional) bits.Add("OPTIONAL");
        if (s.Say.Length > 0) bits.Add("SAY");
        if (s.Kill.Count > 0 || s.KillAny.Count > 0) bits.Add("KILL");
        if (s.Loot.Count > 0) bits.Add("LOOT");
        if (s.HasTrade) bits.Add("HAND IN");
        if (s.Click.Length > 0) bits.Add("CLICK");
        if (bits.Count == (s.Optional ? 1 : 0))
            bits.Add(s.Kind switch
            {
                "combine" => "COMBINE", "buy" => "BUY", "forage" => "FORAGE", "ground" => "PICK UP",
                "pickpocket" => "PICKPOCKET", "talk" => "TALK", var k => k.ToUpperInvariant(),
            });
        return string.Join(" · ", bits);
    }

    private static Brush KindBrush(QuestLines.Step s) =>
        s.Kill.Count > 0 || s.KillAny.Count > 0 ? Red : s.Say.Length > 0 ? Violet : s.HasTrade ? Cyan : s.Loot.Count > 0 ? Amber
        : s.Kind == "combine" ? Violet : s.Kind is "buy" or "forage" or "ground" or "pickpocket" ? Amber : Hint;

    // ---- test hooks ----
    internal IEnumerable<string> ListTextsForTest => ListHost.Children.OfType<FrameworkElement>().SelectMany(TextsOf);
    internal IEnumerable<string> DetailTextsForTest => DetailHost.Children.OfType<FrameworkElement>().SelectMany(TextsOf);
    internal string NoticeForTest => NoticeBox.Visibility == Visibility.Visible ? NoticeText.Text : "";
    internal void SetLensForTest(string lens) { _lens = lens; Refresh(); }
    internal void FlipPartForTest(string questKey, string part) { if (!_partFlips.Add(questKey + "|" + part)) _partFlips.Remove(questKey + "|" + part); Refresh(); }
    private static IEnumerable<string> TextsOf(DependencyObject root)
    {
        if (root is TextBlock tb)
        {
            yield return tb.Inlines.Count > 0 ? string.Concat(tb.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) : tb.Text;
            yield break;
        }
        int n = VisualTreeHelper.GetChildrenCount(root);
        if (n == 0)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var t in TextsOf(child)) yield return t;
            yield break;
        }
        for (int i = 0; i < n; i++)
            foreach (var t in TextsOf(VisualTreeHelper.GetChild(root, i))) yield return t;
    }

    private static string FirstName(string npc) =>
        npc.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Equals("Lord", StringComparison.OrdinalIgnoreCase) && !w.Equals("Brother", StringComparison.OrdinalIgnoreCase)).FirstOrDefault() ?? npc;

    private static string StatsLine(string stats) =>
        string.Join(" · ", stats.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("MAGIC ITEM", StringComparison.Ordinal) && !l.StartsWith("Race:", StringComparison.Ordinal)));

    // ---------------------------------------------------------------- held?

    /// <summary>Where a copy of the item sits: the inventory dump first
    /// (rewards never hit the ledger), then the ledger's looted-minus-handed-in.</summary>
    private (bool Held, string Where) Holds(string item)
    {
        var rows = DumpRows();
        if (rows is not null)
        {
            string key = FocusEffects.ItemKey(item);
            var hit = rows.FirstOrDefault(r => FocusEffects.ItemKey(r.Name) == key && !r.Name.EndsWith("(Exaltation)", StringComparison.Ordinal));
            if (hit is not null) return (true, hit.Location);
        }
        int n = _lines?.LedgerHeld(item) ?? 0;
        if (n > 0) return (true, rows is null ? "looted (no inventory snapshot)" : "looted after the snapshot");
        return (false, "");
    }

    private List<InventoryStore.CarryRow>? DumpRows()
    {
        string? path = _dumpFile?.Invoke();
        if (path is null || !System.IO.File.Exists(path)) return null;
        var t = System.IO.File.GetLastWriteTime(path);
        if (_dumpCache is not { } c || c.Path != path || c.Stamp != t)
        {
            try
            {
                var dump = InventoryStore.Parse(System.IO.File.ReadAllText(path));
                _dumpCache = (path, t, InventoryStore.CarryAll(dump).Rows);
            }
            catch { return null; }
        }
        return _dumpCache!.Value.Rows;
    }

    // ---------------------------------------------------------------- bits

    private static Border Chip(string text, string tag) => new()
    {
        Tag = tag,
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(9, 2, 9, 3),
        Margin = new Thickness(0, 0, 6, 0),
        Cursor = Cursors.Hand,
        Background = Field, BorderBrush = Line,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Hint },
    };

    private static Border Badge(string text, Brush fg, Brush line, Brush? bg = null) => new()
    {
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = line,
        Background = bg ?? Brushes.Transparent,
        Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 4, 4),
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = fg },
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Faint,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4),
    };

    private static Border ItemChip(string name, string sub, Brush fg, Brush line, bool strike = false)
    {
        var tb = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fg };
        tb.Inlines.Add(new System.Windows.Documents.Run(name) { TextDecorations = strike ? TextDecorations.Strikethrough : null });
        if (sub.Length > 0) tb.Inlines.Add(new System.Windows.Documents.Run(" · " + sub) { Foreground = Hint, FontWeight = FontWeights.Normal });
        return new Border
        {
            CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = line, Background = Field,
            Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 6, 4), Child = tb,
        };
    }

    private static System.Windows.Documents.Inline EvTag(string text, Brush fg) =>
        new System.Windows.Documents.Run(text.ToUpperInvariant()) { Foreground = fg, FontWeight = FontWeights.Bold, FontSize = 9.5 };

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
