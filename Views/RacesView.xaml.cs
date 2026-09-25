using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Races tab (owner, 22 Sep): every race the achievements dump lists as
/// a badge — a ring filling with the average of its three factions, solid
/// with a rim tick once unlocked (DONE · YOU · AUTO · TASK), done first —
/// and, for the selected race, its factions as standing bars out of the
/// dump's max with the mobs from your own log that moved them and a plain
/// "N more kills". ★ tracks a race for the faction helper card.
/// </summary>
public partial class RacesView : UserControl
{
    private static readonly Brush Dim = Freeze("#C9D4E3");
    private static readonly Brush Hint = Freeze("#7F93AD");
    private static readonly Brush Faint = Freeze("#5C6B82");
    private static readonly Brush Gold = Freeze("#E8C15A");
    private static readonly Brush Green = Freeze("#81C784");
    private static readonly Brush GreenDark = Freeze("#3D7A46");
    private static readonly Brush Red = Freeze("#FF8A80");
    private static readonly Brush RedDark = Freeze("#7A2E33");
    private static readonly Brush Blue = Freeze("#4FC3F7");
    private static readonly Brush Ring = Freeze("#2A3347");
    private static readonly Brush Track = Freeze("#0F141E");
    private static readonly Brush Hair = Freeze("#1F2637");
    private static readonly Brush Panel2 = Freeze("#1B2130");
    private static readonly Brush Ink = Freeze("#0B1A10");
    private static readonly string[] Palette =
    {
        "#C9A15C", "#9FD9C3", "#86E0B0", "#E07F7F", "#6FBF73", "#6FA8F0", "#EFE3B0", "#C97C5D",
        "#EFA9C4", "#F2E063", "#D9A06F", "#4DB6AC", "#E8C15A", "#A883D9", "#C883E8", "#E06060", "#6FD3D3",
    };

    private RaceBook? _book;
    private string? _selected;

    public RacesView()
    {
        InitializeComponent();
        Unloaded += (_, _) => { if (_book is not null) _book.Changed -= OnChanged; };
    }

    public void Init(RaceBook? book)
    {
        if (_book is not null) _book.Changed -= OnChanged;
        _book = book;
        if (_book is not null) _book.Changed += OnChanged;
        _book?.RefreshDumps(force: true);
        Build();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Build);

    /// <summary>Selftest: the value texts of the selected race's factions.</summary>
    internal List<string> ValueTextsForTest()
    {
        var list = new List<string>();
        foreach (var row in DetailHost.Children.OfType<Grid>())
            foreach (var tb in row.Children.OfType<TextBlock>().Where(t => Grid.GetColumn(t) == 2))
                list.Add(new System.Windows.Documents.TextRange(tb.ContentStart, tb.ContentEnd).Text);
        return list;
    }

    /// <summary>Selftest hooks.</summary>
    public int RowCount { get; private set; }
    public IReadOnlyList<string> BadgeOrderForTest { get; private set; } = Array.Empty<string>();
    public string? SelectedForTest => _selected;

    public void Select(string race) { _selected = race; Build(); }

    public void Build()
    {
        BadgesHost.Children.Clear();
        DetailHost.Children.Clear();
        if (_book is null || !_book.HasDumps)
        {
            RowCount = 0;
            BadgeOrderForTest = Array.Empty<string>();
            SummaryText.Text = "";
            HintText.Text = "No dumps yet. In game, type  /outputfile achievements  and  /outputfile faction  — the two files land next to your inventory dump and this tab reads them: every race's three factions, how far each stands, and what your log has moved since.";
            return;
        }
        var races = _book.Views();
        RowCount = races.Count;
        BadgeOrderForTest = races.Select(r => r.Name).ToList();
        int done = races.Count(r => r.Done);
        int near = races.Count(r => !r.Done && r.Factions.Count > 0 && r.DoneCount >= r.Factions.Count - 1 && r.DoneCount > 0);
        string age = RaceBook.Age(_book.DumpAt);
        SummaryText.Text = $"{done} unlocked · {near} nearly · dumps {age}" + (_book.DumpAt is { } d && (DateTime.Now - d).TotalDays > 7 ? " — re-type /outputfile faction and achievements" : "");
        HintText.Text = "Click a race for its factions. ★ tracks it — the faction helper card then follows every hit that moves it.";

        _selected ??= races.FirstOrDefault(r => !r.Done)?.Name ?? races.FirstOrDefault()?.Name;

        int doneCount = races.Count(r => r.Done);
        for (int i = 0; i < races.Count; i++)
        {
            var r = races[i];
            if (i == doneCount && doneCount > 0 && doneCount < races.Count)
            {
                BadgesHost.Children.Add(new Border { Width = 1, Background = Freeze("#3A4560"), Margin = new Thickness(4, 12, 6, 12) });
            }
            BadgesHost.Children.Add(Badge(r, Freeze(Palette[Math.Abs(r.Name.GetHashCode(StringComparison.OrdinalIgnoreCase)) % Palette.Length])));
        }

        var sel = races.FirstOrDefault(r => r.Name.Equals(_selected, StringComparison.OrdinalIgnoreCase));
        if (sel is not null) BuildDetail(sel);
    }

    private UIElement Badge(RaceBook.RaceView r, Brush tint)
    {
        bool selected = r.Name.Equals(_selected, StringComparison.OrdinalIgnoreCase);
        var tintC = ((SolidColorBrush)tint).Color;
        var root = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(0, 0, 3, 3), Cursor = Cursors.Hand,
            Background = selected ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xC1, 0x2E)) : Brushes.Transparent,
            ToolTip = r.Done ? $"{r.Name} — unlocked" : r.Task is not null ? $"{r.Name} — {r.Task}" : $"{r.Name} — {r.DoneCount} of {r.Factions.Count} factions maxed",
            Tag = r.Name,
        };
        root.MouseLeftButtonUp += (_, _) => { _selected = r.Name; Build(); };
        var sp = new StackPanel { Width = 58 };
        // "HUMAN (QEYNOS)" wraps to two lines — "HUMAN" over "QEYNOS" — instead of
        // trimming to "HUMAN · QE…" (owner, 22 Sep). Single-word races keep a blank
        // second line so every ring sits on the same baseline.
        string abbr = r.Name.ToUpperInvariant().Replace(" (", "\n").Replace(")", "");
        if (!abbr.Contains('\n')) abbr += "\n";
        var top = new TextBlock
        {
            Text = abbr + (r.Tracked ? " ★" : ""), Foreground = r.Done ? Green : r.Tracked ? Gold : Hint, FontSize = 8.5,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 2),
            LineHeight = 10, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Height = 20,
        };
        sp.Children.Add(top);
        var g = new Grid { Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Center };
        g.Children.Add(new Ellipse { Stroke = Ring, StrokeThickness = 3 });
        if (r.Done) g.Children.Add(new Ellipse { Fill = tint, Margin = new Thickness(1.5) });
        var arc = Arc(r.Done ? 1 : r.Progress);
        if (arc is not null) g.Children.Add(new Path { Data = arc, Stroke = tint, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        g.Children.Add(new TextBlock
        {
            Text = Monogram(r.Name), FontSize = 10.5, FontWeight = FontWeights.ExtraBold,
            Foreground = r.Done ? Ink : new SolidColorBrush(Color.FromArgb(0xD8, tintC.R, tintC.G, tintC.B)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        if (r.Done)
        {
            g.Children.Add(new Border
            {
                Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Background = Green, BorderBrush = Freeze("#141A24"), BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -4, 0),
                Child = new TextBlock { Text = "✓", Foreground = Ink, FontSize = 8, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) },
            });
        }
        sp.Children.Add(g);
        sp.Children.Add(new TextBlock
        {
            Text = r.CountText, FontSize = 9.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
            Foreground = r.Done ? Green : Hint, FontWeight = r.Done ? FontWeights.Bold : FontWeights.Normal,
        });
        root.Child = sp;
        return root;
    }

    private void BuildDetail(RaceBook.RaceView r)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var star = new Button
        {
            Content = r.Tracked ? "★ Tracked — the helper follows it" : "☆ Track for the helper card",
            Padding = new Thickness(10, 3, 10, 3), FontSize = 11.5, Cursor = Cursors.Hand,
            Foreground = r.Tracked ? Gold : Dim, Background = Panel2, BorderBrush = r.Tracked ? Freeze("#7A5B22") : Freeze("#3A4560"), BorderThickness = new Thickness(1),
            ToolTip = "The faction helper card only speaks for tracked races",
        };
        star.Click += (_, _) => _book?.SetTracked(r.Name, !r.Tracked);
        DockPanel.SetDock(star, Dock.Right);
        head.Children.Add(star);
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new System.Windows.Documents.Run(r.Name));
        string sub = r.Done ? (r.Note == "YOU" ? "  ·  the race you were created as" : r.Note == "AUTO" ? $"  ·  unlocked through {r.DependsOn}" : "  ·  unlocked")
            : r.Task is not null ? $"  ·  {r.Task}"
            : r.DependsOn is not null ? $"  ·  unlocks with {r.DependsOn}"
            : $"  ·  {r.DoneCount} of {r.Factions.Count} factions maxed";
        title.Inlines.Add(new System.Windows.Documents.Run(sub) { FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = Hint });
        head.Children.Add(title);
        DetailHost.Children.Add(head);

        foreach (var f in r.Factions)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            var name = new TextBlock { Text = f.Name, Foreground = Dim, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(name);

            var bar = new Grid { Height = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            bar.Children.Add(new Border { Background = f.Negative && !f.Done ? Freeze("#2A1416") : Track, CornerRadius = new CornerRadius(4) });
            if (f.Done)
            {
                // MAXED is MAXED — a full bar, even when your ceiling sits under
                // the dump's 2,000 (owner, 25 Sep: "says MAXED but bars are not").
                bar.Children.Add(new Border { Background = new LinearGradientBrush(((SolidColorBrush)GreenDark).Color, ((SolidColorBrush)Green).Color, 0), CornerRadius = new CornerRadius(4) });
            }
            else if (f.Negative)
            {
                double frac = Math.Clamp(-f.Standing / (double)Math.Max(1, f.Max), 0, 1);
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - frac), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, frac), GridUnitType.Star) });
                var fill = new Border { Background = new LinearGradientBrush(((SolidColorBrush)RedDark).Color, ((SolidColorBrush)Red).Color, 0), CornerRadius = new CornerRadius(4) };
                Grid.SetColumn(fill, 1);
                Grid.SetColumnSpan(bar.Children[0], 2);
                bar.Children.Add(fill);
            }
            else
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, f.Fraction), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - f.Fraction), GridUnitType.Star) });
                Grid.SetColumnSpan(bar.Children[0], 2);
                if (f.Fraction > 0.001)
                    bar.Children.Add(new Border { Background = new LinearGradientBrush(((SolidColorBrush)GreenDark).Color, ((SolidColorBrush)Green).Color, 0), CornerRadius = new CornerRadius(4) });
            }
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var val = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Hint };
            if (f.Done && f.CappedBelowMax) { val.Inlines.Add(new System.Windows.Documents.Run("MAXED ") { Foreground = Green, FontWeight = FontWeights.Bold }); val.Inlines.Add(new System.Windows.Documents.Run($"· your cap {f.Standing:N0}")); val.ToolTip = $"The game says this can't get any better for you — race, class and deity modifiers put your ceiling at {f.Standing:N0}, under the dump's {f.Max:N0}."; }
            else if (f.Done) { val.Inlines.Add(new System.Windows.Documents.Run("MAXED ") { Foreground = Green, FontWeight = FontWeights.Bold }); val.Inlines.Add(new System.Windows.Documents.Run($"{Math.Min(f.Standing, f.Max):N0} / {f.Max:N0}")); }
            else if (!f.Known) val.Inlines.Add(new System.Windows.Documents.Run("not in the faction dump") { Foreground = Faint });
            else if (f.Negative) { val.Inlines.Add(new System.Windows.Documents.Run($"{f.Standing:N0}") { Foreground = Red, FontWeight = FontWeights.SemiBold }); val.Inlines.Add(new System.Windows.Documents.Run($" · {f.ToGo:N0} to max")); }
            else { val.Inlines.Add(new System.Windows.Documents.Run($"{f.Standing:N0}") { Foreground = Dim, FontWeight = FontWeights.SemiBold }); val.Inlines.Add(new System.Windows.Documents.Run($" / {f.Max:N0} · {f.ToGo:N0} to max")); }
            Grid.SetColumn(val, 2);
            row.Children.Add(val);
            DetailHost.Children.Add(row);

            if (!f.Done)
            {
                var src = new TextBlock { FontSize = 11.5, Foreground = Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 0) };
                var pos = f.Sources.Where(s => s.Hit > 0).Take(3).ToList();
                var neg = f.Sources.Where(s => s.Hit < 0).Take(2).ToList();
                if (pos.Count == 0 && neg.Count == 0)
                    src.Inlines.Add(new System.Windows.Documents.Run("no hits in your log yet — the tab learns the mob the first time one lands") { Foreground = Faint });
                else
                {
                    src.Inlines.Add(new System.Windows.Documents.Run("hits in your log: "));
                    foreach (var s in pos)
                    {
                        src.Inlines.Add(new System.Windows.Documents.Run($"{s.Mob} +{s.Hit}") { Foreground = Green, FontWeight = FontWeights.SemiBold });
                        src.Inlines.Add(new System.Windows.Documents.Run($" ×{s.Count}   ") { Foreground = Faint });
                    }
                    foreach (var s in neg)
                    {
                        src.Inlines.Add(new System.Windows.Documents.Run($"{s.Mob} {s.Hit}") { Foreground = Red });
                        src.Inlines.Add(new System.Windows.Documents.Run($" ×{s.Count}   ") { Foreground = Faint });
                    }
                    if (f.EstimateKills is int est && pos.Count > 0)
                    {
                        src.Inlines.Add(new System.Windows.Documents.Run("→ "));
                        src.Inlines.Add(new System.Windows.Documents.Run(est == 1 ? "1 more kill" : $"~{est:N0} more kills") { Foreground = Dim, FontWeight = FontWeights.SemiBold });
                        src.Inlines.Add(new System.Windows.Documents.Run($" of {pos[0].Mob}"));
                    }
                }
                DetailHost.Children.Add(src);
            }
            DetailHost.Children.Add(new Border { Height = 1, Background = Hair, Margin = new Thickness(0, 6, 0, 0) });
        }
        if (r.Factions.Count == 0 && r.Task is not null)
            DetailHost.Children.Add(new TextBlock { Text = $"This race unlocks by completing the task '{r.Task}' — no factions to farm.", Foreground = Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap });
    }

    private static string Monogram(string race)
    {
        string n = race.ToUpperInvariant();
        int p = n.IndexOf(" (", StringComparison.Ordinal);
        if (p > 0) return n[(p + 2)..].TrimEnd(')')[..Math.Min(3, n.Length - p - 3)];
        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2) return (words[0][..1] + words[1][..Math.Min(2, words[1].Length)]);
        return n[..Math.Min(3, n.Length)];
    }

    /// <summary>A completion arc from 12 o'clock, clockwise, radius 17 in a 38 px box.</summary>
    private static Geometry? Arc(double fraction)
    {
        if (fraction <= 0) return null;
        if (fraction >= 0.999) return new EllipseGeometry(new Point(19, 19), 17, 17);
        double angle = fraction * 2 * Math.PI;
        var start = new Point(19, 2);
        var end = new Point(19 + 17 * Math.Sin(angle), 19 - 17 * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(17, 17), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
