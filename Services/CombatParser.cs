using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// ACT-style combat parser: accumulates damage and healing per source for the
/// current fight, plus incoming damage taken by the player (and their pet).
/// A fight starts on the first damage/heal line and freezes after ~10s with no
/// combat activity; the next combat line then starts a fresh fight.
/// UI-thread only (fed from the same dispatcher callback as the trigger engine).
/// </summary>
public sealed class CombatParser
{
    /// <summary>Seconds of silence after which the current fight is considered over.</summary>
    public const double IdleSeconds = 10;

    /// <summary>The logging character's name; "You"/"YOU" in lines maps to this.</summary>
    public string SelfName { get; set; } = "You";

    /// <summary>Optional pet name — enables the pet line in the incoming footer.</summary>
    public string PetName { get; set; } = "";

    /// <summary>How many finished fights the session keeps for the history window
    /// (★-kept fights are stored separately and never expire).</summary>
    public const int MaxHistory = 50;

    /// <summary>Which scrolling-combat-text lane an event belongs to.</summary>
    public enum SctKind { IncomingSelf, IncomingPet, OutgoingSelf, OutgoingPet, HealOut, HealIn, Progress }

    /// <summary>What kind of hit it was — lanes color melee, spells and procs differently.</summary>
    public enum SctFlavor { Melee, Spell, Proc, Heal }

    /// <summary>When <paramref name="Text"/> is set, the lane shows it verbatim
    /// instead of formatting <paramref name="Amount"/> ("+3,5%" xp floats).</summary>
    public readonly record struct SctHit(SctKind Kind, string Ability, double Amount,
        SctFlavor Flavor, bool Crit, string? Text = null);

    /// <summary>Raised per parsed combat event, for scrolling combat text.</summary>
    public event Action<SctHit>? SctEvent;

    public readonly record struct Row(string Name, double Total, double Dps, double Percent, bool Enemy,
        int Hits = 0, int Misses = 0, int Resists = 0, double Min = 0, double Max = 0, int Crits = 0);

    /// <summary>Per-ability accumulator: landed damage plus attempt bookkeeping.</summary>
    private sealed class AbilityStat
    {
        public double Total;
        public int Hits;
        public int Misses;
        public int Resists;
        public int Crits;
        public double Min = double.MaxValue;
        public double Max;

        public void Land(double amount, bool crit = false)
        {
            Total += amount;
            Hits++;
            if (crit) Crits++;
            if (amount < Min) Min = amount;
            if (amount > Max) Max = amount;
        }

        public double MinOrZero => Hits > 0 ? Min : 0;
    }

    /// <summary>Which side of the fight a timeline event belongs to.</summary>
    public enum FightStream { SelfOut = 0, PetOut = 1, SelfIn = 2, PetIn = 3, HealOut = 4, HealIn = 5 }

    /// <summary>
    /// One timeline event: offset seconds from the fight start, the ability, the
    /// amount (0 for misses/resists) and flags. JSON names are single letters —
    /// kept fights persist thousands of these.
    /// </summary>
    public sealed record FightEvent(
        [property: JsonPropertyName("t")] double T,
        [property: JsonPropertyName("a")] string Ability,
        [property: JsonPropertyName("v")] double Amount,
        [property: JsonPropertyName("s")] FightStream Stream,
        [property: JsonPropertyName("c")] bool Crit = false,
        [property: JsonPropertyName("m")] bool Miss = false,
        [property: JsonPropertyName("r")] bool Resist = false);

    /// <summary>A finished fight, frozen for the history/compare window.</summary>
    public sealed class FightRecord
    {
        public required string Label { get; init; }
        public string Zone { get; init; } = "";
        public DateTime EndedAt { get; init; }
        public double DurationSeconds { get; init; }
        public List<Row> Damage { get; init; } = new();
        public List<Row> Healing { get; init; } = new();
        public double IncomingSelfTotal { get; init; }
        public double IncomingPetTotal { get; init; }
        public double TotalDps { get; init; }
        public double TotalHps { get; init; }

        // Drill-down (added later — older kept fights just have these empty).
        public List<Row> SelfAbilities { get; init; } = new();
        public List<Row> PetAbilities { get; init; } = new();
        public List<Row> IncomingSelfAbilities { get; init; } = new();
        public List<Row> IncomingPetAbilities { get; init; } = new();

        // Timeline (added later — older kept fights just have this empty).
        public List<FightEvent> Events { get; init; } = new();
        public bool EventsTruncated { get; init; }
    }

    private readonly List<FightRecord> _history = new();

    /// <summary>Finished fights, newest first.</summary>
    public IReadOnlyList<FightRecord> History => _history;

    private readonly Dictionary<string, double> _damage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _healing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _taken = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, AbilityStat>> _abilities = new(StringComparer.OrdinalIgnoreCase);
    // Healing split per spell (damage and heals must never share totals).
    private readonly Dictionary<string, Dictionary<string, AbilityStat>> _healAbilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AbilityStat> _incomingSelfAbility = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AbilityStat> _incomingPetAbility = new(StringComparer.OrdinalIgnoreCase);
    private double _incomingSelf;
    private double _incomingPet;
    private DateTime _start;
    private DateTime _last;
    private bool _active;
    private string _fightZone = "";

    // ---- session skill tracker -------------------------------------------------
    // Session-wide attempts of YOUR abilities for the skill tracker panel.
    // Deliberately NOT part of the fight model: it accumulates across fights and
    // only ResetSessionSkills() (the panel's ⟲ button) clears it.

    public sealed class SkillStat
    {
        public int Hits;
        public int Misses;
        public int Resists;
        public int Crits;
        public double Total;
        public double Max;

        /// <summary>Current skill level from "You have become better at X! (N)" (0 = never seen).</summary>
        public int Level;

        /// <summary>Skill-ups seen this session.</summary>
        public int Ups;

        public int Attempts => Hits + Misses + Resists;
        public double HitRate => Attempts > 0 ? (double)Hits / Attempts : 0;
    }

    private readonly Dictionary<string, SkillStat> _sessionSkills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Session totals for one of your abilities (null if never attempted).</summary>
    public SkillStat? GetSessionSkill(string ability) =>
        _sessionSkills.TryGetValue(ability.Trim(), out var s) ? s : null;

    /// <summary>Every ability you've used this session, exactly as the log spells it
    /// (the Manager's "seen in your log" skill picker).</summary>
    public IReadOnlyDictionary<string, SkillStat> SessionSkills => _sessionSkills;

    /// <summary>The meter's ⟲ wipes ALL session stats: skills, procs, active time.</summary>
    public void ResetSessionSkills()
    {
        _sessionSkills.Clear();
        _sessionProcs.Clear();
        _archivedActiveSec = 0;
    }

    private SkillStat SessionSkill(string ability)
    {
        if (!_sessionSkills.TryGetValue(ability, out var s))
            _sessionSkills[ability] = s = new SkillStat();
        return s;
    }

    // ---- session proc watcher ---------------------------------------------------
    // A proc is a spell-effect damage/heal line of YOURS with no own cast line
    // behind it (design from jmoyers/everquest-companion's proc-analytics plan):
    // "You begin casting X." within the window marks X as hand-cast; a spell
    // that lands without one procced off a weapon, poison or buff. DoT ticks
    // are never candidates (their lines are cast-detached by construction) and
    // thorns rides INCOMING swings, so neither belongs in a per-swing proc
    // rate. Session-scoped like the skill tracker; the ⟲ clears both.

    public sealed class ProcStat
    {
        public int Count;
        public double Damage;
        public double Heal;
        public double Max;
        public int Crits;
    }

    /// <summary>Begin-cast → landing window. 12s, measured by the Companion on a
    /// 1.1M-line log: every pure proc scores cast=0, every hand-cast nuke proc=0.</summary>
    public const double ProcCastWindowSec = 12;

    private readonly Dictionary<string, DateTime> _recentCasts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProcStat> _sessionProcs = new(StringComparer.OrdinalIgnoreCase);
    private double _archivedActiveSec;

    /// <summary>Session proc lanes, exactly as the log spells them.</summary>
    public IReadOnlyDictionary<string, ProcStat> SessionProcs => _sessionProcs;

    /// <summary>Active combat seconds this session (archived fights + the LIVE
    /// one) — the procs-per-minute denominator. Only an active fight adds its
    /// running duration; an idled-out fight already counted via Archive().</summary>
    public double SessionActiveSeconds => _archivedActiveSec + (_active ? DurationSeconds : 0);

    /// <summary>Your melee swing attempts this session (hits + misses) — the
    /// mechanically-correct denominator for a chance-on-hit proc.</summary>
    public int SessionSwings => _sessionSkills
        .Where(kv => IsMeleeAbility(kv.Key))
        .Sum(kv => kv.Value.Hits + kv.Value.Misses);

    private void NoteProc(string ability, double amount, bool heal, DateTime time, bool crit)
    {
        if (_recentCasts.TryGetValue(SpellDurations.BaseKey(ability), out var castAt))
        {
            // A HEAL you've ever cast is your spell: HoT ticks name the spell
            // 20-40s after the cast, far outside the window, and real heal
            // procs (Blood Siphon Strike) are never castable. Damage keeps the
            // window — Spellblade fires the same nuke you also hand-cast.
            if (heal) return;
            if ((time - castAt).TotalSeconds is >= 0 and <= ProcCastWindowSec)
                return; // hand-cast, not a proc
        }

        if (!_sessionProcs.TryGetValue(ability, out var p))
            _sessionProcs[ability] = p = new ProcStat();
        p.Count++;
        if (heal) p.Heal += amount; else p.Damage += amount;
        if (amount > p.Max) p.Max = amount;
        if (crit) p.Crits++;
    }

    // ---- enemy DoT tracker -------------------------------------------------------
    // Automatic per-(spell, mob-name) rows for YOUR damage-over-time spells.
    // Tick lines name both halves every ~6s ("A froglok has taken 169 damage
    // from your Curse.") and the exact wear-off names both too ("Your Curse
    // spell has worn off of a bok ghoul knight."). Same-named mobs are
    // genuinely indistinguishable in an EQ log (the Companion's ruling too),
    // but each live instance ticks once per period — so the number of ticks in
    // one trailing tick-period IS the live instance count, and the ×N chip
    // self-corrects as mobs die. Censors: exact wear-off, mob death, zoning,
    // your own death, and 18s of tick silence (three missed ticks).

    public sealed record EnemyDotView(string Spell, string Target, int Ordinal,
        double? RemainingSeconds, double SinceSeconds, bool Overrun, double OverrunSeconds);

    private sealed class DotInstance
    {
        public int Ordinal;      // stable per-group bar number ("01", "02", …)
        public DateTime Start;
        public DateTime LastTick;
        public int Ticks;        // 0 = landing-only (a non-ticking debuff)
    }

    private sealed class DotGroup
    {
        public string Spell = "";
        public string Target = "";
        public readonly List<DotInstance> Instances = new();
    }

    // A tick "belongs" to the instance that is DUE one (its own last tick at
    // least this long ago); when nobody is due, it's a NEW same-named mob.
    private const double DotTickDueSec = 4.5;
    private const double DotSilenceCullSec = 13;    // two missed ticks = it's gone
    private const double DotOverrunCapSec = 60;     // landing-only rows: unwitnessed cull
    private const double DotHygieneCapSec = 90;     // landing-only + unknown duration
    private const double DebuffLandingWindowSec = 10; // your begin-cast -> its landing

    private readonly Dictionary<string, DotGroup> _enemyDots = new(StringComparer.OrdinalIgnoreCase);
    private (string Spell, string Suffix, DateTime At)? _pendingEnemyLanding;

    /// <summary>Optional duration lookup (learned/library) for the countdown;
    /// null keeps the row counting up instead of lying.</summary>
    public Func<string, double?>? DotDurationLookup { get; set; }

    /// <summary>Optional lookup (SpellLibrary): a DETRIMENTAL spell's
    /// third-person landing suffix ("has been poisoned.") — arms the
    /// non-ticking-debuff detector on your begin-cast.</summary>
    public Func<string, (string Suffix, bool Detrimental)?>? OtherLandingLookup { get; set; }

    private static readonly Regex WornOffOfRx = new(
        @"^Your (?<spell>.+?) spell has worn off of (?<mob>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WornOffRx = new(
        @"^Your (?<spell>.+?) spell has worn off\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string DotKey(string spell, string target) =>
        SpellDurations.BaseKey(spell) + "|" + target.Trim().ToLowerInvariant();

    private void NoteDotTick(string spell, string target, DateTime time)
    {
        target = Normalize(target);
        if (!IsEnemyName(target)) return;
        string key = DotKey(spell, target);
        if (!_enemyDots.TryGetValue(key, out var g))
            _enemyDots[key] = g = new DotGroup { Spell = spell.Trim(), Target = target };

        // The most-overdue due instance owns this tick; nobody due = new mob.
        // A never-ticked instance (fresh landing) owns its FIRST tick whenever
        // it comes — the landing line is not a tick, so the cadence window
        // doesn't apply yet. (A tick past the known duration keeps the
        // instance — the view shows it as OVERRUN; the re-cast's own landing
        // line resets the clock.)
        var inst = g.Instances
            .Where(i => i.Ticks == 0 || (time - i.LastTick).TotalSeconds >= DotTickDueSec)
            .OrderByDescending(i => time - i.LastTick)
            .FirstOrDefault();
        if (inst is null)
        {
            int ordinal = 1;
            while (g.Instances.Any(i => i.Ordinal == ordinal)) ordinal++; // lowest free number
            g.Instances.Add(inst = new DotInstance { Ordinal = ordinal, Start = time });
        }
        inst.LastTick = time;
        inst.Ticks++;
    }

    // A ticking instance that misses its tick window is STALE — EQ dots tick
    // every ~6s, so >7.5s of silence means the dot ended (early break, resist
    // of the remaining ticks, or an estimate that ran long).
    private const double DotStaleTickSec = 7.5;

    /// <summary>Your cast's landing on an enemy ("A froglok has been
    /// poisoned.") — the entry point for NON-ticking debuffs and the re-cast
    /// reset for ticking ones.
    ///
    /// THE BOUNDED READING (the Companion's JOS-140 round rule): a re-landing
    /// is either the same mob re-hit or a second mob of that name newly hit,
    /// and no log line separates them — so a landing must never grow a ghost
    /// bar. It REFRESHES an instance whose dot has plausibly ended (overrun /
    /// tick-stale / in the last stretch of its clock), and only reads as a
    /// SECOND MOB when every instance is comfortably mid-duration with a
    /// known clock (you spread to a tab target). With no known duration it
    /// always refreshes — under-counting self-corrects (a real twin's ticks
    /// split it via the heartbeat within one cadence), over-counting never
    /// does.</summary>
    private void NoteDotLanding(string spell, string target, DateTime time)
    {
        target = Normalize(target);
        if (!IsEnemyName(target)) return;
        string key = DotKey(spell, target);
        if (!_enemyDots.TryGetValue(key, out var g))
            _enemyDots[key] = g = new DotGroup { Spell = spell.Trim(), Target = target };

        void Append()
        {
            int ordinal = 1;
            while (g.Instances.Any(i => i.Ordinal == ordinal)) ordinal++;
            g.Instances.Add(new DotInstance { Ordinal = ordinal, Start = time, LastTick = time });
        }

        if (g.Instances.Count == 0) { Append(); return; }

        double? dur = DotDurationLookup?.Invoke(g.Spell) is double d && d > 1 ? d : null;

        // The instance this landing most plausibly re-casts, in order of proof:
        // past its known duration; ticking but silent past a tick window; or
        // inside the final stretch (≤ max(6s, 20%)) of its clock.
        var refresh =
            g.Instances.Where(i => dur is double dd && (time - i.Start).TotalSeconds > dd)
                .OrderByDescending(i => time - i.Start).FirstOrDefault()
            ?? g.Instances.Where(i => i.Ticks > 0 && (time - i.LastTick).TotalSeconds > DotStaleTickSec)
                .OrderByDescending(i => time - i.LastTick).FirstOrDefault()
            ?? g.Instances.Where(i => dur is double dd
                    && dd - (time - i.Start).TotalSeconds <= Math.Max(6, 0.2 * dd))
                .OrderBy(i => dur!.Value - (time - i.Start).TotalSeconds).FirstOrDefault();

        if (refresh is not null)
        {
            Refresh(refresh);
            return;
        }

        if (dur is null)
        {
            // No clock to argue with — bounded reading, refresh the newest.
            Refresh(g.Instances.OrderByDescending(i => i.Start).First());
            return;
        }

        void Refresh(DotInstance i)
        {
            i.Start = time;    // the re-cast: same bar, fresh clock
            i.LastTick = time;
            i.Ticks = 0;       // awaiting its first tick again — the cadence
                               // window must not ghost an early first tick
        }

        Append(); // everything running comfortably: a second mob of the name
    }

    private void RemoveDotsFor(string mobName)
    {
        // The dying twin's instance is unknowable — its silence culls it in
        // seconds. Only a group with a SINGLE instance clears immediately.
        string suffix = "|" + mobName.Trim().ToLowerInvariant();
        foreach (var key in _enemyDots.Keys.Where(k =>
                     k.EndsWith(suffix, StringComparison.Ordinal)).ToList())
            if (_enemyDots[key].Instances.Count <= 1)
                _enemyDots.Remove(key);
    }

    /// <summary>The wear-off closes the OLDEST instance — first landed fades
    /// first (the Companion's rule).</summary>
    private void CloseOldestDot(DotGroup g)
    {
        var oldest = g.Instances.OrderBy(i => i.Start).FirstOrDefault();
        if (oldest is not null) g.Instances.Remove(oldest);
    }

    /// <summary>Live enemy-DoT rows: one bar per same-named mob ("a froglok
    /// 01/02"), grouped per spell, soonest fade first inside the group.
    /// Silence-culled inline, so callers can poll this directly.</summary>
    public IReadOnlyList<EnemyDotView> EnemyDots(DateTime now)
    {
        var rows = new List<EnemyDotView>();
        foreach (var (key, g) in _enemyDots.ToList())
        {
            double? dur = DotDurationLookup?.Invoke(g.Spell) is double d && d > 1 ? d : null;

            // Ticking instances die by silence; landing-only ones by the
            // unwitnessed-overrun cap (or the hygiene cap with no duration).
            g.Instances.RemoveAll(i => i.Ticks > 0
                ? (now - i.LastTick).TotalSeconds > DotSilenceCullSec
                : dur is double dd2
                    ? (now - i.Start).TotalSeconds > dd2 + DotOverrunCapSec
                    : (now - i.Start).TotalSeconds > DotHygieneCapSec);
            if (g.Instances.Count == 0) { _enemyDots.Remove(key); continue; }

            foreach (var i in g.Instances)
            {
                double since = (now - i.Start).TotalSeconds;
                bool overrun = dur is double dd && since > dd;
                rows.Add(new EnemyDotView(g.Spell, g.Target, i.Ordinal,
                    dur is double dd3 ? Math.Max(0, dd3 - since) : null, since,
                    overrun, overrun ? since - dur!.Value : 0));
            }
        }
        return rows.OrderBy(r => r.Spell, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Overrun) // live countdowns above gray overruns
            .ThenBy(r => r.RemainingSeconds ?? double.MaxValue)
            .ThenBy(r => r.Ordinal).ToList();
    }

    /// <summary>Wipe every tracked enemy DoT (zoning, your death, manual reset).</summary>
    public void ClearEnemyDots() => _enemyDots.Clear();

    /// <summary>Test hook (Ctrl+Alt+T): seed demo enemy-DoT bars — twins with
    /// countdowns plus a gray overrun — so the panel can be placed without
    /// picking a fight. They age out through the normal culls.</summary>
    public void AddDemoEnemyDots()
    {
        var now = DateTime.Now;
        DotGroup G(string spell, string target)
        {
            string key = DotKey(spell, target);
            if (!_enemyDots.TryGetValue(key, out var g))
                _enemyDots[key] = g = new DotGroup { Spell = spell, Target = target };
            g.Instances.Clear();
            return g;
        }
        var curse = G("Demo Curse", "a demo froglok");
        curse.Instances.Add(new DotInstance { Ordinal = 1, Start = now.AddSeconds(-12), LastTick = now });
        curse.Instances.Add(new DotInstance { Ordinal = 2, Start = now.AddSeconds(-4), LastTick = now });
        var venom = G("Demo Venom", "a demo froglok");
        venom.Instances.Add(new DotInstance { Ordinal = 1, Start = now.AddSeconds(-38), LastTick = now });
    }

    /// <summary>Event cap per fight — a 10-minute raid fight sits well under this.</summary>
    public const int MaxFightEvents = 4000;

    private readonly record struct PendingEvent(DateTime When, string Ability, double Amount,
        FightStream Stream, bool Crit, bool Miss, bool Resist);
    private readonly List<PendingEvent> _events = new();
    private bool _eventsTruncated;

    /// <summary>Record a timeline event for the current fight (drops past the cap).</summary>
    private void Note(DateTime time, string ability, double amount, FightStream stream,
        bool crit = false, bool miss = false, bool resist = false)
    {
        if (_events.Count >= MaxFightEvents) { _eventsTruncated = true; return; }
        _events.Add(new PendingEvent(time, ability, amount, stream, crit, miss, resist));
    }

    // ---- death recap ------------------------------------------------------------
    // Rolling window of the last hits/heals on YOU, snapshotted when a death
    // line appears so the recap window can show what killed you.

    public sealed record RecapEntry(DateTime When, string Source, string Ability,
        double Amount, bool Heal, bool Crit, bool Miss = false);

    public sealed record DeathEvent(DateTime When, string Killer, IReadOnlyList<RecapEntry> Events);

    /// <summary>Raised on "You died." / "You have been slain by X!" (caller's thread).</summary>
    public event Action<DeathEvent>? PlayerDied;

    // Sized for a raid: ~8 events/s on you × the 15s recap window.
    private const int RecapCapacity = 120;

    /// <summary>The recap shows this many seconds before the death.</summary>
    public const double RecapWindowSec = 15;
    private readonly List<RecapEntry> _recap = new();
    private DateTime _lastDeathAt = DateTime.MinValue;

    private void RecapNote(DateTime when, string source, string ability, double amount,
        bool heal, bool crit, bool miss = false)
    {
        _recap.Add(new RecapEntry(when, source, ability, amount, heal, crit, miss));
        if (_recap.Count > RecapCapacity) _recap.RemoveAt(0);
    }

    /// <summary>"You died." and "You have been slain by X!" can both appear for one
    /// death — the first one within a few seconds wins.</summary>
    private void FireDeath(DateTime time, string killer)
    {
        if (Math.Abs((time - _lastDeathAt).TotalSeconds) < 5) return;
        _lastDeathAt = time;
        _enemyDots.Clear(); // your death strips your DoTs' bookkeeping too
        var snapshot = _recap.ToList();
        _recap.Clear();
        PlayerDied?.Invoke(new DeathEvent(time, killer, snapshot));
    }

    /// <summary>The zone we're in, from "You have entered <zone>." lines.</summary>
    public string CurrentZone { get; private set; } = "";

    public bool InCombat => _active;
    public bool HasData => _damage.Count > 0 || _healing.Count > 0;
    public double IncomingSelfTotal => _incomingSelf;
    public double IncomingPetTotal => _incomingPet;

    /// <summary>Fight length used for all DPS math (activity window, min 1s).</summary>
    public double DurationSeconds =>
        HasData ? Math.Max(1, (_last - _start).TotalSeconds) : 0;

    public double IncomingSelfDps => HasData ? _incomingSelf / DurationSeconds : 0;
    public double IncomingPetDps => HasData ? _incomingPet / DurationSeconds : 0;

    /// <summary>
    /// The fight label: the enemy that took the most damage, with "+N" when the
    /// pull contained more enemies (e.g. "a royal guard +3").
    /// </summary>
    public string TargetLabel
    {
        get
        {
            string best = ""; double most = -1; int enemies = 0;
            foreach (var (name, dmg) in _taken)
            {
                if (!IsEnemyName(name)) continue;
                enemies++;
                if (dmg > most) { most = dmg; best = name; }
            }
            if (enemies == 0)
            {
                // Fallback (e.g. a duel): most-damaged non-self target.
                foreach (var (name, dmg) in _taken)
                {
                    if (IsSelf(name) || IsPet(name)) continue;
                    if (dmg > most) { most = dmg; best = name; }
                }
                return best;
            }
            return enemies > 1 ? $"{best} +{enemies - 1}" : best;
        }
    }

    /// <summary>
    /// Log lines can't tell two mobs named "a royal guard" apart, so same-named
    /// enemies merge into one bucket. To keep the rankings honest, anything
    /// enemy-shaped is split out of them: player (and pet) names in EQ are
    /// always a single word, while mobs are "a/an/the ..." or multi-word names.
    /// Rare single-word named mobs will slip through as "players".
    /// </summary>
    public bool IsEnemyName(string name)
    {
        if (IsSelf(name) || IsPet(name)) return false;
        return name.Trim().Contains(' '); // "a royal guard", "Lady Vox", …
    }

    // ---- line formats (confirmed from the real EQ Legends log) ---------------
    // The first number is the effective amount; "0 (65)" means fully mitigated.

    private static readonly Regex NonMeleeRx = new(
        @"^(?<att>.+?) hit (?<tgt>.+?) for (?<dmg>\d+)(?: \(\d+\))? points? of \w+ damage by (?<spell>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DotRx = new(
        @"^(?<tgt>.+?) has taken (?<dmg>\d+)(?: \(\d+\))? damage from (?<spell>.+?) by (?<att>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Your own DoT ticks use a shorter form with no "by" clause:
    // "Orc legionnaire has taken 12 damage from your Tainted Breath."
    private static readonly Regex DotYourRx = new(
        @"^(?<tgt>.+?) has taken (?<dmg>\d+)(?: \(\d+\))? damage from your (?<spell>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "by <spell>" is optional — plain "You healed Thorrak for 12 hit points."
    // exists too, as does "healed <tgt> over time for" on HoT ticks.
    private static readonly Regex HealRx = new(
        @"^(?<att>.+?) (?:healed|heals?) (?<tgt>.+?)(?: over time)? for (?<amt>\d+)(?: \(\d+\))? hit points?(?: by (?<spell>.+?))?\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MeleeRx = new(
        @"^(?<att>.+?) (?<verb>slash(?:es)?|bash(?:es)?|crush(?:es)?|pierces?|kicks?|hits?|bites?|claws?|backstabs?|cleaves?|punch(?:es)?|gores?|mauls?|stings?|rends?|slams?|reaves?) (?<tgt>.+?) for (?<dmg>\d+)(?: \(\d+\))? points? of damage\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Damage shields: "Orc slaver is pierced by YOUR thorns for 8 points of
    // non-melee damage." / "YOU are pierced by an orc's thorns for 6 points of
    // non-melee damage!"
    private static readonly Regex ThornsOutRx = new(
        @"^(?<tgt>.+?) is pierced by YOUR thorns for (?<dmg>\d+)(?: \(\d+\))? points? of non-melee damage\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ThornsInRx = new(
        @"^YOU are pierced by (?<att>.+?)'s thorns for (?<dmg>\d+)(?: \(\d+\))? points? of non-melee damage!",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Unattributed spell damage on you: "You were hit by non-melee for 100 damage."
    private static readonly Regex NonMeleeYouRx = new(
        @"^You were hit by non-melee for (?<dmg>\d+)(?: \(\d+\))? damage\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Third-person melee verbs → the base form used as the ability label.</summary>
    private static readonly Dictionary<string, string> VerbBase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slashes"] = "slash", ["bashes"] = "bash", ["crushes"] = "crush", ["punches"] = "punch",
        ["pierces"] = "pierce", ["kicks"] = "kick", ["hits"] = "hit", ["bites"] = "bite",
        ["claws"] = "claw", ["backstabs"] = "backstab", ["cleaves"] = "cleave", ["gores"] = "gore",
        ["mauls"] = "maul", ["stings"] = "sting", ["rends"] = "rend", ["slams"] = "slam",
        ["reaves"] = "reave", // EQL shadowknight skill; rider damage logs separately as "Reaving Strike"
    };

    private static readonly HashSet<string> MeleeAbilities =
        new(VerbBase.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for plain weapon-swing abilities (slash, backstab, …) —
    /// everything else is a spell, DoT or proc for rate purposes.</summary>
    public static bool IsMeleeAbility(string ability) => MeleeAbilities.Contains(ability);

    // Avoided melee: "You try to slash a rat, but miss!" / "A rat tries to bite
    // YOU, but misses!" / "..., but a rat dodges!" (dodge/parry/riposte/block all
    // count as a miss for hit-rate purposes).
    private static readonly Regex MissRx = new(
        @"^(?<att>.+?) tr(?:y|ies) to (?<verb>\w+) (?<tgt>.+?), but .+!",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Spell resists: yours going out, and ones you shrug off.
    private static readonly Regex ResistTargetRx = new(
        @"^Your target resisted the (?<spell>.+?) spell\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResistOtherRx = new(
        @"^(?<tgt>.+?) resisted your (?<spell>.+?)[!.]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "You resist ice boned skeleton's Ice Bone Frost Burst!" — the attacker's
    // possessive is stripped in the handler (split on the first "'s ").
    private static readonly Regex ResistYouRx = new(
        @"^You resist(?:ed)? (?:the )?(?<rest>.+?)(?: spell)?[!.]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Skill-ups: "You have become better at Reave! (3)" — (3) is the new level.
    private static readonly Regex SkillUpRx = new(
        @"^You have become better at (?<skill>.+?)! \((?<lvl>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Progress lane: "You gain experience! (3.552%)",
    // "Your faction standing with Burning Dead has been adjusted by -2.",
    // "You have gained an ability point!  You now have 2 ability points."
    private static readonly Regex XpRx = new(
        @"^You gain (?:party |group )?experience! \((?<pct>\d+(?:\.\d+)?)%\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FactionRx = new(
        @"^Your faction standing with (?<fac>.+?) has been adjusted by (?<amt>-?\d+)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The total is singular at 1 ("1 ability point.") and absent in no variant we
    // know — but stay tolerant and float the gain even without a readable total.
    private static readonly Regex AaRx = new(
        @"^You have gained an ability point!(?: +You now have (?<n>\d+) ability points?\.?)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // AA spend: "You have improved Mastery of the Past 2 at a cost of 4 ability points."
    private static readonly Regex AaSpendRx = new(
        @"^You have improved (?<ab>.+?) at a cost of (?<n>\d+) ability points?\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Confirmed forms: "You died." and "You have been slain by a wan ghoul knight!"
    private static readonly Regex SlainYouRx = new(
        @"^You have been slain by (?<mob>.+?)!",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Own cast lines mark a spell as hand-cast for the proc detector.
    private static readonly Regex BeginCastRx = new(
        @"^You begin (?:casting|singing) (?<s>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimestampPrefix =
        new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);

    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    private const string ZonePrefix = "You have entered ";

    public void ProcessLine(string rawLine)
    {
        DateTime time = ExtractTimestamp(rawLine, out string body);

        if (body.StartsWith(ZonePrefix, StringComparison.Ordinal) && body.EndsWith('.'))
        {
            CurrentZone = body[ZonePrefix.Length..^1];
            _enemyDots.Clear(); // hostiles are left behind on zone
            return;
        }

        if (body.StartsWith("You died.", StringComparison.Ordinal)) { FireDeath(time, ""); return; }
        if (body.StartsWith("You have been slain", StringComparison.Ordinal)
            && SlainYouRx.Match(body) is { Success: true } slain)
        {
            FireDeath(time, slain.Groups["mob"].Value);
            return;
        }

        // Enemy-DoT censors: the exact wear-off ("… has worn off of <mob>."),
        // the target-less form (all rows of that spell), and mob deaths.
        if (body.StartsWith("Your ", StringComparison.Ordinal) && _enemyDots.Count > 0)
        {
            if (WornOffOfRx.Match(body) is { Success: true } wOf)
            {
                string k = DotKey(wOf.Groups["spell"].Value, wOf.Groups["mob"].Value);
                if (_enemyDots.TryGetValue(k, out var g1))
                {
                    CloseOldestDot(g1);
                    if (g1.Instances.Count == 0) _enemyDots.Remove(k);
                }
                return;
            }
            if (WornOffRx.Match(body) is { Success: true } wAll)
            {
                // Target-less fade: one instance faded somewhere — close the
                // globally oldest of that spell.
                string sk = SpellDurations.BaseKey(wAll.Groups["spell"].Value) + "|";
                var g2 = _enemyDots.Where(kv => kv.Key.StartsWith(sk, StringComparison.Ordinal))
                    .OrderBy(kv => kv.Value.Instances.Min(i => i.Start))
                    .Select(kv => kv.Value).FirstOrDefault();
                if (g2 is not null)
                {
                    CloseOldestDot(g2);
                    if (g2.Instances.Count == 0)
                        _enemyDots.Remove(DotKey(g2.Spell, g2.Target));
                }
                return;
            }
        }
        if (_enemyDots.Count > 0 && RaidKills.TryParseKill(body, out string deadMob))
            RemoveDotsFor(deadMob); // no return — death lines aren't otherwise consumed here

        // Own casts feed the proc detector: a spell landing WITHOUT one procced.
        if (body.StartsWith("You begin ", StringComparison.Ordinal)
            && BeginCastRx.Match(body) is { Success: true } cast)
        {
            string castName = cast.Groups["s"].Value.Trim();
            _recentCasts[SpellDurations.BaseKey(castName)] = time;
            // A detrimental cast arms the enemy-landing detector: its
            // third-person landing ("A froglok has been poisoned.") opens a
            // per-mob bar even when the spell never ticks.
            if (OtherLandingLookup?.Invoke(castName) is { Detrimental: true } ol)
                _pendingEnemyLanding = (castName, ol.Suffix, time);
            return;
        }

        if (_pendingEnemyLanding is { } pe)
        {
            if ((time - pe.At).TotalSeconds > DebuffLandingWindowSec)
            {
                _pendingEnemyLanding = null; // resisted / interrupted / fizzled
            }
            else if (body.Length > pe.Suffix.Length + 1
                     && body.EndsWith(pe.Suffix, StringComparison.Ordinal))
            {
                NoteDotLanding(pe.Spell, body[..^pe.Suffix.Length].Trim(), time);
                _pendingEnemyLanding = null;
            }
        }

        bool crit = body.Contains("(Critical)", StringComparison.Ordinal);

        Match m = NonMeleeRx.Match(body);
        if (m.Success) { AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, m.Groups["spell"].Value, Amount(m, "dmg"), time, SctFlavor.Spell, crit, procCandidate: true); return; }

        m = DotRx.Match(body);
        if (m.Success)
        {
            if (IsSelf(Normalize(m.Groups["att"].Value)))
                NoteDotTick(m.Groups["spell"].Value, m.Groups["tgt"].Value, time);
            AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, m.Groups["spell"].Value, Amount(m, "dmg"), time, SctFlavor.Spell, crit);
            return;
        }

        m = DotYourRx.Match(body);
        if (m.Success)
        {
            NoteDotTick(m.Groups["spell"].Value, m.Groups["tgt"].Value, time);
            AddDamage(Self(), m.Groups["tgt"].Value, m.Groups["spell"].Value, Amount(m, "dmg"), time, SctFlavor.Spell, crit);
            return;
        }

        m = HealRx.Match(body);
        if (m.Success)
        {
            string spell = m.Groups["spell"].Success ? m.Groups["spell"].Value : "heal";
            // Named heals only — a bare "You healed X" line can't identify a proc.
            AddHealing(m.Groups["att"].Value, m.Groups["tgt"].Value, spell, Amount(m, "amt"), time, crit,
                procCandidate: m.Groups["spell"].Success);
            return;
        }

        m = MeleeRx.Match(body);
        if (m.Success)
        {
            string verb = m.Groups["verb"].Value;
            string ability = VerbBase.TryGetValue(verb, out var baseForm) ? baseForm : verb.ToLowerInvariant();
            AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, ability, Amount(m, "dmg"), time, SctFlavor.Melee, crit);
            return;
        }

        m = ThornsOutRx.Match(body);
        if (m.Success) { AddDamage(Self(), m.Groups["tgt"].Value, "thorns", Amount(m, "dmg"), time, SctFlavor.Proc, crit); return; }

        m = ThornsInRx.Match(body);
        if (m.Success) { AddDamage(m.Groups["att"].Value, Self(), "thorns", Amount(m, "dmg"), time, SctFlavor.Proc, crit); return; }

        m = NonMeleeYouRx.Match(body);
        if (m.Success) { AddIncomingOnly("non-melee", Amount(m, "dmg"), time, crit); return; }

        m = MissRx.Match(body);
        if (m.Success)
        {
            string verb = m.Groups["verb"].Value;
            string ability = VerbBase.TryGetValue(verb, out var baseForm) ? baseForm : verb.ToLowerInvariant();
            AddMiss(m.Groups["att"].Value, m.Groups["tgt"].Value, ability, time);
            return;
        }

        m = ResistTargetRx.Match(body);
        if (m.Success) { AddOutgoingResist(m.Groups["spell"].Value, time); return; }

        m = ResistYouRx.Match(body);
        if (m.Success)
        {
            string rest = m.Groups["rest"].Value;
            int poss = rest.IndexOf("'s ", StringComparison.Ordinal);
            AddIncomingResist(poss > 0 ? rest[(poss + 3)..] : rest, time);
            return;
        }

        m = ResistOtherRx.Match(body);
        if (m.Success) { AddOutgoingResist(m.Groups["spell"].Value, time); return; }

        m = SkillUpRx.Match(body);
        if (m.Success)
        {
            // Deliberately does NOT touch the fight model — a skill-up is not combat.
            var s = SessionSkill(m.Groups["skill"].Value.Trim());
            s.Level = (int)Amount(m, "lvl");
            s.Ups++;
            return;
        }

        // Progress floats (xp / faction / AA) — informational only, never combat.
        m = XpRx.Match(body);
        if (m.Success)
        {
            double pct = double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture);
            SctEvent?.Invoke(new SctHit(SctKind.Progress, "xp", pct, SctFlavor.Melee, false,
                $"+{pct.ToString("0.##", CultureInfo.CurrentCulture)}%"));
            return;
        }

        m = FactionRx.Match(body);
        if (m.Success)
        {
            double amt = Amount(m, "amt");
            SctEvent?.Invoke(new SctHit(SctKind.Progress, m.Groups["fac"].Value, amt,
                amt >= 0 ? SctFlavor.Spell : SctFlavor.Proc, false,
                amt >= 0 ? $"+{amt:N0}" : amt.ToString("N0")));
            return;
        }

        m = AaRx.Match(body);
        if (m.Success)
        {
            double n = Amount(m, "n");
            SctEvent?.Invoke(new SctHit(SctKind.Progress,
                m.Groups["n"].Success ? $"{n:N0} total" : "", n,
                SctFlavor.Melee, true, "AA point!"));
            return;
        }

        m = AaSpendRx.Match(body);
        if (m.Success)
        {
            double n = Amount(m, "n");
            SctEvent?.Invoke(new SctHit(SctKind.Progress, m.Groups["ab"].Value, -n,
                SctFlavor.Spell, false, $"-{n:N0} AA"));
        }
    }

    /// <summary>Replay a historical line: fight-splitting uses the LINE's timestamp.</summary>
    public void Replay(string rawLine)
    {
        DateTime t = ExtractTimestamp(rawLine, out _);
        Tick(t);
        ProcessLine(rawLine);
    }

    /// <summary>Freeze the fight once it has been idle long enough (call periodically).</summary>
    public void Tick(DateTime now)
    {
        if (_active && (now - _last).TotalSeconds >= IdleSeconds)
        {
            _active = false;
            Archive();
        }
    }

    /// <summary>Raised when a finished fight is frozen into the history —
    /// the hook for auto-keeping raid-target fights.</summary>
    public event Action<FightRecord>? FightArchived;

    /// <summary>Snapshot the finished fight into the history list (newest first).</summary>
    private void Archive()
    {
        if (!HasData) return;
        var rec = new FightRecord
        {
            Label = string.IsNullOrEmpty(TargetLabel) ? "fight" : TargetLabel,
            Zone = _fightZone,
            EndedAt = _last,
            DurationSeconds = DurationSeconds,
            Damage = GetRows(healing: false),
            Healing = GetRows(healing: true),
            IncomingSelfTotal = _incomingSelf,
            IncomingPetTotal = _incomingPet,
            TotalDps = TotalPerSecond(healing: false),
            TotalHps = TotalPerSecond(healing: true),
            SelfAbilities = GetAbilityRows(Self()),
            PetAbilities = GetAbilityRows(PetName),
            IncomingSelfAbilities = GetIncomingAbilityRows(pet: false),
            IncomingPetAbilities = GetIncomingAbilityRows(pet: true),
            Events = _events
                .Select(e => new FightEvent(Math.Max(0, (e.When - _start).TotalSeconds),
                    e.Ability, e.Amount, e.Stream, e.Crit, e.Miss, e.Resist))
                .ToList(),
            EventsTruncated = _eventsTruncated,
        };
        _history.Insert(0, rec);
        while (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);
        _archivedActiveSec += rec.DurationSeconds; // session PPM denominator
        FightArchived?.Invoke(rec);
    }

    /// <summary>Clear everything (manual reset button).</summary>
    public void Reset()
    {
        _damage.Clear();
        _healing.Clear();
        _taken.Clear();
        _abilities.Clear();
        _healAbilities.Clear();
        _incomingSelfAbility.Clear();
        _incomingPetAbility.Clear();
        _incomingSelf = 0;
        _incomingPet = 0;
        _active = false;
        _events.Clear();
        _eventsTruncated = false;
    }

    /// <summary>
    /// Ranked sources for the given metric, highest first. Percentages are
    /// within each group (players share of player total, enemies of enemy total).
    /// </summary>
    public List<Row> GetRows(bool healing)
    {
        var src = healing ? _healing : _damage;
        double duration = DurationSeconds;

        double friendlyGrand = 0, enemyGrand = 0;
        foreach (var (name, total) in src)
        {
            if (IsEnemyName(name)) enemyGrand += total; else friendlyGrand += total;
        }

        var rows = new List<Row>(src.Count);
        foreach (var (name, total) in src)
        {
            bool enemy = IsEnemyName(name);
            double grand = enemy ? enemyGrand : friendlyGrand;
            rows.Add(new Row(name, total,
                duration > 0 ? total / duration : 0,
                grand > 0 ? total / grand * 100 : 0,
                enemy));
        }
        rows.Sort((a, b) => b.Total.CompareTo(a.Total));
        return rows;
    }

    /// <summary>Damage split by ability for one source (backstab / slash / spell …), highest first.</summary>
    public List<Row> GetAbilityRows(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)
            || !_abilities.TryGetValue(sourceName.Trim(), out var byAbility))
            return new();
        return AbilityRows(byAbility);
    }

    /// <summary>Healing split by spell for one source, highest first (solo HPS view).</summary>
    public List<Row> GetHealAbilityRows(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)
            || !_healAbilities.TryGetValue(sourceName.Trim(), out var byAbility))
            return new();
        return AbilityRows(byAbility);
    }

    /// <summary>Incoming damage on you (or your pet) split by the ability that dealt it.</summary>
    public List<Row> GetIncomingAbilityRows(bool pet) =>
        AbilityRows(pet ? _incomingPetAbility : _incomingSelfAbility);

    private List<Row> AbilityRows(Dictionary<string, AbilityStat> byAbility)
    {
        double duration = DurationSeconds;
        double grand = byAbility.Values.Sum(s => s.Total);
        var rows = new List<Row>(byAbility.Count);
        foreach (var (ability, s) in byAbility)
            rows.Add(new Row(ability, s.Total,
                duration > 0 ? s.Total / duration : 0,
                grand > 0 ? s.Total / grand * 100 : 0,
                false,
                s.Hits, s.Misses, s.Resists, s.MinOrZero, s.Max, s.Crits));
        rows.Sort((a, b) => b.Total.CompareTo(a.Total));
        return rows;
    }

    /// <summary>Players' combined metric per second (enemies excluded — that's the "raid" total).</summary>
    public double TotalPerSecond(bool healing)
    {
        double duration = DurationSeconds;
        if (duration <= 0) return 0;
        double sum = 0;
        foreach (var (name, total) in healing ? _healing : _damage)
            if (!IsEnemyName(name)) sum += total;
        return sum / duration;
    }

    // ---- accumulation ---------------------------------------------------------

    private void AddDamage(string attacker, string target, string ability, double amount, DateTime time,
        SctFlavor flavor = SctFlavor.Melee, bool crit = false, bool procCandidate = false)
    {
        attacker = Normalize(attacker);
        target = Normalize(target);
        if (IsReflexive(target)) target = attacker;
        Touch(time);

        Bump(_damage, attacker, amount);
        Bump(_taken, target, amount);
        Stat(attacker, ability).Land(amount, crit);

        if (IsSelf(target))
        {
            _incomingSelf += amount;
            StatIn(_incomingSelfAbility, ability).Land(amount, crit);
            Note(time, ability, amount, FightStream.SelfIn, crit);
            RecapNote(time, attacker, ability, amount, heal: false, crit);
            SctEvent?.Invoke(new SctHit(SctKind.IncomingSelf, ability, amount, flavor, crit));
        }
        else if (IsPet(target))
        {
            _incomingPet += amount;
            StatIn(_incomingPetAbility, ability).Land(amount, crit);
            Note(time, ability, amount, FightStream.PetIn, crit);
            SctEvent?.Invoke(new SctHit(SctKind.IncomingPet, ability, amount, flavor, crit));
        }

        if (IsSelf(attacker))
        {
            var skill = SessionSkill(ability);
            skill.Hits++;
            skill.Total += amount;
            if (crit) skill.Crits++;
            if (amount > skill.Max) skill.Max = amount;
            if (procCandidate) NoteProc(ability, amount, heal: false, time, crit);
            Note(time, ability, amount, FightStream.SelfOut, crit);
            SctEvent?.Invoke(new SctHit(SctKind.OutgoingSelf, ability, amount, flavor, crit));
        }
        else if (IsPet(attacker))
        {
            Note(time, ability, amount, FightStream.PetOut, crit);
            SctEvent?.Invoke(new SctHit(SctKind.OutgoingPet, ability, amount, flavor, crit));
        }
    }

    /// <summary>Damage on you with no attacker in the line ("You were hit by non-melee …").</summary>
    private void AddIncomingOnly(string ability, double amount, DateTime time, bool crit)
    {
        Touch(time);
        _incomingSelf += amount;
        StatIn(_incomingSelfAbility, ability).Land(amount, crit);
        Note(time, ability, amount, FightStream.SelfIn, crit);
        RecapNote(time, "", ability, amount, heal: false, crit);
        SctEvent?.Invoke(new SctHit(SctKind.IncomingSelf, ability, amount, SctFlavor.Spell, crit));
    }

    /// <summary>An avoided melee attempt (miss/dodge/parry/riposte/block).</summary>
    private void AddMiss(string attacker, string target, string ability, DateTime time)
    {
        attacker = Normalize(attacker);
        target = Normalize(target);
        Touch(time);

        Stat(attacker, ability).Misses++;
        if (IsSelf(target))
        {
            StatIn(_incomingSelfAbility, ability).Misses++;
            RecapNote(time, attacker, ability, 0, heal: false, crit: false, miss: true);
        }
        else if (IsPet(target)) StatIn(_incomingPetAbility, ability).Misses++;

        if (IsSelf(attacker))
        {
            SessionSkill(ability).Misses++;
            Note(time, ability, 0, FightStream.SelfOut, miss: true);
        }
        else if (IsPet(attacker)) Note(time, ability, 0, FightStream.PetOut, miss: true);
        if (IsSelf(target)) Note(time, ability, 0, FightStream.SelfIn, miss: true);
        else if (IsPet(target)) Note(time, ability, 0, FightStream.PetIn, miss: true);
    }

    /// <summary>One of YOUR spells got resisted.</summary>
    private void AddOutgoingResist(string spell, DateTime time)
    {
        Touch(time);
        Stat(Self(), spell.Trim()).Resists++;
        SessionSkill(spell.Trim()).Resists++;
        Note(time, spell.Trim(), 0, FightStream.SelfOut, resist: true);
    }

    /// <summary>You resisted an enemy spell.</summary>
    private void AddIncomingResist(string spell, DateTime time)
    {
        Touch(time);
        StatIn(_incomingSelfAbility, spell.Trim()).Resists++;
        Note(time, spell.Trim(), 0, FightStream.SelfIn, resist: true);
    }

    private AbilityStat Stat(string source, string ability)
    {
        if (!_abilities.TryGetValue(source, out var byAbility))
            _abilities[source] = byAbility = new Dictionary<string, AbilityStat>(StringComparer.OrdinalIgnoreCase);
        return StatIn(byAbility, ability);
    }

    private static AbilityStat StatIn(Dictionary<string, AbilityStat> dict, string ability)
    {
        if (!dict.TryGetValue(ability, out var stat))
            dict[ability] = stat = new AbilityStat();
        return stat;
    }

    private void AddHealing(string healer, string target, string spell, double amount, DateTime time,
        bool crit, bool procCandidate = false)
    {
        healer = Normalize(healer);
        target = Normalize(target);
        if (IsReflexive(target)) target = healer;
        Touch(time);
        Bump(_healing, healer, amount);

        // Per-spell split for the solo HPS view (bare heals have no spell name).
        if (!_healAbilities.TryGetValue(healer, out var bySpell))
            _healAbilities[healer] = bySpell = new Dictionary<string, AbilityStat>(StringComparer.OrdinalIgnoreCase);
        StatIn(bySpell, string.IsNullOrWhiteSpace(spell) ? "heal" : spell).Land(amount, crit);

        if (IsSelf(target)) RecapNote(time, healer, spell, amount, heal: true, crit);

        if (IsSelf(healer))
        {
            if (procCandidate) NoteProc(spell, amount, heal: true, time, crit);
            Note(time, spell, amount, FightStream.HealOut, crit);
            SctEvent?.Invoke(new SctHit(SctKind.HealOut, spell, amount, SctFlavor.Heal, crit));
        }
        else if (IsSelf(target))
        {
            Note(time, spell, amount, FightStream.HealIn, crit);
            SctEvent?.Invoke(new SctHit(SctKind.HealIn, spell, amount, SctFlavor.Heal, crit));
        }
    }

    /// <summary>First combat line after an idle fight wipes it and starts fresh.</summary>
    private void Touch(DateTime time)
    {
        if (!_active)
        {
            Reset();
            _start = time;
            _active = true;
            _fightZone = CurrentZone; // the zone where the fight began
        }
        if (time > _last) _last = time;
        if (time < _start) _start = time;
    }

    private static void Bump(Dictionary<string, double> dict, string key, double amount) =>
        dict[key] = dict.GetValueOrDefault(key) + amount;

    // ---- name handling --------------------------------------------------------

    private string Normalize(string name)
    {
        name = name.Trim();
        return name.Equals("you", StringComparison.OrdinalIgnoreCase)
            || name.Equals("your", StringComparison.OrdinalIgnoreCase)
            ? Self()
            : name;
    }

    private static bool IsReflexive(string target) =>
        target.Equals("himself", StringComparison.OrdinalIgnoreCase)
        || target.Equals("herself", StringComparison.OrdinalIgnoreCase)
        || target.Equals("itself", StringComparison.OrdinalIgnoreCase)
        || target.Equals("yourself", StringComparison.OrdinalIgnoreCase)
        || target.Equals("themself", StringComparison.OrdinalIgnoreCase);

    private string Self() => string.IsNullOrWhiteSpace(SelfName) ? "You" : SelfName;

    private bool IsSelf(string name) =>
        name.Equals(Self(), StringComparison.OrdinalIgnoreCase);

    private bool IsPet(string name) =>
        !string.IsNullOrWhiteSpace(PetName)
        && name.Equals(PetName.Trim(), StringComparison.OrdinalIgnoreCase);

    private static double Amount(Match m, string group) =>
        double.TryParse(m.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double v)
            ? v : 0;

    private static DateTime ExtractTimestamp(string line, out string body)
    {
        var m = TimestampPrefix.Match(line);
        if (m.Success)
        {
            body = line.Substring(m.Length);
            string ts = Regex.Replace(m.Groups["ts"].Value.Trim(), @"\s+", " ");
            if (DateTime.TryParseExact(ts, TimestampFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed;
        }
        else
        {
            body = line;
        }
        return DateTime.Now;
    }

    // ---- demo (Ctrl+Alt+T) ----------------------------------------------------

    /// <summary>Fill the meter with a fake 30s fight so the panel can be previewed.</summary>
    public void AddDemoFight()
    {
        Reset();
        var now = DateTime.Now;
        _start = now.AddSeconds(-30);
        _last = now;
        _active = true;
        if (CurrentZone.Length == 0) CurrentZone = "Permafrost Keep";
        _fightZone = CurrentZone;

        string pet = string.IsNullOrWhiteSpace(PetName) ? "Gobaner" : PetName.Trim();
        Bump(_damage, Self(), 5210);
        Bump(_damage, "Sneakstab", 4480);
        Bump(_damage, pet, 2950);
        Bump(_damage, "Bonkfist", 2310);
        Bump(_damage, "Lady Vox", 1900);
        Bump(_damage, "a royal guard", 640);   // same-named adds merge into one enemy bucket
        Bump(_healing, "Kindheart", 3620);
        Bump(_healing, Self(), 640);
        Bump(_taken, "Lady Vox", 14950);
        Bump(_taken, "a royal guard", 1210);
        Bump(_taken, Self(), 1350);
        Bump(_taken, pet, 550);
        _incomingSelf = 1350;
        _incomingPet = 550;

        // Ability drill-down (a rog/war/nec-style split for the demo).
        static AbilityStat DemoStat(double total, int hits, int misses, double min, double max, int resists = 0)
        {
            var s = new AbilityStat { Misses = misses, Resists = resists };
            s.Land(min);
            if (hits > 1) s.Land(max);
            s.Hits = hits;
            s.Total = total;
            return s;
        }
        _abilities[Self()] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["backstab"] = DemoStat(2300, 9, 3, 180, 340),
            ["slash"] = DemoStat(1450, 21, 6, 40, 95),
            ["Lifetap"] = DemoStat(860, 10, 0, 62, 105, resists: 2),
            ["Poison Bolt"] = DemoStat(600, 5, 0, 90, 140, resists: 1),
        };
        _abilities[pet] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bite"] = DemoStat(1800, 30, 11, 25, 80),
            ["claw"] = DemoStat(1150, 26, 9, 18, 66),
        };
        _healAbilities[Self()] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Slugs Healing"] = DemoStat(420, 6, 0, 60, 80),
        };
        _incomingSelfAbility["hit"] = DemoStat(780, 7, 5, 60, 150);
        _incomingSelfAbility["Frost Breath"] = DemoStat(570, 2, 0, 250, 320, resists: 1);
        _incomingPetAbility["claw"] = DemoStat(550, 6, 3, 50, 120);

        // Skill-tracker demo: session-scope, so only seed when nothing real is there.
        if (_sessionSkills.Count == 0)
        {
            _sessionSkills["backstab"] = new SkillStat { Hits = 41, Misses = 11, Crits = 6, Total = 9800, Max = 340 };
            _sessionSkills["slash"] = new SkillStat { Hits = 210, Misses = 35, Crits = 12, Total = 14200, Max = 95 };
            _sessionSkills["Lifetap"] = new SkillStat { Hits = 44, Resists = 6, Crits = 2, Total = 3900, Max = 105 };
        }

        // Give the history window something to compare against.
        if (_history.Count == 0)
        {
            _history.Insert(0, new FightRecord
            {
                Label = "a royal guard +3",
                Zone = "Clan Crushbone",
                EndedAt = now.AddMinutes(-4),
                DurationSeconds = 44,
                Damage = new List<Row>
                {
                    new(Self(), 3980, 3980 / 44.0, 47, false),
                    new("Sneakstab", 2900, 2900 / 44.0, 34, false),
                    new(pet, 1600, 1600 / 44.0, 19, false),
                    new("a royal guard", 2200, 2200 / 44.0, 100, true),
                },
                Healing = new List<Row> { new("Kindheart", 1750, 1750 / 44.0, 100, false) },
                IncomingSelfTotal = 980,
                IncomingPetTotal = 240,
                TotalDps = (3980 + 2900 + 1600) / 44.0,
                TotalHps = 1750 / 44.0,
            });
            _history.Insert(0, new FightRecord
            {
                Label = "Lady Vox",
                Zone = "Permafrost Keep",
                EndedAt = now.AddMinutes(-2),
                DurationSeconds = 92,
                Damage = new List<Row>
                {
                    new(Self(), 11200, 11200 / 92.0, 44, false),
                    new("Sneakstab", 9100, 9100 / 92.0, 36, false),
                    new(pet, 5300, 5300 / 92.0, 20, false),
                    new("Lady Vox", 20100, 20100 / 92.0, 100, true),
                },
                Healing = new List<Row> { new("Kindheart", 8800, 8800 / 92.0, 100, false) },
                IncomingSelfTotal = 4100,
                IncomingPetTotal = 900,
                TotalDps = (11200 + 9100 + 5300) / 92.0,
                TotalHps = 8800 / 92.0,
                SelfAbilities = new List<Row>
                {
                    new("backstab", 5100, 5100 / 92.0, 46, false),
                    new("slash", 3300, 3300 / 92.0, 29, false),
                    new("Lifetap", 1700, 1700 / 92.0, 15, false),
                    new("Poison Bolt", 1100, 1100 / 92.0, 10, false),
                },
                IncomingSelfAbilities = new List<Row>
                {
                    new("Frost Breath", 2500, 2500 / 92.0, 61, false),
                    new("hit", 1600, 1600 / 92.0, 39, false),
                },
                Events = DemoEvents(),
            });
        }

        // Deterministic pseudo-fight so the timeline window can be previewed.
        static List<FightEvent> DemoEvents()
        {
            var ev = new List<FightEvent>();
            for (int i = 0; i < 92; i += 2)
            {
                ev.Add(new FightEvent(i, "slash", 40 + i % 50, FightStream.SelfOut, Crit: i % 14 == 0));
                if (i % 3 == 0) ev.Add(new FightEvent(i + 0.4, "slash", 0, FightStream.SelfOut, Miss: true));
                if (i % 6 == 0) ev.Add(new FightEvent(i + 0.8, "backstab", 180 + i * 7 % 160, FightStream.SelfOut, Crit: i % 18 == 0));
                if (i % 8 == 0) ev.Add(new FightEvent(i + 1.0, "Lifetap", 60 + i % 45, FightStream.SelfOut));
                if (i % 24 == 0) ev.Add(new FightEvent(i + 1.1, "Lifetap", 0, FightStream.SelfOut, Resist: true));
                if (i % 5 == 0) ev.Add(new FightEvent(i + 1.2, "Frost Breath", 250 + i % 70, FightStream.SelfIn, Crit: i % 25 == 0));
                if (i % 4 == 0) ev.Add(new FightEvent(i + 1.5, "hit", 60 + i % 90, FightStream.SelfIn, Miss: i % 12 == 0));
                if (i % 7 == 0) ev.Add(new FightEvent(i + 1.7, "bite", 25 + i % 55, FightStream.PetOut));
                if (i % 9 == 0) ev.Add(new FightEvent(i + 1.9, "Superior Healing", 300 + i % 120, FightStream.HealIn));
            }
            return ev;
        }
    }
}
