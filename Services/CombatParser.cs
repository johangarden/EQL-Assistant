using System.Globalization;
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

    public readonly record struct Row(string Name, double Total, double Dps, double Percent, bool Enemy);

    /// <summary>A finished fight, frozen for the history/compare window.</summary>
    public sealed class FightRecord
    {
        public required string Label { get; init; }
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
    }

    private readonly List<FightRecord> _history = new();

    /// <summary>Finished fights, newest first.</summary>
    public IReadOnlyList<FightRecord> History => _history;

    private readonly Dictionary<string, double> _damage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _healing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _taken = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, double>> _abilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _incomingSelfAbility = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _incomingPetAbility = new(StringComparer.OrdinalIgnoreCase);
    private double _incomingSelf;
    private double _incomingPet;
    private DateTime _start;
    private DateTime _last;
    private bool _active;

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
        @"^(?<att>.+?) hit (?<tgt>.+?) for (?<dmg>\d+)(?: \(\d+\))? points of \w+ damage by (?<spell>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DotRx = new(
        @"^(?<tgt>.+?) has taken (?<dmg>\d+)(?: \(\d+\))? damage from (?<spell>.+?) by (?<att>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HealRx = new(
        @"^(?<att>.+?) (?:healed|heals?) (?<tgt>.+?) for (?<amt>\d+)(?: \(\d+\))? hit points by (?<spell>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MeleeRx = new(
        @"^(?<att>.+?) (?<verb>slash(?:es)?|bash(?:es)?|crush(?:es)?|pierces?|kicks?|hits?|bites?|claws?|backstabs?|cleaves?|punch(?:es)?|gores?|mauls?|stings?|rends?|slams?) (?<tgt>.+?) for (?<dmg>\d+)(?: \(\d+\))? points of damage\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Third-person melee verbs → the base form used as the ability label.</summary>
    private static readonly Dictionary<string, string> VerbBase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slashes"] = "slash", ["bashes"] = "bash", ["crushes"] = "crush", ["punches"] = "punch",
        ["pierces"] = "pierce", ["kicks"] = "kick", ["hits"] = "hit", ["bites"] = "bite",
        ["claws"] = "claw", ["backstabs"] = "backstab", ["cleaves"] = "cleave", ["gores"] = "gore",
        ["mauls"] = "maul", ["stings"] = "sting", ["rends"] = "rend", ["slams"] = "slam",
    };

    private static readonly Regex TimestampPrefix =
        new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);

    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    public void ProcessLine(string rawLine)
    {
        DateTime time = ExtractTimestamp(rawLine, out string body);

        // Cheapest-first ordering: spell hits, DoT ticks, heals, then melee
        // (the melee alternation is the widest net). Misses/"tries to" lines
        // match none of these.
        Match m = NonMeleeRx.Match(body);
        if (m.Success) { AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, m.Groups["spell"].Value, Amount(m, "dmg"), time); return; }

        m = DotRx.Match(body);
        if (m.Success) { AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, m.Groups["spell"].Value, Amount(m, "dmg"), time); return; }

        m = HealRx.Match(body);
        if (m.Success) { AddHealing(m.Groups["att"].Value, m.Groups["tgt"].Value, Amount(m, "amt"), time); return; }

        m = MeleeRx.Match(body);
        if (m.Success)
        {
            string verb = m.Groups["verb"].Value;
            string ability = VerbBase.TryGetValue(verb, out var baseForm) ? baseForm : verb.ToLowerInvariant();
            AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, ability, Amount(m, "dmg"), time);
        }
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

    /// <summary>Snapshot the finished fight into the history list (newest first).</summary>
    private void Archive()
    {
        if (!HasData) return;
        _history.Insert(0, new FightRecord
        {
            Label = string.IsNullOrEmpty(TargetLabel) ? "fight" : TargetLabel,
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
        });
        while (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);
    }

    /// <summary>Clear everything (manual reset button).</summary>
    public void Reset()
    {
        _damage.Clear();
        _healing.Clear();
        _taken.Clear();
        _abilities.Clear();
        _incomingSelfAbility.Clear();
        _incomingPetAbility.Clear();
        _incomingSelf = 0;
        _incomingPet = 0;
        _active = false;
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

    /// <summary>Incoming damage on you (or your pet) split by the ability that dealt it.</summary>
    public List<Row> GetIncomingAbilityRows(bool pet) =>
        AbilityRows(pet ? _incomingPetAbility : _incomingSelfAbility);

    private List<Row> AbilityRows(Dictionary<string, double> byAbility)
    {
        double duration = DurationSeconds;
        double grand = byAbility.Values.Sum();
        var rows = new List<Row>(byAbility.Count);
        foreach (var (ability, total) in byAbility)
            rows.Add(new Row(ability, total,
                duration > 0 ? total / duration : 0,
                grand > 0 ? total / grand * 100 : 0,
                false));
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

    private void AddDamage(string attacker, string target, string ability, double amount, DateTime time)
    {
        attacker = Normalize(attacker);
        target = Normalize(target);
        if (IsReflexive(target)) target = attacker;
        Touch(time);

        Bump(_damage, attacker, amount);
        Bump(_taken, target, amount);

        if (!_abilities.TryGetValue(attacker, out var byAbility))
            _abilities[attacker] = byAbility = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Bump(byAbility, ability, amount);

        if (IsSelf(target))
        {
            _incomingSelf += amount;
            Bump(_incomingSelfAbility, ability, amount);
        }
        else if (IsPet(target))
        {
            _incomingPet += amount;
            Bump(_incomingPetAbility, ability, amount);
        }
    }

    private void AddHealing(string healer, string target, double amount, DateTime time)
    {
        healer = Normalize(healer);
        Touch(time);
        Bump(_healing, healer, amount);
    }

    /// <summary>First combat line after an idle fight wipes it and starts fresh.</summary>
    private void Touch(DateTime time)
    {
        if (!_active)
        {
            Reset();
            _start = time;
            _active = true;
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
        _abilities[Self()] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["backstab"] = 2300, ["slash"] = 1450, ["Lifetap"] = 860, ["Poison Bolt"] = 600,
        };
        _abilities[pet] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bite"] = 1800, ["claw"] = 1150,
        };
        _incomingSelfAbility["hit"] = 780;
        _incomingSelfAbility["Frost Breath"] = 570;
        _incomingPetAbility["claw"] = 550;

        // Give the history window something to compare against.
        if (_history.Count == 0)
        {
            _history.Insert(0, new FightRecord
            {
                Label = "a royal guard +3",
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
            });
        }
    }
}
