using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EQLOverlay.Services;
using static EQLOverlay.Services.CombatParser;

namespace EQLOverlay.Views;

/// <summary>
/// The fight report (embedded in Fight History): highlight tiles and context
/// chips up top, the ⚡ analysis, then TWO timelines — offence (your and your
/// pet's output: DoTs as one lane each with cast chevron + uptime span + own
/// ticks, direct damage, pet lanes) and defence (your debuffs on the enemy,
/// what hit you, healing, CC on you, pet death, the stance strip). The
/// per-ability tables live below in the History window itself.
/// </summary>
public partial class TimelineView : UserControl
{
    private const double LabelWidth = 150;
    private const double LaneHeight = 20;
    private const double SlimHeight = 13;
    private const int MaxLanesPerGroup = 8;

    private FightRecord? _rec;
    private IReadOnlyList<string> _drops = Array.Empty<string>();

    private static readonly Brush LaneBg = Freeze(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush AxisFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));
    private static readonly Brush MissFg = Freeze(Color.FromArgb(0x77, 0x8F, 0xA6, 0xC4));
    private static readonly Brush SpanFill = Freeze(Color.FromArgb(0x2E, 0xB3, 0x9D, 0xDB));
    private static readonly Brush SpanStroke = Freeze(Color.FromRgb(0xB3, 0x9D, 0xDB));
    private static readonly Brush CcFill = Freeze(Color.FromArgb(0x2E, 0xFF, 0x70, 0x43));
    private static readonly Brush CcStroke = Freeze(Color.FromRgb(0xFF, 0x70, 0x43));
    private static readonly Brush CastFg = Freeze(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly Brush DmgFg = Freeze(Color.FromRgb(0xFF, 0xC1, 0x2E));
    private static readonly Brush DmgCrit = Freeze(Color.FromRgb(0xFF, 0xE0, 0x82));
    private static readonly Brush PetFg = Freeze(Color.FromRgb(0xA1, 0x88, 0x7F));
    private static readonly Brush PetCrit = Freeze(Color.FromRgb(0xD7, 0xCC, 0xC8));
    private static readonly Brush TakenFg = Freeze(Color.FromRgb(0xFF, 0x8A, 0x80));
    private static readonly Brush TakenCrit = Freeze(Color.FromRgb(0xFF, 0xCD, 0xD2));
    private static readonly Brush HealFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush HealCrit = Freeze(Color.FromRgb(0xC8, 0xE6, 0xC9));
    private static readonly Brush HealInFg = Freeze(Color.FromRgb(0x4D, 0xB6, 0xAC));
    private static readonly Brush StanceFill = Freeze(Color.FromArgb(0x26, 0x90, 0xA4, 0xAE));
    private static readonly Brush StanceFg = Freeze(Color.FromRgb(0xB8, 0xC6, 0xD4));
    private static readonly Brush NeutralFg = Freeze(Color.FromRgb(0xAE, 0xB8, 0xC4));
    private static readonly Brush ChipFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush ChipBold = Freeze(Color.FromRgb(0xC9, 0xD4, 0xE3));
    private static readonly Brush CardBg = Freeze(Color.FromRgb(0x1B, 0x21, 0x30));
    private static readonly Brush CardLine = Freeze(Color.FromRgb(0x3A, 0x45, 0x60));

    /// <summary>One drawn lane: ticks are marks, spans are uptime bars, casts
    /// are chevrons at the top edge (interrupted ones dim).</summary>
    private sealed record LaneSpec(string Label, string Sub, Brush Mark, Brush Crit,
        List<FightEvent> Ticks, List<FightEvent> Spans, List<FightEvent> Casts,
        Brush SpanFillBrush, Brush SpanStrokeBrush, string SpanText,
        bool Slim = false, bool Death = false);

    public TimelineView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild();
    }

    /// <summary>Point the report at one fight (no-op when already showing it).</summary>
    public void ShowFight(FightRecord rec, IReadOnlyList<string>? drops = null)
    {
        if (ReferenceEquals(_rec, rec)) return;
        _rec = rec;
        _drops = drops ?? Array.Empty<string>();

        TitleText.Text = rec.Label;
        string truncated = rec.EventsTruncated ? $" · first {MaxFightEvents} events shown" : "";
        SubtitleText.Text =
            $"{rec.EndedAt:dd MMM HH:mm:ss} · {FormatDuration(rec.DurationSeconds)} · " +
            $"{rec.Events.Count} events{truncated} — hover a mark for details.";

        BuildChips(rec);
        BuildTiles(rec);
        AnalysisText.Text = BuildAnalysis(rec);
        AnalysisPanel.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        Rebuild();
    }

    /// <summary>Nothing selected — hide the whole section.</summary>
    public void Clear()
    {
        _rec = null;
        Visibility = Visibility.Collapsed;
    }

    // ---- header: chips + highlight tiles ---------------------------------------

    private void BuildChips(FightRecord rec)
    {
        ChipsPanel.Children.Clear();
        if (rec.Character.Length > 0)
            AddChip(rec.Loadout.Length > 0 ? $"{rec.Character} · {rec.Loadout}" : rec.Character);
        if (rec.StanceAtStart.Length > 0)
            AddChip($"{rec.StanceAtStart} stance");
        if (rec.BuffsAtStart.Count > 0)
            AddChip($"Buffs up {rec.BuffsAtStart.Count}", string.Join(" · ", rec.BuffsAtStart));
        foreach (var (mob, lvl) in rec.EnemyLevels)
            AddChip($"{mob} · Lvl {lvl}");
        if (rec.Zone.Length > 0) AddChip(rec.Zone);
        if (_drops.Count > 0)
            AddChip($"Dropped: {string.Join(" · ", _drops)}");
    }

    private void AddChip(string text, string? tip = null)
    {
        var b = new Border
        {
            Background = CardBg,
            BorderBrush = CardLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(0, 0, 6, 4),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = ChipFg },
        };
        if (tip is not null) b.ToolTip = tip;
        ChipsPanel.Children.Add(b);
    }

    private void BuildTiles(FightRecord rec)
    {
        TilesPanel.Children.Clear();
        double dur = Math.Max(1, rec.DurationSeconds);

        double selfTotal = rec.SelfAbilities.Sum(r => r.Total);
        if (selfTotal <= 0) // pre-drill-down record: fall back to the damage rows
            selfTotal = rec.Damage.Where(r => !r.Enemy).Sum(r => r.Total);
        double petTotal = rec.PetAbilities.Sum(r => r.Total);
        double healTotal = rec.Healing.Where(r => !r.Enemy).Sum(r => r.Total);

        double peak = RollingPeak(rec.Events.Where(e =>
            e.Stream is FightStream.SelfOut or FightStream.PetOut && e.Amount > 0), dur);
        string peakTxt = peak > 0 ? $" · peak {peak:N0}/s" : "";
        AddTile("DAMAGE DEALT", FormatDps(selfTotal / dur), "dps",
            $"{FormatNum(selfTotal)} total{peakTxt}", DmgFg);

        if (petTotal > 0)
        {
            double share = 100.0 * petTotal / Math.Max(1, selfTotal + petTotal);
            AddTile((rec.Pet.Length > 0 ? rec.Pet.ToUpperInvariant() : "PET") + " (PET)",
                FormatDps(petTotal / dur), "dps",
                $"{FormatNum(petTotal)} · {share:0}% of the team", PetFg);
        }

        string takenSub = rec.IncomingPetTotal > 0
            ? $"{FormatNum(rec.IncomingSelfTotal)} you · {FormatNum(rec.IncomingPetTotal)} pet"
            : $"{FormatNum(rec.IncomingSelfTotal)} total";
        AddTile("DAMAGE TAKEN", FormatDps(rec.IncomingSelfTotal / dur), "dps", takenSub, TakenFg);

        if (healTotal > 0)
            AddTile("HEALING", FormatDps(healTotal / dur), "/s", $"{FormatNum(healTotal)} total", HealFg);

        AddTile("DURATION", FormatDuration(rec.DurationSeconds), "",
            $"{rec.EndedAt:dd MMM HH:mm}", NeutralFg);

        var bigIn = rec.Events.Where(e => e.Stream == FightStream.SelfIn && e.Amount > 0)
            .OrderByDescending(e => e.Amount).FirstOrDefault();
        if (bigIn is not null)
            AddTile("BIGGEST HIT ON YOU", FormatNum(bigIn.Amount), "",
                $"{bigIn.Ability} at {FormatDuration(bigIn.T)}", TakenFg);

        var bigOut = rec.Events.Where(e => e.Stream == FightStream.SelfOut && e.Amount > 0)
            .OrderByDescending(e => e.Amount).FirstOrDefault();
        if (bigOut is not null)
            AddTile("BIGGEST HIT BY YOU", FormatNum(bigOut.Amount), "",
                $"{bigOut.Ability}{(bigOut.Crit ? " (crit)" : "")} at {FormatDuration(bigOut.T)}", DmgFg);
    }

    private void AddTile(string label, string big, string unit, string sub, Brush accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9, Foreground = AxisFg,
            // uppercase labels read as labels; a touch of tracking helps
            FontWeight = FontWeights.SemiBold,
        });
        var bigLine = new TextBlock { Margin = new Thickness(0, 1, 0, 0) };
        bigLine.Inlines.Add(new System.Windows.Documents.Run(big)
        { FontSize = 22, FontWeight = FontWeights.Bold, Foreground = accent });
        if (unit.Length > 0)
            bigLine.Inlines.Add(new System.Windows.Documents.Run(" " + unit)
            { FontSize = 12, Foreground = ChipFg });
        stack.Children.Add(bigLine);
        stack.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = ChipFg });

        TilesPanel.Children.Add(new Border
        {
            Background = CardBg,
            BorderBrush = CardLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 150,
            Child = stack,
        });
    }

    /// <summary>Max 5s-rolling rate over the given events (the tiles' "peak").</summary>
    private static double RollingPeak(IEnumerable<FightEvent> events, double dur)
    {
        var evs = events.OrderBy(e => e.T).ToList();
        if (evs.Count == 0) return 0;
        double win = Math.Min(5, dur), max = 0, sum = 0;
        int drop = 0;
        for (int i = 0; i < evs.Count; i++)
        {
            sum += evs[i].Amount;
            while (evs[drop].T <= evs[i].T - win) sum -= evs[drop++].Amount;
            max = Math.Max(max, sum / win);
        }
        return max;
    }

    // ---- the two boards --------------------------------------------------------

    private void Rebuild()
    {
        if (_rec is null) return;
        BoardsHost.Children.Clear();

        // Boards live in padded cards now — the tracks get what's left.
        double width = Math.Max(60, BoardsHost.ActualWidth - LabelWidth - 48);
        double dur = Math.Max(1, _rec.DurationSeconds);
        double scale = width / dur;

        if (_rec.Events.Count == 0)
        {
            BoardsHost.Children.Add(new TextBlock
            {
                Text = "No timeline data for this fight — it was recorded (or ★-kept) before fight timelines existed. New fights record automatically.",
                Foreground = MissFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 0),
            });
            return;
        }

        BuildOffence(width, dur, scale);
        BuildDefence(width, dur, scale);
    }

    private void BuildOffence(double width, double dur, double scale)
    {
        var rec = _rec!;
        var lanes = new List<(string Group, LaneSpec Spec)>();

        var selfOut = ByAbility(rec, FightStream.SelfOut);
        var castsBy = ByAbility(rec, FightStream.Cast);
        var debuffs = ByAbility(rec, FightStream.Debuff);
        var claimedCasts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // DoTs get ONE lane — cast chevron, uptime span, ticks inside it. A
        // spell is a DoT when the log said so: a recorded landing span, OR
        // tick-shaped damage lines ("has taken X damage from Odium") — the
        // latter catches DoTs whose landing sentence was never observed.
        var dotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in debuffs.Keys)
            if (selfOut.ContainsKey(name)) dotNames.Add(name); // pure debuff → defence
        foreach (var (name, ticks) in selfOut)
            if (ticks.Any(e => e.Dot)) dotNames.Add(name);
        foreach (var name in dotNames.OrderBy(n =>
                     Math.Min(debuffs.GetValueOrDefault(n)?[0].T ?? double.MaxValue,
                         selfOut[n][0].T)))
        {
            claimedCasts.Add(name);
            lanes.Add(("DoTs — cast · uptime · ticks", new LaneSpec(name, "DoT", DmgFg, DmgCrit,
                selfOut[name], debuffs.GetValueOrDefault(name) ?? new(),
                castsBy.GetValueOrDefault(name) ?? new(),
                SpanFill, SpanStroke, "running")));
        }

        // Direct damage, biggest first; each lane carries its own cast chevrons.
        var direct = selfOut.Where(kv => !dotNames.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value.Sum(e => e.Amount))
            .ToList();
        foreach (var (name, ticks) in Capped(direct))
        {
            claimedCasts.Add(name);
            lanes.Add(("Direct damage", new LaneSpec(name, "", DmgFg, DmgCrit,
                ticks, new(), castsBy.GetValueOrDefault(name) ?? new(),
                SpanFill, SpanStroke, "", Slim: ticks.Count > 45)));
        }

        // Pet lanes, its own group in its own color.
        var pet = ByAbility(rec, FightStream.PetOut)
            .OrderByDescending(kv => kv.Value.Sum(e => e.Amount)).ToList();
        string petGroup = rec.Pet.Length > 0 ? $"{rec.Pet} — pet" : "Pet";
        foreach (var (name, ticks) in Capped(pet))
            lanes.Add((petGroup, new LaneSpec(name, "", PetFg, PetCrit,
                ticks, new(), new(), SpanFill, SpanStroke, "", Slim: ticks.Count > 45)));

        // Casts that never matched a lane here or in defence (buffs etc.).
        var healNames = new HashSet<string>(
            ByAbility(rec, FightStream.HealOut).Keys, StringComparer.OrdinalIgnoreCase);
        var other = castsBy.Where(kv => !claimedCasts.Contains(kv.Key) && !healNames.Contains(kv.Key)
                                        && !debuffs.ContainsKey(kv.Key))
            .SelectMany(kv => kv.Value).OrderBy(e => e.T).ToList();
        if (other.Count > 0)
            lanes.Add(("Direct damage", new LaneSpec("other casts", "buffs etc.", CastFg, CastFg,
                new(), new(), other, SpanFill, SpanStroke, "", Slim: true)));

        if (lanes.Count == 0 && rec.Events.All(e => e.Stream != FightStream.SelfOut)) return;

        var board = NewBoard("OFFENCE", DmgFg,
            "▾ cast · ▬ DoT running · | hit (taller = harder), dim = miss/resist",
            () => _offenceOpen, v => _offenceOpen = v, out var content);
        AddAxis(content, width, dur, scale);
        DrawGraph(content, width, dur, scale, new[]
        {
            ("You", DmgFg, (Func<FightEvent, bool>)(e => e.Stream == FightStream.SelfOut)),
            (rec.Pet.Length > 0 ? rec.Pet : "Pet", PetFg, e => e.Stream == FightStream.PetOut),
        });
        AddLanes(content, lanes, width, scale);
        BoardsHost.Children.Add(board);
    }

    private void BuildDefence(double width, double dur, double scale)
    {
        var rec = _rec!;
        var lanes = new List<(string Group, LaneSpec Spec)>();

        var selfOut = ByAbility(rec, FightStream.SelfOut);
        var castsBy = ByAbility(rec, FightStream.Cast);

        // Your PURE debuffs (no damage ticks of yours): a slow and a malo
        // shape the incoming side — their gaps sit above the curve they explain.
        foreach (var (name, spanEvents) in ByAbility(rec, FightStream.Debuff)
                     .Where(kv => !selfOut.ContainsKey(kv.Key)).OrderBy(kv => kv.Value[0].T))
            lanes.Add(("Your debuffs on the enemy", new LaneSpec(name, "", SpanStroke, SpanStroke,
                new(), spanEvents, castsBy.GetValueOrDefault(name) ?? new(),
                SpanFill, SpanStroke, "running")));

        foreach (var (name, ticks) in Capped(ByAbility(rec, FightStream.SelfIn)
                     .OrderByDescending(kv => kv.Value.Sum(e => e.Amount)).ToList()))
            lanes.Add(("What hit you", new LaneSpec(name, "", TakenFg, TakenCrit,
                ticks, new(), new(), SpanFill, SpanStroke, "", Slim: ticks.Count > 45)));

        var petIn = rec.Events.Where(e => e.Stream == FightStream.PetIn).OrderBy(e => e.T).ToList();
        if (petIn.Count > 0)
            lanes.Add(("What hit you", new LaneSpec(
                rec.Pet.Length > 0 ? $"{rec.Pet} took" : "Pet took", "pet", PetFg, PetCrit,
                petIn, new(), new(), SpanFill, SpanStroke, "", Slim: true)));

        foreach (var (name, ticks) in Capped(ByAbility(rec, FightStream.HealOut)
                     .OrderByDescending(kv => kv.Value.Sum(e => e.Amount)).ToList(), 5))
            lanes.Add(("Healing & control", new LaneSpec(name, "", HealFg, HealCrit,
                ticks, new(), castsBy.GetValueOrDefault(name) ?? new(),
                SpanFill, SpanStroke, "", Slim: ticks.Count > 45)));

        foreach (var (name, ticks) in Capped(ByAbility(rec, FightStream.HealIn)
                     .OrderByDescending(kv => kv.Value.Sum(e => e.Amount)).ToList(), 3))
            lanes.Add(("Healing & control", new LaneSpec(name, "on you", HealInFg, HealInFg,
                ticks, new(), new(), SpanFill, SpanStroke, "")));

        foreach (var (name, spans) in ByAbility(rec, FightStream.Condition))
            lanes.Add(("Healing & control", new LaneSpec(name, "held you", CcStroke, CcStroke,
                new(), spans, new(), CcFill, CcStroke, "held you for")));

        var deaths = rec.Events.Where(e => e.Stream == FightStream.PetDeath).ToList();
        if (deaths.Count > 0)
            lanes.Add(("Healing & control", new LaneSpec(deaths[0].Ability, "", TakenFg, TakenFg,
                deaths, new(), new(), SpanFill, SpanStroke, "", Death: true)));

        var stanceSegs = StanceSegments(rec);
        if (lanes.Count == 0 && stanceSegs.Count == 0) return;

        var board = NewBoard("DEFENCE", TakenFg,
            "▬ your debuff / CC · | hit on you · | heal · ✕ pet died",
            () => _defenceOpen, v => _defenceOpen = v, out var content);
        AddAxis(content, width, dur, scale);
        DrawGraph(content, width, dur, scale, new[]
        {
            ("Taken", TakenFg, (Func<FightEvent, bool>)(e => e.Stream is FightStream.SelfIn or FightStream.PetIn)),
            ("Healing", HealFg, e => e.Stream is FightStream.HealOut or FightStream.HealIn),
        });
        AddLanes(content, lanes, width, scale);

        if (stanceSegs.Count > 0)
            AddStanceStrip(content, stanceSegs, width, scale);
        BoardsHost.Children.Add(board);
    }

    // ---- board plumbing --------------------------------------------------------

    // Fold state survives selection changes and fights — app-session memory.
    private static bool _offenceOpen = true;
    private static bool _defenceOpen = true;

    /// <summary>A foldable board in a section card: clickable ▾/▸ header,
    /// content below. Custom, never a stock Expander — every control themed
    /// from day one.</summary>
    private static Border NewBoard(string title, Brush accent, string key,
        Func<bool> isOpen, Action<bool> setOpen, out Panel content)
    {
        var board = new StackPanel();
        var head = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent, // whole row clickable
        };
        var arrow = new TextBlock
        {
            Text = isOpen() ? "▾" : "▸", Foreground = accent, FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(arrow);
        head.Children.Add(new TextBlock
        {
            Text = title, Foreground = accent, FontWeight = FontWeights.Bold, FontSize = 12,
        });
        var keyTb = new TextBlock
        {
            Text = key, Foreground = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82)), FontSize = 11,
            Margin = new Thickness(12, 1, 0, 0), VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        head.Children.Add(keyTb);
        board.Children.Add(head);

        // Content sits in a grid with a hit-test-transparent overlay on top —
        // the hover time-cursor draws there, across graph and lanes alike.
        var inner = new StackPanel();
        var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        var wrap = new Grid
        {
            Background = Brushes.Transparent,
            Visibility = isOpen() ? Visibility.Visible : Visibility.Collapsed,
        };
        wrap.Children.Add(inner);
        wrap.Children.Add(overlay);
        board.Children.Add(wrap);
        content = inner;

        head.MouseLeftButtonDown += (_, _) =>
        {
            bool open = !isOpen();
            setOpen(open);
            inner.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = open ? "▾" : "▸";
        };
        return new Border
        {
            Background = CardBg,
            BorderBrush = CardLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = board,
        };
    }

    private static void AddGroupLabel(Panel board, string text)
    {
        board.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 10,
            Margin = new Thickness(0, 7, 0, 2),
        });
    }

    private void AddLanes(Panel board, List<(string Group, LaneSpec Spec)> lanes,
        double width, double scale)
    {
        string current = "";
        foreach (var (group, spec) in lanes)
        {
            if (group != current)
            {
                AddGroupLabel(board, group);
                current = group;
            }
            board.Children.Add(BuildLane(spec, width, scale));
        }
    }

    private FrameworkElement BuildLane(LaneSpec s, double width, double scale)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        double laneH = s.Slim ? SlimHeight : LaneHeight;
        int hits = s.Ticks.Count(e => !e.Miss && !e.Resist);
        var label = new TextBlock
        {
            Foreground = (Brush)FindResource("Brush.TextDim"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 8, 0),
        };
        label.Inlines.Add(new System.Windows.Documents.Run(s.Label));
        if (s.Sub.Length > 0)
            label.Inlines.Add(new System.Windows.Documents.Run("  " + s.Sub)
            { FontSize = 10, Foreground = AxisFg });
        label.ToolTip = s.Spans.Count > 0 && s.Ticks.Count == 0
            ? $"{s.Label} — {s.Spans.Count}×"
            : $"{s.Label} — {hits} landed, {FormatNum(s.Ticks.Where(e => !e.Miss && !e.Resist).Sum(e => e.Amount))} total";
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var canvas = new Canvas { Height = laneH, ClipToBounds = true, Background = LaneBg };
        Grid.SetColumn(canvas, 1);
        row.Children.Add(canvas);

        foreach (var e in s.Spans)
        {
            double x = Math.Min(width - 3, e.T * scale);
            double w = e.Amount > 0 ? Math.Max(3, Math.Min(width - x, e.Amount * scale)) : 3;
            var span = new Rectangle
            {
                Width = w, Height = laneH - 6, RadiusX = 2, RadiusY = 2,
                Fill = s.SpanFillBrush, Stroke = s.SpanStrokeBrush, StrokeThickness = 1,
                ToolTip = e.Amount > 0
                    ? $"{FormatDuration(e.T)} · {s.Label} {s.SpanText} ~{e.Amount:0}s"
                    : $"{FormatDuration(e.T)} · {s.Label} landed (duration unknown)",
            };
            ToolTipService.SetInitialShowDelay(span, 150);
            Canvas.SetLeft(span, x);
            Canvas.SetBottom(span, 3);
            canvas.Children.Add(span);
        }

        double max = Math.Max(1, s.Ticks.Count > 0 ? s.Ticks.Max(e => e.Amount) : 1);
        foreach (var e in s.Ticks)
        {
            double x = Math.Min(width - 2, e.T * scale);
            FrameworkElement mark;
            string when = FormatDuration(e.T);

            if (s.Death)
            {
                mark = new TextBlock
                {
                    Text = "✕", Foreground = TakenFg, FontSize = 11, FontWeight = FontWeights.Bold,
                    ToolTip = $"{when} · {s.Label}",
                };
                Canvas.SetTop(mark, 0);
            }
            else if (e.Miss || e.Resist)
            {
                mark = new Rectangle { Width = 2, Height = 7, Fill = MissFg };
                mark.ToolTip = $"{when} · {s.Label} {(e.Resist ? "resisted" : "missed")}";
            }
            else
            {
                double h = 4 + (laneH - 8) * Math.Sqrt(e.Amount / max);
                mark = new Rectangle
                {
                    Width = e.Crit ? 3 : 2,
                    Height = Math.Min(laneH - 2, h),
                    Fill = e.Crit ? s.Crit : s.Mark,
                };
                mark.ToolTip = $"{when} · {s.Label} {e.Amount:N0}{(e.Crit ? " (Critical)" : "")}";
            }

            ToolTipService.SetInitialShowDelay(mark, 150);
            Canvas.SetLeft(mark, x);
            if (!s.Death) Canvas.SetBottom(mark, 1);
            canvas.Children.Add(mark);
        }

        foreach (var e in s.Casts)
        {
            var chev = new Polygon
            {
                Points = new PointCollection { new Point(0, 0), new Point(7, 0), new Point(3.5, 5) },
                Fill = e.Miss ? MissFg : CastFg,
                ToolTip = e.Miss
                    ? $"{FormatDuration(e.T)} · {e.Ability} INTERRUPTED"
                    : $"{FormatDuration(e.T)} · cast {e.Ability}",
            };
            ToolTipService.SetInitialShowDelay(chev, 150);
            Canvas.SetLeft(chev, Math.Min(width - 7, Math.Max(0, e.T * scale - 3.5)));
            Canvas.SetTop(chev, 0);
            canvas.Children.Add(chev);
        }

        return row;
    }

    private void AddStanceStrip(Panel board, List<(string Name, double T0, double T1)> segs,
        double width, double scale)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lb = new TextBlock
        {
            Text = "Stance", Foreground = (Brush)FindResource("Brush.TextDim"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0),
        };
        Grid.SetColumn(lb, 0);
        row.Children.Add(lb);

        var canvas = new Canvas { Height = 15, ClipToBounds = true, Background = LaneBg };
        Grid.SetColumn(canvas, 1);
        row.Children.Add(canvas);

        foreach (var (name, t0, t1) in segs)
        {
            double x = t0 * scale;
            double w = Math.Max(2, (t1 - t0) * scale);
            var seg = new Border
            {
                Width = Math.Min(width - x, w), Height = 15,
                Background = StanceFill,
                BorderBrush = Freeze(Color.FromRgb(0x13, 0x17, 0x21)),
                BorderThickness = new Thickness(0, 0, 1, 0),
                ToolTip = $"{name} · {FormatDuration(t0)} → {FormatDuration(t1)}",
                Child = new TextBlock
                {
                    Text = w > 46 ? name : "",
                    Foreground = StanceFg, FontSize = 10,
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ToolTipService.SetInitialShowDelay(seg, 150);
            Canvas.SetLeft(seg, x);
            canvas.Children.Add(seg);
        }
        board.Children.Add(row);
    }

    private static Dictionary<string, List<FightEvent>> ByAbility(FightRecord rec, FightStream stream)
    {
        var lanes = new Dictionary<string, List<FightEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in rec.Events)
        {
            if (e.Stream != stream) continue;
            if (!lanes.TryGetValue(e.Ability, out var list))
                lanes[e.Ability] = list = new List<FightEvent>();
            list.Add(e);
        }
        return lanes;
    }

    /// <summary>Cap a lane list, folding the tail into "(+N more)".</summary>
    private static List<(string Name, List<FightEvent> Events)> Capped(
        List<KeyValuePair<string, List<FightEvent>>> ordered, int cap = MaxLanesPerGroup)
    {
        var outp = ordered.Select(kv => (kv.Key, kv.Value)).ToList();
        if (outp.Count <= cap) return outp;
        var rest = outp.Skip(cap - 1).ToList();
        outp = outp.Take(cap - 1).ToList();
        outp.Add(($"(+{rest.Count} more)",
            rest.SelectMany(l => l.Item2).OrderBy(e => e.T).ToList()));
        return outp;
    }

    private void AddAxis(Panel board, double width, double dur, double scale)
    {
        var canvas = new Canvas
        {
            Height = 18, ClipToBounds = true,
            Margin = new Thickness(LabelWidth, 0, 0, 3),
        };
        double step = new[] { 1.0, 2, 5, 10, 15, 30, 60, 120, 300, 600 }
            .FirstOrDefault(s => dur / s <= 10, 600);
        for (double t = 0; t <= dur; t += step)
        {
            double x = t * scale;
            var tick = new Rectangle { Width = 1, Height = 5, Fill = AxisFg };
            Canvas.SetLeft(tick, x);
            Canvas.SetBottom(tick, 0);
            canvas.Children.Add(tick);
            var lbl = new TextBlock { Text = FormatDuration(t), Foreground = AxisFg, FontSize = 10 };
            Canvas.SetLeft(lbl, Math.Max(0, x - 10));
            Canvas.SetTop(lbl, 0);
            canvas.Children.Add(lbl);
        }
        board.Children.Add(canvas);
    }

    /// <summary>Rolling-DPS curves (5s window) for the given series.</summary>
    private void DrawGraph(Panel board, double width, double dur, double scale,
        (string Label, Brush Brush, Func<FightEvent, bool> Pick)[] series)
    {
        var rec = _rec!;
        double win = Math.Min(5, dur);
        double step = Math.Max(0.25, dur / 400);

        var sampled = new List<(string Label, Brush Brush, List<(double T, double Dps)> Points)>();
        double maxDps = 0;
        foreach (var (label, brush, pick) in series)
        {
            var evs = rec.Events.Where(e => pick(e) && e.Amount > 0).OrderBy(e => e.T).ToList();
            if (evs.Count == 0) continue;
            var pts = new List<(double, double)>();
            double sum = 0;
            int add = 0, drop = 0;
            for (double t = 0; t <= dur + 1e-9; t += step)
            {
                while (add < evs.Count && evs[add].T <= t) sum += evs[add++].Amount;
                while (drop < add && evs[drop].T <= t - win) sum -= evs[drop++].Amount;
                double dps = sum / Math.Min(Math.Max(t, step), win);
                pts.Add((t, dps));
                if (dps > maxDps) maxDps = dps;
            }
            sampled.Add((label, brush, pts));
        }
        if (maxDps <= 0) return;

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(LabelWidth, 2, 0, 2),
        };
        var canvas = new Canvas { Height = 96, Width = width, ClipToBounds = true };
        double h = canvas.Height;

        foreach (var (label, brush, pts) in sampled)
        {
            var line = new Polyline
            {
                Stroke = brush, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round,
            };
            foreach (var (t, dps) in pts)
                line.Points.Add(new Point(t * scale, h - 4 - dps / maxDps * (h - 12)));
            canvas.Children.Add(line);

            legend.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = brush,
                Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            legend.Children.Add(new TextBlock
            {
                Text = label, FontSize = 10, Foreground = (Brush)FindResource("Brush.TextHint"),
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        }
        legend.Children.Add(new TextBlock
        {
            Text = $"5s rolling · peak {maxDps:N0}/s", FontSize = 10, Foreground = AxisFg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        board.Children.Add(legend);

        board.Children.Add(new Border
        {
            Margin = new Thickness(LabelWidth, 0, 0, 4),
            Background = Freeze(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = canvas,
        });
    }

    // ---- analysis (runs once per visited fight — ShowFight no-ops on repeats) --

    /// <summary>Rules over ONE fight's own numbers — every claim cites them,
    /// and what the fight didn't record is said, not guessed.</summary>
    internal static string BuildAnalysis(FightRecord rec)
    {
        var lines = new List<string>();
        double dur = Math.Max(1, rec.DurationSeconds);
        double takenTotal = rec.IncomingSelfTotal;

        // 1 · The dominant incoming ability, its school, and your resist rate
        //     against it — the "you need more Cold resist" rule.
        var tops = rec.IncomingSelfAbilities
            .Where(a => a.Total > 0)
            .OrderByDescending(a => a.Total)
            .ToList();
        if (tops.Count > 0 && takenTotal > 0)
        {
            var top = tops[0];
            double share = 100.0 * top.Total / takenTotal;
            rec.Schools.TryGetValue(top.Name, out string? school);
            string line = school is not null
                ? $"• {share:0}% of the damage you took was {school.ToUpperInvariant()} — {top.Name} ({FormatNum(top.Total)} of {FormatNum(takenTotal)})."
                : $"• {share:0}% of the damage you took was {top.Name} ({FormatNum(top.Total)} of {FormatNum(takenTotal)}).";
            int casts = top.Hits + top.Resists;
            if (casts > 0 && top.Resists > 0)
                line += $" You resisted {top.Resists} of {casts} casts ({100.0 * top.Resists / casts:0}%).";
            if (school is not null && share >= 25 && !school.Equals("unresistable", StringComparison.OrdinalIgnoreCase))
                line += $" More {char.ToUpperInvariant(school[0]) + school[1..]} resist bites directly into their biggest tool.";
            if (school is null && rec.Schools.Count == 0 && top.Name != "hit")
                line += " (School unknown — recorded before school capture; new fights carry it.)";
            lines.Add(line);
        }

        // 2 · Your spells that kept bouncing — stick rates worth acting on.
        foreach (var a in rec.SelfAbilities
                     .Where(a => a.Resists > 0 && a.Hits + a.Resists >= 3)
                     .OrderByDescending(a => (double)a.Resists / (a.Hits + a.Resists)))
        {
            int casts = a.Hits + a.Resists;
            double rr = 100.0 * a.Resists / casts;
            if (rr >= 30)
                lines.Add($"• {a.Name} stuck only {100 - rr:0}% ({a.Resists} of {casts} resisted) — malo/tash territory, or a bad school matchup on this mob.");
        }

        // 2b · Melee landing under ~60% over a real sample: a weapon-skill or
        //      level gap — pair it with the /con level when known.
        var meleeRows = rec.SelfAbilities.Where(a => IsMeleeAbility(a.Name)).ToList();
        int swings = meleeRows.Sum(a => a.Hits + a.Misses);
        int landed = meleeRows.Sum(a => a.Hits);
        if (swings >= 20 && 100.0 * landed / swings < 60)
        {
            string vs = rec.EnemyLevels.Count > 0
                ? $" vs a Lvl {rec.EnemyLevels.Values.Max()} target" : "";
            lines.Add($"• Your melee landed only {100.0 * landed / swings:0}% ({landed} of {swings} swings){vs} — weapon skill or level gap.");
        }

        // 3 · Debuff coverage — the gaps are the story. When a debuff had real
        //     downtime, say what the naked stretches actually cost you; a
        //     damage DoT running sloppy gets the refresh nudge, and re-casts
        //     into a running span are clipping (paid-for ticks thrown away).
        double takenEvented = rec.Events
            .Where(e => e.Stream == FightStream.SelfIn && e.Amount > 0).Sum(e => e.Amount);
        int enemyCount = rec.Damage.Count(r => r.Enemy);
        foreach (var g in rec.Events.Where(e => e.Stream == FightStream.Debuff)
                     .GroupBy(e => e.Ability, StringComparer.OrdinalIgnoreCase))
        {
            double covered = CoverageSeconds(g.Select(e => (e.T, e.Amount)), dur);
            if (covered <= 0)
            {
                lines.Add($"• {g.Key} landed {g.Count()}× (duration unknown — coverage not computable).");
                continue;
            }
            bool isDot = rec.Events.Any(e => e.Stream == FightStream.SelfOut && e.Dot
                && e.Ability.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            var timed = g.Where(e => e.Amount > 0).OrderBy(e => e.T).ToList();
            double avgDur = timed.Average(e => e.Amount);

            string line = $"• {g.Key} was up ~{100.0 * covered / dur:0}% of the fight ({FormatDuration(covered)} of {FormatDuration(dur)}).";
            double gapSec = dur - covered;
            if (takenEvented > 0 && gapSec >= 3)
            {
                var spans = MergedSpans(g.Select(e => (e.T, e.Amount)), dur);
                double inGaps = rec.Events
                    .Where(e => e.Stream == FightStream.SelfIn && e.Amount > 0
                                && !spans.Any(s => e.T >= s.Start && e.T <= s.End))
                    .Sum(e => e.Amount);
                double share = 100.0 * inGaps / takenEvented;
                double gapShare = 100.0 * gapSec / dur;
                if (inGaps > 0 && share > gapShare + 10)
                    line += $" The gaps bit: {share:0}% of the damage you took landed in that {FormatDuration(gapSec)} without it.";
            }
            if (isDot && covered < 0.85 * dur && dur >= 2 * avgDur)
                line += " Refresh sooner — every gap second is unpaid ticks.";
            lines.Add(line);

            // Clipping only reads cleanly on single-enemy fights: with adds,
            // a re-land is usually a SECOND mob, not a wasted refresh.
            if (isDot && enemyCount == 1)
            {
                double clipped = 0;
                int clips = 0;
                for (int i = 1; i < timed.Count; i++)
                {
                    double over = timed[i - 1].T + timed[i - 1].Amount - timed[i].T;
                    if (over > 1) { clipped += over; clips++; }
                }
                if (clips > 0 && clipped >= 5)
                    lines.Add($"• You clipped {g.Key} {clips}× — ~{clipped:0}s of paid-for ticks thrown away (re-cast before it faded).");
            }
        }

        // 3b · CC on you — how long you were a passenger, and what it cost.
        var cc = rec.Events.Where(e => e.Stream == FightStream.Condition && e.Amount > 0).ToList();
        if (cc.Count > 0)
        {
            double held = CoverageSeconds(cc.Select(e => (e.T, e.Amount)), dur);
            string kinds = string.Join(", ", cc.GroupBy(e => e.Ability, StringComparer.OrdinalIgnoreCase)
                .Select(k => $"{k.Key.ToLowerInvariant()} {k.Count()}×"));
            string line = $"• CC held you for {FormatDuration(held)} ({100.0 * held / dur:0}% of the fight) — {kinds}.";
            if (takenEvented > 0)
            {
                var spans = MergedSpans(cc.Select(e => (e.T, e.Amount)), dur);
                double whileHeld = rec.Events
                    .Where(e => e.Stream == FightStream.SelfIn && e.Amount > 0
                                && spans.Any(s => e.T >= s.Start && e.T <= s.End))
                    .Sum(e => e.Amount);
                if (whileHeld > 0)
                    line += $" {100.0 * whileHeld / takenEvented:0}% of the damage you took landed while you were held.";
            }
            lines.Add(line);
        }

        // 3c · Casts that broke.
        var broken = rec.Events.Where(e => e.Stream == FightStream.Cast && e.Miss)
            .OrderBy(e => e.T).ToList();
        if (broken.Count > 0)
            lines.Add(broken.Count == 1
                ? $"• Your {broken[0].Ability} was interrupted at {FormatDuration(broken[0].T)}."
                : $"• {broken.Count} of your casts were interrupted (first: {broken[0].Ability} at {FormatDuration(broken[0].T)}).");

        // 3d · Stance — where the fight was actually spent.
        var stanceSegs = StanceSegments(rec);
        if (stanceSegs.Count > 0)
        {
            var byStance = stanceSegs.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(k => (Name: k.Key, Sec: k.Sum(s => s.T1 - s.T0)))
                .OrderByDescending(x => x.Sec).ToList();
            int switches = rec.Events.Count(e => e.Stream == FightStream.Stance);
            lines.Add(switches == 0
                ? $"• Entire fight in {byStance[0].Name} stance."
                : $"• Mostly {byStance[0].Name} stance ({100.0 * byStance[0].Sec / dur:0}%) — switched {switches}× mid-fight.");
        }

        // 3e · The pet: its share of the work, and the disaster moment.
        double petTotal = rec.PetAbilities.Sum(a => a.Total);
        double selfTotal = rec.SelfAbilities.Sum(a => a.Total);
        if (petTotal > 0)
        {
            string pet = rec.Pet.Length > 0 ? rec.Pet : "Your pet";
            string line = $"• {pet} dealt {100.0 * petTotal / Math.Max(1, selfTotal + petTotal):0}% of the team's damage ({FormatNum(petTotal)})";
            line += rec.IncomingPetTotal > 0
                ? $" and ate {FormatNum(rec.IncomingPetTotal)} of the incoming."
                : ".";
            lines.Add(line);
        }
        foreach (var d in rec.Events.Where(e => e.Stream == FightStream.PetDeath).OrderBy(e => e.T))
        {
            string line = $"• {d.Ability} at {FormatDuration(d.T)}";
            double before = d.T, after = dur - d.T;
            if (before >= 5 && after >= 5 && takenEvented > 0)
            {
                double takenAfter = rec.Events
                    .Where(e => e.Stream == FightStream.SelfIn && e.Amount > 0 && e.T > d.T)
                    .Sum(e => e.Amount);
                double rateBefore = (takenEvented - takenAfter) / before;
                double rateAfter = takenAfter / after;
                if (rateAfter > rateBefore * 1.3)
                    line += $" — you took {rateAfter:0}/s after vs {rateBefore:0}/s with the pet up.";
            }
            lines.Add(line + (line.EndsWith(".") ? "" : "."));
        }

        // 4 · The danger window: the 10s stretch where incoming outran healing
        //     the hardest — that's where deaths live. Needs healing events to
        //     compare against (a fight with none is covered by biggest-hit).
        var healEvents = rec.Events
            .Where(e => e.Stream is FightStream.HealOut or FightStream.HealIn && e.Amount > 0)
            .ToList();
        if (healEvents.Count > 0 && takenEvented > 0 && dur >= 20)
        {
            const double W = 10;
            double bestDef = 0, bestT = 0, bestTaken = 0, bestHeal = 0;
            for (double t = 0; t <= dur - W; t += 1)
            {
                double tk = rec.Events
                    .Where(e => e.Stream == FightStream.SelfIn && e.Amount > 0
                                && e.T >= t && e.T < t + W)
                    .Sum(e => e.Amount);
                double hl = healEvents.Where(e => e.T >= t && e.T < t + W).Sum(e => e.Amount);
                if (tk - hl > bestDef) { bestDef = tk - hl; bestT = t; bestTaken = tk; bestHeal = hl; }
            }
            if (bestDef >= 0.3 * takenEvented)
                lines.Add($"• Danger window {FormatDuration(bestT)}–{FormatDuration(bestT + W)}: you took {FormatNum(bestTaken)} while healing covered {FormatNum(bestHeal)} — that's where deaths live.");
        }

        // 4b · Who kept you alive, and the hit that nearly didn't let them.
        var healers = rec.Healing.Where(h => !h.Enemy && h.Total > 0)
            .OrderByDescending(h => h.Total).ToList();
        if (healers.Count > 0)
            lines.Add($"• {healers[0].Name} carried {healers[0].Percent:0}% of the healing ({FormatNum(healers[0].Total)}).");
        var big = rec.Events.Where(e => e.Stream == FightStream.SelfIn && e.Amount > 0)
            .OrderByDescending(e => e.Amount).FirstOrDefault();
        if (big is not null)
            lines.Add($"• Biggest hit on you: {big.Ability} {FormatNum(big.Amount)} at {FormatDuration(big.T)}.");

        return lines.Count > 0
            ? string.Join("\n", lines)
            : "Nothing to analyse — this fight recorded no incoming data.";
    }

    /// <summary>The fight cut into stance stretches: the stance at pull runs
    /// until the first change, each change until the next, the last to the end.
    /// Stretches before any known stance are simply absent — never guessed.</summary>
    internal static List<(string Name, double T0, double T1)> StanceSegments(FightRecord rec)
    {
        double dur = Math.Max(1, rec.DurationSeconds);
        var segs = new List<(string, double, double)>();
        string cur = rec.StanceAtStart;
        double t0 = 0;
        foreach (var c in rec.Events.Where(e => e.Stream == FightStream.Stance).OrderBy(e => e.T))
        {
            if (cur.Length > 0 && c.T > t0) segs.Add((cur, t0, c.T));
            cur = c.Ability;
            t0 = c.T;
        }
        if (cur.Length > 0 && dur > t0) segs.Add((cur, t0, dur));
        return segs;
    }

    /// <summary>Union length of [T, T+Dur] spans clipped to the fight.</summary>
    internal static double CoverageSeconds(IEnumerable<(double T, double Dur)> spans, double fightDur) =>
        MergedSpans(spans, fightDur).Sum(s => s.End - s.Start);

    /// <summary>[T, T+Dur] spans clipped to the fight, merged where they overlap.</summary>
    internal static List<(double Start, double End)> MergedSpans(
        IEnumerable<(double T, double Dur)> spans, double fightDur)
    {
        var list = spans.Where(s => s.Dur > 0)
            .Select(s => (Start: Math.Max(0, s.T), End: Math.Min(fightDur, s.T + s.Dur)))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
        var merged = new List<(double Start, double End)>();
        foreach (var s in list)
        {
            if (merged.Count > 0 && s.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, s.End));
            else
                merged.Add(s);
        }
        return merged;
    }

    // ---- helpers ---------------------------------------------------------------

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private static string FormatNum(double v) => v.ToString("N0");

    private static string FormatDps(double v) =>
        v >= 100 ? v.ToString("N0") : v.ToString("0.0");

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
