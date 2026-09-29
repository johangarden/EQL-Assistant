using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The INVOCATIONS tab (owner, 29 Sep): a real stretch of your log — the last
/// 30 / 60 / 90 minutes, or fights you RECORD — replayed under every
/// invocation your combo recites (<see cref="InvocationPlanner"/>), with the
/// pick per fight, a ledger for the stretch and the regen floor the log
/// proves. Two numbers come from the in-game character sheet: base mana
/// regen a tick and max mana.
/// </summary>
public partial class SpellLibraryWindow
{
    private Func<string?>? _logPath;
    private int _invWindow = 60;           // minutes; 0 = the recorded stretch
    private DateTime? _recFrom, _recTo;    // Record's marks (log clock = local time)
    private int _invRegen, _invPool = 0;
    private InvocationPlanner.Stretch? _invStretch;
    private string _invStretchKey = "";
    private bool _invReading;
    private TextBox? _regenBox, _poolBox;

    /// <summary>Selftest / render: the replay painted last.</summary>
    internal InvocationPlanner.Result? InvResultForTest { get; private set; }

    /// <summary>Selftest / render: use these lines instead of the followed log, "now" = <paramref name="now"/>.</summary>
    internal void InvUseLinesForTest(List<string> lines, DateTime now, int regen, int pool)
    {
        _testLines = lines; _testNow = now; _invRegen = regen; _invPool = pool;
        _invStretchKey = ""; Refresh();
    }
    private List<string>? _testLines;
    private DateTime? _testNow;
    private DateTime Now => _testNow ?? DateTime.Now;

    private static readonly Dictionary<string, Brush> InvColor = new()
    {
        [InvocationPlanner.Recovery] = FreezeHex("#4FC3F7"),
        [InvocationPlanner.ArcaneMastery] = FreezeHex("#BA68C8"),
        [InvocationPlanner.Empower] = FreezeHex("#E57373"),
        [InvocationPlanner.Inversion] = FreezeHex("#81C784"),
        [InvocationPlanner.OverChannel] = FreezeHex("#FFB074"),
    };
    private static Brush FreezeHex(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    private (DateTime From, DateTime To) InvSpan() =>
        _invWindow == 0 && _recFrom is { } rf ? (rf, _recTo ?? Now) : (Now.AddMinutes(-Math.Max(1, _invWindow)), Now);

    private void RefreshInvocations()
    {
        InvHost.Children.Clear();
        var combo = (_classesProvider?.Invoke() ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => SpellEfficiency.AllClasses.Contains(c, StringComparer.OrdinalIgnoreCase)).Select(c => c.ToUpperInvariant()).ToList();
        string snap = _snapshotText?.Invoke() ?? "";
        var who = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        who.Inlines.Add(new Run("USING  ") { Foreground = EffFaint, FontWeight = FontWeights.Bold, FontSize = 10 });
        who.Inlines.Add(new Run(snap.Length > 0 ? snap + " · spells at the ranks your cast lines show" : "no /who yet — type /who in game so the planner knows your classes")
        { Foreground = snap.Length > 0 ? EffText : EffLow });
        InvHost.Children.Add(who);
        InvHost.Children.Add(InvControls());
        CountText.Text = "";

        if (combo.Count == 0) { InvHost.Children.Add(Hint("Type /who in game — the invocations and their depths depend on your classes.")); return; }
        var avail = InvocationPlanner.Available(combo);
        if (avail.Count == 0) { InvHost.Children.Add(Hint($"{string.Join("/", combo)} recites none of the mana invocations — nothing to weigh.")); return; }

        // The stretch: read once per span, replayed on every input change.
        var (from, to) = InvSpan();
        string key = _invWindow == 0 ? $"rec|{from:O}|{_recTo:O}" : $"win|{_invWindow}|{Now:yyyyMMddHHmm}";
        if (key != _invStretchKey && !_invReading)
        {
            string? path = _testLines is null ? _logPath?.Invoke() : null;
            if (_testLines is null && (path is null || !System.IO.File.Exists(path)))
            { InvHost.Children.Add(Hint("No log file is being followed yet.")); return; }
            _invReading = true;
            var lib = _library;
            var lines = _testLines;
            if (lines is not null)
            {
                _invStretch = InvocationPlanner.Parse(lines, from, to, lib);
                _invStretchKey = key; _invReading = false;
            }
            else
            {
                InvHost.Children.Add(Hint(_invWindow == 0 ? "Reading the recorded fights from your log…" : $"Reading the last {_invWindow} minutes of your log…"));
                System.Threading.Tasks.Task.Run(() => InvocationPlanner.Parse(InvocationPlanner.ReadSince(path!, from), from, to, lib))
                    .ContinueWith(t => Dispatcher.BeginInvoke(() =>
                    {
                        _invReading = false;
                        if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) { _invStretch = t.Result; _invStretchKey = key; }
                        else Log.Warn("invocation stretch: " + t.Exception?.GetBaseException().Message);
                        if (_tab == "invocations") Refresh();
                    }));
                return;
            }
        }
        if (_invStretch is null) return;
        var st = _invStretch;
        if (st.Fights.Count == 0 || st.Casts.Count == 0)
        { InvHost.Children.Add(Hint($"No fights with your own casts between {Hm(from)} and {Hm(to)} — play a while, or pick a longer stretch.")); return; }

        int pool = _invPool > 0 ? _invPool : 0;
        if (pool <= 0) { InvHost.Children.Add(Hint("Type your max mana from the character sheet — each fight's pick is what a full pool could pay for.")); return; }
        var r = InvocationPlanner.Replay(st, _library, combo, _invRegen, pool);
        InvResultForTest = r;
        CountText.Text = $"{r.Fights.Count} fights · {r.Casts:N0} casts priced";

        // The floor the log proves.
        var proof = new TextBlock { FontSize = 11.5, Foreground = DurDimFg, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        if (r.ProvenRegen > _invRegen)
        {
            proof.Inlines.Add(new Run(_invRegen > 0 ? $"You typed {_invRegen} a tick, but " : "No regen typed — "));
            proof.Inlines.Add(new Run($"your log proves at least {r.ProvenRegen}") { Foreground = EffGold, FontWeight = FontWeights.SemiBold });
            proof.Inlines.Add(new Run(" — the fights you didn't run dry in had to be paid for (buffs, Cannibalize and items count too). The replay uses " + r.ProvenRegen + "."));
        }
        else proof.Inlines.Add(new Run($"Your {_invRegen} a tick covers every fight of this stretch the log saw you finish without running dry."));
        if (st.OutOfMana.Count > 0) proof.Inlines.Add(new Run($"  You ran dry {st.OutOfMana.Count} time(s) here.") { Foreground = EffLow });
        InvHost.Children.Add(proof);

        InvHost.Children.Add(InvVerdict(r));
        InvHost.Children.Add(InvTimeline(st, r, from, to));
        var two = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var led = InvLedger(r); Grid.SetColumn(led, 0); two.Children.Add(led);
        var fights = InvFights(r); Grid.SetColumn(fights, 2); two.Children.Add(fights);
        InvHost.Children.Add(two);
        InvHost.Children.Add(new TextBlock
        {
            Foreground = EffFaint, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            Text = "How it reads the stretch: every cast that landed, at the rank its cast line shows, priced by the wiki and re-priced by each invocation's rules for your combo; "
                 + "your regen in the seconds you were fighting (Recovery doubles it) — out of combat, Recovery's regen applies whichever invocation is up, so that part is the same for all. "
                 + "A fight's pick is the most spell damage a full pool could have paid for, else the one that spends least. "
                 + $"Arcane Mastery's faster casting would have freed {Mmss(r.Ledgers.FirstOrDefault(l => l.Name == InvocationPlanner.Recovery)?.CastSec - r.Ledgers.FirstOrDefault(l => l.Name == InvocationPlanner.ArcaneMastery)?.CastSec ?? 0)} of casting — time a replay can't turn into extra casts, so it isn't scored. "
                 + $"Over Channel: {r.ResistNote}. Inversion and Over Channel also spend endurance, which the log never shows."
                 + (r.Unpriced > 0 ? $" {r.Unpriced} cast(s) without a wiki mana cost (items, abilities) stay out." : ""),
        });
        SaveViewState();
    }

    private StackPanel Hint(string text) => new() { Children = { new TextBlock { Text = text, Foreground = DurDimFg, FontSize = 12, Margin = new Thickness(2, 8, 0, 0), TextWrapping = TextWrapping.Wrap } } };

    private WrapPanel InvControls()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var based = Group("BASED ON");
        based.Children.Add(Seg(new[] { ("30", "Last 30 min"), ("60", "Last 60 min"), ("90", "Last 90 min") },
            _invWindow == 0 ? "" : _invWindow.ToString(), v => { _invWindow = int.Parse(v); _invStretchKey = ""; }));
        // Record: a start mark, then Stop — the stretch between is the one replayed.
        bool recording = _recFrom is not null && _recTo is null;
        string recText = recording ? $"● Recording since {Hm(_recFrom)} — stop"
            : _recFrom is { } a && _recTo is { } b2 ? $"Recorded {Hm(a)}–{Hm(b2)}" : "● Record fights";
        var rec = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12, 2, 12, 3), Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = recording ? FreezeHex("#7A2E33") : _invWindow == 0 ? EffGoldEdge : EffEdge,
            Background = recording ? FreezeHex("#2A1416") : _invWindow == 0 ? EffGoldBg : EffSurface,
            Child = new TextBlock { Text = recText, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = recording ? FreezeHex("#FF8A80") : _invWindow == 0 ? EffGold : DurDimFg },
            ToolTip = recording ? "Stop — the fights since the mark are the stretch" : "Start a mark now; fight; press again to stop — then the replay covers exactly those fights",
        };
        rec.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (recording) _recTo = Now;
            else { _recFrom = Now; _recTo = null; }
            _invWindow = 0; _invStretchKey = "";
            SaveViewState(); Refresh();
        };
        based.Children.Add(rec);
        var reread = Chip("↻", false, () => _invStretchKey = "");
        reread.ToolTip = "Read the stretch again (new fights since)";
        reread.Margin = new Thickness(6, 0, 0, 0);
        based.Children.Add(reread);
        bar.Children.Add(based);

        var sheet = Group("FROM THE CHARACTER SHEET");
        _regenBox = NumberBox(_invRegen, "Base mana regen a tick, in combat — from the in-game character sheet", v => _invRegen = v, need: true);
        sheet.Children.Add(_regenBox);
        sheet.Children.Add(new TextBlock { Text = "mana regen a tick", Foreground = EffFaint, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 12, 0) });
        _poolBox = NumberBox(_invPool, "Max mana — from the in-game character sheet", v => _invPool = v, need: _invPool <= 0);
        sheet.Children.Add(_poolBox);
        sheet.Children.Add(new TextBlock { Text = "max mana", Foreground = EffFaint, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
        bar.Children.Add(sheet);
        return bar;
    }

    private TextBox NumberBox(int value, string tip, Action<int> set, bool need)
    {
        var tb = new TextBox { Text = value > 0 ? value.ToString() : "", Width = 64, MinHeight = 26, ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right };
        if (need) tb.BorderBrush = EffGoldEdge;
        tb.LostFocus += (_, _) => Apply();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        void Apply()
        {
            int v = int.TryParse(tb.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? Math.Max(0, n) : 0;
            set(v); SaveViewState(); Refresh();
        }
        return tb;
    }

    private Border InvVerdict(InvocationPlanner.Result r)
    {
        var best = r.Best!;
        var sp = new StackPanel();
        string label = _invWindow == 0 ? "THE RECORDED FIGHTS" : $"YOUR LAST {_invWindow} MINUTES";
        sp.Children.Add(new TextBlock { Text = $"{label} · REGEN {r.RegenUsed} A TICK · {_invPool:N0} MANA", Foreground = EffFaint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        var head = new TextBlock { Margin = new Thickness(0, 2, 0, 0) };
        head.Inlines.Add(new Run(best.Name) { Foreground = EffText, FontSize = 15, FontWeight = FontWeights.Bold });
        head.Inlines.Add(new Run($"  — wanted in {best.Won} of {r.Fights.Count} fights") { Foreground = DurDimFg, FontSize = 12 });
        sp.Children.Add(head);
        // Short vs long: the switch rule when they differ.
        string Winner(IEnumerable<InvocationPlanner.FightPick> fs) => fs.GroupBy(f => f.Pick).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        string ws = Winner(r.Fights.Where(f => f.Seconds < 180)), wl = Winner(r.Fights.Where(f => f.Seconds >= 180));
        string picks = string.Join(" · ", r.Ledgers.Where(l => l.Won > 0).Select(l => $"{l.Name} {l.Won}"));
        var rec = r.Ledgers.FirstOrDefault(l => l.Name == InvocationPlanner.Recovery);
        string money = rec is null || best.Name == InvocationPlanner.Recovery ? "" :
            $" Over the stretch {best.Name} would have left you {Math.Abs(best.VsRecovery):N0} mana {(best.VsRecovery >= 0 ? "better" : "worse")} off than Recovery, for {Math.Abs(best.Damage - rec.Damage):N0} {(best.Damage >= rec.Damage ? "more" : "less")} spell damage.";
        sp.Children.Add(new TextBlock
        {
            Foreground = DurDimFg, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            Text = (ws.Length > 0 && wl.Length > 0 && ws != wl ? $"Short fights (under 3 min) wanted {ws}, the long ones {wl} — switch as the pull tells you. " : "")
                   + $"Picks: {picks}." + money,
        });
        return new Border { Background = EffSurface, BorderBrush = EffGoldEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 10), Child = sp };
    }

    private Border InvTimeline(InvocationPlanner.Stretch st, InvocationPlanner.Result r, DateTime from, DateTime to)
    {
        const double W = 1000, H = 96;
        var cv = new Canvas { Width = W, Height = H };
        double span = Math.Max(1, (to - from).TotalSeconds);
        double X(DateTime t) => 8 + (W - 16) * Math.Clamp((t - from).TotalSeconds / span, 0, 1);
        cv.Children.Add(new Line { X1 = 8, X2 = W - 8, Y1 = 78, Y2 = 78, Stroke = DurLine, StrokeThickness = 1 });
        foreach (var f in r.Fights)
        {
            var c = InvColor.GetValueOrDefault(f.Pick, DurDimFg);
            var rect = new Rectangle
            {
                Width = Math.Max(2, X(f.End) - X(f.Start)), Height = 40, RadiusX = 3, RadiusY = 3, Stroke = c, StrokeThickness = 1,
                Fill = new SolidColorBrush(((SolidColorBrush)c).Color) { Opacity = 0.28 },
                ToolTip = $"{Hm(f.Start, true)} · {Mmss(f.Seconds)} · {f.Casts} casts · wanted {f.Pick}",
            };
            Canvas.SetLeft(rect, X(f.Start)); Canvas.SetTop(rect, 16);
            cv.Children.Add(rect);
        }
        foreach (var c in st.Casts.Where(c => c.Landed))
        {
            var s = _library.FindByBaseName(c.Spell);
            string kind = s is null ? "other" : SpellEfficiency.IsDamage(s.Effect) ? "dmg" : SpellEfficiency.IsHealing(s.Effect) ? "heal" : "other";
            double y1 = kind == "dmg" ? 22 : kind == "heal" ? 34 : 44;
            cv.Children.Add(new Line { X1 = X(c.At), X2 = X(c.At), Y1 = y1, Y2 = y1 + 12, StrokeThickness = 1.4,
                Stroke = kind == "dmg" ? EffText : kind == "heal" ? EffHigh : EffFaint });
        }
        double step = span > 2400 ? 600 : span > 900 ? 300 : 60;
        for (double s = Math.Ceiling((from - from.Date).TotalSeconds / step) * step - (from - from.Date).TotalSeconds; s <= span; s += step)
        {
            var t = from.AddSeconds(s);
            cv.Children.Add(new Line { X1 = X(t), X2 = X(t), Y1 = 74, Y2 = 82, Stroke = EffEdge });
            var lbl = new TextBlock { Text = Hm(t), Foreground = EffFaint, FontSize = 10 };
            Canvas.SetLeft(lbl, X(t) - 14); Canvas.SetTop(lbl, 82);
            cv.Children.Add(lbl);
        }
        var legend = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var n in r.Available)
            legend.Children.Add(LegendItem(InvColor.GetValueOrDefault(n, DurDimFg), $"{n} — {InvocationPlanner.RuleText(n, r.Rules)}"));
        legend.Children.Add(LegendItem(EffText, "damage cast"));
        legend.Children.Add(LegendItem(EffHigh, "heal"));
        legend.Children.Add(LegendItem(EffFaint, "buff / debuff / other"));
        var sp = new StackPanel();
        var head = new DockPanel();
        var right = new TextBlock { Foreground = DurDimFg, FontSize = 10.5, Text = $"{Hm(from)}–{Hm(to)} · {r.Fights.Count} fights, {Mmss(r.CombatSec)} in combat · {st.Casts.Count(c => c.Landed)} casts" };
        DockPanel.SetDock(right, Dock.Right); head.Children.Add(right);
        head.Children.Add(new TextBlock { Text = "THE STRETCH", Foreground = EffFaint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        sp.Children.Add(head);
        sp.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = cv, Margin = new Thickness(0, 4, 0, 0) });
        sp.Children.Add(legend);
        return new Border { Background = EffSurface, BorderBrush = DurLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10), Child = sp };
    }

    private static StackPanel LegendItem(Brush c, string text) => new()
    {
        Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 3),
        Children =
        {
            new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = c, Opacity = 0.75, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, Foreground = DurDimFg, FontSize = 10.5 },
        },
    };

    private Border InvLedger(InvocationPlanner.Result r)
    {
        var g = TableGrid(new[] { "INVOCATION", "SPENT", "REGEN", "VS RECOVERY", "DAMAGE", "WON" });
        int row = 1;
        var best = r.Best;
        foreach (var l in r.Ledgers)
        {
            g.RowDefinitions.Add(new RowDefinition());
            if (ReferenceEquals(l, best)) Band(g, row, 6);
            TCell(g, Swatch(l.Name), row, 0);
            TCell(g, Txt($"{l.Spent:N0}"), row, 1);
            TCell(g, Txt($"{l.Regen:N0}"), row, 2);
            TCell(g, Txt(l.Name == InvocationPlanner.Recovery ? "—" : (l.VsRecovery >= 0 ? "+" : "−") + $"{Math.Abs(l.VsRecovery):N0}",
                l.Name == InvocationPlanner.Recovery ? EffFaint : l.VsRecovery >= 0 ? EffHigh : EffLow), row, 3);
            TCell(g, Txt($"{l.Damage:N0}"), row, 4);
            TCell(g, Txt(l.Won.ToString(), EffText, bold: true), row, 5);
            row++;
        }
        return Panel("OVER THE STRETCH", "out-of-combat regen is the same for all — Recovery's applies anyway", g);
    }

    private Border InvFights(InvocationPlanner.Result r)
    {
        var g = TableGrid(new[] { "FIGHT", "LENGTH", "CASTS", "MANA NEEDED", "WANTED" });
        int row = 1;
        foreach (var f in r.Fights)
        {
            g.RowDefinitions.Add(new RowDefinition());
            TCell(g, Txt(Hm(f.Start)), row, 0);
            TCell(g, Txt(Mmss(f.Seconds)), row, 1);
            TCell(g, Txt(f.Casts.ToString()), row, 2);
            TCell(g, Txt($"{f.ManaNeeded:N0}"), row, 3);
            var pick = Swatch(f.Pick);
            if (!f.Affordable) ((TextBlock)pick.Children[1]).Inlines.Add(new Run(" · runs dry in any") { Foreground = EffLow, FontWeight = FontWeights.Normal });
            TCell(g, pick, row, 4);
            row++;
        }
        return Panel("FIGHT BY FIGHT", "the most damage a full pool could pay for", g);
    }

    private static Grid TableGrid(string[] heads)
    {
        var g = new Grid();
        for (int i = 0; i < heads.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < heads.Length; i++)
        {
            var tb = new TextBlock { Text = heads[i], Foreground = DurHeadFg, FontSize = 9.5, FontWeight = FontWeights.Bold, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            var b = new Border { BorderBrush = DurLine, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(i == 0 ? 0 : 10, 3, 2, 4), Child = tb };
            Grid.SetColumn(b, i); g.Children.Add(b);
        }
        return g;
    }

    private static void Band(Grid g, int row, int cols)
    {
        var band = new Border { Background = EffBest };
        Grid.SetRow(band, row); Grid.SetColumnSpan(band, cols); g.Children.Add(band);
    }

    private static void TCell(Grid g, UIElement el, int row, int col)
    {
        var b = new Border { BorderBrush = DurLine, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(col == 0 ? 0 : 10, 4, 2, 4), Child = el };
        if (el is FrameworkElement fe) fe.HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Grid.SetRow(b, row); Grid.SetColumn(b, col); g.Children.Add(b);
    }

    private static TextBlock Txt(string s, Brush? fg = null, bool bold = false) =>
        new() { Text = s, Foreground = fg ?? DurValFg, FontSize = 12, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    private static StackPanel Swatch(string name) => new()
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = InvColor.GetValueOrDefault(name, DurDimFg), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = name, Foreground = EffText, FontSize = 12, FontWeight = FontWeights.SemiBold },
        },
    };

    private static Border Panel(string title, string note, UIElement body)
    {
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var n = new TextBlock { Text = note, Foreground = DurDimFg, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(n, Dock.Right); head.Children.Add(n);
        head.Children.Add(new TextBlock { Text = title, Foreground = EffFaint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        sp.Children.Add(head); sp.Children.Add(body);
        return new Border { Background = EffSurface, BorderBrush = DurLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Child = sp, VerticalAlignment = VerticalAlignment.Top };
    }

    /// <summary>Clock times the way the game's log writes them ("22:31"), whatever the Windows culture.</summary>
    private static string Hm(DateTime? t, bool seconds = false) =>
        t is { } v ? v.ToString(seconds ? "HH:mm:ss" : "HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";

    private static string Mmss(double sec) { int t = (int)Math.Round(Math.Max(0, sec)); return $"{t / 60}:{t % 60:00}"; }
}
