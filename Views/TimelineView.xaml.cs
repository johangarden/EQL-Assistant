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
        // WIDTH changes only: folding a board changes our HEIGHT, and a
        // height-triggered rebuild recreates the headers mid-click (worse,
        // the outer scrollbar can flip the width back and forth and loop it).
        SizeChanged += (_, e) => { if (e.WidthChanged) Rebuild(); };
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
        var (off, def) = BuildAnalysisParts(rec);
        string quiet = rec.Events.Count > 0
            ? "Nothing to flag."
            : "No data — recorded before fight timelines existed.";
        OffAnalysisText.Text = off.Count > 0 ? string.Join("\n", off) : quiet;
        DefAnalysisText.Text = def.Count > 0 ? string.Join("\n", def) : quiet;
        AnalysisHost.Visibility = Visibility.Visible;
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
        if (rec.Allies.Count > 0)
            AddChip($"With: {string.Join(" · ", rec.Allies)}");
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

        // Actor identity from the record's own stamps (legacy fights fall back
        // to ability sums — they can't name the pet, but the math still holds).
        string selfName = rec.Character.Length > 0 ? rec.Character : "You";
        var petNames = new HashSet<string>(rec.Pets, StringComparer.OrdinalIgnoreCase);
        if (rec.Pet.Length > 0) petNames.Add(rec.Pet);
        bool IsSelfRow(Row r) => r.Name.Equals(selfName, StringComparison.OrdinalIgnoreCase)
                                 || r.Name.Equals("You", StringComparison.OrdinalIgnoreCase);
        bool IsPetRow(Row r) => petNames.Contains(r.Name);

        var friendly = rec.Damage.Where(r => !r.Enemy).ToList();
        double selfTotal = friendly.Where(IsSelfRow).Sum(r => r.Total);
        if (selfTotal <= 0) selfTotal = rec.SelfAbilities.Sum(a => a.Total);
        double petTotal = friendly.Where(IsPetRow).Sum(r => r.Total);
        if (petTotal <= 0) petTotal = rec.PetAbilities.Sum(a => a.Total);
        var otherRows = friendly.Where(r => !IsSelfRow(r) && !IsPetRow(r))
            .OrderByDescending(r => r.Total).ToList();
        double othersTotal = otherRows.Sum(r => r.Total);
        double friendlyTotal = Math.Max(friendly.Sum(r => r.Total), selfTotal + petTotal + othersTotal);
        if (friendlyTotal <= 0) friendlyTotal = 1;
        double healTotal = rec.Healing.Where(r => !r.Enemy).Sum(r => r.Total);
        double takenTotal = rec.IncomingSelfTotal + rec.IncomingPetTotal;

        // ---- row 1 · the fight -------------------------------------------------
        var row1 = new List<Border>
        {
            Tile("DURATION", FormatDuration(rec.DurationSeconds), "",
                $"{rec.EndedAt:dd MMM HH:mm}", NeutralFg),
        };
        double peak = RollingPeak(rec.Events.Where(e =>
            e.Stream is FightStream.SelfOut or FightStream.PetOut && e.Amount > 0), dur);
        row1.Add(Tile("TOTAL DPS", FormatDps(friendlyTotal / dur), "dps",
            peak > 0 ? $"peak {peak:N0}/s" : "", DmgFg));
        var dmgParts = new List<string> { "you" };
        if (petTotal > 0) dmgParts.Add(rec.Pet.Length > 0 ? rec.Pet : "pet");
        if (othersTotal > 0) dmgParts.Add("others");
        row1.Add(Tile("TOTAL DAMAGE", FormatNum(friendlyTotal), "",
            dmgParts.Count > 1 ? string.Join(" + ", dmgParts) : "", DmgFg));
        row1.Add(Tile("DAMAGE TAKEN", FormatNum(takenTotal), "",
            petTotal > 0 || rec.IncomingPetTotal > 0
                ? $"You: {FormatNum(rec.IncomingSelfTotal)} · Pet(s): {FormatNum(rec.IncomingPetTotal)}"
                : "", TakenFg));
        if (healTotal > 0)
            row1.Add(Tile("HEALED", FormatNum(healTotal), "", $"{FormatDps(healTotal / dur)}/s", HealFg));
        AddRow(row1);

        // ---- row 2 · who dealt it ----------------------------------------------
        var row2 = new List<Border>();
        var topSelf = rec.SelfAbilities.OrderByDescending(a => a.Total).FirstOrDefault();
        row2.Add(ActorCard($"DPS · You · {selfName}", DmgFg,
            100.0 * selfTotal / friendlyTotal, selfTotal / dur, selfTotal,
            topSelf.Total > 0 ? topSelf.Name : null,
            topSelf.Total > 0 ? $" — {FormatNum(topSelf.Total)} ({100.0 * topSelf.Total / Math.Max(1, selfTotal):0}%)" : null));

        if (petTotal > 0)
        {
            // Top = the pets' most damaging ABILITY, same as the You card.
            // Legacy fights without a pet drill-down fall back to naming the
            // top-contributing pet instead of showing nothing.
            var topPetAbility = rec.PetAbilities.OrderByDescending(a => a.Total).FirstOrDefault();
            string? topName;
            double topTotal;
            if (topPetAbility.Total > 0)
            {
                topName = topPetAbility.Name;
                topTotal = topPetAbility.Total;
            }
            else
            {
                var petRows = friendly.Where(IsPetRow).OrderByDescending(r => r.Total).ToList();
                topName = petRows.Count > 0 ? petRows[0].Name : null;
                topTotal = petRows.Count > 0 ? petRows[0].Total : 0;
            }
            row2.Add(ActorCard("DPS · Pet(s)", PetFg,
                100.0 * petTotal / friendlyTotal, petTotal / dur, petTotal,
                topName,
                topName is not null
                    ? $" — {FormatNum(topTotal)} ({100.0 * topTotal / Math.Max(1, petTotal):0}%)"
                    : null));
        }

        if (othersTotal > 0)
        {
            string names = string.Join(", ", otherRows.Take(3).Select(r => r.Name))
                + (otherRows.Count > 3 ? $" +{otherRows.Count - 3}" : "");
            var topOther = otherRows[0];
            row2.Add(ActorCard($"DPS · Others · {names}", NeutralFg,
                100.0 * othersTotal / friendlyTotal, othersTotal / dur, othersTotal,
                topOther.Name,
                $" — {FormatNum(topOther.Total)} ({100.0 * topOther.Total / Math.Max(1, othersTotal):0}%)"));
        }
        AddRow(row2);

        // ---- row 3 · what hit you ----------------------------------------------
        var hits = rec.Events.Where(e => e.Stream == FightStream.SelfIn && e.Amount > 0)
            .OrderByDescending(e => e.Amount).Take(3).ToList();
        var worst = rec.IncomingSelfAbilities.Where(a => a.Total > 0)
            .OrderByDescending(a => a.Total).Take(3).ToList();
        if (hits.Count > 0 || worst.Count > 0 || rec.Events.Count > 0)
        {
            // The lists keep natural width; the pulse claims the rest — and
            // the ROW spans the same full width as every other row.
            var row3 = new Grid { Margin = new Thickness(0, 0, -8, 0) };
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TilesPanel.Children.Add(row3);
            if (hits.Count > 0)
            {
                var card = ListCard("HARDEST HITS ON YOU", hits.Select(e =>
                    (FormatNum(e.Amount), $"{e.Ability} · {FormatDuration(e.T)}")));
                Grid.SetColumn(card, 0);
                row3.Children.Add(card);
            }
            if (worst.Count > 0)
            {
                var card = ListCard("WORST INCOMING ABILITIES", worst.Select(a =>
                    (FormatNum(a.Total),
                     $"{a.Name} · {100.0 * a.Total / Math.Max(1, rec.IncomingSelfTotal):0}% · {a.Hits} hits")));
                Grid.SetColumn(card, 1);
                row3.Children.Add(card);
            }
            if (rec.Events.Count > 0)
            {
                var pulse = PulseCard(rec);
                Grid.SetColumn(pulse, 2);
                row3.Children.Add(pulse);
            }
        }
    }

    /// <summary>The fight's heartbeat in miniature: dealt (gold), taken (red)
    /// and healing (green) as 5s-rolling curves — fills the leftover width.</summary>
    private static Border PulseCard(FightRecord rec)
    {
        var stack = new StackPanel();
        stack.Children.Add(TileLabel("THE PULSE"));
        var canvas = new Canvas
        {
            Height = 52,
            ClipToBounds = true,
            Margin = new Thickness(0, 5, 0, 0),
            Background = Freeze(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
        };
        canvas.SizeChanged += (_, _) => DrawSparkline(canvas, rec);
        stack.Children.Add(canvas);
        return TileCard(stack, minWidth: 180);
    }

    private static void DrawSparkline(Canvas canvas, FightRecord rec)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        if (w < 20) return;
        double h = canvas.Height;
        double dur = Math.Max(1, rec.DurationSeconds);
        double win = Math.Min(5, dur);
        double step = Math.Max(0.25, dur / 300);
        double scale = w / dur;

        var series = new (Brush Brush, double Thick, Func<FightEvent, bool> Pick)[]
        {
            (HealFg, 1.1, e => e.Stream is FightStream.HealOut or FightStream.HealIn),
            (TakenFg, 1.3, e => e.Stream is FightStream.SelfIn or FightStream.PetIn),
            (DmgFg, 1.5, e => e.Stream is FightStream.SelfOut or FightStream.PetOut),
        };

        var sampled = new List<(Brush Brush, double Thick, List<(double T, double Dps)> Pts)>();
        double maxDps = 0;
        foreach (var (brush, thick, pick) in series)
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
            sampled.Add((brush, thick, pts));
        }
        if (maxDps <= 0) return;

        foreach (var (brush, thick, pts) in sampled)
        {
            var line = new Polyline
            {
                Stroke = brush, StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round,
            };
            foreach (var (t, dps) in pts)
                line.Points.Add(new Point(t * scale, h - 2 - dps / maxDps * (h - 6)));
            canvas.Children.Add(line);
        }
    }

    /// <summary>Lay one row of cards on equal star columns spanning the full
    /// report width — every row shares both edges, every card in a row is the
    /// same size. (The −8 right margin swallows the last card's gutter so the
    /// rows sit flush with the analysis boxes and boards below.)</summary>
    private void AddRow(List<Border> cards)
    {
        if (cards.Count == 0) return;
        var row = new Grid { Margin = new Thickness(0, 0, -8, 0) };
        for (int i = 0; i < cards.Count; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(cards[i], i);
            row.Children.Add(cards[i]);
        }
        TilesPanel.Children.Add(row);
    }

    private static Border TileCard(UIElement child, double minWidth = 150) => new()
    {
        Background = CardBg,
        BorderBrush = CardLine,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 7, 14, 7),
        Margin = new Thickness(0, 0, 8, 8),
        MinWidth = minWidth,
        Child = child,
    };

    private static TextBlock TileLabel(string label) => new()
    {
        Text = label, FontSize = 9, Foreground = AxisFg, FontWeight = FontWeights.SemiBold,
    };

    private static Border Tile(string label, string big, string unit, string sub, Brush accent)
    {
        // Row-1 tiles read centered — they're single numbers, not lists.
        var stack = new StackPanel();
        var lbl = TileLabel(label);
        lbl.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(lbl);
        var bigLine = new TextBlock
        {
            Margin = new Thickness(0, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        bigLine.Inlines.Add(new System.Windows.Documents.Run(big)
        { FontSize = 22, FontWeight = FontWeights.Bold, Foreground = accent });
        if (unit.Length > 0)
            bigLine.Inlines.Add(new System.Windows.Documents.Run(" " + unit)
            { FontSize = 12, Foreground = ChipFg });
        stack.Children.Add(bigLine);
        if (sub.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = sub, FontSize = 11, Foreground = ChipFg,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        return TileCard(stack);
    }

    /// <summary>Row-2 card: share · dps · total on one even line, top tool under.</summary>
    private static Border ActorCard(string label, Brush accent,
        double sharePct, double dps, double total, string? topName, string? topRest)
    {
        var stack = new StackPanel();
        stack.Children.Add(TileLabel(label.ToUpperInvariant()));
        var line = new TextBlock { Margin = new Thickness(0, 2, 0, 1) };
        line.Inlines.Add(new System.Windows.Documents.Run($"{sharePct:0}%")
        { FontSize = 16, FontWeight = FontWeights.Bold, Foreground = accent });
        line.Inlines.Add(new System.Windows.Documents.Run($" · {FormatDps(dps)} dps · {FormatNum(total)}")
        { FontSize = 13, Foreground = Freeze(Color.FromRgb(0xC9, 0xD4, 0xE3)) });
        stack.Children.Add(line);
        if (topName is not null)
        {
            var top = new TextBlock { FontSize = 11 };
            top.Inlines.Add(new System.Windows.Documents.Run("top: ") { Foreground = ChipFg });
            top.Inlines.Add(new System.Windows.Documents.Run(topName)
            { Foreground = accent, FontWeight = FontWeights.SemiBold });
            top.Inlines.Add(new System.Windows.Documents.Run(topRest) { Foreground = ChipFg });
            stack.Children.Add(top);
        }
        return TileCard(stack, minWidth: 250);
    }

    /// <summary>Row-3 card: a top-3 list, value leading in the accent color.</summary>
    private static Border ListCard(string label, IEnumerable<(string Val, string Tail)> rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(TileLabel(label));
        foreach (var (val, rest) in rows)
        {
            var line = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            line.Inlines.Add(new System.Windows.Documents.Run(val)
            { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = TakenFg });
            line.Inlines.Add(new System.Windows.Documents.Run("  " + rest) { Foreground = ChipFg });
            stack.Children.Add(line);
        }
        return TileCard(stack, minWidth: 250);
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
            () => _offenceOpen, v => _offenceOpen = v, out var graphPart, out var detailPart);
        AddAxis(graphPart, width, dur, scale);
        DrawGraph(graphPart, width, dur, scale, new[]
        {
            ("You", DmgFg, (Func<FightEvent, bool>)(e => e.Stream == FightStream.SelfOut)),
            (rec.Pet.Length > 0 ? rec.Pet : "Pet", PetFg, e => e.Stream == FightStream.PetOut),
        });
        AddLanes(detailPart, lanes, width, scale);
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
            () => _defenceOpen, v => _defenceOpen = v, out var graphPart, out var detailPart);
        AddAxis(graphPart, width, dur, scale);
        DrawGraph(graphPart, width, dur, scale, new[]
        {
            ("Taken", TakenFg, (Func<FightEvent, bool>)(e => e.Stream is FightStream.SelfIn or FightStream.PetIn)),
            ("Healing", HealFg, e => e.Stream is FightStream.HealOut or FightStream.HealIn),
        });
        AddLanes(detailPart, lanes, width, scale);

        if (stanceSegs.Count > 0)
            AddStanceStrip(detailPart, stanceSegs, width, scale);
        BoardsHost.Children.Add(board);
    }

    // ---- board plumbing --------------------------------------------------------

    // Fold state survives selection changes and fights — app-session memory.
    // Folded by default: the pulse at first glance, details on demand.
    private static bool _offenceOpen;
    private static bool _defenceOpen;

    /// <summary>A foldable board in a section card: clickable ▾/▸ header, the
    /// graph part ALWAYS visible (a folded board still shows the fight's
    /// pulse), only the lane details fold. Custom, never a stock Expander —
    /// every control themed from day one.</summary>
    private static Border NewBoard(string title, Brush accent, string key,
        Func<bool> isOpen, Action<bool> setOpen, out Panel graphPart, out Panel detailPart)
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

        var always = new StackPanel();
        board.Children.Add(always);
        graphPart = always;

        var details = new StackPanel
        {
            Visibility = isOpen() ? Visibility.Visible : Visibility.Collapsed,
        };
        board.Children.Add(details);
        detailPart = details;

        // ONE element carries the folded state, and the toggle flips THAT
        // element — a builder/handler mismatch here once froze boards shut.
        head.MouseLeftButtonDown += (_, _) =>
        {
            bool open = !isOpen();
            setOpen(open);
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>All verdicts as one text (the selftests' view of the analysis).</summary>
    internal static string BuildAnalysis(FightRecord rec)
    {
        var (off, def) = BuildAnalysisParts(rec);
        var all = off.Concat(def).ToList();
        return all.Count > 0
            ? string.Join("\n", all)
            : rec.Events.Count > 0
                ? "Nothing to flag — a clean fight by the playbook's thresholds."
                : "Nothing to analyse — this fight recorded no incoming data.";
    }

    /// <summary>Rules over ONE fight's own numbers — every claim cites them,
    /// and what the fight didn't record is said, not guessed. Split by the
    /// player's mindset: offence (why was my dps low) and defence (why did I
    /// take so much / will I survive).</summary>
    internal static (List<string> Offence, List<string> Defence) BuildAnalysisParts(FightRecord rec)
    {
        var off = new List<string>();
        var def = new List<string>();
        double dur = Math.Max(1, rec.DurationSeconds);
        double takenTotal = rec.IncomingSelfTotal;

        // 1 · The dominant incoming ability, its school, and your resist rate
        //     against it — the "you need more Cold resist" rule.
        var tops = rec.IncomingSelfAbilities
            .Where(a => a.Total > 0)
            .OrderByDescending(a => a.Total)
            .ToList();
        if (tops.Count > 0 && takenTotal > 0
            // Verdicts, not statistics: with no school (bare melee "hit") and
            // no resist data, there is no advice to give — the tiles and the
            // What-hit-you table already carry the number itself.
            && (rec.Schools.ContainsKey(tops[0].Name) || tops[0].Resists > 0))
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
            def.Add(line);
        }

        // 2 · Your spells that kept bouncing — stick rates worth acting on.
        foreach (var a in rec.SelfAbilities
                     .Where(a => a.Resists > 0 && a.Hits + a.Resists >= 3)
                     .OrderByDescending(a => (double)a.Resists / (a.Hits + a.Resists)))
        {
            int casts = a.Hits + a.Resists;
            double rr = 100.0 * a.Resists / casts;
            if (rr >= 30)
                off.Add($"• {a.Name} stuck only {100 - rr:0}% ({a.Resists} of {casts} resisted) — malo/tash territory, or a bad school matchup on this mob.");
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
            off.Add($"• Your melee landed only {100.0 * landed / swings:0}% ({landed} of {swings} swings){vs} — weapon skill or level gap.");
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
            // A damage DoT is an offence lever; a pure debuff (slow/malo)
            // shapes the incoming side — each verdict lands in its box.
            bool isDot = rec.Events.Any(e => e.Stream == FightStream.SelfOut && e.Dot
                && e.Ability.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            var box = isDot ? off : def;
            double covered = CoverageSeconds(g.Select(e => (e.T, e.Amount)), dur);
            if (covered <= 0)
            {
                box.Add($"• {g.Key} landed {g.Count()}× (duration unknown — coverage not computable).");
                continue;
            }
            var timed = g.Where(e => e.Amount > 0).OrderBy(e => e.T).ToList();
            double avgDur = timed.Average(e => e.Amount);

            string line = $"• {g.Key} was up ~{100.0 * covered / dur:0}% of the fight ({FormatDuration(covered)} of {FormatDuration(dur)}).";
            bool verdict = false;
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
                {
                    line += $" The gaps bit: {share:0}% of the damage you took landed in that {FormatDuration(gapSec)} without it.";
                    verdict = true;
                }
            }
            if (isDot && covered < 0.85 * dur && dur >= 2 * avgDur)
            {
                line += " Refresh sooner — every gap second is unpaid ticks.";
                verdict = true;
            }
            // A clean high uptime is not a finding — the span lane already
            // shows it. Print only below the flag threshold, or with a verdict.
            if (verdict || covered < 0.85 * dur)
                box.Add(line);

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
                    off.Add($"• You clipped {g.Key} {clips}× — ~{clipped:0}s of paid-for ticks thrown away (re-cast before it faded).");
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
            def.Add(line);
        }

        // 3c · Casts that broke.
        var broken = rec.Events.Where(e => e.Stream == FightStream.Cast && e.Miss)
            .OrderBy(e => e.T).ToList();
        if (broken.Count > 0)
            off.Add(broken.Count == 1
                ? $"• Your {broken[0].Ability} was interrupted at {FormatDuration(broken[0].T)}."
                : $"• {broken.Count} of your casts were interrupted (first: {broken[0].Ability} at {FormatDuration(broken[0].T)}).");

        // 3d · Stance — a verdict only when the fight actually danced; the
        //      stance chip and strip already say where it sat.
        var stanceSegs = StanceSegments(rec);
        if (stanceSegs.Count > 0)
        {
            var byStance = stanceSegs.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(k => (Name: k.Key, Sec: k.Sum(s => s.T1 - s.T0)))
                .OrderByDescending(x => x.Sec).ToList();
            int switches = rec.Events.Count(e => e.Stream == FightStream.Stance);
            if (switches > 0)
                def.Add($"• Mostly {byStance[0].Name} stance ({100.0 * byStance[0].Sec / dur:0}%) — switched {switches}× mid-fight.");
        }

        // 3e · The pet's disaster moment. Its SHARE lives in the tiles — the
        //      analysis only speaks when the pet dies.
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
            def.Add(line + (line.EndsWith(".") ? "" : "."));
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
                def.Add($"• Danger window {FormatDuration(bestT)}–{FormatDuration(bestT + W)}: you took {FormatNum(bestTaken)} while healing covered {FormatNum(bestHeal)} — that's where deaths live.");
        }

        // 4b · Who kept you alive — a finding only when it WASN'T you (solo
        //      self-healing is the tiles' story; the biggest hit is a tile too).
        var healers = rec.Healing.Where(h => !h.Enemy && h.Total > 0)
            .OrderByDescending(h => h.Total).ToList();
        if (healers.Count > 0
            && !healers[0].Name.Equals(rec.Character, StringComparison.OrdinalIgnoreCase)
            && !healers[0].Name.Equals("You", StringComparison.OrdinalIgnoreCase))
            def.Add($"• {healers[0].Name} carried {healers[0].Percent:0}% of the healing ({FormatNum(healers[0].Total)}) — send a thanks.");

        return (off, def);
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
