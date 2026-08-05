using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Tracks kills of known raid targets, grouped by zone tier. Targets live in a
/// user-editable raid-targets.json (a default EQ Legends list is written on
/// first run); kills are detected from death lines and persisted to
/// raid-kills.json so progression survives restarts.
/// </summary>
public sealed class RaidKills
{
    public sealed class Tier
    {
        public string Name { get; set; } = "";
        public List<string> Targets { get; set; } = new();
    }

    public sealed record TargetView(string Name, int Count, DateTime? Last);
    public sealed record TierView(string Name, List<TargetView> Targets)
    {
        public int Killed => Targets.Count(t => t.Count > 0);
        public bool Cleared => Targets.Count > 0 && Killed == Targets.Count;
    }

    private readonly string _targetsPath;
    private readonly string _killsPath;
    private readonly List<Tier> _tiers = new();
    private readonly HashSet<string> _targetSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> _kills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the caller's thread when a listed raid target dies.</summary>
    public event Action<string, DateTime>? KillRecorded;

    /// <summary>The last few mob deaths of ANY kind — used as a picker when adding respawns.</summary>
    public sealed record RecentDeath(string Name, DateTime When);

    private const int MaxRecentDeaths = 10;
    private readonly List<RecentDeath> _recentDeaths = new();
    public IReadOnlyList<RecentDeath> RecentDeaths => _recentDeaths;

    private static readonly Regex SlainRx = new(
        @"^(?<mob>.+?) has been slain by .+?!", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex YouSlewRx = new(
        @"^You have slain (?<mob>.+?)!", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimestampPrefix = new(@"^\[.+?\]\s?", RegexOptions.Compiled);

    private static readonly Regex LevelSuffix = new(@"\s*\(\d+\)$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public RaidKills(ConfigService config)
    {
        _targetsPath = Path.Combine(config.ConfigDirectory, "raid-targets.json");
        _killsPath = Path.Combine(config.ConfigDirectory, "raid-kills.json");
        LoadTargets();
        LoadKills();
    }

    public IReadOnlyList<TierView> GetView() =>
        _tiers.Select(t => new TierView(t.Name, t.Targets.Select(name =>
        {
            _kills.TryGetValue(name, out var when);
            return new TargetView(name, when?.Count ?? 0,
                when is { Count: > 0 } ? when.Max() : null);
        }).ToList())).ToList();

    public int TotalTargets => _tiers.Sum(t => t.Targets.Count);
    public int TotalKilled => _tiers.Sum(t => t.Targets.Count(x => _kills.ContainsKey(x)));

    public void ProcessLine(string rawLine)
    {
        string body = TimestampPrefix.Replace(rawLine, "", 1);
        if (!TryParseKill(body, out string mob)) return;

        // Every death lands in the recent list (latest kill of a name wins).
        _recentDeaths.RemoveAll(d => d.Name.Equals(mob, StringComparison.OrdinalIgnoreCase));
        _recentDeaths.Insert(0, new RecentDeath(mob, DateTime.Now));
        if (_recentDeaths.Count > MaxRecentDeaths)
            _recentDeaths.RemoveAt(MaxRecentDeaths);

        if (!_targetSet.TryGetValue(mob, out string? canonical)) return;

        var when = DateTime.Now;
        if (!_kills.TryGetValue(canonical, out var list))
            _kills[canonical] = list = new List<DateTime>();
        list.Add(when);
        SaveKills();
        Log.Info($"Raid target down: {canonical}");
        KillRecorded?.Invoke(canonical, when);
    }

    /// <summary>Extract the victim from a death line ("(17)" level suffixes stripped).</summary>
    public static bool TryParseKill(string body, out string mob)
    {
        var m = SlainRx.Match(body);
        if (!m.Success) m = YouSlewRx.Match(body);
        mob = m.Success ? LevelSuffix.Replace(m.Groups["mob"].Value.Trim(), "") : "";
        return mob.Length > 0;
    }

    // ---- persistence ---------------------------------------------------------

    private void LoadTargets()
    {
        try
        {
            if (!File.Exists(_targetsPath))
                File.WriteAllText(_targetsPath, DefaultTargetsJson);
            var tiers = JsonSerializer.Deserialize<List<Tier>>(File.ReadAllText(_targetsPath), JsonOpts);
            if (tiers is not null) _tiers.AddRange(tiers);
        }
        catch { /* corrupt/unreadable targets file -> empty tracker rather than crash */ }

        foreach (var t in _tiers)
            foreach (var name in t.Targets)
                _targetSet.Add(name);
    }

    private void LoadKills()
    {
        try
        {
            if (!File.Exists(_killsPath)) return;
            var kills = JsonSerializer.Deserialize<Dictionary<string, List<DateTime>>>(
                File.ReadAllText(_killsPath), JsonOpts);
            if (kills is null) return;
            foreach (var (name, list) in kills)
                _kills[name] = list;
        }
        catch { /* ignore */ }
    }

    private void SaveKills()
    {
        try { File.WriteAllText(_killsPath, JsonSerializer.Serialize(_kills, JsonOpts)); }
        catch { /* best-effort */ }
    }

    // Default target list (zones + names as tracked by the EQ Legends community).
    // Edit raid-targets.json in the config folder to add/remove targets.
    private const string DefaultTargetsJson = """
    [
      {
        "name": "Open World",
        "targets": [ "Lord Nagafen", "Lady Vox", "Master Yael" ]
      },
      {
        "name": "Plane of Fear",
        "targets": [ "Cazic Thule", "Dread", "Fright", "Terror", "A dracoliche" ]
      },
      {
        "name": "Plane of Hate",
        "targets": [
          "Innoruuk", "Maestro of Rancor", "Lord of Loathing", "Lord of Ire",
          "Master of Spite", "Mistress of Scorn", "High Priest M'kari", "Magi P'tasa",
          "Coercer T'vala", "Grandmaster R'Tal", "Ashenbone Broodmaster", "Avatar of Abhorrence"
        ]
      },
      {
        "name": "Plane of Sky",
        "targets": [
          "Noble Dojorn", "Thunder Spirit Princess", "Protector of Sky", "Gorgalosk",
          "Keeper of Souls", "The Spiroc Lord", "Bazzt Zzzt", "Sister of the Spire",
          "Eye of Veeshan", "Overseer of Air", "The Hand of Veeshan"
        ]
      }
    ]
    """;
}
