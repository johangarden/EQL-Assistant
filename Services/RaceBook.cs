using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Race unlocks (owner, 22 Sep): the achievements dump gives each race's
/// recipe (three factions to max, or a task), the factions dump gives the
/// standings, and the log keeps them live — "Your faction standing with X
/// has been adjusted by N." moves a standing (only lines AFTER the dump; a
/// reparse dedupes by line), "could not possibly get any better" marks it
/// maxed, and the "You have slain X!" line just before teaches which mob
/// gives the hit (persisted in races.json with the ★-tracked races). The
/// Character window's Races tab reads <see cref="Views"/>; the faction
/// helper card follows <see cref="FactionHit"/> / <see cref="FactionMaxed"/>
/// for tracked races.
/// </summary>
public sealed class RaceBook
{
    private static readonly Regex AdjustRx = new(@"^Your faction standing with (?<f>.+?) has been adjusted by (?<n>-?\d+)\.$", RegexOptions.Compiled);
    private static readonly Regex CapRx = new(@"^Your faction standing with (?<f>.+?) could not possibly get any (?<dir>better|worse)\.$", RegexOptions.Compiled);
    private static readonly Regex KillRx = new(@"^You have slain (?<m>.+?)!$", RegexOptions.Compiled);
    // A pet's or a groupmate's kill moves your faction too (owner, 25 Sep:
    // Dreadguard Outer read "no hits in your log yet" after a night of kills).
    private static readonly Regex SlainByRx = new(@"^(?<m>.+?) has been slain by (?<k>.+?)!$", RegexOptions.Compiled);
    private const double KillPairSec = 3; // faction lines follow the kill line within the same second or the next

    public sealed record HitSource(string Mob, int Hit, int Count, DateTime Last);

    /// <param name="Cap">YOUR ceiling, learned from the game's "could not possibly
    /// get any better": race / class / deity modifiers put it far under the
    /// dump's 2,000 (an Ogre maxes Dark Bargainers at −220 — owner, 25 Sep:
    /// "2 factions says MAXED but bars are not"). Null until the game says so.</param>
    public sealed record FactionView(string Name, int Standing, int Max, bool Done, IReadOnlyList<HitSource> Sources, int? EstimateKills, bool Known, int? Cap = null)
    {
        /// <summary>MAXED below the dump's max — the character's own ceiling.</summary>
        public bool CappedBelowMax => Done && Standing < Max;
        public int ToGo => Math.Max(0, Max - Standing);
        public double Fraction => Max <= 0 ? 0 : Math.Clamp((double)Math.Max(0, Standing) / Max, 0, 1);
        public bool Negative => Standing < 0;
    }

    /// <param name="Note">"" · "YOU" (the race you were created as) · "AUTO" (rides on another race) · "TASK".</param>
    public sealed record RaceView(string Name, bool Done, string Note, IReadOnlyList<FactionView> Factions, string? Task, string? DependsOn, bool Tracked)
    {
        public int DoneCount => Factions.Count(f => f.Done);
        public double Progress => Done ? 1 : Factions.Count == 0 ? 0 : Factions.Average(f => f.Done ? 1 : f.Fraction);
        public string CountText => Done ? (Note.Length > 0 ? Note : "DONE") : Task is not null ? "TASK" : $"{DoneCount}/{Factions.Count}";
    }

    private sealed class HitRec { public int Hit { get; set; } public int Count { get; set; } public DateTime Last { get; set; } }
    private sealed class Doc
    {
        public List<string> Tracked { get; set; } = new();
        public Dictionary<string, Dictionary<string, HitRec>> Hits { get; set; } = new();
        public Dictionary<string, int> Caps { get; set; } = new();
    }

    private readonly string? _path;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private Dictionary<string, FactionDumps.Standing> _standings = new(StringComparer.OrdinalIgnoreCase);
    private List<FactionDumps.Race> _races = new();
    private readonly Dictionary<string, int> _delta = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deltaKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _maxed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _maxedSaid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, HitRec>> _hits = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _caps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedHit = new(StringComparer.OrdinalIgnoreCase);
    private (string Mob, DateTime At)? _lastKill;
    private bool _dirty;

    private Func<(string? Factions, string? Achievements)>? _finder;
    private DateTime _lastProbe = DateTime.MinValue;
    private (string? F, DateTime Fm, string? A, DateTime Am) _loaded;

    public event Action? Changed;
    /// <summary>A live faction line: faction, delta, the mob it came from (null when no kill preceded).</summary>
    public event Action<string, int, string?>? FactionHit;
    /// <summary>"could not possibly get any better" — once per faction per session.</summary>
    public event Action<string>? FactionMaxed;

    public RaceBook(ConfigService? config, string? pathOverride = null)
    {
        _path = pathOverride ?? (config is null ? null : Path.Combine(config.ConfigDirectory, "races.json"));
        Load();
    }

    /// <summary>When the factions dump was written — the baseline the live deltas sit on.</summary>
    public DateTime? DumpAt { get; private set; }
    public DateTime? AchievementsAt { get; private set; }
    public bool HasDumps => _races.Count > 0;
    public string? FactionsPath => _loaded.F;
    public string? AchievementsPath => _loaded.A;

    // ---- dumps --------------------------------------------------------------------

    /// <summary>Where the dumps live (the host knows the log's folder); probed
    /// on <see cref="RefreshDumps"/>, re-read when a file's clock moves.</summary>
    public void UseFinder(Func<(string? Factions, string? Achievements)> finder)
    {
        _finder = finder;
        RefreshDumps(force: true);
    }

    /// <summary>Re-read the dumps if they changed (throttled to one probe per 5 s). True when something reloaded.</summary>
    public bool RefreshDumps(bool force = false)
    {
        if (_finder is null) return false;
        var now = DateTime.Now;
        if (!force && (now - _lastProbe).TotalSeconds < 5) return false;
        _lastProbe = now;
        try
        {
            var (f, a) = _finder();
            var fm = f is not null && File.Exists(f) ? File.GetLastWriteTime(f) : DateTime.MinValue;
            var am = a is not null && File.Exists(a) ? File.GetLastWriteTime(a) : DateTime.MinValue;
            if (f == _loaded.F && fm == _loaded.Fm && a == _loaded.A && am == _loaded.Am) return false;
            LoadDumpFiles(f, a);
            _loaded = (f, fm, a, am);
            return true;
        }
        catch (Exception ex) { Log.Warn("race dumps: " + ex.Message); return false; }
    }

    public void LoadDumpFiles(string? factionsPath, string? achievementsPath)
    {
        string? ft = factionsPath is not null && File.Exists(factionsPath) ? File.ReadAllText(factionsPath) : null;
        string? at = achievementsPath is not null && File.Exists(achievementsPath) ? File.ReadAllText(achievementsPath) : null;
        LoadDumpText(ft, at,
            factionsPath is not null && File.Exists(factionsPath) ? File.GetLastWriteTime(factionsPath) : null,
            achievementsPath is not null && File.Exists(achievementsPath) ? File.GetLastWriteTime(achievementsPath) : null);
    }

    /// <summary>The parsed dumps; <paramref name="factionsAt"/> is the baseline
    /// — live deltas before it are already inside the dump and are dropped.</summary>
    public void LoadDumpText(string? factionsText, string? achievementsText, DateTime? factionsAt, DateTime? achievementsAt = null)
    {
        if (factionsText is not null)
        {
            _standings = FactionDumps.ParseFactions(factionsText);
            DumpAt = factionsAt;
            // A fresh dump absorbs what the log added since the last one.
            var stale = _deltaKeys.Where(k => DateTime.TryParse(k[..k.IndexOf('|')], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) && factionsAt is { } fa && t <= fa).ToList();
            if (stale.Count > 0) { _delta.Clear(); _deltaKeys.Clear(); }
            _maxed.Clear();
        }
        if (achievementsText is not null)
        {
            _races = FactionDumps.ParseRaces(achievementsText);
            AchievementsAt = achievementsAt;
        }
        Log.Info($"race dumps: {_standings.Count} factions (written {DumpAt:dd MMM HH:mm}), {_races.Count} races (written {AchievementsAt:dd MMM HH:mm}), {_tracked.Count} tracked, {_caps.Count} learned caps");
        Changed?.Invoke();
    }

    // ---- the log ------------------------------------------------------------------

    public void ProcessLine(string line, bool live = true)
    {
        if (line.Length < 28 || line[0] != '[') return;
        int close = line.IndexOf(']');
        if (close < 0) return;
        string body = line[(close + 1)..].TrimStart();
        DateTime time = ParseTime(line) ?? DateTime.Now;

        var k = KillRx.Match(body);
        if (k.Success) { _lastKill = (k.Groups["m"].Value, time); return; }
        if (body.Contains(" has been slain by ", StringComparison.Ordinal) && SlainByRx.Match(body) is { Success: true } sb)
        {
            // "a Dreadguard has been slain by Jobtik!" — your pet / group got the kill.
            string victim = sb.Groups["m"].Value;
            if (victim.StartsWith("A ", StringComparison.Ordinal) || victim.StartsWith("An ", StringComparison.Ordinal))
                victim = char.ToLowerInvariant(victim[0]) + victim[1..]; // the sentence-initial capital
            _lastKill = (victim, time);
            return;
        }
        if (!body.StartsWith("Your faction standing with ", StringComparison.Ordinal)) return;

        var a = AdjustRx.Match(body);
        if (a.Success)
        {
            string faction = a.Groups["f"].Value;
            int n = int.Parse(a.Groups["n"].Value);
            string? mob = _lastKill is { } lk && (time - lk.At).TotalSeconds is >= 0 and <= KillPairSec ? lk.Mob : null;
            if (mob is not null)
            {
                if (!_hits.TryGetValue(faction, out var byMob)) _hits[faction] = byMob = new Dictionary<string, HitRec>(StringComparer.OrdinalIgnoreCase);
                if (!byMob.TryGetValue(mob, out var rec)) byMob[mob] = rec = new HitRec();
                rec.Hit = n; rec.Count++; rec.Last = time;
                _dirty = true;
            }
            if (DumpAt is null || time > DumpAt.Value)
            {
                string key = $"{time:O}|{faction}|{n}";
                if (_deltaKeys.Add(key))
                {
                    if (_deltaKeys.Count > 50_000) _deltaKeys.Clear();
                    _delta[faction] = _delta.GetValueOrDefault(faction) + n;
                    if (n > 0) _maxed.Remove(faction);
                }
            }
            if (live)
            {
                if (_dirty) Save();
                if (_loggedHit.Add(faction))
                    Log.Info($"faction: {faction} {n:+0;-0} → {Standing(faction)}{(TrackedRaceOf(faction) is { } tr ? $" (tracked: {tr})" : RaceOf(faction) is { } rr ? $" ({rr}, not tracked)" : "")}{(DumpAt is null ? " — no factions dump yet" : "")}");
                FactionHit?.Invoke(faction, n, mob);
                Changed?.Invoke();
            }
            return;
        }

        var c = CapRx.Match(body);
        if (c.Success && c.Groups["dir"].Value == "better")
        {
            string faction = c.Groups["f"].Value;
            if (DumpAt is null || time > DumpAt.Value)
            {
                _maxed.Add(faction);
                // The standing right now IS your ceiling — remembered, so the
                // next dump (which clears the session's MAXED marks) keeps it.
                if (Known(faction) && _caps.GetValueOrDefault(faction, int.MinValue) != Standing(faction))
                {
                    _caps[faction] = Standing(faction);
                    Log.Info($"faction: {faction} capped at {Standing(faction)} for you");
                    Save();
                }
            }
            if (live && _maxedSaid.Add(faction) && RaceOf(faction) is not null)
            {
                FactionMaxed?.Invoke(faction);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>After a replay: persist the mob → faction hits it learned.</summary>
    public void SaveLearned() { if (_dirty) Save(); }

    // ---- the view ----------------------------------------------------------------------

    public int Standing(string faction) =>
        (_standings.TryGetValue(faction, out var s) ? s.Value : 0) + _delta.GetValueOrDefault(faction);

    public int MaxOf(string faction) => _standings.TryGetValue(faction, out var s) && s.Max > 0 ? s.Max : 2000;
    public bool Known(string faction) => _standings.ContainsKey(faction);
    public bool IsMaxed(string faction) => _maxed.Contains(faction)
        || (Known(faction) && (Standing(faction) >= MaxOf(faction) || _caps.TryGetValue(faction, out int cap) && Standing(faction) >= cap));

    /// <summary>Your learned ceiling for a faction (null until the game said "could not possibly get any better").</summary>
    public int? CapOf(string faction) => _caps.TryGetValue(faction, out int c) ? c : null;

    public IReadOnlyList<HitSource> SourcesOf(string faction) =>
        _hits.TryGetValue(faction, out var byMob)
            ? byMob.OrderByDescending(kv => kv.Value.Hit > 0).ThenByDescending(kv => kv.Value.Count)
                .Select(kv => new HitSource(kv.Key, kv.Value.Hit, kv.Value.Count, kv.Value.Last)).ToList()
            : new List<HitSource>();

    /// <summary>Kills left at the usual positive hit of the mob you farm most — null without a positive source.</summary>
    public int? EstimateKills(string faction)
    {
        var best = SourcesOf(faction).FirstOrDefault(s => s.Hit > 0);
        if (best is null) return null;
        int toGo = Math.Max(0, MaxOf(faction) - Standing(faction));
        return (int)Math.Ceiling(toGo / (double)best.Hit);
    }

    public FactionView ViewOf(string faction, bool doneInDump = false) =>
        new(faction, Standing(faction), MaxOf(faction), doneInDump || IsMaxed(faction), SourcesOf(faction), EstimateKills(faction), Known(faction), CapOf(faction));

    /// <summary>Every race, done first, then by progress.</summary>
    public IReadOnlyList<RaceView> Views()
    {
        var list = new List<RaceView>();
        foreach (var r in _races)
        {
            var factions = r.Factions.Select(f => ViewOf(f.Faction, f.Done)).ToList();
            bool done = r.Done || (factions.Count > 0 && factions.All(f => f.Done)) || (r.Task is not null && r.TaskDone);
            string note = r.BornAs ? "YOU" : r.DependsOn is not null ? (done ? "AUTO" : "") : "";
            list.Add(new RaceView(r.Name, done, note, factions, r.Task, r.DependsOn, _tracked.Contains(r.Name)));
        }
        // Done first, then progress, then the fewest points still to earn, then the name.
        return list.OrderByDescending(v => v.Done).ThenByDescending(v => v.Progress)
            .ThenBy(v => v.Factions.Sum(f => f.Done ? 0 : f.ToGo))
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public RaceView? View(string race) => Views().FirstOrDefault(v => v.Name.Equals(race, StringComparison.OrdinalIgnoreCase));

    /// <summary>The race whose recipe names this faction (first match), or null.</summary>
    public string? RaceOf(string faction) =>
        _races.FirstOrDefault(r => r.Factions.Any(f => f.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase)))?.Name;

    /// <summary>The ★-tracked race this faction belongs to, or null — the helper card speaks only for these.</summary>
    public string? TrackedRaceOf(string faction) =>
        _races.Where(r => _tracked.Contains(r.Name))
            .FirstOrDefault(r => r.Factions.Any(f => f.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase)))?.Name;

    public bool IsTracked(string race) => _tracked.Contains(race);
    public IReadOnlyCollection<string> Tracked => _tracked;

    public void SetTracked(string race, bool on)
    {
        bool changed = on ? _tracked.Add(race) : _tracked.Remove(race);
        if (!changed) return;
        Save();
        Changed?.Invoke();
    }

    /// <summary>"2 h ago" / "3 days ago" for the dump's age — "" without one.</summary>
    public static string Age(DateTime? at)
    {
        if (at is null) return "";
        var span = DateTime.Now - at.Value;
        if (span.TotalMinutes < 90) return $"{Math.Max(1, (int)span.TotalMinutes)} min ago";
        if (span.TotalHours < 36) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} days ago";
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static DateTime? ParseTime(string line)
    {
        int close = line.IndexOf(']');
        if (close < 5) return null;
        return DateTime.TryParseExact(line[1..close], "ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var t) ? t : null;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(_path), JsonOpts);
            if (doc is null) return;
            foreach (var t in doc.Tracked) _tracked.Add(t);
            foreach (var (f, byMob) in doc.Hits)
                _hits[f] = new Dictionary<string, HitRec>(byMob, StringComparer.OrdinalIgnoreCase);
            foreach (var (f, c) in doc.Caps) _caps[f] = c;
        }
        catch (Exception ex) { Log.Warn("races.json unreadable: " + ex.Message); }
    }

    private void Save()
    {
        _dirty = false;
        if (_path is null) return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new Doc
            {
                Tracked = _tracked.OrderBy(x => x).ToList(),
                Hits = _hits.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                Caps = new Dictionary<string, int>(_caps, StringComparer.OrdinalIgnoreCase),
            }, JsonOpts));
        }
        catch (Exception ex) { Log.Warn("races.json not saved: " + ex.Message); }
    }
}
