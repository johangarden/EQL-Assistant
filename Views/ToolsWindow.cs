using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Tools window (owner, 29 Sep: "we built a lot of tools lately and placed
/// them under other functions — pull them out into a Tools section"). One
/// place for everything you open ON PURPOSE to make a decision: spell
/// efficiency, invocations, the BiS finder, race unlocks, resists and the
/// tradeskill helper (its card is shown and hidden from here — the toolbar's
/// hammer opens this window). Live panels stay under ☰ → Panels; records
/// (loot, raid kills, fights, charmed pets) stay in their own windows.
/// </summary>
public sealed class ToolsWindow : Window
{
    /// <summary>What the tools need from the app.</summary>
    public sealed class Context
    {
        public required SpellLibrary Library { get; init; }
        public SpellYield? Yield { get; init; }
        public Func<string> Classes { get; init; } = () => "";
        public Func<int> Level { get; init; } = () => 0;
        public Func<string> Snapshot { get; init; } = () => "";
        public Func<string?> LogPath { get; init; } = () => null;
        public string? ViewStatePath { get; init; }
        public RaceBook? Races { get; init; }
        public ResistBook? Resists { get; init; }
        public Func<string> Zone { get; init; } = () => "";
        public required TradeskillData TsData { get; init; }
        public TradeskillWatch? Ts { get; init; }
        public Func<bool> TsVisible { get; init; } = () => false;
        public Func<string> TsSkill { get; init; } = () => "";
        public Action ToggleTs { get; init; } = () => { };
        public Action<string> PickTs { get; init; } = _ => { };
        public Func<(List<InventoryStore.CarryRow>? Rows, DateTime Stamp)> Dump { get; init; } = () => (null, default);
        public ConfigService? Config { get; init; }
        public Func<string> CharKey { get; init; } = () => "";
    }

    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush RailBg = F("#121824"), Surface = F("#1B2130"), Card = F("#232B3D"), Line = F("#2A3347"), Edge = F("#3A4560"),
        Text = F("#E6ECF5"), Dim = F("#C9D4E3"), Hint = F("#7F93AD"), Faint = F("#5C6B82"), Gold = F("#E8C15A"), GoldBg = F("#2A2210"),
        GoldEdge = F("#7A5B22"), Green = F("#81C784"), GreenDark = F("#3D7A46"), Warn = F("#FFB074");

    public static readonly (string Id, string Label, string Icon)[] Pages =
    {
        ("home", "Home", "M4 11 L12 4 L20 11 L20 20 L4 20 Z"),
        ("eff", "Spell efficiency", "M4 20 L4 10 L6 10 L6 20 Z M10 20 L10 4 L12 4 L12 20 Z M16 20 L16 13 L18 13 L18 20 Z"),
        ("inv", "Invocations", "M12 4 A8 8 0 1 0 12.01 4 Z M11 7 L13 7 L13 12 L16 14 L15 15.6 L11 13 Z"),
        ("bis", "BiS finder", "M12 3 L19 6 L19 12 C19 16 16 19 12 21 C8 19 5 16 5 12 L5 6 Z"),
        ("races", "Race unlocks", "M9 5 A3.5 3.5 0 1 0 9.01 5 Z M3 20 C3 15 6 13 9 13 C12 13 15 15 15 20 Z M16.5 7 A2.8 2.8 0 1 0 16.51 7 Z M16 20 C16 16 17 14.5 18.5 14.5 C20 14.5 21.5 16 21.5 20 Z"),
        ("res", "Resists", "M12 3 C15 7 18 9 18 13 A6 6 0 0 1 6 13 C6 9 9 7 12 3 Z"),
        ("ts", "Tradeskills", "M5 21 L4 20 L13 11 L14 12 Z M11.5 6.5 L14.5 3.5 L20.5 9.5 L17.5 12.5 Z"),
    };

    private readonly Context _c;
    private readonly StackPanel _rail = new() { Margin = new Thickness(8, 12, 8, 12) };
    private readonly ContentControl _host = new();
    private string _page = "home";
    private SpellLibraryPanel? _library;
    private BisFinderView? _bis;
    private RacesView? _races;
    private ResistsView? _resists;

    public ToolsWindow(Context context)
    {
        _c = context;
        Title = "EQL Assistant — Tools";
        // Wide enough for the Efficiency table beside the rail.
        Width = Math.Min(1320, SystemParameters.WorkArea.Width - 40); Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
        MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Brush.Window");
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/assets/eqloverlay.ico"));
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Themes/Controls.xaml", UriKind.Relative) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rail = new Border { Background = RailBg, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = _rail };
        grid.Children.Add(rail);
        var main = new Border { Padding = new Thickness(16, 14, 16, 14), Child = _host };
        Grid.SetColumn(main, 1);
        grid.Children.Add(main);
        Content = grid;
        WindowTheme.ApplyDark(this);
        ShowPage("home");
    }

    /// <summary>Selftest / deep links: the page showing.</summary>
    public string PageShown => _page;

    /// <summary>Selftest: the home cards' live lines, in order.</summary>
    internal List<string> HomeLines { get; } = new();

    public void ShowPage(string id)
    {
        if (Pages.All(p => p.Id != id)) id = "home";
        _page = id;
        BuildRail();
        _host.Content = id switch
        {
            "eff" or "inv" => LibraryPage(id == "eff" ? "efficiency" : "invocations"),
            "bis" => BisPage(),
            "races" => RacesPage(),
            "res" => ResistsPage(),
            "ts" => TradeskillPage(),
            _ => HomePage(),
        };
    }

    /// <summary>The tradeskill card came or went (the toolbar, ☰, the card's ✕) — mirror it.</summary>
    public void TradeskillChanged() { if (_page is "home" or "ts") ShowPage(_page); }

    private void BuildRail()
    {
        _rail.Children.Clear();
        _rail.Children.Add(new TextBlock { Text = "Tools", Foreground = Text, FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 2, 0, 10) });
        foreach (var (id, label, icon) in Pages)
        {
            bool on = id == _page;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), Fill = on ? Gold : Hint, Width = 15, Height = 15, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, Foreground = on ? Gold : Hint, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var b = new Border
            {
                Child = row, Padding = new Thickness(9, 7, 9, 7), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand,
                Background = on ? GoldBg : Brushes.Transparent, BorderBrush = Gold, BorderThickness = new Thickness(on ? 2 : 0, 0, 0, 0),
                Margin = new Thickness(0, 0, 0, 2), ToolTip = label,
            };
            string pid = id;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowPage(pid); };
            b.MouseEnter += (_, _) => { if (pid != _page) b.Background = Surface; };
            b.MouseLeave += (_, _) => { if (pid != _page) b.Background = Brushes.Transparent; };
            _rail.Children.Add(b);
            if (id == "home") _rail.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(6, 6, 6, 8) });
        }
    }

    // ---- home ----------------------------------------------------------------------

    private UIElement HomePage()
    {
        HomeLines.Clear();
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Tools", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.Bold });
        sp.Children.Add(new TextBlock
        {
            Text = "Everything you open on purpose to make a decision. Live panels stay under ☰ → Panels; records (loot, raid kills, fights, charmed pets) stay in their own windows.",
            Foreground = Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
        });
        sp.Children.Add(TradeskillCard(compact: true));
        var cards = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (id, label, icon) in Pages.Where(p => p.Id is not ("home" or "ts")))
        {
            var (purpose, live) = HomeText(id);
            HomeLines.Add(live);
            cards.Children.Add(ToolCard(id, label, icon, purpose, live));
        }
        sp.Children.Add(cards);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp };
    }

    private (string Purpose, string Live) HomeText(string id)
    {
        var combo = _c.Classes().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        try
        {
            switch (id)
            {
                case "eff":
                {
                    var band = SpellEfficiency.Bands[SpellEfficiency.BandFor(_c.Level())];
                    var best = SpellEfficiency.Rows(_c.Library, _c.Yield, combo, band.Lo, band.Hi, false, 1).OrderByDescending(r => r.BestPerMana).FirstOrDefault();
                    return ("Damage and healing per mana — the wiki, at your rank, and yours.",
                        best is null ? "Type /who in game for your classes." : $"Best per mana at {band.Label}: {best.Spell.Name} · {best.BestPerMana:0.#} a mana{(best.Rank is > 0 and var rk ? $" at {SpellEfficiency.Roman(rk)}" : "")}");
                }
                case "inv":
                {
                    string s = _library?.InvocationSummary ?? "";
                    return ("Your last fights replayed under each invocation.", s.Length > 0 ? s : "Open it to replay your last hour of fights.");
                }
                case "bis":
                {
                    EnsureBis();
                    int n = _bis?.UpgradeCount ?? 0;
                    return ("The best of what you own, slot by slot.",
                        _bis is null || !_bis.HasBoard ? "Type /outputfile inventory in game for your gear." : n > 0 ? $"{n} upgrade(s) sitting in storage" : "You're wearing your best.");
                }
                case "races":
                {
                    var views = _c.Races?.Views() ?? new List<RaceBook.RaceView>();
                    var tracked = views.Where(v => v.Tracked && !v.Done).ToList();
                    return ("Every race's factions, and the mobs that move them.",
                        views.Count == 0 ? "Type /outputfile achievements and /outputfile faction in game."
                        : tracked.Count > 0 ? string.Join(" · ", tracked.Select(t => $"★ {t.Name} {t.DoneCount}/{t.Factions.Count}"))
                        : $"{views.Count(v => v.Done)} of {views.Count} races unlocked — ★ one to follow it");
                }
                case "res":
                {
                    var worst = (_c.Resists?.ByMob("", "") ?? new List<(string, int, string, IReadOnlyList<ResistBook.Cell>)>())
                        .SelectMany(g => _c.Resists!.Notable(g.Mob)).OrderByDescending(v => v.Rate).FirstOrDefault();
                    return ("Which mobs shrug off which spells — from your own casts.",
                        worst is null ? "Nothing notable yet — it learns from every cast." : $"Worst: {worst.Mob} resists {worst.Spell} {worst.Rate * 100:0}%");
                }
            }
        }
        catch (Exception ex) { Log.Warn("tools home: " + ex.Message); }
        return ("", "");
    }

    private Border ToolCard(string id, string label, string icon, string purpose, string live)
    {
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Background = Card, BorderBrush = Edge, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 9, 0),
            Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), Fill = Gold, Width = 16, Height = 16, Stretch = Stretch.Uniform },
        });
        top.Children.Add(new TextBlock { Text = label, Foreground = Text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock { Text = purpose, Foreground = Hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new Border
        {
            BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(0, 6, 0, 0),
            Child = new TextBlock { Text = live, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap },
        });
        var card = new Border
        {
            Width = 262, MinHeight = 128, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12, 11, 12, 12), CornerRadius = new CornerRadius(7),
            Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = sp,
        };
        card.MouseEnter += (_, _) => card.BorderBrush = Edge;
        card.MouseLeave += (_, _) => card.BorderBrush = Line;
        card.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowPage(id); };
        return card;
    }

    // ---- tradeskills ---------------------------------------------------------------------

    /// <summary>The card switch, the skill, your value and step — the home's top card and the page's head.</summary>
    private Border TradeskillCard(bool compact)
    {
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Background = Card, BorderBrush = Edge, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 9, 0),
            Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(Pages.First(p => p.Id == "ts").Icon), Fill = Gold, Width = 16, Height = 16, Stretch = Stretch.Uniform },
        });
        top.Children.Add(new TextBlock { Text = "Tradeskill helper", Foreground = Text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(top);

        var row = new WrapPanel();
        bool shown = _c.TsVisible();
        row.Children.Add(Switch(shown, shown ? "Card shown" : "Card hidden", () => { _c.ToggleTs(); ShowPage(_page); }));
        var picker = new ComboBox { Width = 150, MinHeight = 26, Margin = new Thickness(14, 0, 0, 0), ItemsSource = TradeskillData.Names, ToolTip = "The skill the card follows" };
        string skill = _c.TsSkill();
        picker.SelectedItem = TradeskillData.Names.FirstOrDefault(n => n.Equals(TradeskillData.Canonical(skill), StringComparison.OrdinalIgnoreCase));
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is string s && !s.Equals(_c.TsSkill(), StringComparison.OrdinalIgnoreCase)) { _c.PickTs(s); ShowPage(_page); } };
        row.Children.Add(picker);
        var sk = _c.TsData.Find(skill);
        if (sk is not null)
        {
            int? v = _c.Ts?.ValueOf(sk.Name);
            var steps = sk.Steps.ToList();
            int idx = v is { } vv ? Math.Max(0, steps.FindIndex(s => vv < s.To)) : 0;
            if (v is { } v2 && steps.Count > 0 && v2 >= steps[^1].To) idx = steps.Count - 1;
            row.Children.Add(Pill($"{(v?.ToString() ?? "?")} / {sk.Cap} · step {idx + 1} of {steps.Count}"));
            if (compact)
            {
                var open = Pill("Open the ladder →");
                open.Cursor = Cursors.Hand;
                open.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowPage("ts"); };
                row.Children.Add(open);
            }
            sp.Children.Add(row);
            var ladder = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0), Height = 6 };
            for (int i = 0; i < steps.Count; i++)
                ladder.Children.Add(new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 3, 0), Background = i < idx ? GreenDark : i == idx ? Gold : Card });
            sp.Children.Add(ladder);
        }
        else sp.Children.Add(row);
        sp.Children.Add(new TextBlock
        {
            Text = "The card floats over the game while you combine — this switch shows and hides it (the toolbar used to have an anvil for it).",
            Foreground = Hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 11, 12, 12), Child = sp, MaxWidth = 1086, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    /// <summary>Selftest: the ladder rows painted last on the Tradeskills page.</summary>
    internal List<string> LadderLines { get; } = new();

    private UIElement TradeskillPage()
    {
        LadderLines.Clear();
        var sp = new StackPanel();
        sp.Children.Add(PageTitle("Tradeskills", "The card's switch, the skill it follows, and the whole leveling ladder with what each step needs."));
        sp.Children.Add(TradeskillCard(compact: false));
        var sk = _c.TsData.Find(_c.TsSkill());
        if (sk is null)
        {
            sp.Children.Add(new TextBlock { Text = "Pick a skill above.", Foreground = Hint, FontSize = 12, Margin = new Thickness(2, 12, 0, 0) });
            return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp };
        }
        int? v = _c.Ts?.ValueOf(sk.Name);
        foreach (var tier in sk.Tiers)
        {
            sp.Children.Add(new TextBlock { Text = tier.Name.ToUpperInvariant(), Foreground = Gold, FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(2, 14, 0, 4) });
            foreach (var step in tier.Steps)
            {
                bool now = v is { } vv && vv >= step.From && vv < step.To;
                bool done = v is { } vd && vd >= step.To;
                var recipe = _c.TsData.RecipeFor(step.Recipe);
                string tag = recipe is null ? "CHECK" : _c.TsData.SourcingOf(recipe).Tag;
                var g = new Grid { Margin = new Thickness(0, 0, 0, 1) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = $"{step.From}–{step.To}", Foreground = done ? Faint : now ? Gold : Hint, FontSize = 12, FontWeight = now ? FontWeights.Bold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
                var name = new TextBlock { Text = step.Title + (recipe?.Container is { Length: > 0 } ct ? $"  ·  {ct}" : ""), Foreground = done ? Faint : now ? Text : Dim, FontSize = 12.5, FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1); g.Children.Add(name);
                var tg = new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), BorderThickness = new Thickness(1), Margin = new Thickness(10, 0, 0, 0),
                    BorderBrush = tag == "ALL BOUGHT" ? GreenDark : GoldEdge, Background = tag == "ALL BOUGHT" ? F("#12261C") : GoldBg,
                    Child = new TextBlock { Text = tag, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = tag == "ALL BOUGHT" ? Green : Warn },
                };
                Grid.SetColumn(tg, 2); g.Children.Add(tg);
                sp.Children.Add(new Border { Child = g, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(4), Background = now ? GoldBg : Brushes.Transparent, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1) });
                LadderLines.Add($"{step.From}-{step.To} {step.Title} {tag}{(now ? " NOW" : "")}");
            }
        }
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp };
    }

    // ---- the tool pages -------------------------------------------------------------------

    private UIElement LibraryPage(string tab)
    {
        _library ??= new SpellLibraryPanel(_c.Library, _ => { }, null, _c.Yield, _c.Classes, _c.Level, _c.ViewStatePath, _c.Snapshot, _c.LogPath,
            new[] { "efficiency", "invocations" }) { TabRowShown = false };
        _library.Heading = tab == "efficiency" ? "Spell efficiency" : "Invocations";
        _library.ShowTab(tab);
        return _library;
    }

    /// <summary>Selftest / render: the shared spell library panel (Efficiency · Invocations).</summary>
    internal SpellLibraryPanel? LibraryForTest => _library;

    private void EnsureBis()
    {
        if (_bis is not null) return;
        var (rows, stamp) = _c.Dump();
        if (rows is null) return;
        _bis = new BisFinderView();
        _bis.Init(new ItemStats(), _c.Config, _c.CharKey());
        _bis.LevelProvider = _c.Level;
        _bis.Update(rows, _c.Classes(), stamp.ToString("dd MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    private UIElement BisPage()
    {
        EnsureBis();
        if (_bis is null)
            return Framed("BiS finder", "The best of what you own, slot by slot.",
                new TextBlock { Text = "No inventory dump found — type /outputfile inventory in game, then open this page again.", Foreground = Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var (rows, stamp) = _c.Dump();
        if (rows is not null) _bis.Update(rows, _c.Classes(), stamp.ToString("dd MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        return Framed("BiS finder", "The best of what you own, slot by slot — moved here from the Character window.", _bis);
    }

    /// <summary>Selftest: the BiS finder once built.</summary>
    internal BisFinderView? BisForTest => _bis;

    private UIElement RacesPage()
    {
        _races ??= new RacesView();
        _races.Init(_c.Races);
        return Framed("Race unlocks", "Every race's factions, and the mobs that move them — moved here from the Character window. The faction helper card still floats over the game for ★-tracked races.", _races);
    }

    /// <summary>Selftest: the races tab once built.</summary>
    internal RacesView? RacesForTest => _races;

    private UIElement ResistsPage()
    {
        _resists ??= new ResistsView();
        _resists.Init(_c.Resists, _c.Zone);
        return Framed("Resists", "Per mob, per spell: landed vs resisted, from your own casts. Fight history shows the same table.", _resists);
    }

    // ---- bits ---------------------------------------------------------------------------------

    private static UIElement PageTitle(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.Bold });
        sp.Children.Add(new TextBlock { Text = sub, Foreground = Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    private static UIElement Framed(string title, string sub, UIElement body)
    {
        var dp = new DockPanel();
        var t = PageTitle(title, sub);
        DockPanel.SetDock(t, Dock.Top);
        dp.Children.Add(t);
        dp.Children.Add(body);
        return dp;
    }

    private static Border Pill(string text) => new()
    {
        CornerRadius = new CornerRadius(12), BorderBrush = Edge, BorderThickness = new Thickness(1), Background = Card, Padding = new Thickness(10, 3, 10, 4),
        Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = Dim, FontSize = 11.5, FontWeight = FontWeights.SemiBold },
    };

    private static Border Switch(bool on, string label, Action toggle)
    {
        var track = new Border { Width = 34, Height = 18, CornerRadius = new CornerRadius(9), Background = on ? GoldBg : Card, BorderBrush = on ? GoldEdge : Edge, BorderThickness = new Thickness(1) };
        var knob = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = on ? Gold : Hint, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        track.Child = knob;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(track);
        sp.Children.Add(new TextBlock { Text = label, Foreground = Dim, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Child = sp, Cursor = Cursors.Hand, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, ToolTip = on ? "Hide the tradeskill card" : "Show the tradeskill card" };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; toggle(); };
        return b;
    }
}
