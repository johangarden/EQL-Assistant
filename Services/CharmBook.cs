using System.IO;
using System.Text.Json;

namespace EQLOverlay.Services;

/// <summary>
/// The charm ledger (owner, 21 Sep: "do we keep track of charmed mobs? avg
/// duration per mob, damage per hit, DPS…"): one episode per charm the
/// <see cref="CrowdControl"/> engine saw end — the pet, the spell, how long
/// it held, how it ended, and what the pet did meanwhile. Aggregated per mob
/// for the Fight history's CHARMS view and the live card's "this mob" line.
/// Persisted to charms.json; live-only (the engine never runs on replays).
/// </summary>
public sealed class CharmBook
{
    public sealed record Episode(string Mob, string Spell, string Zone, DateTime Start, DateTime End, string How,
        double PetDamage, int PetHits, double MaxHit, int PetKills)
    {
        public double HeldSec => Math.Max(0, (End - Start).TotalSeconds);
    }

    public sealed record MobStats(string Mob, int Charms, double AvgHeldSec, double LongestSec, int Breaks, int Deaths,
        double DamagePerHit, double Dps, int Kills, double MaxHit, DateTime Last, string Zone);

    public event Action? Changed;

    private readonly List<Episode> _episodes = new();
    private readonly string? _path;
    private const int MaxEpisodes = 2000;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public CharmBook(ConfigService? config, string? pathOverride = null)
    {
        _path = pathOverride ?? (config is null ? null : Path.Combine(config.ConfigDirectory, "charms.json"));
        Load();
    }

    public IReadOnlyList<Episode> Episodes => _episodes;

    public void Add(Episode e)
    {
        if (e.Mob.Length == 0 || e.HeldSec < 1) return;
        if (_episodes.Any(x => x.Mob.Equals(e.Mob, StringComparison.OrdinalIgnoreCase) && x.Start == e.Start)) return; // dedupe
        _episodes.Add(e);
        if (_episodes.Count > MaxEpisodes) _episodes.RemoveAt(0);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Per mob, most-charmed first; <paramref name="filter"/> narrows by mob or spell.</summary>
    public List<MobStats> ByMob(string filter = "")
    {
        return _episodes
            .Where(e => filter.Length == 0 || e.Mob.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || e.Spell.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Mob, StringComparer.OrdinalIgnoreCase)
            .Select(g => Aggregate(g.Key, g.ToList()))
            .OrderByDescending(s => s.Charms).ThenBy(s => s.Mob, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>What this mob has done for you before — null when never charmed.</summary>
    public MobStats? Stats(string mob)
    {
        var list = _episodes.Where(e => e.Mob.Equals(mob.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return list.Count == 0 ? null : Aggregate(list[0].Mob, list);
    }

    private static MobStats Aggregate(string mob, List<Episode> list)
    {
        double held = list.Sum(e => e.HeldSec);
        int hits = list.Sum(e => e.PetHits);
        double dmg = list.Sum(e => e.PetDamage);
        var last = list.OrderByDescending(e => e.End).First();
        return new MobStats(mob, list.Count, held / list.Count, list.Max(e => e.HeldSec),
            list.Count(e => e.How == "broke"), list.Count(e => e.How == "died"),
            hits > 0 ? dmg / hits : 0, held > 0 ? dmg / held : 0,
            list.Sum(e => e.PetKills), list.Max(e => e.MaxHit), last.End, last.Zone);
    }

    public void ResetAll()
    {
        _episodes.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Episode>>(File.ReadAllText(_path), JsonOpts);
            if (list is not null) _episodes.AddRange(list.TakeLast(MaxEpisodes));
        }
        catch (Exception ex) { Log.Warn("charms.json unreadable: " + ex.Message); }
    }

    private void Save()
    {
        if (_path is null) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_episodes, JsonOpts)); }
        catch (Exception ex) { Log.Warn("charms.json not saved: " + ex.Message); }
    }
}
