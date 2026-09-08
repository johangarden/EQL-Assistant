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
        Brush AmountBrush, Brush TextBrush, Brush RowBg, string Kind, Brush KindBrush);

    /// <summary>One merged ledger row: every event of (source, ability, heal) in the window.</summary>
    public sealed record RecapGroup(string Source, string Ability, bool Heal,
        int Count, double Total, DateTime First, bool HasBiggestHit,
        CombatParser.SctFlavor Flavor = CombatParser.SctFlavor.Melee);

    private static readonly Brush BarSpell = Freeze(Color.FromRgb(0x95, 0x75, 0xCD));
    private static readonly Brush BarSpellKill = Freeze(Color.FromRgb(0xB3, 0x9D, 0xDB));
    private static readonly Brush MeleeTag = Freeze(Color.FromRgb(0xC9, 0x6B, 0x6B));
    private static readonly Brush SpellTag = Freeze(Color.FromRgb(0x95, 0x75, 0xCD));
    private static readonly Brush HealTag = Freeze(Color.FromRgb(0x5E, 0x8F, 0x62));

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

        // ---- melee vs spell + the stance verdict ------------------------------
        double melee = events.Where(e => !e.Heal && !e.Miss && e.Flavor != CombatParser.SctFlavor.Spell).Sum(e => e.Amount);
        double spell = events.Where(e => !e.Heal && !e.Miss && e.Flavor == CombatParser.SctFlavor.Spell).Sum(e => e.Amount);
        SplitSection.Visibility = melee + spell > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (melee + spell > 0)
        {
            MeleeCol.Width = new GridLength(Math.Max(0, melee), GridUnitType.Star);
            SpellCol.Width = new GridLength(Math.Max(0, spell), GridUnitType.Star);
            MeleeBar.Visibility = melee > 0 ? Visibility.Visible : Visibility.Collapsed;
            SpellBar.Visibility = spell > 0 ? Visibility.Visible : Visibility.Collapsed;
            SplitText.Text = SplitLine(melee, spell);
            StanceText.Text = StanceVerdict(death.Stance, melee, spell);
        }

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
                g.HasBiggestHit ? RowBig : i % 2 == 0 ? RowEven : RowOdd,
                g.Heal ? "HEAL" : g.Flavor == CombatParser.SctFlavor.Spell ? "SPELL" : "MELEE",
                g.Heal ? HealTag : g.Flavor == CombatParser.SctFlavor.Spell ? SpellTag : MeleeTag));
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
                biggest is not null && g.Contains(biggest),
                g.First().Flavor))
            .OrderBy(g => g.First)
            .ToList();
    }

    /// <summary>"Melee −1,235 (31%) · Spells −2,685 (69%)".</summary>
    public static string SplitLine(double melee, double spell)
    {
        double total = melee + spell;
        if (total <= 0) return "";
        return $"Melee −{melee:N0} ({Pct(melee / total)}) · Spells −{spell:N0} ({Pct(spell / total)})";
    }

    private static string Pct(double f) => $"{Math.Round(f * 100):0}%";

    /// <summary>The stance lesson (owner, 8 Sep): defensive halves melee,
    /// mage hunter halves spells. Named against the stance the log last saw
    /// you assume; when one kind carried ≥60% of the damage, say which
    /// stance would have halved it — or that the stance was right and the
    /// death was a numbers problem.</summary>
    public static string StanceVerdict(string stance, double melee, double spell)
    {
        double total = melee + spell;
        if (total <= 0) return "";
        double mp = melee / total, sp = spell / total;
        string kind = sp >= 0.6 ? "spell" : mp >= 0.6 ? "melee" : "mixed";
        string st = (stance ?? "").Trim().ToLowerInvariant();
        bool defensive = st == "defensive", hunter = st == "mage hunter";

        if (kind == "mixed")
            return (st.Length > 0 ? $"You were in a {st} stance. " : "")
                 + $"Melee and spells split the damage {Pct(mp)} / {Pct(sp)} — no stance halves both; "
                 + "defensive covers the melee half, mage hunter the spell half.";

        if (kind == "spell")
        {
            if (hunter) return $"Mage hunter was the right stance: {Pct(sp)} of the damage was spells, already halved. This death was a numbers problem, not a stance problem.";
            if (defensive) return $"You were in a defensive stance (halves melee) but {Pct(sp)} of the damage was spells — a mage hunter stance would have halved that instead. Engage this one in mage hunter.";
            if (st.Length > 0) return $"You were in a {st} stance. {Pct(sp)} of the damage was spells — a mage hunter stance would have halved it.";
            return $"No stance change seen in the log this session. {Pct(sp)} of the damage was spells — a mage hunter stance halves spell damage.";
        }

        if (defensive) return $"Defensive was the right stance: {Pct(mp)} of the damage was melee, already halved. This death was a numbers problem, not a stance problem.";
        if (hunter) return $"You were in a mage hunter stance (halves spells) but {Pct(mp)} of the damage was melee — a defensive stance would have halved that instead. Engage this one in defensive.";
        if (st.Length > 0) return $"You were in a {st} stance. {Pct(mp)} of the damage was melee — a defensive stance would have halved it.";
        return $"No stance change seen in the log this session. {Pct(mp)} of the damage was melee — a defensive stance halves melee damage.";
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
        var spellDmg = new double[cols];
        var heal = new double[cols];
        var perSecond = new List<CombatParser.RecapEntry>[cols];
        foreach (var e in events)
        {
            if (e.Miss) continue;
            int back = (int)Math.Clamp((death.When - e.When).TotalSeconds, 0, cols - 1);
            int col = cols - 1 - back;
            if (e.Heal) heal[col] += e.Amount;
            else
            {
                dmg[col] += e.Amount;
                if (e.Flavor == CombatParser.SctFlavor.Spell) spellDmg[col] += e.Amount;
            }
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
                // Stacked: melee hangs from the axis, spells beneath it —
                // the split reads second by second, not just in the total.
                double meleePart = dmg[i] - spellDmg[i];
                double total = Math.Max(2, dmg[i] / max * halfHeight);
                double meleeH = dmg[i] > 0 ? total * meleePart / dmg[i] : 0;
                double spellH = total - meleeH;
                var down = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1.5, 0, 1.5, 0), Opacity = 0.9 };
                if (meleeH > 0.5)
                    down.Children.Add(new Border
                    {
                        Background = inBurst ? BarKill : BarDmg, Height = meleeH,
                        CornerRadius = spellH > 0.5 ? new CornerRadius(0) : new CornerRadius(0, 0, 2, 2),
                    });
                if (spellH > 0.5)
                    down.Children.Add(new Border
                    {
                        Background = inBurst ? BarSpellKill : BarSpell, Height = spellH,
                        CornerRadius = new CornerRadius(0, 0, 2, 2),
                    });
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
