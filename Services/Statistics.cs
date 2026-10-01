using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Your log, counted (owner, 1 Oct: "most killed mobs, most used spells, most
/// killed raid targets, most looted items, most visited zone — any stat that
/// could be fun"). Every line lands in the DAY it happened — kills, casts,
/// loot, zone visits and time, deaths and who dealt them, skill-ups, hits and
/// crits, damage and healing, dings, sessions, the hour-of-day histogram — so
/// the Statistics page can show all time, the last 30 or 7 days, or today.
/// Filled by the live feed, the catch-up and Data → Reparse (merged logs too);
/// DEDUPE is a per-day bitmap of MINUTES already counted — a minute counted
/// once is never counted from another pass, and no two logs of one character
/// share a minute unless they are copies. Persisted in <c>stats.json</c>.
/// </summary>
public sealed class Statistics
{
    public sealed class Session { public DateTime Start { get; set; } public DateTime End { get; set; } }
    public sealed class Record { public long Value { get; set; } public string What { get; set; } = ""; public string Target { get; set; } = ""; public DateTime At { get; set; } }

    public sealed class Day
    {
        public Dictionary<string, int> Kills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>"X has been slain by Y!" — kills the log credits to someone (your pet, a groupmate); raid bosses read from here too.</summary>
        public Dictionary<string, int> OtherKills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>"mob|zone" → kills, for the zone a mob is mostly killed in.</summary>
        public Dictionary<string, int> KillZones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Casts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Loot { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> LootMobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Visits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> ZoneSec { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Killers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int[] Hours { get; set; } = new int[24];
        public int[] KillHours { get; set; } = new int[24];
        public int Minutes { get; set; }
        public int Interrupted { get; set; } public int Resisted { get; set; } public int Oom { get; set; } public int Deaths { get; set; }
        public int Hails { get; set; } public int Says { get; set; } public int Tells { get; set; } public int Stunned { get; set; } public int Xp { get; set; }
        public int Combines { get; set; } public int Fails { get; set; } public int MezBreaks { get; set; } public int Hits { get; set; } public int Crits { get; set; }
        public long Damage { get; set; } public long Healed { get; set; }
        public List<int> Dings { get; set; } = new();
        public List<Session> Sessions { get; set; } = new();
        public Record? BigHit { get; set; }
        /// <summary>1440 minute bits, base64 — the minutes already counted.</summary>
        public string Seen { get; set; } = "";
    }

    private sealed class Doc { public Dictionary<string, Day> Days { get; set; } = new(); }
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string? _path;
    private readonly Dictionary<string, Day> _days = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _seen = new(StringComparer.Ordinal);
    private int _dirty;
    private DateTime _lastSave = DateTime.MinValue;

    // the pass in progress: the minute whose lines are flowing, the zone we're in, the last line's time
    private string _curMinuteKey = ""; private string _curDay = ""; private int _curMinute = -1;
    private string _zone = ""; private DateTime _lastTime; private DateTime _lastDeath = DateTime.MinValue;
    private Session? _lastSession;

    public string SelfName { get; set; } = "You";
    public IReadOnlyDictionary<string, Day> Days => _days;

    public Statistics(string? path)
    {
        _path = path;
        try
        {
            if (path is not null && File.Exists(path) && JsonSerializer.Deserialize<Doc>(File.ReadAllText(path), JsonOpts) is { } doc)
                foreach (var (k, d) in doc.Days)
                {
                    _days[k] = d;
                    if (d.Seen.Length > 0) { try { _seen[k] = Convert.FromBase64String(d.Seen); } catch { /* a bad bitmap just counts again */ } }
                    foreach (var se in d.Sessions) if (_lastSession is null || se.End > _lastSession.End) _lastSession = se;
                }
        }
        catch (Exception ex) { Log.Warn("statistics load: " + ex.Message); }
    }

    /// <summary>Data → Reset & rebuild: forget everything before the full reparse.</summary>
    public void Reset() { _days.Clear(); _seen.Clear(); _curMinuteKey = ""; _zone = ""; _lastTime = default; _lastSession = null; Save(); }

    /// <summary>Instances fold into their zone: "The Ruins of Old Guk 1 (Awakened)" → "The Ruins of Old Guk".</summary>
    public static string FoldZone(string zone) => InstanceRx.Replace(zone.Trim(), "");
    private static readonly Regex InstanceRx = new(@"\s+\d+\s+\([^)]*\)$", RegexOptions.Compiled);

    private static readonly Regex TsRx = new(@"^\[(?<t>\w{3} \w{3} +\d{1,2} \d\d:\d\d:\d\d \d{4})\] (?<b>.*)$", RegexOptions.Compiled);
    private static readonly Regex SpellHitRx = new(@"^You hit (?<t>.+?) for (?<n>\d+) points of .+? damage by (?<s>.+?)\.(?<c> \(Critical\))?$", RegexOptions.Compiled);
    private static readonly Regex MeleeRx = new(@"^You (?<v>[a-z]+) (?<t>.+?) for (?<n>\d+) points of damage\.(?<c> \(Critical\))?$", RegexOptions.Compiled);
    private static readonly Regex HealRx = new(@"^(?<h>You|\w+) healed (?<t>.+?) for (?<n>\d+) hit points", RegexOptions.Compiled);
    private static readonly Regex SkillRx = new(@"^You have become better at (?<s>.+?)! \((?<n>\d+)\)$", RegexOptions.Compiled);
    private static readonly Regex DingRx = new(@"^You have gained a level! Welcome to level (?<l>\d+)!", RegexOptions.Compiled);
    private static readonly Regex OtherSlainRx = new(@"^(?<m>.+?) has been slain by (?<w>.+?)!$", RegexOptions.Compiled);

    public void ProcessLine(string line)
    {
        var m = TsRx.Match(line);
        if (!m.Success) return;
        if (!DateTime.TryParseExact(m.Groups["t"].Value, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var t)) return;
        string b = m.Groups["b"].Value;
        string dayKey = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int minute = t.Hour * 60 + t.Minute;
        string minuteKey = dayKey + ":" + minute;

        // The minute gate: a new minute closes the one before it; a minute already closed is a repeat.
        if (minuteKey != _curMinuteKey)
        {
            if (_curMinuteKey.Length > 0) MarkSeen(_curDay, _curMinute);
            if (IsSeen(dayKey, minute)) { _curMinuteKey = ""; return; }
            _curMinuteKey = minuteKey; _curDay = dayKey; _curMinute = minute;
            var dd = DayOf(dayKey); dd.Minutes++;
        }
        else if (_curMinuteKey.Length == 0) return;
        var d = DayOf(dayKey);
        d.Hours[t.Hour]++;

        // Sessions: a line within 30 min of the last extends; else a new one starts.
        if (_lastTime != default && (t - _lastTime).TotalMinutes <= 30 && _lastSession is not null)
        {
            if (t > _lastSession.End) _lastSession.End = t;
            if (_zone.Length > 0 && (t - _lastTime).TotalSeconds < 600 && t > _lastTime)
                d.ZoneSec[_zone] = d.ZoneSec.GetValueOrDefault(_zone) + (t - _lastTime).TotalSeconds;
        }
        else { _lastSession = new Session { Start = t, End = t }; d.Sessions.Add(_lastSession); }
        if (t > _lastTime) _lastTime = t;

        Match x;
        if (b.StartsWith("You have slain ", StringComparison.Ordinal) && b.EndsWith("!", StringComparison.Ordinal))
        {
            string mob = b[15..^1];
            Bump(d.Kills, mob); d.KillHours[t.Hour]++;
            if (_zone.Length > 0) Bump(d.KillZones, mob + "|" + _zone);
        }
        else if (b.EndsWith("!", StringComparison.Ordinal) && b.Contains(" has been slain by ", StringComparison.Ordinal) && (x = OtherSlainRx.Match(b)).Success)
        {
            if (x.Groups["w"].Value.Equals(SelfName, StringComparison.OrdinalIgnoreCase)) { Bump(d.Kills, x.Groups["m"].Value); d.KillHours[t.Hour]++; }
            else Bump(d.OtherKills, x.Groups["m"].Value);
        }
        else if (b.StartsWith("You begin casting ", StringComparison.Ordinal) || b.StartsWith("You begin singing ", StringComparison.Ordinal))
            Bump(d.Casts, SpellDurations.BaseName(b[18..].TrimEnd('.')).Trim());
        else if (b.StartsWith("You have entered ", StringComparison.Ordinal) && b.EndsWith(".", StringComparison.Ordinal))
        {
            _zone = FoldZone(b[17..^1]);
            Bump(d.Visits, _zone);
        }
        else if (b.StartsWith("You looted ", StringComparison.Ordinal) || b.StartsWith("--You have looted ", StringComparison.Ordinal))
        {
            if (LootTracker.TryParseLoot(b, out _, out string item, out string mob, out _, out _, out int count) && item.Length > 0)
            {
                Bump(d.Loot, StripTier(StripArticle(item)), Math.Max(1, count));
                if (mob.Length > 0) Bump(d.LootMobs, mob);
            }
        }
        else if (b.StartsWith("You have been slain by ", StringComparison.Ordinal) && b.EndsWith("!", StringComparison.Ordinal))
        {
            if ((t - _lastDeath).TotalSeconds >= 5) { d.Deaths++; Bump(d.Killers, b[23..^1]); }
            _lastDeath = t;
        }
        else if (b == "You died.") { if ((t - _lastDeath).TotalSeconds >= 5) d.Deaths++; _lastDeath = t; }
        else if (b == "Insufficient Mana to cast this spell!") d.Oom++;
        else if (b.EndsWith(" spell is interrupted.", StringComparison.Ordinal)) d.Interrupted++;
        else if (b.EndsWith("!", StringComparison.Ordinal) && b.Contains(" resisted your ", StringComparison.Ordinal)) d.Resisted++;
        else if (b.StartsWith("You say, '", StringComparison.Ordinal)) { d.Says++; if (b.StartsWith("You say, 'Hail", StringComparison.Ordinal)) d.Hails++; }
        else if (b.StartsWith("You told ", StringComparison.Ordinal)) d.Tells++;
        else if (b == "You are stunned!") d.Stunned++;
        else if (b.StartsWith("You have gained a level!", StringComparison.Ordinal) && (x = DingRx.Match(b)).Success) d.Dings.Add(int.Parse(x.Groups["l"].Value));
        else if (b.StartsWith("You gain experience", StringComparison.Ordinal) || b.StartsWith("You gain party experience", StringComparison.Ordinal)) d.Xp++;
        else if (b.StartsWith("You have become better at ", StringComparison.Ordinal) && (x = SkillRx.Match(b)).Success) Bump(d.Skills, x.Groups["s"].Value);
        else if (b.StartsWith("You have fashioned", StringComparison.Ordinal)) d.Combines++;
        else if (b.StartsWith("You lacked the skills", StringComparison.Ordinal)) d.Fails++;
        else if (b.Contains(" has been awakened by ", StringComparison.Ordinal)) d.MezBreaks++;
        else if (b.StartsWith("You hit ", StringComparison.Ordinal) && (x = SpellHitRx.Match(b)).Success) Hit(d, long.Parse(x.Groups["n"].Value), x.Groups["s"].Value, x.Groups["t"].Value, x.Groups["c"].Success, t);
        else if (b.StartsWith("You ", StringComparison.Ordinal) && b.EndsWith("damage.", StringComparison.Ordinal) | b.EndsWith("(Critical)", StringComparison.Ordinal))
        {
            if ((x = MeleeRx.Match(b)).Success && x.Groups["v"].Value is not ("hit" or "have"))
                Hit(d, long.Parse(x.Groups["n"].Value), x.Groups["v"].Value, x.Groups["t"].Value, x.Groups["c"].Success, t);
        }
        if ((b.StartsWith("You healed ", StringComparison.Ordinal) || b.StartsWith(SelfName + " healed ", StringComparison.Ordinal)) && (x = HealRx.Match(b)).Success)
            d.Healed += long.Parse(x.Groups["n"].Value);

        if (++_dirty >= 400 || (DateTime.Now - _lastSave).TotalSeconds > 30) Save();
    }

    private void Hit(Day d, long n, string what, string target, bool crit, DateTime t)
    {
        d.Hits++; if (crit) d.Crits++; d.Damage += n;
        if (d.BigHit is null || n > d.BigHit.Value) d.BigHit = new Record { Value = n, What = what, Target = target, At = t };
    }

    private static readonly Regex TierRx = new(@" \+\d+$", RegexOptions.Compiled);
    private static string StripTier(string item) => TierRx.Replace(item.Trim(), "");
    /// <summary>The kept-loot line keeps the article ("--You have looted a Burnt Sash…"); the item is the rest.</summary>
    private static string StripArticle(string item)
    {
        foreach (var a in new[] { "a ", "an ", "the " })
            if (item.StartsWith(a, StringComparison.OrdinalIgnoreCase) && item.Length > a.Length && char.IsUpper(item[a.Length])) return item[a.Length..];
        return item;
    }
    private static void Bump(Dictionary<string, int> d, string k, int n = 1) { if (k.Length > 0) d[k] = d.GetValueOrDefault(k) + n; }

    private Day DayOf(string key)
    {
        if (!_days.TryGetValue(key, out var d)) _days[key] = d = new Day();
        return d;
    }

    private bool IsSeen(string day, int minute) => _seen.TryGetValue(day, out var bits) && (bits[minute >> 3] & (1 << (minute & 7))) != 0;
    private void MarkSeen(string day, int minute)
    {
        if (!_seen.TryGetValue(day, out var bits)) _seen[day] = bits = new byte[180];
        bits[minute >> 3] |= (byte)(1 << (minute & 7));
    }

    /// <summary>Close the minute in progress and write the file (also on shutdown).</summary>
    public void Save()
    {
        if (_curMinuteKey.Length > 0) MarkSeen(_curDay, _curMinute);
        _dirty = 0; _lastSave = DateTime.Now;
        if (_path is null) return;
        try
        {
            foreach (var (k, bits) in _seen) if (_days.TryGetValue(k, out var d)) d.Seen = Convert.ToBase64String(bits);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Doc { Days = _days }, JsonOpts));
        }
        catch (Exception ex) { Log.Warn("statistics save: " + ex.Message); }
    }

    // ---- the view's question --------------------------------------------------------------

    public sealed record Top(string Name, double N, string Note = "");

    public sealed class Summary
    {
        public DateTime? First, Last; public int DaysWithPlay; public double Hours;
        public long Kills, Casts, Loot, Deaths, Interrupted, Resisted, Oom, Hails, Says, Tells, Stunned, Xp, Combines, Fails, MezBreaks, Hits, Crits, Damage, Healed, Dings;
        public List<Top> TopKills = new(), TopCasts = new(), TopLoot = new(), TopLootMobs = new(), TopZones = new(), TopKillers = new(), TopSkills = new();
        public Dictionary<string, int> AllKills = new(StringComparer.OrdinalIgnoreCase), AllCasts = new(StringComparer.OrdinalIgnoreCase);
        public int[] Hours24 = new int[24]; public int[] Dow = new int[7]; // Dow: Monday first, in minutes
        public int Sessions; public Session? Longest; public Record? BigHit; public (int Count, DateTime Hour) BestKillHour;
        public List<(DateTime Day, List<int> Levels)> DingDays = new();
    }

    /// <summary>Everything from <paramref name="since"/> (null = all time), tops cut to <paramref name="top"/>.</summary>
    public Summary Summarize(DateTime? since, int top = 10)
    {
        var s = new Summary();
        var kills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var killZones = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var casts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var loot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lootMobs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var visits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var zoneSec = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); var killers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var killHours = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, d) in _days)
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if (since is { } sc && day < sc.Date) continue;
            if (d.Minutes == 0) continue;
            s.DaysWithPlay++; s.Hours += d.Minutes / 60.0;
            s.First = s.First is null || day < s.First ? day : s.First; s.Last = s.Last is null || day > s.Last ? day : s.Last;
            foreach (var (k, v) in d.Kills) { kills[k] = kills.GetValueOrDefault(k) + v; s.Kills += v; }
            foreach (var (k, v) in d.OtherKills) s.AllKills[k] = s.AllKills.GetValueOrDefault(k) + v;
            foreach (var (k, v) in d.KillZones) killZones[k] = killZones.GetValueOrDefault(k) + v;
            foreach (var (k, v) in d.Casts) { casts[k] = casts.GetValueOrDefault(k) + v; s.Casts += v; }
            foreach (var (k, v) in d.Loot) { loot[k] = loot.GetValueOrDefault(k) + v; s.Loot += v; }
            foreach (var (k, v) in d.LootMobs) lootMobs[k] = lootMobs.GetValueOrDefault(k) + v;
            foreach (var (k, v) in d.Visits) visits[k] = visits.GetValueOrDefault(k) + v;
            foreach (var (k, v) in d.ZoneSec) zoneSec[k] = zoneSec.GetValueOrDefault(k) + v;
            foreach (var (k, v) in d.Killers) killers[k] = killers.GetValueOrDefault(k) + v;
            foreach (var (k, v) in d.Skills) skills[k] = skills.GetValueOrDefault(k) + v;
            for (int h = 0; h < 24; h++) { s.Hours24[h] += d.Hours[h]; if (d.KillHours[h] > 0) killHours[key + " " + h] = d.KillHours[h]; }
            s.Dow[((int)day.DayOfWeek + 6) % 7] += d.Minutes;
            s.Deaths += d.Deaths; s.Interrupted += d.Interrupted; s.Resisted += d.Resisted; s.Oom += d.Oom; s.Hails += d.Hails; s.Says += d.Says; s.Tells += d.Tells;
            s.Stunned += d.Stunned; s.Xp += d.Xp; s.Combines += d.Combines; s.Fails += d.Fails; s.MezBreaks += d.MezBreaks; s.Hits += d.Hits; s.Crits += d.Crits;
            s.Damage += d.Damage; s.Healed += d.Healed; s.Dings += d.Dings.Count;
            if (d.Dings.Count > 0) s.DingDays.Add((day, d.Dings.ToList()));
            s.Sessions += d.Sessions.Count;
            foreach (var se in d.Sessions) if (s.Longest is null || se.End - se.Start > s.Longest.End - s.Longest.Start) s.Longest = se;
            if (d.BigHit is not null && (s.BigHit is null || d.BigHit.Value > s.BigHit.Value)) s.BigHit = d.BigHit;
        }
        foreach (var (k, v) in kills) s.AllKills[k] = s.AllKills.GetValueOrDefault(k) + v;
        s.AllCasts = casts;
        string ZoneOf(string mob)
        {
            string best = ""; int most = 0;
            foreach (var (k, v) in killZones) { int bar = k.IndexOf('|'); if (bar > 0 && k[..bar].Equals(mob, StringComparison.OrdinalIgnoreCase) && v > most) { most = v; best = k[(bar + 1)..]; } }
            return best;
        }
        s.TopKills = kills.OrderByDescending(kv => kv.Value).Take(top).Select(kv => new Top(kv.Key, kv.Value, ZoneOf(kv.Key))).ToList();
        s.TopCasts = casts.OrderByDescending(kv => kv.Value).Take(top).Select(kv => new Top(kv.Key, kv.Value)).ToList();
        s.TopLoot = loot.OrderByDescending(kv => kv.Value).Take(top).Select(kv => new Top(kv.Key, kv.Value)).ToList();
        s.TopLootMobs = lootMobs.OrderByDescending(kv => kv.Value).Take(3).Select(kv => new Top(kv.Key, kv.Value)).ToList();
        s.TopZones = zoneSec.OrderByDescending(kv => kv.Value).Take(top).Select(kv => new Top(kv.Key, kv.Value / 3600.0, $"{visits.GetValueOrDefault(kv.Key)} visit{(visits.GetValueOrDefault(kv.Key) == 1 ? "" : "s")}")).ToList();
        s.TopKillers = killers.OrderByDescending(kv => kv.Value).Take(top).Select(kv => new Top(kv.Key, kv.Value)).ToList();
        s.TopSkills = skills.OrderByDescending(kv => kv.Value).Take(top).Select(kv => new Top(kv.Key, kv.Value)).ToList();
        var bestHour = killHours.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (bestHour.Key is not null)
        {
            var parts = bestHour.Key.Split(' ');
            s.BestKillHour = (bestHour.Value, DateTime.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture).AddHours(int.Parse(parts[1])));
        }
        s.DingDays.Sort((a, b) => a.Day.CompareTo(b.Day));
        return s;
    }
}
