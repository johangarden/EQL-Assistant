using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Damage and healing per mana (owner, 25 Sep): the wiki baseline (rank 0),
/// the same spell AT YOUR RANK by eqlwiki's Spell Upgrades rules, and what
/// your log says you actually got (<see cref="SpellYield"/>). Rank rules per
/// rank r = 0..10, taken as ADDITIVE steps (the wiki doesn't say whether they
/// compound; at rank X the gap is 1.6× vs 1.8× for a nuke):
/// direct damage +6 % damage, −2 % mana, −2 % cast time;
/// damage / healing over time +3 % per tick, +5 % duration, −2 % mana, −4 % cast;
/// heals +3 % healing, −2 % mana, −4 % cast.
/// </summary>
public static class SpellEfficiency
{
    public const int MinObservedCasts = 5;

    /// <summary>EQ Legends' level cap: Classic ends at 50 (Kunark is announced at
    /// 55 — raise this then). The library carries old EverQuest's levels up to
    /// 60; a class level above the cap is a spell you can't have yet and stays
    /// out (owner, 25 Sep — Torpor at SHM 60 topped the heal list).</summary>
    public const int LevelCap = 50;

    /// <summary>The level filter's bands (owner, 25 Sep): All · 1–20 · 21–30 · 31–40 · 41–50.</summary>
    public static readonly (int Lo, int Hi, string Label)[] Bands =
        { (1, LevelCap, "All"), (1, 20, "1–20"), (21, 30, "21–30"), (31, 40, "31–40"), (41, LevelCap, $"41–{LevelCap}") };

    /// <summary>The band your level sits in (0 = All when the level is unknown).</summary>
    public static int BandFor(int level)
    {
        if (level <= 0) return 0;
        for (int i = 1; i < Bands.Length; i++)
            if (level >= Bands[i].Lo && level <= Bands[i].Hi) return i;
        return Bands.Length - 1;
    }

    public static bool IsDamage(string effect) => effect is "Direct damage" or "AE damage" or "Damage over time" or "Lifetap";
    public static bool IsHealing(string effect) => effect is "Heal" or "Heal over time";

    /// <summary>A spell the tab can rank: a damage or heal effect with a wiki mana cost and amount.</summary>
    public static bool Rankable(SpellLibrary.Spell s) => s.Mana > 0 && s.Hit + s.Tick * s.Ticks > 0 && (IsDamage(s.Effect) || IsHealing(s.Effect));

    /// <summary>Over-time spells take the DoT/HoT rule, the rest by kind.</summary>
    private static bool OverTime(SpellLibrary.Spell s) => s.Tick > 0 && s.Ticks > 0;

    /// <summary>The wiki's total for one target: the instant part plus every tick of the full run.</summary>
    public static double BaseTotal(SpellLibrary.Spell s) => s.Hit + s.Tick * s.Ticks;

    /// <summary>Total, mana and cast time at rank <paramref name="rank"/> (0 = the wiki's own numbers).</summary>
    public static (double Total, double Mana, double Cast) AtRank(SpellLibrary.Spell s, int rank)
    {
        int r = Math.Clamp(rank, 0, 10);
        double mana = s.Mana * (1 - 0.02 * r);
        if (OverTime(s))
        {
            double tick = s.Tick * (1 + 0.03 * r), ticks = s.Ticks * (1 + 0.05 * r);
            return (s.Hit * (1 + 0.03 * r) + tick * ticks, mana, s.CastSec * (1 - 0.04 * r));
        }
        if (IsHealing(s.Effect)) return (s.Hit * (1 + 0.03 * r), mana, s.CastSec * (1 - 0.04 * r));
        return (s.Hit * (1 + 0.06 * r), mana, s.CastSec * (1 - 0.02 * r));
    }

    /// <summary>Seconds one spell ties up when chained: its cast plus its reuse
    /// (owner, 25 Sep: "a high damage spell with a reuse of 10+ secs might not
    /// perform better") — and never less than a DoT/HoT's own run, which you
    /// don't re-cast early. Rank shortens the cast and stretches the duration;
    /// the reuse stays (the wiki's "−X % reuse" has no number).</summary>
    public static double CycleSec(SpellLibrary.Spell s, int rank = 0)
    {
        int r = Math.Clamp(rank, 0, 10);
        var at = AtRank(s, r);
        double cycle = Math.Max(0.5, at.Cast) + Math.Max(0, s.RecastSec);
        if (OverTime(s) && s.DurSec > 0) cycle = Math.Max(cycle, s.DurSec * (1 + 0.05 * r));
        return cycle;
    }

    public sealed record Row(
        SpellLibrary.Spell Spell, int Level, string ClassText, bool Multi,
        double Total, double PerMana, double PerCastSec, double Sustained,
        int? Rank, double? RankTotal, double? RankMana, double? RankPerMana, double? RankSustained,
        double? Observed, int Casts, double? ObservedPerMana, double? ObservedPct, double? ObservedTargets)
    {
        /// <summary>The figure the list ranks by: at your rank when you cast it, else the wiki's.</summary>
        public double BestPerMana => RankPerMana ?? PerMana;
        /// <summary>Damage / healing a second when chained, at your rank if known.</summary>
        public double BestSustained => RankSustained ?? Sustained;
        /// <summary>A reuse long enough to matter (10 s or more).</summary>
        public bool LongReuse => Spell.RecastSec >= 10;
    }

    private static readonly Regex ClassLevelRx = new(@"(?<cls>[A-Z]{2,3}) (?<lvl>\d+)", RegexOptions.Compiled);

    /// <summary>Every rankable spell for <paramref name="classes"/> (empty = every
    /// class) whose level sits in <paramref name="minLevel"/>..<paramref name="maxLevel"/>
    /// (never above <see cref="LevelCap"/>), damage or healing;
    /// AE / group spells multiplied by <paramref name="targets"/> (the observed
    /// figure never — it already hit whatever it hit).</summary>
    public static List<Row> Rows(SpellLibrary library, SpellYield? yield, IReadOnlyCollection<string> classes,
        int minLevel, int maxLevel, bool healing, int targets, string search = "", string resist = "")
    {
        var want = new HashSet<string>(classes, StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in library.Spells)
        {
            if (!Rankable(s) || IsHealing(s.Effect) != healing) continue;
            if (search.Length > 0 && !s.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !s.Effect.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (resist.Length > 0 && !s.Resist.Equals(resist, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(s.Name)) continue; // the library lists a few spells twice
            int level = int.MaxValue;
            var parts = new List<string>();
            foreach (Match m in ClassLevelRx.Matches(s.Classes))
            {
                string cls = m.Groups["cls"].Value;
                if (want.Count > 0 && !want.Contains(cls)) continue;
                int lv = int.Parse(m.Groups["lvl"].Value);
                if (lv > LevelCap) continue; // not in the game yet
                level = Math.Min(level, lv);
                parts.Add($"{cls} {lv}");
            }
            if (level == int.MaxValue || level < minLevel || level > maxLevel) continue;

            bool multi = s.Targets is "ae" or "group";
            int n = multi ? Math.Max(1, targets) : 1;
            double total = BaseTotal(s) * n;
            double perSec = s.CastSec > 0 ? total / s.CastSec : total;
            double sustained = total / CycleSec(s);

            var rec = yield?.Of(s.Name);
            int? rank = rec is { Casts: > 0 } ? rec.Rank : null;
            double? rTotal = null, rMana = null, rPer = null, rSus = null;
            if (rank is { } rk)
            {
                var at = AtRank(s, rk);
                rTotal = at.Total * n; rMana = at.Mana; rPer = at.Mana > 0 ? at.Total * n / at.Mana : null;
                rSus = at.Total * n / CycleSec(s, rk);
            }
            double? obs = null, obsPer = null, obsPct = null, obsTargets = null;
            int casts = rec?.Casts ?? 0;
            if (rec is not null && casts >= MinObservedCasts)
            {
                double amount = healing ? rec.Healing : rec.Damage;
                if (amount > 0)
                {
                    obs = amount / casts;
                    double mana = rMana ?? s.Mana;
                    obsPer = mana > 0 ? obs / mana : null;
                    double single = rank is { } r2 ? AtRank(s, r2).Total : BaseTotal(s);
                    if (single > 0)
                    {
                        if (multi) obsTargets = obs / single; // an AE / group spell: how many it really reached
                        else obsPct = obs / single;
                    }
                }
            }
            rows.Add(new Row(s, level, string.Join(" · ", parts), multi, total, total / s.Mana, perSec, sustained,
                rank, rTotal, rMana, rPer, rSus, obs, casts, obsPer, obsPct, obsTargets));
        }
        return rows;
    }

    /// <summary>"X" for 10, "VIII" for 8, "—" for rank 0 (the base spell).</summary>
    public static string Roman(int rank) => rank switch
    {
        <= 0 => "0", 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => "X",
    };
}
