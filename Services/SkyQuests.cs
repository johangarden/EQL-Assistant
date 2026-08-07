using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Plane of Sky Test-quest tracker. Quest data (per-class quests with turn-in
/// items, droppers and rewards) ships embedded; progress is tracked from the
/// log: held counts feed off the loot tracker's kept items, and a quest
/// auto-completes when its unique reward item shows up in a receive/hand-in
/// line. Progress persists to sky-progress.json.
/// </summary>
public sealed class SkyQuests
{
    public sealed class SkyItem
    {
        public string Name { get; set; } = "";
        public string Who { get; set; } = "";
        public string Where { get; set; } = "";
        public int Count { get; set; } = 1;
        public string? Stats { get; set; }
    }

    public sealed class SkyQuest
    {
        public string Class { get; set; } = "";
        public string Name { get; set; } = "";
        public string Giver { get; set; } = "";
        public string Reward { get; set; } = "";
        public string RewardStats { get; set; } = "";
        public List<SkyItem> Items { get; set; } = new();

        [JsonIgnore] public string Key => Class + "|" + Name;
    }

    private sealed class SkyDoc
    {
        public string Credit { get; set; } = "";
        public List<SkyQuest> Quests { get; set; } = new();
    }

    private sealed class ProgressDoc
    {
        public Dictionary<string, int> Counts { get; set; } = new();
        public List<string> Completed { get; set; } = new();
    }

    private readonly string _progressPath;
    private readonly Dictionary<string, int> _counts = new();          // ItemKey -> held
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _questItemKeys = new();           // fast loot filter
    private readonly List<SkyQuest> _quests = new();

    /// <summary>All quests (16 classes, ~95 quests).</summary>
    public IReadOnlyList<SkyQuest> Quests => _quests;

    public int CompletedCount => _completed.Count;

    /// <summary>Raised when counts or completion change (caller's thread).</summary>
    public event Action? Changed;

    /// <summary>Raised when a reward line auto-completes a quest.</summary>
    public event Action<SkyQuest>? QuestCompleted;

    private static readonly Regex TimestampPrefix = new(@"^\[.+?\]\s?", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SkyQuests(ConfigService config, LootTracker loot, string? progressPathOverride = null)
    {
        _progressPath = progressPathOverride
            ?? Path.Combine(config.ConfigDirectory, "sky-progress.json");
        LoadQuests();

        foreach (var q in _quests)
            foreach (var it in q.Items)
                _questItemKeys.Add(LootTracker.ItemKey(it.Name));

        bool firstRun = !File.Exists(_progressPath);
        LoadProgress();

        // First run: the loot history may already hold quest pieces — seed from it
        // once, then stay incremental (immune to the loot list's cap).
        if (firstRun)
        {
            foreach (var e in loot.Entries)
                CountLoot(e, save: false);
            SaveProgress();
        }

        loot.Added += e => { if (CountLoot(e, save: true)) Changed?.Invoke(); };
    }

    private bool CountLoot(LootTracker.LootEntry e, bool save)
    {
        if (e.Kind != LootTracker.LootKind.Kept) return false;
        string key = LootTracker.ItemKey(e.Item);
        if (!_questItemKeys.Contains(key)) return false;
        _counts[key] = _counts.GetValueOrDefault(key) + Math.Max(1, e.Count);
        if (save) SaveProgress();
        return true;
    }

    /// <summary>Watch for reward receipts — the exact wording is unconfirmed, so any
    /// receive/hand-in-shaped line naming a known reward completes its quest.</summary>
    public void ProcessLine(string rawLine)
    {
        string body = TimestampPrefix.Replace(rawLine, "", 1);
        if (!body.StartsWith("You receive", StringComparison.Ordinal)
            && !body.StartsWith("You have received", StringComparison.Ordinal)
            && !body.Contains(" hands you ", StringComparison.Ordinal))
            return;
        if (body.Contains("from the corpse", StringComparison.Ordinal)) return; // coin loot

        foreach (var q in _quests)
        {
            if (_completed.Contains(q.Key)) continue;
            if (!body.Contains(q.Reward, StringComparison.OrdinalIgnoreCase)) continue;

            _completed.Add(q.Key);
            SaveProgress();
            Log.Info($"Sky quest complete: {q.Name} -> {q.Reward}");
            QuestCompleted?.Invoke(q);
            Changed?.Invoke();
            return;
        }
    }

    public int HeldCount(SkyItem item) =>
        _counts.GetValueOrDefault(LootTracker.ItemKey(item.Name));

    public bool IsCompleted(SkyQuest q) => _completed.Contains(q.Key);

    /// <summary>Manual ✓ toggle from the Sky window.</summary>
    public void SetCompleted(SkyQuest q, bool done)
    {
        bool changed = done ? _completed.Add(q.Key) : _completed.Remove(q.Key);
        if (!changed) return;
        SaveProgress();
        Changed?.Invoke();
    }

    /// <summary>Items still owed for a quest (0 when ready to turn in).</summary>
    public (int Have, int Need) Progress(SkyQuest q)
    {
        int have = 0, need = 0;
        foreach (var it in q.Items)
        {
            need += it.Count;
            have += Math.Min(it.Count, HeldCount(it));
        }
        return (have, need);
    }

    // ---- persistence / data ---------------------------------------------------

    private void LoadProgress()
    {
        try
        {
            if (!File.Exists(_progressPath)) return;
            var doc = JsonSerializer.Deserialize<ProgressDoc>(File.ReadAllText(_progressPath), JsonOpts);
            if (doc is null) return;
            foreach (var (k, v) in doc.Counts) _counts[k] = v;
            foreach (var k in doc.Completed) _completed.Add(k);
        }
        catch { /* corrupt -> start empty */ }
    }

    private void SaveProgress()
    {
        try
        {
            File.WriteAllText(_progressPath, JsonSerializer.Serialize(new ProgressDoc
            {
                Counts = new Dictionary<string, int>(_counts),
                Completed = _completed.ToList(),
            }, JsonOpts));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Disk copy (next to the exe) wins so users can tweak; the embedded
    /// resource keeps a lone copied exe fully functional.</summary>
    private void LoadQuests()
    {
        try
        {
            string diskPath = Path.Combine(AppContext.BaseDirectory, "data", "sky-quests.json");
            string? json = null;
            if (File.Exists(diskPath))
            {
                json = File.ReadAllText(diskPath);
            }
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                string? res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("sky-quests.json", StringComparison.OrdinalIgnoreCase));
                if (res is not null)
                {
                    using var stream = asm.GetManifestResourceStream(res)!;
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
            }
            if (json is null) return;
            var doc = JsonSerializer.Deserialize<SkyDoc>(json, JsonOpts);
            if (doc is not null) _quests.AddRange(doc.Quests);
        }
        catch { /* no data -> empty tracker rather than crash */ }
    }
}
