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
    // MOMENT badges — not states with wear-offs, but flashes about YOUR cast:
    // it broke, or it bounced. They expire by themselves after a beat.
    public const string Interrupted = "INTERRUPTED";
    public const string Resisted = "RESISTED";

    /// <summary>One active condition: what, for how long so far — and, for the
    /// moment badges, which spell (shown where the elapsed time would sit).</summary>
    public sealed record View(string Kind, double ElapsedSeconds, string Detail = "");

    private const double MomentSeconds = 1.5; // a flash, not a lecture (owner tuning)

    /// <summary>A moment just fired: (kind, spell) — the audible notice's cue.</summary>
    public event Action<string, string>? Moment;
    private readonly Dictionary<string, (DateTime Since, DateTime Deadline, string Detail)> _moments
        = new(StringComparer.Ordinal);

    // Own-cast markers, confirmed in the real logs: "Your Siphon Life spell is
    // interrupted." (a pet's says "Xarer's …" and stays silent) and
    // "A froglok shin knight resisted your Ignite!" / "Your target resisted
    // the X spell." ("resisted Gonartik's" is the pet's spell, silent).
    private static readonly Regex OwnInterruptRx = new(
        @"^Your (?<s>.+?) spell is interrupted\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OwnResistRx = new(
        @"^(?:Your target resisted the (?<s1>.+?) spell\.|.+? resisted your (?<s2>.+?)!)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        // STUN is special: the game prints the STATE itself — "You are
        // stunned!" paired 1:1 with "You are no longer stunned." (measured
        // 488/488 across Thorrak's logs, spell and melee stuns alike, chain
        // stuns included). Spell-flavor landings ("You are struck by a
        // sudden force.") also fire for stunless knockbacks and once made
        // the badge "fire randomly" — the state pair is the only truth.
        _onLines["You are stunned!"] = Stunned;
        _onLines["You are mesmerized."] = Mezzed;
        _onLines["You have been charmed."] = Charmed;

        foreach (var s in library.Spells)
        {
            if (!OffLines.TryGetValue(s.WearsOff, out string? kind)) continue;
            if (kind == Stunned) continue; // the state pair covers stuns
            if (SpellLibrary.JunkMessage(s.CastOnYou)) continue;
            _onLines[s.CastOnYou] = kind;
        }
        // Landings whose spells carry no wear-off text but say it plainly —
        // the word IS the condition (stun excluded: state pair only).
        foreach (var s in library.Spells)
        {
            if (s.WearsOff.Length > 0 || SpellLibrary.JunkMessage(s.CastOnYou)) continue;
            if (s.CastOnYou.StartsWith("You are mesmerized", StringComparison.Ordinal))
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
            // Logged verbatim: "randomly firing" badges are debugged by
            // matching this against the game log's same instant.
            Log.Info($"[conditions] {onKind} ON at {time:HH:mm:ss} — matched line: \"{body}\" (cap {CapFor(onKind):0}s{(_active.ContainsKey(onKind) ? ", re-land" : "")})");
            _active[onKind] = (time, time.AddSeconds(CapFor(onKind)));
            return;
        }
        if (OffLines.TryGetValue(body, out string? offKind))
        {
            if (_active.Remove(offKind))
                Log.Info($"[conditions] {offKind} OFF at {time:HH:mm:ss} — wear-off line: \"{body}\"");
            return;
        }

        // The moment flashes: your cast broke / your spell bounced.
        if (OwnInterruptRx.Match(body) is { Success: true } im)
        {
            _moments[Interrupted] = (time, time.AddSeconds(MomentSeconds), im.Groups["s"].Value);
            Moment?.Invoke(Interrupted, im.Groups["s"].Value);
            return;
        }
        if (OwnResistRx.Match(body) is { Success: true } rm)
        {
            string spell = rm.Groups["s1"].Success ? rm.Groups["s1"].Value : rm.Groups["s2"].Value;
            _moments[Resisted] = (time, time.AddSeconds(MomentSeconds), spell);
            Moment?.Invoke(Resisted, spell);
            return;
        }

        // Censors: death breaks everything on you; zoning means it's over too.
        if (body.StartsWith("You died.", StringComparison.Ordinal)
            || body.StartsWith("You have been slain", StringComparison.Ordinal)
            || body.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            if (_active.Count > 0)
                Log.Info($"[conditions] cleared ({string.Join(", ", _active.Keys)}) at {time:HH:mm:ss} — censor line: \"{body}\"");
            _active.Clear();
            _moments.Clear();
        }
    }

    /// <summary>Active conditions, oldest first; entries past their hygiene
    /// cap are dropped (the wear-off line never arrived).</summary>
    public IReadOnlyList<View> Active(DateTime now)
    {
        foreach (var kind in _active.Where(kv => now > kv.Value.Deadline)
                     .Select(kv => kv.Key).ToList())
        {
            Log.Info($"[conditions] {kind} expired by hygiene cap (no wear-off line seen)");
            _active.Remove(kind);
        }

        foreach (var kind in _moments.Where(kv => now > kv.Value.Deadline)
                     .Select(kv => kv.Key).ToList())
            _moments.Remove(kind);

        return _active
            .OrderBy(kv => kv.Value.Since)
            .Select(kv => new View(kv.Key, Math.Max(0, (now - kv.Value.Since).TotalSeconds)))
            .Concat(_moments
                .OrderBy(kv => kv.Value.Since)
                .Select(kv => new View(kv.Key,
                    Math.Max(0, (now - kv.Value.Since).TotalSeconds), kv.Value.Detail)))
            .ToList();
    }

    public void Clear()
    {
        _active.Clear();
        _moments.Clear();
    }

    /// <summary>Test hook: every badge, self-clearing after ~12s.</summary>
    public void AddDemo()
    {
        var now = DateTime.Now;
        foreach (string kind in new[] { Stunned, Feared, Charmed, Mezzed })
            _active[kind] = (now, now.AddSeconds(12));
        _moments[Interrupted] = (now, now.AddSeconds(12), "Siphon Life");
        _moments[Resisted] = (now, now.AddSeconds(12), "Ignite");
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
