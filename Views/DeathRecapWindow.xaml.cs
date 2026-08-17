using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Death recap popup, rebuilt around two questions ("C + A"): the DEATH GRAPH
/// — one column per second over the last 15s, damage down / healing up, the
/// killing burst in bright red — answers "was I burst down or worn down?" at
/// a glance; the GROUPED LEDGER below is the receipt: repeats of the same
/// attacker · ability merge into ×N rows, and misses collapse into chips
/// instead of eating rows.
/// </summary>
public partial class DeathRecapWindow : Window
{
    /// <summary>The killing-burst window: the final N seconds before death.</summary>
    public const double BurstWindowSec = 2;

    private static readonly Brush DamageFg = Freeze(Color.FromRgb(0xFF, 0x8A, 0x80));
    private static readonly Brush HealFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush TextFg = Freeze(Color.FromRgb(0xDC, 0xE6, 0xF5));
    private static readonly Brush RowEven = Freeze(Color.FromRgb(0x1B, 0x21, 0x30));
    private static readonly Brush RowBig = Freeze(Color.FromRgb(0x3A, 0x1F, 0x24)); // killing-blow tint
    private static readonly Brush RowOdd = Brushes.Transparent;
    private static readonly Brush BarDmg = Freeze(Color.FromRgb(0xE5, 0x73, 0x73));
    private static readonly Brush BarHeal = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush BarKill = Freeze(Color.FromRgb(0xFF, 0x5A, 0x50));
    private static readonly Brush AxisFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));

    public sealed record RowVm(string T, string Text, string AmountText,
        Brush AmountBrush, Brush TextBrush, Brush RowBg);

    /// <summary>One merged ledger row: every event of (source, ability, heal) in the window.</summary>
    public sealed record RecapGroup(string Source, string Ability, bool Heal,
        int Count, double Total, DateTime First, bool HasBiggestHit);

    public DeathRecapWindow(CombatParser.DeathEvent death)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        Update(death);
    }

    /// <summary>Refill the window for a newer death (window is reused).</summary>
    public void Update(CombatParser.DeathEvent death)
    {
        TitleText.Text = death.Killer.Length > 0
            ? $"💀 Killed by {death.Killer}"
            : "💀 You died";

        var events = death.Events
            .Where(e => (death.When - e.When).TotalSeconds <= CombatParser.RecapWindowSec)
            .ToList();
        var biggest = events.Where(e => !e.Heal && !e.Miss)
            .MaxBy(e => e.Amount);

        // ---- header ---------------------------------------------------------
        double taken = events.Where(e => !e.Heal).Sum(e => e.Amount);
        double healed = events.Where(e => e.Heal).Sum(e => e.Amount);
        double span = events.Count > 0
            ? Math.Max(1, (death.When - events[0].When).TotalSeconds) : 0;
        int attackers = events.Where(e => !e.Heal && e.Source.Length > 0)
            .Select(e => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SummaryText.Text = events.Count == 0
            ? $"{death.When:HH:mm:ss} — no incoming damage was recorded before this death."
            : $"{death.When:HH:mm:ss} · last {span:0}s: took {taken:N0}"
              + (healed > 0 ? $" · healed {healed:N0}" : "")
              + (attackers > 0 ? $" · {attackers} attacker{(attackers == 1 ? "" : "s")}" : "");

        // ---- the story ------------------------------------------------------
        string story = BuildStory(death, events, taken, healed, span);
        StoryBorder.Visibility = story.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StoryText.Text = story;

        // ---- the death graph ------------------------------------------------
        bool burst = HasKillingBurst(death, events, taken);
        BuildGraph(death, events, burst);
        GraphSection.Visibility = events.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BurstLegendChip.Visibility = burst ? Visibility.Visible : Visibility.Collapsed;
        BurstLegendText.Visibility = burst ? Visibility.Visible : Visibility.Collapsed;

        // ---- the grouped ledger ---------------------------------------------
        var groups = GroupEvents(events, biggest);
        var rows = new List<RowVm>();
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            double dt = (death.When - g.First).TotalSeconds;
            string who = g.Source.Length > 0 ? g.Source : "(unknown)";
            string times = g.Count > 1 ? $" ×{g.Count}" : "";
            rows.Add(new RowVm(
                dt <= 0 ? "0.0s" : $"-{dt:0.0}s",
                $"{who} · {g.Ability}{times}",
                g.Heal ? $"+{g.Total:N0}" : $"-{g.Total:N0}",
                g.Heal ? HealFg : DamageFg,
                TextFg,
                g.HasBiggestHit ? RowBig : i % 2 == 0 ? RowEven : RowOdd));
        }
        RowsControl.ItemsSource = rows;

        // ---- misses as chips ------------------------------------------------
        var missChips = events.Where(e => e.Miss)
            .GroupBy(e => e.Source.Length > 0 ? e.Source : "(unknown)",
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ×{g.Count()}")
            .ToList();
        MissChips.ItemsSource = missChips;
        MissSection.Visibility = missChips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Merge every (source, ability, heal) repeat into one ×N row,
    /// ordered by first occurrence; misses are excluded (they become chips).</summary>
    public static List<RecapGroup> GroupEvents(
        IReadOnlyList<CombatParser.RecapEntry> events, CombatParser.RecapEntry? biggest)
    {
        return events.Where(e => !e.Miss)
            .GroupBy(e => (Source: e.Source.ToLowerInvariant(),
                           Ability: e.Ability.ToLowerInvariant(), e.Heal))
            .Select(g => new RecapGroup(
                g.First().Source, g.First().Ability, g.Key.Heal,
                g.Count(), g.Sum(e => e.Amount), g.Min(e => e.When),
                biggest is not null && g.Contains(biggest)))
            .OrderBy(g => g.First)
            .ToList();
    }

    /// <summary>Did a final-2s spike carry ≥40% of the window's damage?</summary>
    public static bool HasKillingBurst(CombatParser.DeathEvent death,
        IReadOnlyList<CombatParser.RecapEntry> events, double taken)
    {
        double burstDmg = events
            .Where(e => !e.Heal && !e.Miss
                        && (death.When - e.When).TotalSeconds <= BurstWindowSec)
            .Sum(e => e.Amount);
        return burstDmg > 0 && burstDmg >= 0.4 * taken;
    }

    /// <summary>The one-line verdict: the killing burst named outright; a
    /// wearing-down; or — when healing covered everything the log shows — the
    /// honest admission that this window doesn't explain the death.</summary>
    public static string BuildStory(CombatParser.DeathEvent death,
        IReadOnlyList<CombatParser.RecapEntry> events,
        double taken, double healed, double span)
    {
        if (taken <= 0) return "";

        if (HasKillingBurst(death, events, taken))
        {
            var burst = events
                .Where(e => !e.Heal && !e.Miss
                            && (death.When - e.When).TotalSeconds <= BurstWindowSec)
                .ToList();
            var top = burst
                .GroupBy(e => (e.Source, e.Ability))
                .Select(g => (g.Key.Source, g.Key.Ability, Total: g.Sum(x => x.Amount)))
                .OrderByDescending(x => x.Total)
                .Take(2)
                .Select(x => $"{(x.Source.Length > 0 ? x.Source : "unknown")} · {x.Ability} −{x.Total:N0}");
            return $"The burst that killed you: −{burst.Sum(e => e.Amount):N0} in the last {BurstWindowSec:0}s ({string.Join(", ", top)}).";
        }

        // Healing covered everything the log shows, and you still died — the
        // killing damage came before this window, or in a line form the
        // parser doesn't know yet. Saying "worn down" here would be a lie.
        if (healed >= taken)
            return $"These {span:0}s don't add up to a death: +{healed:N0} healing covered the "
                 + $"−{taken:N0} taken — the killing damage came earlier, or in a log line "
                 + "the parser doesn't know yet.";

        return $"Worn down — no single burst: −{taken:N0} over {span:0}s"
               + (healed > 0 ? $" against +{healed:N0} healing." : ".");
    }

    /// <summary>One column per second: damage hangs down, healing stands up
    /// around the centre axis; the killing-burst seconds glow brighter (only
    /// when a burst verdict fired — otherwise plain red throughout). Scaled
    /// to the busiest second; hover a column for its events.</summary>
    private void BuildGraph(CombatParser.DeathEvent death,
        IReadOnlyList<CombatParser.RecapEntry> events, bool burstActive)
    {
        const double halfHeight = 54;
        int cols = (int)CombatParser.RecapWindowSec + 1; // −15 … 0

        var dmg = new double[cols];
        var heal = new double[cols];
        var perSecond = new List<CombatParser.RecapEntry>[cols];
        foreach (var e in events)
        {
            if (e.Miss) continue;
            int back = (int)Math.Clamp((death.When - e.When).TotalSeconds, 0, cols - 1);
            int col = cols - 1 - back;
            if (e.Heal) heal[col] += e.Amount; else dmg[col] += e.Amount;
            (perSecond[col] ??= new()).Add(e);
        }
        double max = Math.Max(1, Math.Max(dmg.Max(), heal.Max()));
        PeakText.Text = $"peak −{dmg.Max():N0} / +{heal.Max():N0} per s";

        GraphGrid.Children.Clear();
        AxisGrid.Children.Clear();
        GraphGrid.Columns = cols;
        AxisGrid.Columns = cols;
        for (int i = 0; i < cols; i++)
        {
            int back = cols - 1 - i;
            bool inBurst = burstActive && back <= BurstWindowSec;

            var cell = new Grid { Background = Brushes.Transparent };
            if (perSecond[i] is { } list)
            {
                var tip = string.Join("\n", list.Select(e =>
                    $"{(e.Source.Length > 0 ? e.Source : "(unknown)")} · {e.Ability}  "
                    + (e.Heal ? $"+{e.Amount:N0}" : $"−{e.Amount:N0}")));
                cell.ToolTip = $"{(back == 0 ? "the last second" : $"−{back}s")}\n{tip}";
            }
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(halfHeight) });
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(halfHeight) });
            if (heal[i] > 0)
            {
                var up = new Border
                {
                    Background = BarHeal, CornerRadius = new CornerRadius(2, 2, 0, 0),
                    Height = Math.Max(2, heal[i] / max * halfHeight),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(1.5, 0, 1.5, 0), Opacity = 0.85,
                };
                Grid.SetRow(up, 0);
                cell.Children.Add(up);
            }
            if (dmg[i] > 0)
            {
                var down = new Border
                {
                    Background = inBurst ? BarKill : BarDmg,
                    CornerRadius = new CornerRadius(0, 0, 2, 2),
                    Height = Math.Max(2, dmg[i] / max * halfHeight),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(1.5, 0, 1.5, 0), Opacity = 0.9,
                };
                Grid.SetRow(down, 1);
                cell.Children.Add(down);
            }
            GraphGrid.Children.Add(cell);

            string label = back == 0 ? "💀" : back % 5 == 0 ? $"−{back}" : "";
            AxisGrid.Children.Add(new TextBlock
            {
                Text = label, FontFamily = new FontFamily("Consolas"), FontSize = 9,
                Foreground = AxisFg, HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
