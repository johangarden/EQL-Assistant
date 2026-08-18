using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EQLOverlay.Interop;
using EQLOverlay.Services;
using static EQLOverlay.Services.CombatParser;

namespace EQLOverlay.Views;

/// <summary>
/// Visual timeline of one fight: a lane per ability, grouped by stream
/// (your damage, pet, damage taken, healing), with a mark per event —
/// height scales with the amount, crits are wider, misses and resists get
/// their own colors. Hover any mark for the exact numbers.
/// </summary>
public partial class TimelineWindow : Window
{
    private const double LabelWidth = 150;
    private const double LaneHeight = 22;
    private const int MaxLanesPerGroup = 8;

    private readonly FightRecord _rec;

    private static readonly Brush LaneBg = Freeze(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush AxisFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));
    private static readonly Brush MissFg = Freeze(Color.FromArgb(0x77, 0x8F, 0xA6, 0xC4));
    private static readonly Brush ResistFg = Freeze(Color.FromRgb(0xB3, 0x9D, 0xDB));

    private sealed record Group(string Title, Brush Header, Brush Mark, Brush CritMark, List<Lane> Lanes);
    private sealed record Lane(string Ability, List<FightEvent> Events, double Total);

    private readonly List<Group> _groups;

    public TimelineWindow(FightRecord rec)
    {
        InitializeComponent();
        DialogPlacement.Persist(this, "timeline");
        WindowTheme.ApplyDark(this);
        _rec = rec;
        _groups = BuildGroups(rec);

        TitleText.Text = $"Timeline — {rec.Label}";
        string zone = rec.Zone.Length > 0 ? $" · {rec.Zone}" : "";
        string truncated = rec.EventsTruncated ? $" · first {MaxFightEvents} events shown" : "";
        SubtitleText.Text =
            $"{rec.EndedAt:dd MMM HH:mm:ss} · {FormatDuration(rec.DurationSeconds)}{zone} · " +
            $"{rec.Events.Count} events{truncated} — hover a mark for details.";

        // Fires after the first layout pass too, so this is also the initial draw.
        LanesHost.SizeChanged += (_, _) => Rebuild();
    }

    private static List<Group> BuildGroups(FightRecord rec)
    {
        var groups = new List<Group>();
        Add("Your damage", FightStream.SelfOut, Rgb(0xFF, 0xC1, 0x2E), Rgb(0xFF, 0xE0, 0x82));
        Add("Pet damage", FightStream.PetOut, Rgb(0xA1, 0x88, 0x7F), Rgb(0xD7, 0xCC, 0xC8));
        Add("Damage taken", FightStream.SelfIn, Rgb(0xFF, 0x8A, 0x80), Rgb(0xFF, 0xCD, 0xD2));
        Add("Pet damage taken", FightStream.PetIn, Rgb(0xFF, 0xB7, 0x4D), Rgb(0xFF, 0xE0, 0xB2));
        Add("Your healing", FightStream.HealOut, Rgb(0x81, 0xC7, 0x84), Rgb(0xC8, 0xE6, 0xC9));
        Add("Heals on you", FightStream.HealIn, Rgb(0x4D, 0xB6, 0xAC), Rgb(0xB2, 0xDF, 0xDB));
        return groups;

        void Add(string title, FightStream stream, Brush mark, Brush crit)
        {
            var byAbility = new Dictionary<string, List<FightEvent>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in rec.Events)
            {
                if (e.Stream != stream) continue;
                if (!byAbility.TryGetValue(e.Ability, out var list))
                    byAbility[e.Ability] = list = new List<FightEvent>();
                list.Add(e);
            }
            if (byAbility.Count == 0) return;

            var lanes = byAbility
                .Select(kv => new Lane(kv.Key, kv.Value, kv.Value.Sum(e => e.Amount)))
                .OrderByDescending(l => l.Total)
                .ToList();
            if (lanes.Count > MaxLanesPerGroup)
            {
                var rest = lanes.Skip(MaxLanesPerGroup - 1).ToList();
                lanes = lanes.Take(MaxLanesPerGroup - 1).ToList();
                lanes.Add(new Lane($"(+{rest.Count} more)",
                    rest.SelectMany(l => l.Events).OrderBy(e => e.T).ToList(),
                    rest.Sum(l => l.Total)));
            }
            groups.Add(new Group(title, mark, mark, crit, lanes));
        }
    }

    // ---- drawing ---------------------------------------------------------------

    private void Rebuild()
    {
        LanesHost.Children.Clear();
        AxisCanvas.Children.Clear();

        double width = Math.Max(60, LanesHost.ActualWidth - LabelWidth);
        double dur = Math.Max(1, _rec.DurationSeconds);
        double scale = width / dur;

        DrawAxis(width, dur, scale);
        DrawGraph(width, dur, scale);

        if (_rec.Events.Count == 0)
        {
            LanesHost.Children.Add(new TextBlock
            {
                Text = "No timeline data for this fight — it was recorded (or ★-kept) before fight timelines existed. New fights record automatically.",
                Foreground = MissFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 12, 4, 0),
            });
            return;
        }

        foreach (var g in _groups)
        {
            LanesHost.Children.Add(new TextBlock
            {
                Text = g.Title,
                Foreground = g.Header,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = new Thickness(0, 9, 0, 3),
            });

            double max = g.Lanes.SelectMany(l => l.Events).Max(e => e.Amount);
            if (max <= 0) max = 1;

            foreach (var lane in g.Lanes)
                LanesHost.Children.Add(BuildLane(lane, g, max, width, scale));
        }
    }

    private FrameworkElement BuildLane(Lane lane, Group g, double groupMax, double width, double scale)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int hits = lane.Events.Count(e => !e.Miss && !e.Resist);
        var label = new TextBlock
        {
            Text = lane.Ability,
            Foreground = (Brush)FindResource("Brush.TextDim"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 8, 0),
            ToolTip = $"{lane.Ability} — {hits} landed, {FormatNum(lane.Total)} total",
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var canvas = new Canvas
        {
            Height = LaneHeight,
            ClipToBounds = true,
            Background = LaneBg,
        };
        Grid.SetColumn(canvas, 1);
        row.Children.Add(canvas);

        foreach (var e in lane.Events)
        {
            double x = Math.Min(width - 2, e.T * scale);
            Rectangle mark;
            string when = FormatDuration(e.T);

            if (e.Miss)
            {
                mark = new Rectangle { Width = 2, Height = 7, Fill = MissFg };
                mark.ToolTip = $"{when} · {lane.Ability} missed";
            }
            else if (e.Resist)
            {
                mark = new Rectangle { Width = 2, Height = 7, Fill = ResistFg };
                mark.ToolTip = $"{when} · {lane.Ability} resisted";
            }
            else
            {
                double h = 4 + 15 * Math.Sqrt(e.Amount / groupMax);
                mark = new Rectangle
                {
                    Width = e.Crit ? 3 : 2,
                    Height = Math.Min(LaneHeight - 2, h),
                    Fill = e.Crit ? g.CritMark : g.Mark,
                };
                mark.ToolTip = $"{when} · {lane.Ability} {e.Amount:N0}{(e.Crit ? " (Critical)" : "")}";
            }

            ToolTipService.SetInitialShowDelay(mark, 150);
            Canvas.SetLeft(mark, x);
            Canvas.SetBottom(mark, 1);
            canvas.Children.Add(mark);
        }

        return row;
    }

    /// <summary>Rolling-DPS curves (5s window) for each side of the fight.</summary>
    private void DrawGraph(double width, double dur, double scale)
    {
        GraphCanvas.Children.Clear();
        LegendPanel.Children.Clear();

        if (_rec.Events.Count == 0)
        {
            GraphHost.Visibility = Visibility.Collapsed;
            return;
        }
        GraphHost.Visibility = Visibility.Visible;
        GraphCanvas.Width = width;

        double h = GraphCanvas.Height;
        double win = Math.Min(5, dur);              // rolling window (short fights shrink it)
        double step = Math.Max(0.25, dur / 400);    // sample spacing

        var series = new (string Label, Brush Brush, Func<FightEvent, bool> Pick)[]
        {
            ("You", Rgb(0xFF, 0xC1, 0x2E), e => e.Stream == FightStream.SelfOut),
            ("Pet", Rgb(0xA1, 0x88, 0x7F), e => e.Stream == FightStream.PetOut),
            ("Taken", Rgb(0xFF, 0x8A, 0x80), e => e.Stream is FightStream.SelfIn or FightStream.PetIn),
            ("Healing", Rgb(0x81, 0xC7, 0x84), e => e.Stream is FightStream.HealOut or FightStream.HealIn),
        };

        // Pass 1: sample every series so they share one vertical scale.
        var sampled = new List<(string Label, Brush Brush, List<(double T, double Dps)> Points)>();
        double maxDps = 0;
        foreach (var (label, brush, pick) in series)
        {
            var evs = _rec.Events.Where(e => pick(e) && e.Amount > 0).OrderBy(e => e.T).ToList();
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
        if (maxDps <= 0) { GraphHost.Visibility = Visibility.Collapsed; return; }

        // Pass 2: draw, and build the legend for the series that exist.
        foreach (var (label, brush, pts) in sampled)
        {
            var line = new Polyline
            {
                Stroke = brush,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
            };
            foreach (var (t, dps) in pts)
                line.Points.Add(new Point(t * scale, h - 4 - dps / maxDps * (h - 12)));
            GraphCanvas.Children.Add(line);

            LegendPanel.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(2),
                Background = brush, Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            LegendPanel.Children.Add(new TextBlock
            {
                Text = label, FontSize = 10,
                Foreground = (Brush)FindResource("Brush.TextHint"),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        PeakText.Text = $"5s rolling · peak {maxDps:N0}/s";
    }

    private void DrawAxis(double width, double dur, double scale)
    {
        double step = new[] { 1.0, 2, 5, 10, 15, 30, 60, 120, 300, 600 }
            .FirstOrDefault(s => dur / s <= 10, 600);

        for (double t = 0; t <= dur; t += step)
        {
            double x = t * scale;
            var tick = new Rectangle { Width = 1, Height = 5, Fill = AxisFg };
            Canvas.SetLeft(tick, x);
            Canvas.SetBottom(tick, 0);
            AxisCanvas.Children.Add(tick);

            var lbl = new TextBlock
            {
                Text = FormatDuration(t),
                Foreground = AxisFg,
                FontSize = 10,
            };
            Canvas.SetLeft(lbl, Math.Max(0, x - 10));
            Canvas.SetTop(lbl, 0);
            AxisCanvas.Children.Add(lbl);
        }
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

    private static Brush Rgb(byte r, byte g, byte b) => Freeze(Color.FromRgb(r, g, b));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
