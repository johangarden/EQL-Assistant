using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Tools window's Statistics page (1 Oct): your log, counted — a range
/// switch, six headline tiles, then the cards (most killed, raid targets, most
/// cast, most looted, where you were, what killed you, when you play, records,
/// skill-ups, odds and ends). Themed by hand; no stock controls.
/// </summary>
public sealed class StatisticsView : DockPanel
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Surface = F("#1B2130"), Line = F("#2A3347"), Edge = F("#3A4560"), Row = F("#1F2637"),
        Text = F("#E6ECF5"), Dim = F("#C9D4E3"), Hint = F("#7F93AD"), Faint = F("#5C6B82"),
        Gold = F("#E8C15A"), GoldEdge = F("#7A5B22"), GoldBg = F("#2A2210"), Green = F("#81C784"), Blue = F("#4FC3F7"), Violet = F("#B39DDB"), Red = F("#FF7A7A"), Teal = F("#4DD0C8");

    private Statistics? _stats;
    private RaidKills? _raids;
    private SpellLibrary? _library;
    private string _range = "all";
    private readonly StackPanel _top = new();
    private readonly StackPanel _body = new();
    private Segmented? _seg;

    public StatisticsView()
    {
        SetDock(_top, Dock.Top);
        Children.Add(_top);
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body });
    }

    public void Init(Statistics stats, RaidKills? raids, SpellLibrary? library, string range = "all")
    {
        _stats = stats; _raids = raids; _library = library; _range = range;
        Build();
    }

    /// <summary>Selftest: the summary painted last and the tile values.</summary>
    internal Statistics.Summary? SummaryForTest { get; private set; }
    internal List<string> TileLines { get; } = new();
    internal int CardCountForTest { get; private set; }
    internal string Range => _range;

    private DateTime? Since => _range switch { "30" => DateTime.Today.AddDays(-29), "7" => DateTime.Today.AddDays(-6), "today" => DateTime.Today, _ => null };

    public void Build()
    {
        _top.Children.Clear(); _body.Children.Clear(); TileLines.Clear(); CardCountForTest = 0;
        if (_stats is null) return;
        var s = _stats.Summarize(Since);
        SummaryForTest = s;

        // ---- the range and the span ----
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(new TextBlock { Text = "RANGE", Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _seg = new Segmented(new[] { new Segmented.Option("all", "All time"), new Segmented.Option("30", "Last 30 days"), new Segmented.Option("7", "Last 7 days"), new Segmented.Option("today", "Today") }, _range, "#E8C15A") { Margin = new Thickness(0, 0, 16, 0) };
        _seg.Changed += id => { _range = id; Build(); };
        bar.Children.Add(_seg);
        var span = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        if (s.First is { } f && s.Last is { } l)
        {
            span.Inlines.Add(new Run(f == l ? f.ToString("d MMM", CultureInfo.InvariantCulture) : $"{f.ToString("d MMM", CultureInfo.InvariantCulture)} → {l.ToString("d MMM", CultureInfo.InvariantCulture)}") { Foreground = Dim, FontWeight = FontWeights.SemiBold });
            span.Inlines.Add(new Run($" · {s.DaysWithPlay} day{(s.DaysWithPlay == 1 ? "" : "s")} with play · {s.Sessions} session{(s.Sessions == 1 ? "" : "s")}") { Foreground = Hint });
        }
        else span.Inlines.Add(new Run("Nothing counted in this range yet.") { Foreground = Hint });
        bar.Children.Add(span);
        _top.Children.Add(bar);
        if (s.First is null)
        {
            _body.Children.Add(new TextBlock { Text = _stats.Days.Count == 0 ? "Nothing counted yet — Data → Reparse fills this from your whole log, and it keeps counting live from here." : "Nothing in this range — pick a longer one.", Foreground = Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            return;
        }

        // ---- the tiles ----
        var tiles = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        double hours = Math.Max(0.01, s.Hours);
        string longest = s.Longest is { } lo ? $"longest {Hm(lo.End - lo.Start)} on {lo.Start.ToString("d MMM", CultureInfo.InvariantCulture)}" : "";
        tiles.Children.Add(Tile("HOURS PLAYED", N1(s.Hours), "h", $"{s.Sessions} session{(s.Sessions == 1 ? "" : "s")}{(longest.Length > 0 ? " · " + longest : "")}"));
        tiles.Children.Add(Tile("KILLS", N(s.Kills), "", $"{N1(s.Kills / hours)} an hour" + (s.BestKillHour.Count > 0 ? $" · best hour {s.BestKillHour.Count} ({s.BestKillHour.Hour.ToString("d MMM HH:00", CultureInfo.InvariantCulture)})" : "")));
        tiles.Children.Add(Tile("CASTS", N(s.Casts), "", $"{N(s.Interrupted)} interrupted · {N(s.Oom)} × out of mana"));
        tiles.Children.Add(Tile("DAMAGE DEALT", Big(s.Damage), "", $"{N(s.Hits)} hits · {(s.Hits > 0 ? (100.0 * s.Crits / s.Hits).ToString("0.0", CultureInfo.InvariantCulture) : "0")}% crits"));
        tiles.Children.Add(Tile("HEALED", Big(s.Healed), "", "hit points, by you"));
        tiles.Children.Add(Tile("DEATHS", N(s.Deaths), "", (s.Deaths > 0 ? $"one every {N1(s.Hours / s.Deaths)} h · " : "") + $"{s.Dings} ding{(s.Dings == 1 ? "" : "s")}"));
        _body.Children.Add(tiles);

        // ---- the cards ----
        var cards = new WrapPanel();
        cards.Children.Add(ListCard("MOST KILLED", $"{N(s.Kills)} kills", s.TopKills, Red, "", "Named mobs you killed; what your pet or a groupmate killed shows under raid targets, where the log names the boss."));
        var raid = s.AllKills.Where(kv => _raids?.IsTarget(kv.Key) == true).OrderByDescending(kv => kv.Value).Take(12).Select(kv => new Statistics.Top(kv.Key, kv.Value)).ToList();
        cards.Children.Add(ListCard("RAID TARGETS", "the Raid Kills window's list", raid, Violet, "×", raid.Count == 0 ? "No raid target down in this range." : ""));
        long mez = _library is null ? 0 : s.AllCasts.Where(kv => _library.FindByBaseName(kv.Key)?.Effect == "Mez").Sum(kv => (long)kv.Value);
        cards.Children.Add(ListCard("MOST CAST", $"{N(s.Casts)} casts · ranks folded", s.TopCasts, Blue, "",
            $"{N(s.Interrupted)} interrupted ({(s.Casts > 0 ? 100 * s.Interrupted / s.Casts : 0)}%) · {N(s.Resisted)} resisted · {N(s.Oom)} × \"Insufficient Mana\"" + (mez > 0 ? $" · {N(mez)} mezzes, {N(s.MezBreaks)} broken early" : "")));
        cards.Children.Add(ListCard("MOST LOOTED", $"{N(s.Loot)} items", s.TopLoot, Green, "",
            s.TopLootMobs.Count > 0 ? "Best loot mobs: " + string.Join(" · ", s.TopLootMobs.Select(t => $"{t.Name} {N((long)t.N)}")) : ""));
        cards.Children.Add(ListCard("WHERE YOU WERE", "hours in zone · visits", s.TopZones, Teal, " h", "Instances fold into their zone — \"Old Guk 1 (Awakened)\" counts under The Ruins of Old Guk."));
        cards.Children.Add(ListCard("WHAT KILLED YOU", $"{N(s.Deaths)} death{(s.Deaths == 1 ? "" : "s")}", s.TopKillers, Red, "×",
            s.Deaths > 0 ? $"A death every {N1(s.Hours / s.Deaths)} hours of play. The death recap keeps the last ones blow by blow." : "Not once. The death recap has nothing to say."));
        cards.Children.Add(WhenCard(s));
        cards.Children.Add(RecordsCard(s));
        cards.Children.Add(ListCard("SKILL-UPS & TRADESKILLS", $"{N(s.Combines)} combine{(s.Combines == 1 ? "" : "s")}" + (s.Combines + s.Fails > 0 ? $" · {N(s.Fails)} failed ({100 * s.Fails / Math.Max(1, s.Combines + s.Fails)}%)" : ""), s.TopSkills, Gold, "",
            "\"You have become better at …\" lines, every skill — the tradeskill helper keeps the per-skill ladder."));
        cards.Children.Add(OddsCard(s));
        _body.Children.Add(cards);
        _body.Children.Add(new TextBlock
        {
            Foreground = Faint, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = "Kills = \"You have slain\" lines (and kills the log credits to you by name). Casts = your begin-cast lines, ranks folded. Loot = your loot lines. Zone time = time between your lines while in a zone, gaps over 10 min left out. "
                 + "Hours played = minutes with at least one line. A session ends after 30 quiet minutes. Everything is counted once per minute of log, so a reparse or a merged log never double-counts.",
        });
    }

    private Border Tile(string k, string v, string unit, string sub)
    {
        TileLines.Add($"{k}: {v}{unit} — {sub}");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = k, Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        var val = new TextBlock { Margin = new Thickness(0, 3, 0, 0) };
        val.Inlines.Add(new Run(v) { Foreground = Text, FontSize = 22, FontWeight = FontWeights.Bold });
        if (unit.Length > 0) val.Inlines.Add(new Run(" " + unit) { Foreground = Hint, FontSize = 11, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(val);
        sp.Children.Add(new TextBlock { Text = sub, Foreground = Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        return new Border { Width = 190, Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 11), Margin = new Thickness(0, 0, 10, 10), Child = sp };
    }

    private Border Card(string title, string note, UIElement body, string foot, double width = 356)
    {
        CardCountForTest++;
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var n = new TextBlock { Text = note, Foreground = Hint, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(n, Dock.Right); head.Children.Add(n);
        head.Children.Add(new TextBlock { Text = title, Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        sp.Children.Add(head);
        sp.Children.Add(body);
        if (foot.Length > 0) sp.Children.Add(new TextBlock { Text = foot, Foreground = Faint, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        return new Border { Width = width, Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 0, 12, 12), Child = sp, VerticalAlignment = VerticalAlignment.Top };
    }

    private Border ListCard(string title, string note, List<Statistics.Top> rows, Brush tint, string unit, string foot)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        double max = rows.Count > 0 ? rows.Max(r => r.N) : 1;
        var tintBg = new SolidColorBrush(((SolidColorBrush)tint).Color) { Opacity = 0.14 }; tintBg.Freeze();
        int row = 0;
        foreach (var r in rows)
        {
            g.RowDefinitions.Add(new RowDefinition());
            var idx = new TextBlock { Text = (row + 1).ToString(), Foreground = Faint, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(idx, row); g.Children.Add(idx);
            var cell = new Grid { Margin = new Thickness(8, 0, 8, 4) };
            cell.Children.Add(new Border { Background = tintBg, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, 1) }.Also(b => b.SetBinding(WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = cell, Converter = new Frac(Math.Max(0.03, r.N / max)) })));
            var lbl = new TextBlock { Padding = new Thickness(6, 2, 6, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            lbl.Inlines.Add(new Run(r.Name) { Foreground = Dim, FontSize = 12 });
            if (r.Note.Length > 0) lbl.Inlines.Add(new Run("  " + r.Note) { Foreground = Faint, FontSize = 10.5 });
            cell.Children.Add(lbl);
            Grid.SetColumn(cell, 1); Grid.SetRow(cell, row); g.Children.Add(cell);
            var val = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            val.Inlines.Add(new Run(r.N == Math.Floor(r.N) ? N((long)r.N) : N1(r.N)) { Foreground = Text, FontSize = 12, FontWeight = FontWeights.SemiBold });
            if (unit.Length > 0) val.Inlines.Add(new Run(unit.TrimStart() == "×" ? " ×" : unit) { Foreground = Faint, FontSize = 10.5 });
            Grid.SetColumn(val, 2); Grid.SetRow(val, row); g.Children.Add(val);
            row++;
        }
        if (rows.Count == 0) { g.RowDefinitions.Add(new RowDefinition()); var none = new TextBlock { Text = "nothing in this range", Foreground = Faint, FontSize = 11.5 }; Grid.SetColumnSpan(none, 3); g.Children.Add(none); }
        return Card(title, note, g, foot);
    }

    /// <summary>A bar's width as a fraction of its host.</summary>
    private sealed class Frac : System.Windows.Data.IValueConverter
    {
        private readonly double _f; public Frac(double f) { _f = f; }
        public object Convert(object v, Type t, object p, CultureInfo c) => v is double w && w > 0 ? Math.Max(4, w * _f) : 4.0;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    private Border WhenCard(Statistics.Summary s)
    {
        var sp = new StackPanel();
        int peak = Math.Max(1, s.Hours24.Max()); int peakH = Array.IndexOf(s.Hours24, s.Hours24.Max());
        var hist = new Grid { Height = 70, Margin = new Thickness(0, 4, 0, 0) };
        for (int h = 0; h < 24; h++) hist.ColumnDefinitions.Add(new ColumnDefinition());
        for (int h = 0; h < 24; h++)
        {
            var b = new Border { Background = Gold, Opacity = h == peakH ? 0.9 : 0.35, CornerRadius = new CornerRadius(2, 2, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0), Height = Math.Max(2, 70.0 * s.Hours24[h] / peak), ToolTip = $"{h:00}:00 — {N(s.Hours24[h])} lines" };
            Grid.SetColumn(b, h); hist.Children.Add(b);
        }
        sp.Children.Add(hist);
        var ticks = new Grid();
        for (int h = 0; h < 24; h++) ticks.ColumnDefinitions.Add(new ColumnDefinition());
        for (int h = 0; h < 24; h += 3) { var t = new TextBlock { Text = $"{h:00}", Foreground = Faint, FontSize = 8.5 }; Grid.SetColumn(t, h); ticks.Children.Add(t); }
        sp.Children.Add(ticks);
        string[] names = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        int dmax = Math.Max(1, s.Dow.Max()); int peakD = Array.IndexOf(s.Dow, s.Dow.Max());
        var dow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        for (int i = 0; i < 7; i++) dow.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 7; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(3, 0, 3, 0) };
            cell.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Gold, Opacity = 0.15 + 0.75 * s.Dow[i] / dmax, Margin = new Thickness(0, 0, 0, 4) });
            cell.Children.Add(new TextBlock { Text = names[i], Foreground = Hint, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(cell, i); dow.Children.Add(cell);
        }
        sp.Children.Add(dow);
        string foot = $"Peak hour {peakH:00}:00, peak day {names[peakD]}." + (s.Longest is { } lo ? $" Longest session {Hm(lo.End - lo.Start)} ({lo.Start.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}); {s.Sessions} sessions, a session ends after 30 quiet minutes." : "");
        return Card("WHEN YOU PLAY", "log lines by hour of day · time by weekday", sp, foot, 724);
    }

    private Border RecordsCard(Statistics.Summary s)
    {
        var sp = new StackPanel();
        void One(string what, string how, string sub)
        {
            var g = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
            var right = new TextBlock { TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
            right.Inlines.Add(new Run(how) { Foreground = Text, FontSize = 12, FontWeight = FontWeights.SemiBold });
            if (sub.Length > 0) { right.Inlines.Add(new LineBreak()); right.Inlines.Add(new Run(sub) { Foreground = Faint, FontSize = 10.5 }); }
            DockPanel.SetDock(right, Dock.Right); g.Children.Add(right);
            g.Children.Add(new TextBlock { Text = what, Foreground = Hint, FontSize = 12, VerticalAlignment = VerticalAlignment.Top });
            sp.Children.Add(new Border { Child = g, Padding = new Thickness(0, 4, 0, 4), BorderBrush = Row, BorderThickness = new Thickness(0, 0, 0, 1) });
        }
        if (s.BigHit is { } bh) One("Biggest hit", N(bh.Value), $"{bh.What} · {bh.Target} · {bh.At.ToString("d MMM", CultureInfo.InvariantCulture)}");
        if (s.BestKillHour.Count > 0) One("Most kills in an hour", s.BestKillHour.Count.ToString(), s.BestKillHour.Hour.ToString("d MMM HH:00", CultureInfo.InvariantCulture));
        if (s.Longest is { } lo) One("Longest session", Hm(lo.End - lo.Start), $"{lo.Start.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} → {lo.End.ToString("HH:mm", CultureInfo.InvariantCulture)}");
        var bestDing = s.DingDays.OrderByDescending(d => d.Levels.Count).FirstOrDefault();
        if (bestDing.Levels is { Count: > 0 }) One("Fastest levelling", $"{bestDing.Levels.Count} ding{(bestDing.Levels.Count == 1 ? "" : "s")} in a day", $"{bestDing.Day.ToString("d MMM", CultureInfo.InvariantCulture)} · {bestDing.Levels.Min()} → {bestDing.Levels.Max()}");
        if (s.Hits > 0) One("Crit rate", (100.0 * s.Crits / s.Hits).ToString("0.0", CultureInfo.InvariantCulture) + "%", $"{N(s.Crits)} of {N(s.Hits)} hits");
        if (sp.Children.Count == 0) sp.Children.Add(new TextBlock { Text = "nothing in this range", Foreground = Faint, FontSize = 11.5 });
        return Card("RECORDS", "the biggest and the longest", sp, "Longest and highest-DPS fights live in Fight history, where they're ★-kept.");
    }

    private Border OddsCard(Statistics.Summary s)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var items = new (string, long)[] { ("Hails", s.Hails), ("Times stunned", s.Stunned), ("Things you said", s.Says), ("Tells sent", s.Tells), ("\"You gain experience!\"", s.Xp), ("Dings", s.Dings), ("Mez breaks", s.MezBreaks), ("Out of mana", s.Oom) };
        for (int i = 0; i < items.Length; i++)
        {
            if (i % 2 == 0) g.RowDefinitions.Add(new RowDefinition());
            var dp = new DockPanel();
            var v = new TextBlock { Text = N(items[i].Item2), Foreground = Text, FontSize = 12, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(v, Dock.Right); dp.Children.Add(v);
            dp.Children.Add(new TextBlock { Text = items[i].Item1, Foreground = Hint, FontSize = 12 });
            var cell = new Border { Child = dp, Padding = new Thickness(0, 3, 0, 3), BorderBrush = Row, BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetRow(cell, i / 2); Grid.SetColumn(cell, (i % 2) * 2); g.Children.Add(cell);
        }
        var sp = new StackPanel(); sp.Children.Add(g);
        if (s.DingDays.Count > 0)
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (day, levels) in s.DingDays.OrderByDescending(d => d.Levels.Count).Take(4).OrderBy(d => d.Day))
                wp.Children.Add(new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = Edge, Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(0, 0, 4, 4), Child = new TextBlock { Text = $"{day.ToString("d MMM", CultureInfo.InvariantCulture)} · {levels.Min()}→{levels.Max()}", Foreground = Hint, FontSize = 10.5 } });
            if (s.DingDays.Count > 4) wp.Children.Add(new TextBlock { Text = $"… {s.Dings} dings over {s.DingDays.Count} days", Foreground = Faint, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(wp);
        }
        return Card("ODDS & ENDS", "the lines nobody counts", sp, "");
    }

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string N1(double n) => n.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Big(long n) => n >= 1_000_000 ? (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + " M" : n >= 10_000 ? (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + " k" : N(n);
    private static string Hm(TimeSpan t) => t.TotalHours >= 1 ? $"{t.TotalHours:0.#} h" : $"{(int)t.TotalMinutes} min";
}

internal static class FluentExt
{
    public static T Also<T>(this T x, Action<T> f) { f(x); return x; }
}
