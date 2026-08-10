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

    /// <summary>One item looted off a raid kill's corpse.</summary>
    public sealed class KillItem
    {
        public string Item { get; set; } = "";
        public int Count { get; set; } = 1;
        public string Kind { get; set; } = "";  // Upgrade / Kept / Sold
    }

    /// <summary>One recorded kill: when, at which zone difficulty (D0–D4), and
    /// what it dropped (attributed by mob name; old files load with it empty).</summary>
    public sealed record Kill(DateTime When, int D)
    {
        public List<KillItem> Items { get; init; } = new();
    }

    public sealed record TargetView(string Name, int Count, DateTime? Last, IReadOnlySet<int> Tiers);
    public sealed record TierView(string Name, List<TargetView> Targets)
    {
        public int Killed => Targets.Count(t => t.Count > 0);
        public bool Cleared => Targets.Count > 0 && Killed == Targets.Count;
    }

    private readonly string _targetsPath;
    private readonly string _killsPath;
    private readonly List<Tier> _tiers = new();
    private readonly HashSet<string> _targetSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Kill>> _kills = new(StringComparer.OrdinalIgnoreCase);

    // EQL zone difficulty rides in the zone name: "Befallen" = D0,
    // "Befallen 1 (Awakened)" = D1 … "Befallen 4 (Refined)" = D4.
    private static readonly Regex DifficultyRx = new(
        @"\s(?<d>[1-4]) \([^)]+\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Difficulty tier (0–4) encoded in a zone name.</summary>
    public static int ParseDifficulty(string zone)
    {
        var m = DifficultyRx.Match(zone.Trim());
        return m.Success ? int.Parse(m.Groups["d"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Raised on the caller's thread when a listed raid target dies.</summary>
    public event Action<string, DateTime>? KillRecorded;

    /// <summary>The last few mob deaths of ANY kind — used as a picker when adding respawns.</summary>
    public sealed record RecentDeath(string Name, DateTime When, string Zone = "");

    private string _zone = "";
    private const string ZonePrefix = "You have entered ";

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

    public RaidKills(ConfigService config, string? killsPathOverride = null)
    {
        _targetsPath = Path.Combine(config.ConfigDirectory, "raid-targets.json");
        _killsPath = killsPathOverride ?? Path.Combine(config.ConfigDirectory, "raid-kills.json");
        LoadTargets();
        LoadKills();
    }

    public IReadOnlyList<TierView> GetView() =>
        _tiers.Select(t => new TierView(t.Name, t.Targets.Select(name =>
        {
            _kills.TryGetValue(name, out var kills);
            return new TargetView(name, kills?.Count ?? 0,
                kills is { Count: > 0 } ? kills.Max(k => k.When) : null,
                kills is { Count: > 0 } ? kills.Select(k => k.D).ToHashSet() : new HashSet<int>());
        }).ToList())).ToList();

    public int TotalTargets => _tiers.Sum(t => t.Targets.Count);
    public int TotalKilled => _tiers.Sum(t => t.Targets.Count(x => _kills.ContainsKey(x)));

    /// <summary><paramref name="when"/> lets catch-up replay use the LINE's time
    /// (with a dedupe window so re-running a catch-up never double-records).</summary>
    public void ProcessLine(string rawLine, DateTime? when = null)
    {
        string body = TimestampPrefix.Replace(rawLine, "", 1);

        if (body.StartsWith(ZonePrefix, StringComparison.Ordinal) && body.EndsWith('.'))
        {
            _zone = body[ZonePrefix.Length..^1];
            return;
        }

        if (!TryParseKill(body, out string mob)) return;

        DateTime t = when ?? DateTime.Now;

        // Every death lands in the recent list (latest kill of a name wins).
        _recentDeaths.RemoveAll(d => d.Name.Equals(mob, StringComparison.OrdinalIgnoreCase));
        _recentDeaths.Insert(0, new RecentDeath(mob, t, _zone));
        if (_recentDeaths.Count > MaxRecentDeaths)
            _recentDeaths.RemoveAt(MaxRecentDeaths);

        if (!_targetSet.TryGetValue(mob, out string? canonical)) return;

        if (!_kills.TryGetValue(canonical, out var list))
            _kills[canonical] = list = new List<Kill>();
        if (list.Any(k => Math.Abs((k.When - t).TotalMinutes) < 10)) return; // replayed line
        list.Add(new Kill(t, ParseDifficulty(_zone)));
        SaveKills();
        Log.Info($"Raid target down: {canonical} (D{ParseDifficulty(_zone)})");
        KillRecorded?.Invoke(canonical, t);
    }

    // ---- loot attribution ------------------------------------------------------
    // Loot lines name the corpse ("from Lady Vox's corpse"), so a loot entry is
    // pinned to the most recent kill of that raid target within a window — long
    // enough to loot at leisure, short enough not to bleed into the next clear.

    private const double LootWindowMinutes = 30;

    /// <summary>All recorded kills of one target, newest first (empty if none).</summary>
    public IReadOnlyList<Kill> KillsFor(string target) =>
        _kills.TryGetValue(target, out var list)
            ? list.OrderByDescending(k => k.When).ToList()
            : Array.Empty<Kill>();

    /// <summary>Attach a loot entry to the raid kill it came from (no-op for
    /// non-target mobs or loot outside the window). Returns true when attached.</summary>
    public bool AttributeLoot(LootTracker.LootEntry e) => AttributeLoot(e, save: true);

    private bool AttributeLoot(LootTracker.LootEntry e, bool save)
    {
        if (!_targetSet.TryGetValue(e.Mob, out string? canonical)) return false;
        if (!_kills.TryGetValue(canonical, out var list)) return false;

        var kill = list
            .Where(k => e.When >= k.When.AddMinutes(-1)          // clock skew tolerance
                     && e.When <= k.When.AddMinutes(LootWindowMinutes))
            .OrderByDescending(k => k.When)
            .FirstOrDefault();
        if (kill is null) return false;

        string kind = e.Kind.ToString();
        var existing = kill.Items.FirstOrDefault(i =>
            i.Kind == kind && i.Item.Equals(e.Item, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Count += Math.Max(1, e.Count);
        else kill.Items.Add(new KillItem { Item = e.Item, Count = Math.Max(1, e.Count), Kind = kind });

        if (save) SaveKills();
        return true;
    }

    /// <summary>One-time upgrade backfill: attribute the existing loot history to
    /// the existing kill history (both carry timestamps). Only runs while every
    /// recorded kill is item-less, so it can never double-count.</summary>
    public void BackfillLoot(IEnumerable<LootTracker.LootEntry> history)
    {
        if (_kills.Values.SelectMany(l => l).Any(k => k.Items.Count > 0)) return;

        int attached = 0;
        foreach (var e in history.OrderBy(x => x.When))
            if (AttributeLoot(e, save: false)) attached++;

        if (attached > 0)
        {
            SaveKills();
            Log.Info($"Raid loot backfill: attributed {attached} loot entries to past kills.");
        }
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
            string json = File.ReadAllText(_killsPath);
            try
            {
                var kills = JsonSerializer.Deserialize<Dictionary<string, List<Kill>>>(json, JsonOpts);
                if (kills is null) return;
                foreach (var (name, list) in kills)
                    _kills[name] = list.Where(k => k is not null).ToList();
            }
            catch (JsonException)
            {
                // Pre-2.3 shape: plain kill timestamps — migrate as D0 (tier unknown).
                var legacy = JsonSerializer.Deserialize<Dictionary<string, List<DateTime>>>(json, JsonOpts);
                if (legacy is null) return;
                foreach (var (name, list) in legacy)
                    _kills[name] = list.Select(t => new Kill(t, 0)).ToList();
            }
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
