namespace EQLOverlay.Services;

/// <summary>
/// Attack-round stats for one fight (owner pick, 8 Sep; Companion's plan,
/// the honest subset). Everything here is counted from the log's own
/// annotations — "(Riposte)", "(Flurry)", "(Rampage)", "(Slay Undead)",
/// "(Finishing Blow)" on hit lines, and the avoid word on miss lines
/// ("but YOU riposte!", "but YOU dodge!") — plus one inference the log
/// leaves silent: multi-swing rounds, read as several swings of the same
/// skill in the same second. That one carries its caveat (dual wield adds
/// a swing too); rates always show their denominator.
/// </summary>
public static class RoundStats
{
    public sealed record SkillRounds(string Ability, int Rounds, int Swings, int Multi2, int Multi3)
    {
        /// <summary>Rounds with 2+ swings, as a share of rounds.</summary>
        public double Multi2Rate => Rounds == 0 ? 0 : (double)Multi2 / Rounds;
        public double Multi3Rate => Rounds == 0 ? 0 : (double)Multi3 / Rounds;
    }

    public sealed record Result(
        int SwingsOnYou, int YouRiposted, int YouParried, int YouDodged, int YouBlocked, int MissedYou,
        int YourRipostes, int YourRiposteHits, double YourRiposteDamage,
        int Flurries, int SlayUndead, int FinishingBlows,
        int RampagesTaken, int MobRipostesTaken, double MobRiposteDamage,
        IReadOnlyList<SkillRounds> Skills)
    {
        public bool HasAnything => SwingsOnYou > 0 || Skills.Count > 0 || YourRipostes > 0 || Flurries > 0;
    }

    private static readonly HashSet<string> MeleeAbilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "slash", "bash", "crush", "pierce", "kick", "hit", "bite", "claw", "backstab", "cleave",
        "punch", "gore", "maul", "sting", "rend", "slam", "reave", "strike",
    };

    /// <summary>Only skills with this many rounds get a multi-swing line.</summary>
    public const int RoundFloor = 5;

    public static Result Compute(CombatParser.FightRecord rec)
    {
        var ev = rec.Events;
        // Defence: every melee attempt on you.
        var onYou = ev.Where(e => e.Stream == CombatParser.FightStream.SelfIn && !e.Dot && MeleeAbilities.Contains(e.Ability)).ToList();
        int riposted = onYou.Count(e => e.Miss && e.Tag == "riposte");
        int parried = onYou.Count(e => e.Miss && e.Tag == "parry");
        int dodged = onYou.Count(e => e.Miss && e.Tag == "dodge");
        int blocked = onYou.Count(e => e.Miss && e.Tag == "block");
        int missed = onYou.Count(e => e.Miss && e.Tag is "" or "miss");
        int rampages = onYou.Count(e => Has(e.Tag, "rampage"));
        var mobRip = onYou.Where(e => !e.Miss && Has(e.Tag, "riposte")).ToList();

        // Offence: your swings (misses included), by annotation.
        var yours = ev.Where(e => e.Stream == CombatParser.FightStream.SelfOut && !e.Dot && MeleeAbilities.Contains(e.Ability)).ToList();
        var yourRip = yours.Where(e => Has(e.Tag, "riposte")).ToList();
        int flurries = yours.Count(e => Has(e.Tag, "flurry"));
        int slay = yours.Count(e => Has(e.Tag, "slay undead"));
        int finish = yours.Count(e => Has(e.Tag, "finishing blow"));

        // Rounds: plain swings (ripostes and flurries are extra swings, not
        // rounds) of one skill in one second. The log's clock is 1 s, so a
        // double attack lands in the same second as its first swing.
        var skills = new List<SkillRounds>();
        foreach (var g in yours.Where(e => !Has(e.Tag, "riposte") && !Has(e.Tag, "flurry"))
                     .GroupBy(e => e.Ability, StringComparer.OrdinalIgnoreCase))
        {
            var rounds = g.GroupBy(e => Math.Floor(e.T)).Select(r => r.Count()).ToList();
            if (rounds.Count < RoundFloor) continue;
            skills.Add(new SkillRounds(g.Key.ToLowerInvariant(), rounds.Count, g.Count(),
                rounds.Count(n => n >= 2), rounds.Count(n => n >= 3)));
        }
        skills.Sort((a, b) => b.Swings.CompareTo(a.Swings));

        return new Result(onYou.Count, riposted, parried, dodged, blocked, missed,
            yourRip.Count, yourRip.Count(e => !e.Miss), yourRip.Sum(e => e.Miss ? 0 : e.Amount),
            flurries, slay, finish,
            rampages, mobRip.Count, mobRip.Sum(e => e.Amount),
            skills);
    }

    private static bool Has(string tag, string word) =>
        tag.Length > 0 && tag.Contains(word, StringComparison.OrdinalIgnoreCase);
}
