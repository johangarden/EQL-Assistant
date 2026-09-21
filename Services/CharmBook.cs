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
    /// <param name="MobLevel">From your /con (0 = never conned).</param>
    /// <param name="Rank">The cast as the log printed it ("Beguile Undead", "Mesmerization VIII").</param>
    /// <param name="PetTaken">Damage the pet took while charmed.</param>
    /// <param name="YourLevel">Your level at the time (0 = unknown).</param>
    public sealed record Episode(string Mob, string Spell, string Zone, DateTime Start, DateTime End, string How,
        double PetDamage, int PetHits, double MaxHit, int PetKills,
        int MobLevel = 0, string Rank = "", double PetTaken = 0, int YourLevel = 0)
    {
        public double HeldSec => Math.Max(0, (End - Start).TotalSeconds);
    }

    /// <summary>A charm that never landed: resisted or interrupted.</summary>
    public sealed record Attempt(string Mob, string Spell, string How, DateTime When, string Zone);

    public sealed record MobStats(string Mob, int Charms, double AvgHeldSec, double LongestSec, int Breaks, int Deaths,
        double DamagePerHit, double Dps, int Kills, double MaxHit, DateTime Last, string Zone,
        int Level = 0, int Resisted = 0, double Taken = 0);

    private sealed class Doc
    {
        public List<Episode> Episodes { get; set; } = new();
        public List<Attempt> Attempts { get; set; } = new();
    }
    private readonly List<Attempt> _attempts = new();
    public IReadOnlyList<Attempt> Attempts => _attempts;

    /// <summary>Record a failed charm attempt (the mob must be named — an
    /// interrupt without a target teaches nothing per mob).</summary>
    public void AddAttempt(Attempt a)
    {
        if (a.Mob.Length == 0) return;
        if (_attempts.Any(x => x.Mob.Equals(a.Mob, StringComparison.OrdinalIgnoreCase) && x.When == a.When)) return;
        _attempts.Add(a);
        if (_attempts.Count > MaxEpisodes) _attempts.RemoveAt(0);
        Save();
        Changed?.Invoke();
    }

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

    private MobStats Aggregate(string mob, List<Episode> list)
    {
        double held = list.Sum(e => e.HeldSec);
        int hits = list.Sum(e => e.PetHits);
        double dmg = list.Sum(e => e.PetDamage);
        var last = list.OrderByDescending(e => e.End).First();
        int resisted = _attempts.Count(a => a.Mob.Equals(mob, StringComparison.OrdinalIgnoreCase) && a.How == "resisted");
        return new MobStats(mob, list.Count, held / list.Count, list.Max(e => e.HeldSec),
            list.Count(e => e.How == "broke"), list.Count(e => e.How == "died"),
            hits > 0 ? dmg / hits : 0, held > 0 ? dmg / held : 0,
            list.Sum(e => e.PetKills), list.Max(e => e.MaxHit), last.End, last.Zone,
            list.Max(e => e.MobLevel), resisted, list.Sum(e => e.PetTaken));
    }

    public void ResetAll()
    {
        _episodes.Clear();
        _attempts.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            string json = File.ReadAllText(_path);
            if (json.TrimStart().StartsWith('['))
            {
                // The first cut wrote a bare episode list.
                var list = JsonSerializer.Deserialize<List<Episode>>(json, JsonOpts);
                if (list is not null) _episodes.AddRange(list.TakeLast(MaxEpisodes));
                return;
            }
            var doc = JsonSerializer.Deserialize<Doc>(json, JsonOpts);
            if (doc is null) return;
            _episodes.AddRange(doc.Episodes.TakeLast(MaxEpisodes));
            _attempts.AddRange(doc.Attempts.TakeLast(MaxEpisodes));
        }
        catch (Exception ex) { Log.Warn("charms.json unreadable: " + ex.Message); }
    }

    private void Save()
    {
        if (_path is null) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(new Doc { Episodes = _episodes, Attempts = _attempts }, JsonOpts)); }
        catch (Exception ex) { Log.Warn("charms.json not saved: " + ex.Message); }
    }
}
