using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The mote-farming analyst: mines the loot ledger (motes are currency
/// pickups with mob + zone + tier already attached) into a zone×tier rate
/// board — where an hour of farming actually pays, which mobs pay it, and
/// what the instance tier does to the grade. Pure computation over
/// LootTracker entries; nothing here persists.
/// </summary>
public static class MoteFarm
{
    /// <summary>The Rank 1→10 ladder, straight from the eqlwiki Mote Guide —
    /// note MAJOR sits BELOW Greater in EQL (rank 5 vs 6), unlike classic
    /// EQ's research ladder. "Potential" is the plain "Mote of Potential".</summary>
    public static readonly string[] Grades =
    {
        "Infinitesimal", "Minor", "Lesser", "Potential", "Major",
        "Greater", "Superior", "Grand", "Ascendant", "Infinite",
    };

    /// <summary>Spell-upgrade points per rank (wiki Mote Guide) — doubles
    /// every rank, the natural weight for a future "value per hour".
    /// (Item points run 1,1,2,4,5..10, capped by the item's tier.)</summary>
    public static readonly int[] SpellPoints = { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512 };

    private static readonly Regex MoteRx = new(
        @"^Mote of (?:(?<g>Infinitesimal|Minor|Lesser|Major|Greater|Superior|Grand|Ascendant|Infinite) )?Potential$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Grade index of a mote item name, or -1 when it isn't a mote.</summary>
    public static int GradeOf(string item)
    {
        var m = MoteRx.Match(item.Trim());
        if (!m.Success) return -1;
        string g = m.Groups["g"].Success ? m.Groups["g"].Value : "Potential";
        return Array.IndexOf(Grades, g);
    }

    /// <summary>Drops in one zone with ≤15 min between them; the clock runs
    /// first drop → last drop, so AFK time never inflates a rate.</summary>
    public const double StintGapMin = 15;

    /// <summary>A rate only prints at ≥30 min farmed AND ≥8 motes — below
    /// either, the row says "small sample" instead of lying (house rule;
    /// the AND is deliberate: 8 lucky motes in 12 minutes is an anecdote,
    /// not a rate).</summary>
    public const double RateMinMinutes = 30;
    public const int RateMinMotes = 8;

    /// <summary>The "best farm" crown demands more than a rate: 45+ minutes
    /// on the clock, so one hot half-hour can't outrank a proven grind.</summary>
    public const double CrownMinMinutes = 45;

    public sealed record Stint(string Zone, DateTime Start, DateTime End, int[] ByGrade)
    {
        public int Total => ByGrade.Sum();
        public double Minutes => (End - Start).TotalMinutes;
    }

    public sealed record ZoneRow(string Zone, int Tier, int[] ByGrade, double Minutes,
        DateTime Last, List<(string Mob, int Count)> Droppers, List<Stint> Stints)
    {
        public int Total => ByGrade.Sum();

        /// <summary>Motes/hour for one grade (or all with -1) — null below the
        /// sample floors, or when the filtered grade never dropped here.</summary>
        public double? RateFor(int gradeIx = -1)
        {
            int count = gradeIx < 0 ? Total : ByGrade[gradeIx];
            if (count == 0) return null;
            if (Minutes < RateMinMinutes || Total < RateMinMotes) return null;
            return count * 60.0 / Minutes;
        }

        /// <summary>Enough clock behind it to be crowned "your best farm".</summary>
        public bool Proven => Minutes >= CrownMinMinutes;

        /// <summary>The grade this zone mostly pays (ladder index).</summary>
        public int DominantGrade
        {
            get
            {
                int best = 0;
                for (int i = 1; i < ByGrade.Length; i++)
                    if (ByGrade[i] >= ByGrade[best]) best = i; // ties go UP the ladder
                return best;
            }
        }
    }

    /// <summary>The zone name minus its tier tail — "Nagafen's Lair - Solo 4
    /// (Refined)" and "Nagafen's Lair" are the same farm at different dials.</summary>
    private static readonly Regex TierTailRx = new(
        @"( - Solo)?( \d+ \([A-Za-z]+\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BaseZone(string zone) => TierTailRx.Replace(zone.Trim(), "", 1);

    /// <summary>The board: one row per zone (tier included in the name),
    /// best-paying first, small samples and never-rated rows sinking.</summary>
    public static List<ZoneRow> Build(IEnumerable<LootTracker.LootEntry> entries)
    {
        var motes = entries
            .Select(e => (Entry: e, Grade: GradeOf(e.Item)))
            .Where(x => x.Grade >= 0)
            .OrderBy(x => x.Entry.When)
            .ToList();

        // Stints first (zone changes or long gaps cut them), then per-zone rollup.
        var stints = new List<Stint>();
        foreach (var x in motes)
        {
            var last = stints.Count > 0 ? stints[^1] : null;
            if (last is null || !last.Zone.Equals(x.Entry.Zone, StringComparison.OrdinalIgnoreCase)
                || (x.Entry.When - last.End).TotalMinutes > StintGapMin)
            {
                stints.Add(last = new Stint(x.Entry.Zone, x.Entry.When, x.Entry.When,
                    new int[Grades.Length]));
            }
            last.ByGrade[x.Grade] += Math.Max(1, x.Entry.Count);
            stints[^1] = last with { End = x.Entry.When };
        }

        var rows = new List<ZoneRow>();
        foreach (var g in stints.GroupBy(s => s.Zone, StringComparer.OrdinalIgnoreCase))
        {
            var byGrade = new int[Grades.Length];
            foreach (var s in g)
                for (int i = 0; i < Grades.Length; i++) byGrade[i] += s.ByGrade[i];
            var droppers = motes
                .Where(x => x.Entry.Zone.Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Entry.Mob, StringComparer.OrdinalIgnoreCase)
                .Select(m => (Mob: m.Key, Count: m.Sum(x => Math.Max(1, x.Entry.Count))))
                .OrderByDescending(m => m.Count)
                .ToList();
            rows.Add(new ZoneRow(g.Key, CombatParser.ZoneTier(g.Key), byGrade,
                g.Sum(s => s.Minutes), g.Max(s => s.End), droppers,
                g.OrderByDescending(s => s.Start).ToList()));
        }
        return rows;
    }

    /// <summary>Sort for one grade lens (-1 = all): rated rows by rate, then
    /// the small samples by their mote count.</summary>
    public static List<ZoneRow> Ranked(List<ZoneRow> rows, int gradeIx = -1) => rows
        .Where(r => gradeIx < 0 || r.ByGrade[gradeIx] > 0)
        .OrderByDescending(r => r.RateFor(gradeIx) ?? -1)
        .ThenByDescending(r => gradeIx < 0 ? r.Total : r.ByGrade[gradeIx])
        .ToList();

    /// <summary>The analyst lines above the board — they speak only from
    /// rated data, and name the small samples for what they are.</summary>
    public static List<(string Text, bool Caveat)> Verdicts(List<ZoneRow> rows, int gradeIx = -1)
    {
        var v = new List<(string, bool)>();
        var ranked = Ranked(rows, gradeIx);
        // The crown goes to the best PROVEN farm — a hot half-hour can lead
        // the table, but only time on the clock earns the sentence.
        var best = ranked.FirstOrDefault(r => r.RateFor(gradeIx) is not null && r.Proven);
        if (best is not null)
        {
            string lens = gradeIx >= 0 ? $" for {Grades[gradeIx]}" : "";
            v.Add(($"Your best farm{lens}: {best.Zone} — " +
                   $"{best.RateFor(gradeIx):0}/h over {FormatMinutes(best.Minutes)}.", false));
        }
        var hot = ranked.FirstOrDefault(r => r.RateFor(gradeIx) is not null && !r.Proven);
        if (hot is not null && (best is null || hot.RateFor(gradeIx) > best.RateFor(gradeIx)))
            v.Add(($"{hot.Zone} shows {hot.RateFor(gradeIx):0}/h but only " +
                   $"{FormatMinutes(hot.Minutes)} on the clock — a lucky window isn't a farm " +
                   $"yet; it's crowned at {FormatMinutes(CrownMinMinutes)}+.", true));

        // The tier lever: the same base zone at different tiers paying
        // different dominant grades is the finding worth a sentence.
        foreach (var g in rows.GroupBy(r => BaseZone(r.Zone), StringComparer.OrdinalIgnoreCase))
        {
            var tiers = g.Where(r => r.Total >= 3).OrderBy(r => r.Tier).ToList();
            if (tiers.Count < 2) continue;
            var lo = tiers[0];
            var hi = tiers[^1];
            if (hi.Tier > lo.Tier && hi.DominantGrade > lo.DominantGrade)
            {
                v.Add(($"Tier is the lever in {g.Key}: T{lo.Tier} mostly pays " +
                       $"{Grades[lo.DominantGrade]}, T{hi.Tier} pays {Grades[hi.DominantGrade]}.", false));
                break; // one example carries the point
            }
        }

        var thin = rows.Where(r => r.Total > 0 && r.RateFor() is null).ToList();
        if (thin.Count > 0 && best is not null)
            v.Add(($"{thin.Count} zone(s) below the sample floor " +
                   $"(≥{RateMinMinutes:0}m farmed and ≥{RateMinMotes} motes) — hints, not rates.", true));
        return v;
    }

    /// <summary>The strictness dial: rows with at least the wanted minutes
    /// farmed stand as farms; the rest are demoted to hints (shown collapsed
    /// under the board, never ranked beside the real ones).</summary>
    public static (List<ZoneRow> Shown, List<ZoneRow> Thin) SplitByFarmed(
        List<ZoneRow> rows, double minMinutes)
    {
        var shown = rows.Where(r => r.Minutes >= minMinutes).ToList();
        var thin = rows.Where(r => r.Minutes < minMinutes && r.Total > 0).ToList();
        return (shown, thin);
    }

    public static string FormatMinutes(double min) =>
        min >= 60 ? $"{(int)(min / 60)}h{(int)min % 60:00}m" : $"{Math.Max(1, (int)min)}m";
}
