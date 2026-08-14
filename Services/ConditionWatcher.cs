using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Watches for crowd control ON YOU — stun / fear / charm / mez — and exposes
/// the active set for the big-badge Conditions panel. Landing and wear-off
/// sentences are DERIVED from the spell library: each condition ends in one
/// uniform wear-off family ("You are no longer stunned."), so every spell
/// carrying one of those wear-offs contributes its cast-on-you line as a
/// landing for that condition. No keyword guessing — "Your muscles scream
/// with strength." is a buff and never matches. The badge shows from the
/// landing until the wear-off line, your death, or zoning; a per-condition
/// hygiene cap covers an eaten wear-off line.
/// </summary>
public sealed class ConditionWatcher
{
    public const string Stunned = "STUNNED";
    public const string Feared = "FEARED";
    public const string Charmed = "CHARMED";
    public const string Mezzed = "MEZZED";

    /// <summary>One active condition: what, and for how long so far.</summary>
    public sealed record View(string Kind, double ElapsedSeconds);

    // The uniform wear-off families (observed in the wiki scrape). NOTE:
    // "Your charm spell has worn off." is the CASTER side of a pet charm
    // (Befriend Animal) — you being freed, not you being charmed — excluded.
    private static readonly Dictionary<string, string> OffLines = new(StringComparer.Ordinal)
    {
        ["You are no longer stunned."] = Stunned,
        ["You are no longer afraid."] = Feared,
        ["You are no longer terrified."] = Feared,
        ["You are no longer charmed."] = Charmed,
        ["You are no longer mesmerized."] = Mezzed,
        ["You are no longer entranced."] = Mezzed,
    };

    // An eaten wear-off must not leave a badge squatting: caps sit above each
    // condition's longest library duration (stuns list no duration — they run
    // seconds; charms reach 19 minutes).
    private static double CapFor(string kind) => kind switch
    {
        Stunned => 30,
        Feared => 120,
        Mezzed => 180,
        _ => 1260, // Charmed: library max 1140s
    };

    private readonly Dictionary<string, string> _onLines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime Since, DateTime Deadline)> _active = new(StringComparer.Ordinal);

    private static readonly Regex TimestampPrefix = new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);
    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    public ConditionWatcher(SpellLibrary library)
    {
        // The plain forms print for melee stuns too (bash/slam), not only for
        // spells — seeded outright so they never depend on the library
        // happening to carry a spell that uses the bare sentence.
        _onLines["You are stunned."] = Stunned;
        _onLines["You are mesmerized."] = Mezzed;
        _onLines["You have been charmed."] = Charmed;

        foreach (var s in library.Spells)
        {
            if (!OffLines.TryGetValue(s.WearsOff, out string? kind)) continue;
            if (SpellLibrary.JunkMessage(s.CastOnYou)) continue;
            _onLines[s.CastOnYou] = kind;
        }
        // Landings whose spells carry no wear-off text but say it plainly
        // ("You are stunned by a gust of air.") — the word IS the condition.
        foreach (var s in library.Spells)
        {
            if (s.WearsOff.Length > 0 || SpellLibrary.JunkMessage(s.CastOnYou)) continue;
            if (s.CastOnYou.StartsWith("You are stunned", StringComparison.Ordinal))
                _onLines.TryAdd(s.CastOnYou, Stunned);
            else if (s.CastOnYou.StartsWith("You are mesmerized", StringComparison.Ordinal))
                _onLines.TryAdd(s.CastOnYou, Mezzed);
            else if (s.CastOnYou.StartsWith("You have been charmed", StringComparison.Ordinal))
                _onLines.TryAdd(s.CastOnYou, Charmed);
        }
    }

    /// <summary>Landing lines known for a condition (selftest visibility).</summary>
    public int LandingCount(string kind) => _onLines.Count(kv => kv.Value == kind);

    public void ProcessLine(string rawLine)
    {
        DateTime time = ExtractTimestamp(rawLine, out string body);

        if (_onLines.TryGetValue(body, out string? onKind))
        {
            // A re-land while active restarts the clock (fresh application).
            _active[onKind] = (time, time.AddSeconds(CapFor(onKind)));
            return;
        }
        if (OffLines.TryGetValue(body, out string? offKind))
        {
            _active.Remove(offKind);
            return;
        }

        // Censors: death breaks everything on you; zoning means it's over too.
        if (body.StartsWith("You died.", StringComparison.Ordinal)
            || body.StartsWith("You have been slain", StringComparison.Ordinal)
            || body.StartsWith("You have entered ", StringComparison.Ordinal))
            _active.Clear();
    }

    /// <summary>Active conditions, oldest first; entries past their hygiene
    /// cap are dropped (the wear-off line never arrived).</summary>
    public IReadOnlyList<View> Active(DateTime now)
    {
        foreach (var kind in _active.Where(kv => now > kv.Value.Deadline)
                     .Select(kv => kv.Key).ToList())
            _active.Remove(kind);

        return _active
            .OrderBy(kv => kv.Value.Since)
            .Select(kv => new View(kv.Key, Math.Max(0, (now - kv.Value.Since).TotalSeconds)))
            .ToList();
    }

    public void Clear() => _active.Clear();

    /// <summary>Test hook: a stun + fear pair that self-clears after ~12s.</summary>
    public void AddDemo()
    {
        var now = DateTime.Now;
        _active[Stunned] = (now, now.AddSeconds(12));
        _active[Feared] = (now, now.AddSeconds(12));
    }

    private static DateTime ExtractTimestamp(string rawLine, out string body)
    {
        var m = TimestampPrefix.Match(rawLine);
        body = m.Success ? rawLine[m.Length..] : rawLine;
        if (m.Success && DateTime.TryParseExact(m.Groups["ts"].Value, TimestampFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var t))
            return t;
        return DateTime.Now;
    }
}
