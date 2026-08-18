using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Observed-duration learner (design borrowed from jmoyers/everquest-companion,
/// MIT): a sample is the span from a CAST-ANCHORED landing to its wear-off line,
/// so only your own spells teach — landing lines are broadcasts and never name
/// a caster, but only you get a "You begin casting X." line. The estimate used
/// by the bars is max(configured floor, max over the recent samples): early
/// terminations read SHORT and must never drag the bar down, while AA/focus
/// extensions read LONG and win. Ranks pool ("Quickness II" teaches
/// "Quickness III"). Samples persist to spell-durations.json.
/// </summary>
public sealed class SpellDurations
{
    private sealed record Sample(DateTime Ts, double Seconds);

    private sealed class SpellRec
    {
        public string Name { get; set; } = "";
        public List<Sample> Samples { get; set; } = new();
    }

    private const double PendingCastWindowSec = 15;   // begin-cast -> landing
    private const double MaxSaneSeconds = 4 * 3600;   // beyond this a "cycle" is a gap, not a buff
    private const int MaxSamplesStored = 20;
    private const int RecentWindow = 5;               // the estimator's window

    private readonly string _path;
    private readonly SpellLibrary _library;
    private readonly Dictionary<string, SpellRec> _byKey = new();          // rank-stripped key
    private readonly Dictionary<string, List<SpellLibrary.Spell>> _byFadeMsg;

    private (SpellLibrary.Spell Spell, DateTime At)? _pending;             // begin-cast seen, landing awaited
    private readonly Dictionary<string, (SpellLibrary.Spell Spell, DateTime Start)> _open = new();

    /// <summary>Raised when a new sample is recorded: (spell, observedSeconds, sampleCount).</summary>
    public event Action<string, double, int>? SampleLearned;

    private static readonly Regex BeginCastRx = new(
        @"^You begin (?:casting|singing) (?<s>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RankSuffixRx = new(
        @"\s+(?:[IVX]{1,7})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimestampPrefix = new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);

    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    public SpellDurations(ConfigService config, SpellLibrary library, string? pathOverride = null)
    {
        _path = pathOverride ?? Path.Combine(config.ConfigDirectory, "spell-durations.json");
        _library = library;

        _byFadeMsg = new Dictionary<string, List<SpellLibrary.Spell>>(StringComparer.Ordinal);
        foreach (var s in library.Spells)
        {
            if (s.WearsOff.Length > 0)
            {
                if (!_byFadeMsg.TryGetValue(s.WearsOff, out var list))
                    _byFadeMsg[s.WearsOff] = list = new List<SpellLibrary.Spell>();
                list.Add(s);
            }
        }

        Load();
    }

    /// <summary>Rank-stripped pooling key: "Mesmerization VII" -> "mesmerization".</summary>
    public static string BaseKey(string spellName) =>
        RankSuffixRx.Replace(spellName.Trim(), "").ToLowerInvariant();

    /// <summary>Rank-stripped DISPLAY name: "Envenomed Bolt VI" -> "Envenomed
    /// Bolt". The log itself is inconsistent — the cast and DD lines carry the
    /// rank, DoT ticks drop it — so ability lanes pool on the base name.</summary>
    public static string BaseName(string spellName) =>
        RankSuffixRx.Replace(spellName.Trim(), "");

    /// <summary>The learner's contribution for a trigger/spell name: the max over
    /// the most recent samples of its base key, or null when nothing observed.
    /// The caller applies the floor (max with the configured duration).</summary>
    public double? LearnedMaxSeconds(string spellOrTriggerName)
    {
        if (!_byKey.TryGetValue(BaseKey(spellOrTriggerName), out var rec) || rec.Samples.Count == 0)
            return null;
        return rec.Samples.TakeLast(RecentWindow).Max(s => s.Seconds);
    }

    /// <summary>Sample count for a name (diagnostics/UI).</summary>
    public int SampleCount(string spellOrTriggerName) =>
        _byKey.TryGetValue(BaseKey(spellOrTriggerName), out var rec) ? rec.Samples.Count : 0;

    public void ProcessLine(string rawLine)
    {
        DateTime time = ExtractTimestamp(rawLine, out string body);

        // Contamination boundaries (the Companion's rule): death strips buffs
        // with no per-spell wear-off lines, and ZONING pauses buff timers — a
        // cycle spanning a zone crossing reads LONGER than the true duration
        // (measured on the real log: a 9m Quickness read 15m48 across a zone).
        // Both discard every open cycle instead of minting a wrong sample.
        if (body.StartsWith("You died.", StringComparison.Ordinal)
            || body.StartsWith("You have been slain", StringComparison.Ordinal)
            || body.StartsWith("LOADING, PLEASE WAIT", StringComparison.Ordinal)
            || body.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            _open.Clear();
            _pending = null;
            return;
        }

        var begin = BeginCastRx.Match(body);
        if (begin.Success)
        {
            string name = begin.Groups["s"].Value.Trim();
            // The cast line carries the RANK ("Slugs Healing V") but the
            // library holds the base name — fall back to a rank-stripped match.
            var spell = _library.Spells.FirstOrDefault(s =>
                    s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? _library.Spells.FirstOrDefault(s => BaseKey(s.Name) == BaseKey(name));
            // A re-cast contaminates any open cycle of the same spell (a refresh
            // would otherwise mint an inflated land-to-fade span).
            if (spell is not null)
            {
                _open.Remove(BaseKey(spell.Name));
                _pending = (spell, time);
            }
            return;
        }

        // Landing: only when the pending cast's own landing line appears —
        // that anchor is what keeps strangers' identical broadcasts out.
        if (_pending is { } p)
        {
            if ((time - p.At).TotalSeconds > PendingCastWindowSec)
            {
                _pending = null; // interrupted / resisted / fizzled
            }
            else if (body == p.Spell.CastOnYou)
            {
                _open[BaseKey(p.Spell.Name)] = (p.Spell, time);
                _pending = null;
                return;
            }
        }

        // An UNANCHORED landing of a spell we're timing: an external caster
        // refreshed the buff, so the eventual wear-off spans two applications
        // (measured on the real log: an external re-haste stretched a 9m
        // Quickness cycle to 15m48). Landing sentences are shared across
        // spells, so compare against each open cycle's OWN landing line.
        if (_open.Count > 0)
        {
            var contaminated = _open.Where(kv => kv.Value.Spell.CastOnYou == body)
                .Select(kv => kv.Key).ToList();
            if (contaminated.Count > 0)
            {
                foreach (var k in contaminated) _open.Remove(k);
                return;
            }
        }

        // Wear-off: closes the matching open cycle and mints a sample.
        if (_byFadeMsg.TryGetValue(body, out var candidates))
        {
            foreach (var spell in candidates)
            {
                string key = BaseKey(spell.Name);
                if (!_open.Remove(key, out var cycle)) continue;

                double seconds = (time - cycle.Start).TotalSeconds;
                if (seconds <= 1 || seconds > MaxSaneSeconds) return;

                if (!_byKey.TryGetValue(key, out var rec))
                    _byKey[key] = rec = new SpellRec { Name = spell.Name };
                if (rec.Samples.Any(s => s.Ts == time)) return; // replayed line (full reparse)
                rec.Name = spell.Name; // latest rank's display name wins
                rec.Samples.Add(new Sample(time, seconds));
                if (rec.Samples.Count > MaxSamplesStored) rec.Samples.RemoveAt(0);
                Save();
                Log.Info($"Duration learned: {spell.Name} ran {seconds:0}s " +
                         $"(sample {rec.Samples.Count}, window max {rec.Samples.TakeLast(RecentWindow).Max(s => s.Seconds):0}s)");
                SampleLearned?.Invoke(spell.Name, seconds, rec.Samples.Count);
                return;
            }
        }
    }

    /// <summary>Wipe all learned samples (Data page reset).</summary>
    public void ResetAll()
    {
        _byKey.Clear();
        _open.Clear();
        _pending = null;
        Save();
    }

    // ---- persistence ---------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var doc = JsonSerializer.Deserialize<Dictionary<string, SpellRec>>(
                File.ReadAllText(_path), JsonOpts);
            if (doc is null) return;
            foreach (var (k, v) in doc)
                if (v is { Samples.Count: > 0 }) _byKey[k] = v;
        }
        catch { /* corrupt -> start empty */ }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_byKey, JsonOpts)); }
        catch { /* best-effort */ }
    }

    private static DateTime ExtractTimestamp(string line, out string body)
    {
        var m = TimestampPrefix.Match(line);
        if (m.Success)
        {
            body = line[m.Length..];
            if (DateTime.TryParseExact(m.Groups["ts"].Value, TimestampFormats,
                    CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var t))
                return t;
        }
        else
        {
            body = line;
        }
        return DateTime.Now;
    }
}
