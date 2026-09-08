using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The per-mob resist table (owner pick, 8 Sep; Companion's resist-mining
/// plan, the light version): for every mob you cast on, per spell, how
/// many of YOUR casts landed and how many were resisted — straight from the
/// log's own words ("A greater sphinx resisted your Envenomed Breath!",
/// "You hit a lava guardian for 178 points of magic damage by Chaos Flux.",
/// the debuff landing suffix after your cast). No model, no intervals: a
/// rate and a sample size, and a verdict only past a sample floor. Levels
/// ride along from /con lines, the zone from the parser. Every event is
/// keyed by second + spell + mob so replay and merge never double-count.
/// </summary>
public sealed class ResistBook
{
    public sealed class Cell
    {
        public string Mob { get; set; } = "";
        public string Spell { get; set; } = "";
        public string School { get; set; } = "";
        public int Landed { get; set; }
        public int Resisted { get; set; }
        public int Level { get; set; }
        public string Zone { get; set; } = "";
        public DateTime Last { get; set; }

        public int N => Landed + Resisted;
        public double Rate => N == 0 ? 0 : (double)Resisted / N;
    }

    /// <summary>What a mob is known to shrug off, past the sample floor.</summary>
    public sealed record Verdict(string Mob, string Spell, string School, double Rate, int N, string Severity);

    private sealed class Doc
    {
        public List<Cell> Cells { get; set; } = new();
        public List<string> Seen { get; set; } = new();
        public Dictionary<string, int> Levels { get; set; } = new();
    }

    /// <summary>Verdicts need at least this many casts on the (mob, spell).</summary>
    public const int SampleFloor = 5;
    private const int SeenCap = 120_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly Regex TimestampPrefix = new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);
    private static readonly Regex ConRx = new(
        @"^(?<mob>.+?) (?:scowls at you|glowers at you|glares at you|regards you|looks upon you|judges you|kindly considers you|looks)\b.*\(Lvl: (?<lvl>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _path;
    private readonly CombatParser? _parser;
    private readonly Dictionary<string, Cell> _cells = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _levels = new(StringComparer.OrdinalIgnoreCase);
    private int _dirty;

    public event Action? Changed;

    /// <summary>A /con line went by: (mob, level). The con card listens.</summary>
    public event Action<string, int>? Conned;

    /// <summary>Every cast the book holds on a mob.</summary>
    public int CastsOn(string mob) => ForMob(mob).Sum(c => c.N);

    public ResistBook(ConfigService config, CombatParser? parser = null, string? pathOverride = null)
    {
        _path = pathOverride ?? Path.Combine(config.ConfigDirectory, "resists.json");
        _parser = parser;
        Load();
        if (parser is not null)
        {
            parser.OwnSpellLanded += (spell, target, school, time) => Record(false, spell, target, school, time);
            parser.OwnSpellResisted += (spell, target, time) => Record(true, spell, target, "", time);
        }
    }

    public IReadOnlyCollection<Cell> Cells => _cells.Values;

    /// <summary>Only the /con lines matter here — everything else arrives as
    /// parser events. Levels outlive fights and sessions.</summary>
    public void ProcessLine(string rawLine)
    {
        string body = TimestampPrefix.Replace(rawLine, "", 1);
        if (!body.Contains("(Lvl: ", StringComparison.Ordinal)) return;
        var m = ConRx.Match(body);
        if (!m.Success) return;
        string mob = MobKey(m.Groups["mob"].Value);
        int lvl = int.Parse(m.Groups["lvl"].Value);
        Conned?.Invoke(mob, lvl);
        if (_levels.GetValueOrDefault(mob) == lvl) return;
        _levels[mob] = lvl;
        foreach (var c in _cells.Values)
            if (c.Mob.Equals(mob, StringComparison.OrdinalIgnoreCase)) c.Level = lvl;
        MarkDirty();
    }

    /// <summary>"A greater sphinx" (line start) and "a greater sphinx" (mid
    /// line) are one mob: articles lower-case, the rest as printed.</summary>
    public static string MobKey(string name)
    {
        name = name.Trim();
        foreach (var art in new[] { "A ", "An ", "The " })
            if (name.StartsWith(art, StringComparison.Ordinal))
                return char.ToLowerInvariant(name[0]) + name[1..];
        return name;
    }

    private void Record(bool resisted, string spell, string target, string school, DateTime time)
    {
        string mob = MobKey(target);
        string spellKey = SpellDurations.BaseName(spell).Trim();
        if (mob.Length == 0 || spellKey.Length == 0) return;
        string key = $"{time:yyMMddHHmmss}|{(resisted ? 'r' : 'l')}|{spellKey.ToLowerInvariant()}|{mob.ToLowerInvariant()}";
        if (!_seen.Add(key)) return; // replay / merge
        if (_seen.Count > SeenCap) _seen.Remove(_seen.First());

        string ck = mob.ToLowerInvariant() + "|" + spellKey.ToLowerInvariant();
        if (!_cells.TryGetValue(ck, out var cell))
        {
            _cells[ck] = cell = new Cell { Mob = mob, Spell = spellKey, Level = _levels.GetValueOrDefault(mob) };
        }
        if (resisted) cell.Resisted++; else cell.Landed++;
        if (school.Length > 0) cell.School = school;
        else if (cell.School.Length == 0 && _parser is not null) cell.School = _parser.SchoolOf(spellKey);
        if (time >= cell.Last)
        {
            cell.Last = time;
            string zone = _parser?.CurrentZone ?? "";
            if (zone.Length > 0) cell.Zone = zone;
        }
        MarkDirty();
        Changed?.Invoke();
    }

    /// <summary>Cells for a mob (any zone), newest first.</summary>
    public IReadOnlyList<Cell> ForMob(string mob) =>
        _cells.Values.Where(c => c.Mob.Equals(MobKey(mob), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Rate).ThenByDescending(c => c.N).ToList();

    /// <summary>Mobs with their cells, newest mob first; <paramref name="zone"/>
    /// narrows to one zone ("" = all).</summary>
    public IReadOnlyList<(string Mob, int Level, string Zone, IReadOnlyList<Cell> Cells)> ByMob(string zone = "", string search = "")
    {
        return _cells.Values
            .Where(c => zone.Length == 0 || c.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase))
            .Where(c => search.Length == 0 || c.Mob.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || c.Spell.Contains(search, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Mob, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Mob: g.Key, Level: g.Max(c => c.Level), Zone: g.OrderByDescending(c => c.Last).First().Zone,
                Cells: (IReadOnlyList<Cell>)g.OrderByDescending(c => c.Rate).ThenByDescending(c => c.N).ToList()))
            .OrderByDescending(x => x.Cells.Max(c => c.Last))
            .ToList();
    }

    /// <summary>The spells a mob shrugs off, past the sample floor: ≥60% resisted
    /// reads "nearly immune", ≥30% "resistant". Nothing below that is a verdict.</summary>
    public IReadOnlyList<Verdict> Notable(string mob)
    {
        var list = new List<Verdict>();
        foreach (var c in ForMob(mob))
        {
            if (c.N < SampleFloor) continue;
            string sev = c.Rate >= 0.6 ? "immune" : c.Rate >= 0.3 ? "resistant" : "";
            if (sev.Length == 0) continue;
            list.Add(new Verdict(c.Mob, c.Spell, c.School, c.Rate, c.N, sev));
        }
        return list.OrderByDescending(v => v.Rate).ToList();
    }

    public static string Severity(double rate, int n) =>
        n < SampleFloor ? "" : rate >= 0.6 ? "immune" : rate >= 0.3 ? "resistant" : "fine";

    // ---------------------------------------------------------------- persistence

    private void MarkDirty()
    {
        if (++_dirty >= 1) { Save(); _dirty = 0; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(_path), JsonOpts);
            if (doc is null) return;
            foreach (var c in doc.Cells) _cells[c.Mob.ToLowerInvariant() + "|" + c.Spell.ToLowerInvariant()] = c;
            foreach (var s in doc.Seen) _seen.Add(s);
            foreach (var (k, v) in doc.Levels) _levels[k] = v;
        }
        catch (Exception ex) { Log.Warn($"resists.json failed to load: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            var doc = new Doc { Cells = _cells.Values.ToList(), Seen = _seen.ToList(), Levels = new Dictionary<string, int>(_levels) };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(doc, JsonOpts));
        }
        catch (Exception ex) { Log.Warn($"resists.json failed to save: {ex.Message}"); }
    }

    public void ResetAll()
    {
        _cells.Clear(); _seen.Clear(); _levels.Clear();
        Save();
        Changed?.Invoke();
    }
}
