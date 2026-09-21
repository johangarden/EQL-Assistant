using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The tradeskill helper card (owner, 21 Sep): opened on purpose for one
/// skill, it reads the wiki's leveling ladder back as you combine — the step
/// you are on with a bar to its trivial, the ingredients with a source glyph
/// each (V bought · D dropped · F foraged · M made · T tool that returns) and
/// the count in your bags, the session's combines / skill-ups / coin, an
/// estimate of what is left, and the amber MOVE ON state the moment the
/// game says a recipe went trivial. The ladder view shows every step with
/// its ALL BOUGHT / FARM n / FORAGE verdict. Interactive, no-activate, like
/// the DPS meter.
/// </summary>
public partial class TradeskillWindow : Window
{
    private static readonly Brush Dim = Freeze("#C9D4E3");
    private static readonly Brush Hint = Freeze("#7F93AD");
    private static readonly Brush Faint = Freeze("#5C6B82");
    private static readonly Brush Gold = Freeze("#E8C15A");
    private static readonly Brush Amber = Freeze("#FFB74D");
    private static readonly Brush Green = Freeze("#81C784");
    private static readonly Brush Red = Freeze("#FF8A80");
    private static readonly Brush Copper = Freeze("#D9A066");
    private static readonly Brush Violet = Freeze("#B39DDB");
    private static readonly Brush Blue = Freeze("#4FC3F7");
    private static readonly Brush Leaf = Freeze("#8BC48A");
    private static readonly Brush Panel2 = Freeze("#151B28");
    private static readonly Brush Sunken = Freeze("#0F141E");
    private static readonly Brush Hair = Freeze("#1F2637");
    private static readonly Brush LineBr = Freeze("#3A4560");
    private static readonly Brush TrackFill = new LinearGradientBrush(Color.FromRgb(0xB0, 0x8A, 0x3A), Color.FromRgb(0xE8, 0xC1, 0x5A), 0);
    private static readonly FontFamily Mono = new("Consolas");

    private readonly TradeskillWatch _watch;
    private readonly TradeskillData _data;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private bool _locked, _hidden, _ladder, _bagCounts;
    private readonly List<string> _texts = new();

    /// <summary>Copies of an item in the last inventory dump (−1 = no dump).</summary>
    public Func<string, int>? BagCount { get; set; }
    /// <summary>The header dropdown picked another skill.</summary>
    public Action<string>? SkillPicked { get; set; }
    /// <summary>The ✕ — the host closes the helper and forgets the open skill.</summary>
    public Action? CloseRequested { get; set; }
    /// <summary>The step/ladder toggle flipped (persist it).</summary>
    public Action<bool>? LadderToggled { get; set; }

    public TradeskillWindow(TradeskillWatch watch, ConfigService configService, double opacity, bool ladder, bool bagCounts)
    {
        InitializeComponent();
        _watch = watch;
        _data = watch.Data;
        _ladder = ladder;
        _bagCounts = bagCounts;
        Title = "EQL Assistant — Tradeskill helper";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "tradeskill", Anchor.TopLeft, 420, 340);
        _watch.Changed += OnChanged;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _placement.Attach(); ApplyLockVisual(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) =>
            // Interactive (skill dropdown, close, ladder toggle) but no-activate.
            NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, false);
        Closed += (_, _) => { _tick.Stop(); _watch.Changed -= OnChanged; };
        Refresh();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

    public void ApplySettings(double opacity, bool ladder, bool bagCounts)
    {
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _ladder = ladder; _bagCounts = bagCounts;
        _placement.Reload();
        Refresh();
    }

    public void SetHidden(bool hidden) { _hidden = hidden; Refresh(); }
    public void SetLocked(bool locked) { _locked = locked; ApplyLockVisual(); }
    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();

    /// <summary>Selftest / render hooks.</summary>
    public string LastPill { get; private set; } = "";
    public string LastStep { get; private set; } = "";
    public IReadOnlyList<string> LineTexts => _texts;
    public bool ShowsLadder => _ladder;

    // ---- render -----------------------------------------------------------------------

    public void Refresh()
    {
        _texts.Clear();
        Body.Children.Clear();
        var s = _watch.Take(DateTime.Now);
        if (s is null)
        {
            SkillText.Text = "TRADESKILLS ▼";
            PillText.Text = "PICK A SKILL"; StylePill("dim");
            ValueText.Text = "";
            Body.Children.Add(Text("Pick the tradeskill you are working today from the ▼ — the guide's ladder, your skill and the session appear here.", Hint, 11.5, wrap: true));
            ShowOrHide();
            return;
        }

        SkillText.Text = s.Skill.Name.ToUpperInvariant() + " ▼";
        ValueText.Inlines.Clear();
        ValueText.Inlines.Add(new Run(s.Value?.ToString() ?? "?"));
        ValueText.Inlines.Add(new Run($"/{s.Skill.Cap}") { FontSize = 10, Foreground = Faint });

        if (s.Trivial) { PillText.Text = "TRIVIAL — MOVE ON"; StylePill("amber"); }
        else if (s.CurrentTier is not null)
        {
            string tn = s.CurrentTier.Name;
            int cut = tn.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) tn = tn[..cut];
            PillText.Text = _ladder ? "LADDER" : $"{tn.ToUpperInvariant()} {s.TierIndex}/{s.TierCount}";
            StylePill(_ladder ? "dim" : "violet");
        }
        else { PillText.Text = "GUIDE"; StylePill("dim"); }
        LastPill = PillText.Text;
        LastStep = s.Current?.Title ?? "";

        if (s.Value is null)
            Body.Children.Add(Box(Text("Skill unknown until your first skill-up — or set it on the Manager's Tradeskills page.", Hint, 11, wrap: true), amber: false));

        if (_ladder) BuildLadder(s);
        else BuildStep(s);

        BuildStats(s);
        if (!_ladder) BuildNext(s);
        if (s.Missing) Body.Children.Add(Text("Out of an ingredient — the game refused the combine.", Red, 11, FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
        BuildFooter();
        ShowOrHide();
    }

    private void ShowOrHide()
    {
        bool show = !_hidden;
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) Hide();
    }

    private void BuildStep(TradeskillWatch.Snapshot s)
    {
        if (s.Current is null)
        {
            Body.Children.Add(Text("The guide has no steps for this skill yet.", Hint, 11.5));
            return;
        }
        var step = s.Current;
        var recipe = s.CurrentRecipe;
        int trivial = step.To; // the guide's mark — the bar runs From → To

        var name = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        string right = s.Trivial ? $"trivial {trivial} · passed"
            : s.Value is null ? $"trivial {trivial}"
            : $"trivial {trivial} · {Math.Max(0, trivial - s.Value.Value)} to go";
        var rt = Text(right, Faint, 11); DockPanel.SetDock(rt, Dock.Right); rt.VerticalAlignment = VerticalAlignment.Center;
        name.Children.Add(rt);
        name.Children.Add(Text(step.Title, Dim, 12.5, FontWeights.SemiBold));
        Body.Children.Add(name);

        Body.Children.Add(Track(s.Trivial ? 1 : s.Fraction, s.Trivial));
        var ticks = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        var l = Text(step.From.ToString(), Faint, 9.5); l.FontFamily = Mono; l.HorizontalAlignment = HorizontalAlignment.Left;
        var r = Text(trivial.ToString(), Faint, 9.5); r.FontFamily = Mono; r.HorizontalAlignment = HorizontalAlignment.Right;
        ticks.Children.Add(l); ticks.Children.Add(r);
        Body.Children.Add(ticks);

        if (recipe is not null && recipe.Ingredients.Count > 0) Body.Children.Add(Chips(recipe, counts: true));
        else Body.Children.Add(Text("Ingredients not on the wiki page — see the recipe in game.", Faint, 10.5, wrap: true));
        if (recipe is { Container.Length: > 0 })
            Body.Children.Add(Text($"in a {Lower(recipe.Container)}" + (recipe.Yield > 1 ? $" · makes {recipe.Yield}" : ""), Faint, 10.5, margin: new Thickness(0, 4, 0, 0)));
        if (step.Notes is { Count: > 0 })
            Body.Children.Add(Text(step.Notes[0], Hint, 10.5, wrap: true, margin: new Thickness(0, 4, 0, 0)));
    }

    private void BuildStats(TradeskillWatch.Snapshot s)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var top = new Border { BorderBrush = Hair, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 0), Child = grid };

        string ok = s.Combines > 0 ? $"{s.Ok} ok · {s.Fail} fail" : "this session";
        Stat(grid, 0, "COMBINES", s.Combines.ToString(), ok, Dim);
        string pace = s.CombinesPerPoint is double c ? $"1 per {c:0.0}" : s.SkillUps > 0 ? "learning pace" : "";
        Stat(grid, 1, "SKILL-UPS", s.SkillUps > 0 ? $"+{s.SkillUps}" : "0", pace, s.SkillUps > 0 ? Green : Dim);
        Stat(grid, 2, "SPENT", TradeskillWatch.Coins(s.SpentCopper), "", s.SpentCopper > 0 ? Copper : Dim);
        Body.Children.Add(top);
    }

    private void Stat(Grid grid, int col, string label, string value, string small, Brush fg)
    {
        var sp = new StackPanel();
        sp.Children.Add(Text(label, Faint, 9, FontWeights.Bold));
        var v = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = fg };
        v.Inlines.Add(new Run(value));
        if (small.Length > 0) v.Inlines.Add(new Run(" " + small) { FontSize = 10, FontWeight = FontWeights.Normal, Foreground = Hint });
        _texts.Add(value + (small.Length > 0 ? " " + small : ""));
        sp.Children.Add(v);
        Grid.SetColumn(sp, col);
        grid.Children.Add(sp);
    }

    private void BuildNext(TradeskillWatch.Snapshot s)
    {
        if (s.Current is null) return;
        if (s.Trivial)
        {
            var sp = new StackPanel();
            if (s.Next is null)
            {
                sp.Children.Add(Text("You have passed the guide's last step for this skill.", Amber, 13, FontWeights.Bold, wrap: true));
            }
            else
            {
                var nr = s.NextRecipe;
                sp.Children.Add(Text($"Next: {s.Next.Title}", Amber, 13, FontWeights.Bold, wrap: true));
                var line = new TextBlock { FontSize = 11, Foreground = Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                line.Inlines.Add(new Run($"to {s.Next.To} · "));
                if (nr is not null)
                {
                    var src = _data.SourcingOf(nr);
                    line.Inlines.Add(new Run(string.Join(" + ", nr.Ingredients.Select(i => Letter(i.Source, i.Returned) + " " + i.Item))) { Foreground = Dim });
                    if (nr.Container.Length > 0) line.Inlines.Add(new Run($" · {Lower(nr.Container)}"));
                    line.Inlines.Add(new Run(" · "));
                    line.Inlines.Add(new Run(src.Tag.ToLowerInvariant()) { Foreground = src.AllBought ? Blue : src.Kind == "farm" ? Red : Amber, FontWeight = FontWeights.SemiBold });
                    _texts.Add(src.Tag);
                }
                sp.Children.Add(line);
                _texts.Add($"Next: {s.Next.Title}");
                string where = WhereFor(nr);
                if (where.Length > 0) sp.Children.Add(Text(where, Hint, 10.5, wrap: true, margin: new Thickness(0, 3, 0, 0)));
            }
            Body.Children.Add(Box(sp, amber: true));
            return;
        }

        // Pace, shopping hint, then what follows.
        var tb = new TextBlock { FontSize = 11.5, Foreground = Hint, TextWrapping = TextWrapping.Wrap };
        var recipe = s.CurrentRecipe;
        if (s.EstimateCombines is int est && s.Value is not null)
        {
            tb.Inlines.Add(new Run("at this pace "));
            tb.Inlines.Add(new Run($"~{est} more combines") { Foreground = Dim, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run($" to {s.Current.To}"));
            if (recipe is not null)
            {
                var buy = recipe.Ingredients.Where(i => !i.Returned && i.Source == "vendor").Select(i => $"~{est * Math.Max(1, i.Count)} {i.Item}").ToList();
                var make = recipe.Ingredients.Where(i => !i.Returned && i.Source == "made").Select(i => $"~{est * Math.Max(1, i.Count)} {i.Item}").ToList();
                if (buy.Count > 0) tb.Inlines.Add(new Run(" · buy " + string.Join(", ", buy)));
                if (make.Count > 0) tb.Inlines.Add(new Run(" · make " + string.Join(", ", make)));
            }
            _texts.Add($"~{est} more combines");
        }
        else if (s.Value is int v && s.Current.To > v)
        {
            tb.Inlines.Add(new Run($"{s.Current.To - v} points to this step's trivial"));
            tb.Inlines.Add(new Run(s.SkillUps < 3 ? " · the pace shows after three skill-ups" : ""));
        }
        else tb.Inlines.Add(new Run("Combine away — the card follows the log."));

        if (s.Next is not null)
        {
            var nr = s.NextRecipe;
            string tag = nr is null ? "" : $", {_data.SourcingOf(nr).Tag.ToLowerInvariant()}";
            tb.Inlines.Add(new Run(" · "));
            tb.Inlines.Add(new Run("then") { Foreground = Dim, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run($" {s.Next.Title} (to {s.Next.To}{tag})"));
            _texts.Add("then " + s.Next.Title);
        }
        Body.Children.Add(Box(tb, amber: false));
    }

    private void BuildLadder(TradeskillWatch.Snapshot s)
    {
        var host = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        int? value = s.Value;
        foreach (var tier in s.Skill.Tiers)
        {
            host.Children.Add(Text(tier.Name.ToUpperInvariant(), Faint, 9.5, FontWeights.Bold, new Thickness(0, 6, 0, 2)));
            foreach (var step in tier.Steps)
            {
                bool cur = ReferenceEquals(step, s.Current);
                bool next = ReferenceEquals(step, s.Next);
                bool done = !cur && value is int v && step.To <= v;
                var recipe = _data.RecipeFor(step.Recipe);
                var src = recipe is null ? null : _data.SourcingOf(recipe);

                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                var range = Text($"{step.From} → {step.To}", Faint, 10.5); range.FontFamily = Mono; range.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(range, Dock.Right); row.Children.Add(range);
                var dot = new Ellipse
                {
                    Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = cur ? Gold : done ? Green : Faint,
                    Stroke = cur ? Freeze("#3A2E14") : null, StrokeThickness = cur ? 2 : 0,
                };
                DockPanel.SetDock(dot, Dock.Left); row.Children.Add(dot);
                if (src is not null)
                {
                    var tag = Tag(src);
                    DockPanel.SetDock(tag, Dock.Right); row.Children.Add(tag);
                }
                var (ok, fail) = _watch.Tally(_data.ProductOf(step));
                string title = step.Title + (ok + fail > 0 ? $"  {ok}/{ok + fail}" : "");
                row.Children.Add(Text(title, cur ? Dim : done ? Faint : Hint, 12, cur ? FontWeights.SemiBold : FontWeights.Normal));
                host.Children.Add(row);

                if ((cur || next) && recipe is not null)
                {
                    var chips = Chips(recipe, counts: cur);
                    chips.Margin = new Thickness(14, 3, 0, 0);
                    host.Children.Add(chips);
                    string where = WhereFor(recipe);
                    if (where.Length > 0) host.Children.Add(Text(where, Hint, 10.5, wrap: true, margin: new Thickness(14, 2, 0, 0)));
                }
            }
        }
        var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (k, txt) in new[] { ("vendor", "bought"), ("drop", "dropped — farm it"), ("forage", "foraged"), ("made", "made — a sub-combine"), ("tool", "tool, returned") })
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2) };
            sp.Children.Add(Glyph(k, false));
            sp.Children.Add(Text(txt, Faint, 10, margin: new Thickness(4, 0, 0, 0)));
            legend.Children.Add(sp);
        }
        var legendBox = new Border { BorderBrush = Hair, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(0, 4, 0, 0), Child = legend };
        host.Children.Add(legendBox);
        Body.Children.Add(host);
    }

    private void BuildFooter()
    {
        var btn = new Button
        {
            Style = (Style)FindResource("Flat"), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 1, 6, 1),
            Content = new TextBlock { Text = _ladder ? "▴ step only" : "▾ whole ladder", FontSize = 10, Foreground = Hint },
            ToolTip = _ladder ? "Back to the step you are on" : "Every step of the guide with its shopping / farming verdict",
        };
        btn.Click += (_, _) => { _ladder = !_ladder; LadderToggled?.Invoke(_ladder); Refresh(); };
        Body.Children.Add(btn);
    }

    // ---- pieces ---------------------------------------------------------------------------

    /// <summary>The line under a step: where the farmed / foraged ingredient
    /// comes from first (that is the trip), else the vendor who sold you one.</summary>
    private string WhereFor(TradeskillData.Recipe? recipe)
    {
        if (recipe is null) return "";
        foreach (var ing in recipe.Ingredients.Where(i => i.Source is "drop" or "forage" or "quest"))
        {
            string w = _data.WhereLine(ing.Item);
            if (w.Length > 0) return $"{ing.Item} {w}";
        }
        foreach (var ing in recipe.Ingredients)
        {
            if (_watch.VendorFor(ing.Item) is { } vm)
                return $"{vm.Npc} sold you {ing.Item}" + (vm.Zone.Length > 0 ? $" in {vm.Zone}" : "");
        }
        return "";
    }

    private WrapPanel Chips(TradeskillData.Recipe recipe, bool counts)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var ing in recipe.Ingredients)
        {
            int have = counts && _bagCounts && BagCount is not null ? BagCount(ing.Item) : -1;
            bool low = have >= 0 && have < Math.Max(3, ing.Count * 3) && !ing.Returned;
            var chip = new Border
            {
                Background = Panel2, BorderBrush = low ? Freeze("#7A2E33") : Hair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(3, 1, 6, 1), Margin = new Thickness(0, 0, 5, 4),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Glyph(ing.Returned ? "tool" : ing.Source, false));
            string nm = ing.Item + (ing.Alternatives is { Count: > 0 } ? " (or…)" : "");
            sp.Children.Add(Text(nm, low ? Red : Dim, 11, margin: new Thickness(4, 0, 0, 0)));
            string extra = ing.Returned ? "↩" : have >= 0 ? $"×{have}" : ing.Count > 1 ? $"×{ing.Count}" : "";
            if (extra.Length > 0) sp.Children.Add(Text(extra, low ? Red : Faint, 10.5, margin: new Thickness(4, 0, 0, 0)));
            chip.Child = sp;
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    private static string Letter(string source, bool returned) => returned ? "T" : source switch
    {
        "vendor" => "V", "drop" => "D", "forage" => "F", "made" => "M", "quest" => "Q", "summoned" => "S", _ => "?",
    };

    private Border Glyph(string kind, bool big)
    {
        var (bg, fg) = kind switch
        {
            "vendor" => ("#2A3350", Blue),
            "drop" or "quest" => ("#3A1B1E", Red),
            "forage" => ("#1B2E1C", Leaf),
            "made" => ("#3A2E14", Amber),
            "tool" => ("#232B3D", Hint),
            _ => ("#232B3D", Faint),
        };
        return new Border
        {
            Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Background = Freeze(bg), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = Letter(kind == "tool" ? "" : kind, kind == "tool"), FontSize = 9, FontWeight = FontWeights.ExtraBold, Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private Border Tag(TradeskillData.Sourcing src)
    {
        var (bg, fg) = src.Kind switch
        {
            "shop" => ("#1B2A45", Blue),
            "farm" => ("#3A1B1E", Red),
            "forage" => ("#1B2E1C", Leaf),
            _ => ("#3A2E14", Amber),
        };
        _texts.Add(src.Tag);
        return new Border
        {
            Background = Freeze(bg), CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = src.Tag, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = fg },
        };
    }

    private static Grid Track(double fraction, bool amber)
    {
        var g = new Grid { Height = 9, Margin = new Thickness(0, 5, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, fraction), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - fraction), GridUnitType.Star) });
        var bg = new Border { Background = Sunken, CornerRadius = new CornerRadius(4) };
        Grid.SetColumnSpan(bg, 2);
        g.Children.Add(bg);
        if (fraction > 0.001)
            g.Children.Add(new Border { Background = amber ? Amber : TrackFill, CornerRadius = new CornerRadius(4) });
        return g;
    }

    private Border Box(UIElement child, bool amber) => new()
    {
        Background = amber ? Freeze("#2A2210") : Panel2,
        BorderBrush = amber ? Freeze("#7A5B22") : Hair,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 8, 0, 0), Child = child,
    };

    private TextBlock Text(string text, Brush fg, double size, FontWeight? weight = null, Thickness? margin = null, bool wrap = false)
    {
        _texts.Add(text);
        return new TextBlock
        {
            Text = text, Foreground = fg, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            Margin = margin ?? new Thickness(0), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
    }

    private static string Lower(string s) => s.ToLowerInvariant();

    private void StylePill(string kind)
    {
        (PillText.Foreground, Pill.Background, Pill.BorderBrush) = kind switch
        {
            "amber" => (Amber, Freeze("#3A2E14"), Freeze("#7A5B22")),
            "violet" => (Violet, Freeze("#2A2440"), Freeze("#5A4E85")),
            _ => (Dim, Freeze("#232B3D"), LineBr),
        };
    }

    // ---- chrome -----------------------------------------------------------------------------

    private void ApplyLockVisual()
    {
        HeaderRow.Cursor = _locked ? Cursors.Arrow : Cursors.SizeAll;
        HeaderRow.ToolTip = _locked ? null : "Drag to place — lock the overlay when done";
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestorButton(d) is not null) return;
        if (e.ButtonState == MouseButtonState.Pressed && !_locked) DragMove();
    }

    private static Button? FindAncestorButton(DependencyObject d)
    {
        while (d is not null)
        {
            if (d is Button b) return b;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Skill_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SkillBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var sk in _data.Skills)
        {
            var row = new DockPanel { MinWidth = 180 };
            int? v = _watch.ValueOf(sk.Name);
            var val = new TextBlock { Text = v?.ToString() ?? "?", Foreground = v is null ? Faint : Gold, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(val, Dock.Right);
            row.Children.Add(val);
            row.Children.Add(new TextBlock { Text = sk.Name });
            var item = new MenuItem { Header = row, IsChecked = sk.Name.Equals(_watch.Selected, StringComparison.OrdinalIgnoreCase) };
            string name = sk.Name;
            item.Click += (_, _) => SkillPicked?.Invoke(name);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
