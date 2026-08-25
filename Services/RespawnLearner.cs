using System.Globalization;
using System.Text.RegularExpressions;
using EQLOverlay.Models;

namespace EQLOverlay.Services;

/// <summary>
/// Learns respawn times from the log, and spots respawned mobs the moment the
/// log names them (Companion's Timers design, adapted — see their respawn.ts
/// for the nine rounds of owner rulings this leans on).
///
/// The core idea, stated honestly: a death→next-appearance gap is an UPPER
/// BOUND on the respawn — you can't meet a mob before it spawns — so the
/// estimate is the MINIMUM over observed gaps, which converges downward onto
/// the truth as you camp (an average would sit above it forever). "Next
/// appearance" is any log line NAMING the mob — a melee swing either way, a
/// cast, a resist, a DoT tick — or its next death, whichever comes first.
///
/// The laws:
///  * SAME STAY ONLY. A gap counts only when the death and the re-appearance
///    fall inside one continuous stay in the zone ("You have entered" lines
///    bound stays — no timeout heuristics). Kill it, port out, come back
///    tomorrow → true-but-useless three-day bound → no sample.
///  * A SIGHTING NEVER MOVES A CLOCK. Seeing the mob proves it is UP; it says
///    nothing about when it spawned. Sightings flip the panel row to UP (and
///    fire the spawn notice early); the clock's base stays the death line.
///  * THE FIGHT'S TAIL IS NOT A SPAWN. A mention within MinGapSeconds of the
///    mob's own death (the killing blow's trailing lines, a same-name twin
///    still swinging) is ignored entirely.
///  * CORPSES DON'T COUNT. Death and loot lines never mark a sighting — or
///    every kill would flip its own row UP as it went down.
///
/// Live-only (not fed on catch-up): stale lines would mint stale sightings.
/// </summary>
public sealed class RespawnLearner
{
    /// <summary>Mentions closer to the mob's own death than this are the
    /// fight's tail (or a twin), never a fresh spawn — and a learned gap
    /// this small would be a twin too.</summary>
    public const double MinGapSeconds = 30;

    /// <summary>A "gap" longer than this is nobody camping anything.</summary>
    private const double MaxGapSeconds = 24 * 3600;

    private sealed class Watch
    {
        public required RespawnEntry Entry;
        public required Regex DeathRx;
        public required Regex ActorRx;   // the mob leads the sentence
        public required Regex TargetRx;  // the mob is hit / missed / attributed
        public DateTime? DeathAt;        // pending cycle: last unclosed death
        public int DeathStay;
        public DateTime LastSighted = DateTime.MinValue;
    }

    private readonly List<Watch> _watches = new();
    private int _stay;          // bumped on every zone line — bounds gap samples
    private string _zone = "";

    /// <summary>The log named this watched mob (throttled, ≥MinGap after its
    /// own death). UI: flip the row UP, fire the spawn notice.</summary>
    public event Action<string>? Sighted;

    /// <summary>A death→appearance gap was measured: (name, gapSeconds, when).</summary>
    public event Action<string, double, DateTime>? GapLearned;

    /// <summary>The zone changed (the watch zone-scopes its rows on this —
    /// clocks keep running, only the display follows the player).</summary>
    public event Action<string>? ZoneChanged;

    public string Zone => _zone;

    /// <summary>Test hook: the pending death, if any, for a watched name.</summary>
    internal DateTime? PendingDeath(string name) => _watches
        .FirstOrDefault(w => w.Entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.DeathAt;

    // Both melee tenses: "hits" (a mob swings) and "hit" (you swing / spell damage).
    private const string Verbs =
        "hits?|slash(?:es)?|pierces?|crush(?:es)?|bash(?:es)?|bites?|kicks?|mauls?"
        + "|gores?|stings?|claws?|slams?|rends?|cleaves?|punch(?:es)?|strikes?"
        + "|backstabs?|reaves?";

    /// <summary>Rebuild the watch list from the respawn entries. Pending
    /// cycles survive by name — a gap mid-measure isn't lost to a Manager save.</summary>
    public void UpdateEntries(IEnumerable<RespawnEntry> entries)
    {
        var old = _watches.ToDictionary(w => w.Entry.Name, StringComparer.OrdinalIgnoreCase);
        _watches.Clear();
        foreach (var e in entries)
        {
            if (!e.Enabled || string.IsNullOrWhiteSpace(e.Name)) continue;
            Watch w;
            try { w = BuildWatch(e); }
            catch { continue; /* bad user death pattern — the trigger skips it too */ }
            if (old.TryGetValue(e.Name, out var prev))
            {
                w.DeathAt = prev.DeathAt;
                w.DeathStay = prev.DeathStay;
                w.LastSighted = prev.LastSighted;
            }
            _watches.Add(w);
        }
    }

    private static Watch BuildWatch(RespawnEntry e)
    {
        string n = Regex.Escape(e.Name.Trim());
        const RegexOptions opts = RegexOptions.Compiled | RegexOptions.CultureInvariant
            | RegexOptions.IgnoreCase;
        return new Watch
        {
            Entry = e,
            DeathRx = new Regex(string.IsNullOrWhiteSpace(e.Pattern)
                ? $@"(?:{n} has been slain by|You have slain {n})"
                : e.Pattern, opts),
            // The mob doing things: swinging, missing, casting, taking DoT
            // ticks, resisting, healing. "has taken" ≠ "has been slain" and
            // none of these match a corpse or a death line.
            ActorRx = new Regex(
                $@"^{n} (?:(?:{Verbs}) |tr(?:y|ies) to \w+ |begins? (?:casting|singing)"
                + $@"|activates |has taken \d|resisted |healed )", opts),
            // Things done TO the mob: your swings and spells landing on it,
            // your misses, and damage attributed to it ("… by <mob>.").
            TargetRx = new Regex(
                $@"(?:(?:{Verbs}|healed) {n} for \d|tr(?:y|ies) to \w+ {n}, but| by {n}[.!]$)",
                opts),
        };
    }

    public void ProcessLine(string rawLine)
    {
        DateTime time = ExtractTimestamp(rawLine, out string body);

        if (body.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            _stay++;
            _zone = body["You have entered ".Length..].TrimEnd('.');
            ZoneChanged?.Invoke(_zone);
            return;
        }
        if (_watches.Count == 0) return;

        // Events are collected and raised AFTER the loop: a GapLearned handler
        // saves respawns.json and calls UpdateEntries, which rebuilds _watches.
        List<(string Name, double Gap, DateTime When)>? gaps = null;
        List<string>? sighted = null;

        foreach (var w in _watches)
        {
            // Cheap gate before any regex — watched names are few, lines are many.
            if (body.IndexOf(w.Entry.Name, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (w.DeathRx.IsMatch(body))
            {
                // A pending cycle closes on the next death too (death→death gap).
                if (TakeGap(w, time) is { } g) (gaps ??= new()).Add((w.Entry.Name, g, time));
                w.DeathAt = time;
                w.DeathStay = _stay;
                Log.Info($"[respawn] {w.Entry.Name} death at {time:HH:mm:ss} — cycle opened (stay {_stay}, {_zone})");
                continue;
            }

            if (!w.ActorRx.IsMatch(body) && !w.TargetRx.IsMatch(body)) continue;

            // The fight's tail / a same-name twin still swinging: not a spawn.
            if (w.DeathAt is { } d && (time - d).TotalSeconds < MinGapSeconds) continue;

            if (TakeGap(w, time) is { } gap)
            {
                (gaps ??= new()).Add((w.Entry.Name, gap, time));
                Log.Info($"[respawn] {w.Entry.Name} reappeared after {gap:0}s — gap learned: \"{body}\"");
            }
            if ((time - w.LastSighted).TotalSeconds >= 2)
            {
                w.LastSighted = time;
                (sighted ??= new()).Add(w.Entry.Name);
            }
        }

        if (gaps is not null)
            foreach (var (name, gap, when) in gaps) GapLearned?.Invoke(name, gap, when);
        if (sighted is not null)
            foreach (var name in sighted) Sighted?.Invoke(name);
    }

    /// <summary>Close the pending cycle; returns the gap when it counts as a
    /// sample (same zone stay, sane bounds, learning on) — null discards.</summary>
    private double? TakeGap(Watch w, DateTime time)
    {
        if (w.DeathAt is not { } death) return null;
        w.DeathAt = null;
        if (!w.Entry.Learn) return null;
        if (w.DeathStay != _stay) return null; // left the zone: a true-but-useless bound
        double gap = (time - death).TotalSeconds;
        return gap is >= MinGapSeconds and <= MaxGapSeconds ? gap : null;
    }

    // ---- timestamp helper (same shape as TriggerEngine's) --------------------

    private static readonly Regex TimestampPrefix =
        new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);

    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

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
}
