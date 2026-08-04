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

    public readonly record struct Row(string Name, double Total, double Dps, double Percent);

    private readonly Dictionary<string, double> _damage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _healing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _taken = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The enemy label for the fight: whoever (other than you/pet) took the most damage.</summary>
    public string TargetLabel
    {
        get
        {
            string best = ""; double most = -1;
            foreach (var (name, dmg) in _taken)
            {
                if (IsSelf(name) || IsPet(name)) continue;
                if (dmg > most) { most = dmg; best = name; }
            }
            return best;
        }
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
        @"^(?<att>.+?) (?:slash(?:es)?|bash(?:es)?|crush(?:es)?|pierces?|kicks?|hits?|bites?|claws?|backstabs?|cleaves?|punch(?:es)?|gores?|mauls?|stings?|rends?|slams?) (?<tgt>.+?) for (?<dmg>\d+)(?: \(\d+\))? points of damage\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        if (m.Success) { AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, Amount(m, "dmg"), time); return; }

        m = DotRx.Match(body);
        if (m.Success) { AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, Amount(m, "dmg"), time); return; }

        m = HealRx.Match(body);
        if (m.Success) { AddHealing(m.Groups["att"].Value, m.Groups["tgt"].Value, Amount(m, "amt"), time); return; }

        m = MeleeRx.Match(body);
        if (m.Success) AddDamage(m.Groups["att"].Value, m.Groups["tgt"].Value, Amount(m, "dmg"), time);
    }

    /// <summary>Freeze the fight once it has been idle long enough (call periodically).</summary>
    public void Tick(DateTime now)
    {
        if (_active && (now - _last).TotalSeconds >= IdleSeconds)
            _active = false;
    }

    /// <summary>Clear everything (manual reset button).</summary>
    public void Reset()
    {
        _damage.Clear();
        _healing.Clear();
        _taken.Clear();
        _incomingSelf = 0;
        _incomingPet = 0;
        _active = false;
    }

    /// <summary>Ranked sources for the given metric, highest first.</summary>
    public List<Row> GetRows(bool healing)
    {
        var src = healing ? _healing : _damage;
        double duration = DurationSeconds;
        double grand = src.Values.Sum();
        var rows = new List<Row>(src.Count);
        foreach (var (name, total) in src)
            rows.Add(new Row(name, total,
                duration > 0 ? total / duration : 0,
                grand > 0 ? total / grand * 100 : 0));
        rows.Sort((a, b) => b.Total.CompareTo(a.Total));
        return rows;
    }

    /// <summary>Total of the given metric across all sources, per second.</summary>
    public double TotalPerSecond(bool healing)
    {
        double duration = DurationSeconds;
        if (duration <= 0) return 0;
        return (healing ? _healing : _damage).Values.Sum() / duration;
    }

    // ---- accumulation ---------------------------------------------------------

    private void AddDamage(string attacker, string target, double amount, DateTime time)
    {
        attacker = Normalize(attacker);
        target = Normalize(target);
        if (IsReflexive(target)) target = attacker;
        Touch(time);

        Bump(_damage, attacker, amount);
        Bump(_taken, target, amount);
        if (IsSelf(target)) _incomingSelf += amount;
        else if (IsPet(target)) _incomingPet += amount;
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
        Bump(_healing, "Kindheart", 3620);
        Bump(_healing, Self(), 640);
        Bump(_taken, "Lady Vox", 14950);
        Bump(_taken, Self(), 1350);
        Bump(_taken, pet, 550);
        _incomingSelf = 1350;
        _incomingPet = 550;
    }
}
