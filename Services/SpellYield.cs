using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// What each of YOUR spells actually does per cast (owner, 25 Sep — the spell
/// efficiency tab's "Yours" and "at your rank"): own casts (begin-cast lines,
/// interrupts taken back), the damage and healing they land (every target of
/// an AE, every tick of a DoT/HoT, the ACTUAL heal — "for 104 (133) hit
/// points" counts 104) and the rank you cast them at ("Harm Touch VIII" = 8).
/// Pooled by base name. Mana never appears in the log — the wiki's cost and
/// its per-rank rule (<see cref="SpellEfficiency"/>) fill that in.
///
/// Dedupe: the log is monotonic, so a line older than the newest one counted
/// is a replay and skipped; lines of that same newest second dedupe by text.
/// Reset &amp; rebuild wipes it before its reparse. Persisted in spell-yield.json.
/// </summary>
public sealed class SpellYield
{
    public sealed class Rec
    {
        public int Casts { get; set; }
        public double Damage { get; set; }
        public double Healing { get; set; }
        public int Hits { get; set; }
        public int Crits { get; set; }
        public int Rank { get; set; }
        public DateTime Last { get; set; }
    }

    private sealed class Doc
    {
        public DateTime Through { get; set; }
        public Dictionary<string, Rec> Spells { get; set; } = new();
    }

    private static readonly Regex CastRx = new(@"^You begin casting (?<s>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex InterruptRx = new(@"^Your (?<s>.+?) spell is interrupted\.$", RegexOptions.Compiled);
    private static readonly Regex HitRx = new(@"^You hit .+? for (?<n>\d+) points of .+? damage by (?<s>.+?)\.(?<c> \(Critical\))?$", RegexOptions.Compiled);
    private static readonly Regex OwnTickRx = new(@"^.+? has taken (?<n>\d+) damage from your (?<s>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex TickByRx = new(@"^.+? has taken (?<n>\d+) damage from (?<s>.+?) by (?<w>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex HealRx = new(@"^You healed .+? for (?<n>\d+)(?: \(\d+\))? hit points by (?<s>.+?)\.(?<c> \(Critical\))?$", RegexOptions.Compiled);
    private static readonly Regex RankRx = new(@" (?<r>[IVX]{1,7})$", RegexOptions.Compiled);

    private readonly string? _path;
    private readonly Dictionary<string, Rec> _spells = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _through = DateTime.MinValue;
    private readonly HashSet<string> _atThrough = new(StringComparer.Ordinal);
    private bool _dirty;
    private DateTime _lastSave = DateTime.MinValue;

    /// <summary>Your character's name — the long DoT form says "by Thorrak".</summary>
    public string SelfName { get; set; } = "";

    public SpellYield(ConfigService? config, string? pathOverride = null)
    {
        _path = pathOverride ?? (config is null ? null : Path.Combine(config.ConfigDirectory, "spell-yield.json"));
        Load();
    }

    public IReadOnlyDictionary<string, Rec> Spells => _spells;

    /// <summary>The record for a spell (any rank spelling), or null.</summary>
    public Rec? Of(string spell) => _spells.TryGetValue(SpellDurations.BaseKey(spell), out var r) ? r : null;

    public void ProcessLine(string line)
    {
        if (line.Length < 28 || line[0] != '[') return;
        int close = line.IndexOf(']');
        if (close < 5) return;
        string body = line[(close + 1)..].TrimStart();
        // Cheap gates first: only our own spell lines matter.
        bool cast = body.StartsWith("You begin casting ", StringComparison.Ordinal);
        bool you = body.StartsWith("You hit ", StringComparison.Ordinal) || body.StartsWith("You healed ", StringComparison.Ordinal);
        bool tick = !cast && !you && body.Contains(" has taken ", StringComparison.Ordinal);
        bool intr = !cast && !you && !tick && body.EndsWith(" spell is interrupted.", StringComparison.Ordinal);
        if (!cast && !you && !tick && !intr) return;

        if (!DateTime.TryParseExact(line[1..close], "ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var time)) return;
        if (time < _through) return;                      // a replayed line
        if (time > _through) { _through = time; _atThrough.Clear(); }
        if (!_atThrough.Add(line)) return;                // this second's line, seen already

        Match m;
        if (cast && (m = CastRx.Match(body)).Success)
        {
            string s = m.Groups["s"].Value;
            var r = Get(s);
            r.Casts++;
            r.Rank = RankOf(s);
            r.Last = time;
        }
        else if (intr && (m = InterruptRx.Match(body)).Success)
        {
            if (_spells.TryGetValue(SpellDurations.BaseKey(m.Groups["s"].Value), out var r) && r.Casts > 0) r.Casts--;
        }
        else if (you && body.StartsWith("You hit ", StringComparison.Ordinal) && (m = HitRx.Match(body)).Success)
        {
            var r = Get(m.Groups["s"].Value);
            r.Damage += int.Parse(m.Groups["n"].Value);
            r.Hits++;
            if (m.Groups["c"].Success) r.Crits++;
        }
        else if (you && (m = HealRx.Match(body)).Success)
        {
            var r = Get(m.Groups["s"].Value);
            r.Healing += int.Parse(m.Groups["n"].Value);
            r.Hits++;
            if (m.Groups["c"].Success) r.Crits++;
        }
        else if (tick && (m = OwnTickRx.Match(body)).Success)
            Get(m.Groups["s"].Value).Damage += int.Parse(m.Groups["n"].Value);
        else if (tick && SelfName.Length > 0 && (m = TickByRx.Match(body)).Success
                 && m.Groups["w"].Value.Equals(SelfName, StringComparison.OrdinalIgnoreCase))
            Get(m.Groups["s"].Value).Damage += int.Parse(m.Groups["n"].Value);
        else return;
        _dirty = true;
    }

    /// <summary>Roman rank suffix → 0..10 ("Envenomed Bolt" = 0, "… X" = 10).</summary>
    public static int RankOf(string spell)
    {
        var m = RankRx.Match(spell.Trim());
        if (!m.Success) return 0;
        int total = 0, prev = 0;
        foreach (char c in m.Groups["r"].Value.Reverse())
        {
            int v = c switch { 'I' => 1, 'V' => 5, 'X' => 10, _ => 0 };
            total += v < prev ? -v : v;
            prev = Math.Max(prev, v);
        }
        return Math.Clamp(total, 0, 10);
    }

    private Rec Get(string spell)
    {
        string key = SpellDurations.BaseKey(spell);
        if (!_spells.TryGetValue(key, out var r)) _spells[key] = r = new Rec();
        return r;
    }

    /// <summary>A copy of the counted state, for a background pass that
    /// continues where this one stopped (lines older than it are skipped).</summary>
    public SpellYield Clone()
    {
        var c = new SpellYield(null, null) { SelfName = SelfName, _through = _through };
        foreach (var (k, v) in _spells)
            c._spells[k] = new Rec { Casts = v.Casts, Damage = v.Damage, Healing = v.Healing, Hits = v.Hits, Crits = v.Crits, Rank = v.Rank, Last = v.Last };
        foreach (var l in _atThrough) c._atThrough.Add(l);
        return c;
    }

    /// <summary>Take a background pass's result wholesale (it read the same log this one follows).</summary>
    public void AdoptFrom(SpellYield other)
    {
        _spells.Clear();
        foreach (var (k, v) in other._spells) _spells[k] = v;
        _through = other._through;
        _atThrough.Clear();
        foreach (var l in other._atThrough) _atThrough.Add(l);
        _dirty = true;
        SaveIfDirty(force: true);
    }

    /// <summary>Feed log files OLDEST FIRST (by their first timestamp — the
    /// monotonic guard needs it) on a background thread. Pure parsing, no UI.</summary>
    public static Task<SpellYield> FeedAsync(SpellYield seed, IEnumerable<string> files, CancellationToken ct = default) => Task.Run(() =>
    {
        var ordered = files.Where(File.Exists).Select(f => (Path: f, First: FirstTime(f))).OrderBy(x => x.First).Select(x => x.Path).ToList();
        foreach (var f in ordered)
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 1 << 16);
            string? line;
            int n = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                seed.ProcessLine(line);
            }
        }
        return seed;
    }, ct);

    private static DateTime FirstTime(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            for (int i = 0; i < 50 && reader.ReadLine() is { } line; i++)
            {
                int close = line.IndexOf(']');
                if (line.StartsWith('[') && close > 5 && DateTime.TryParseExact(line[1..close], "ddd MMM d HH:mm:ss yyyy",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var t))
                    return t;
            }
        }
        catch { /* unreadable → last */ }
        return DateTime.MaxValue;
    }

    public void ResetAll()
    {
        _spells.Clear();
        _through = DateTime.MinValue;
        _atThrough.Clear();
        _dirty = true;
        SaveIfDirty(force: true);
    }

    /// <summary>Persist if something changed (throttled to once a minute unless forced).</summary>
    public void SaveIfDirty(bool force = false)
    {
        if (!_dirty || _path is null) return;
        var now = DateTime.Now;
        if (!force && (now - _lastSave).TotalSeconds < 60) return;
        _lastSave = now;
        _dirty = false;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new Doc
            {
                Through = _through,
                Spells = new Dictionary<string, Rec>(_spells, StringComparer.OrdinalIgnoreCase),
            }));
        }
        catch (Exception ex) { Log.Warn("spell-yield.json not saved: " + ex.Message); }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(_path));
            if (doc is null) return;
            foreach (var (k, v) in doc.Spells) _spells[k] = v;
            _through = doc.Through;
        }
        catch (Exception ex) { Log.Warn("spell-yield.json unreadable: " + ex.Message); }
    }
}
