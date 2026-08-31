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
        /// <summary>Display shorthand from the wiki table ("Gorga", "KoS").</summary>
        public string Who { get; set; } = "";
        public string Where { get; set; } = "";
        public int Count { get; set; } = 1;
        public string? Stats { get; set; }
        /// <summary>FULL dropper names as the log prints them ("Keeper of
        /// Souls") — the sighting matcher's food. Empty = wind rune (any Sky
        /// mob) or an island-only wiki entry: nothing to match a line against.</summary>
        public List<string> Mobs { get; set; } = new();
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

        private string? _slot;

        /// <summary>Equip slot(s) parsed from the reward stats ("FACE", "EAR NECK", …).</summary>
        [JsonIgnore]
        public string Slot => _slot ??= Regex.Match(RewardStats, @"(?im)^Slot:\s*(.+)$")
            is { Success: true } m ? m.Groups[1].Value.Trim() : "";
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
        public List<string> Tracked { get; set; } = new();
        public Dictionary<string, int> Offered { get; set; } = new();
        public Dictionary<string, List<string>> QuestOffers { get; set; } = new();
        public List<string> OfferSeen { get; set; } = new();
    }

    private readonly string _progressPath;
    private readonly Dictionary<string, int> _counts = new();          // ItemKey -> looted
    private readonly Dictionary<string, int> _offered = new();         // ItemKey -> turned in
    private readonly Dictionary<string, HashSet<string>> _questOffers  // quest -> ItemKeys offered
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offerSeen = new(StringComparer.Ordinal); // replay dedupe
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _questItemKeys = new();           // fast loot filter
    private readonly Dictionary<string, string> _keyToName = new();    // ItemKey -> display name
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
            {
                string key = LootTracker.ItemKey(it.Name);
                _questItemKeys.Add(key);
                _keyToName.TryAdd(key, it.Name);
            }

        LoadProgress();

        // Reconcile with the loot history on EVERY start, taking the MAX of
        // persisted vs recomputed per item: the incremental counts can miss
        // entries recorded before their quest key matched (the wind-rune
        // naming saga), while persisted values win where the capped loot
        // list has already forgotten old pieces. Never decreases anything.
        var fromLoot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in loot.Entries)
        {
            if (e.Kind is not (LootTracker.LootKind.Kept or LootTracker.LootKind.Currency)) continue;
            string key = LootTracker.ItemKey(e.Item);
            if (_questItemKeys.Contains(key))
                fromLoot[key] = fromLoot.GetValueOrDefault(key) + Math.Max(1, e.Count);
        }
        bool lifted = false;
        foreach (var (key, n) in fromLoot)
            if (n > _counts.GetValueOrDefault(key)) { _counts[key] = n; lifted = true; }
        if (lifted) SaveProgress();

        loot.Added += e => { if (CountLoot(e, save: true)) Changed?.Invoke(); };
    }

    private bool CountLoot(LootTracker.LootEntry e, bool save)
    {
        // Kept items AND currency pickups: the wind runes became currencies
        // in EQL, and a rune stored in your currency tab is every bit "had".
        if (e.Kind is not (LootTracker.LootKind.Kept or LootTracker.LootKind.Currency))
            return false;
        string key = LootTracker.ItemKey(e.Item);
        if (!_questItemKeys.Contains(key)) return false;
        _counts[key] = _counts.GetValueOrDefault(key) + Math.Max(1, e.Count);
        if (save) SaveProgress();
        return true;
    }

    // Turn-ins ARE logged even though rewards are not (confirmed 29 Aug 2026):
    //   "You offered 1 Wind Rune Meda to Josin Faithbringer."
    //   "You complete the trade with Josin Faithbringer."
    // Offers alone prove NOTHING — a trade can be cancelled (window closed,
    // death mid-trade) and the items come back. Only the completed trade
    // seals them (owner's report, 30 Aug 2026).
    private static readonly Regex OfferRx = new(
        @"^You offered (?<n>\d+) (?<item>.+?) to (?<npc>.+?)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TradeDoneRx = new(
        @"^You complete the trade with (?<npc>.+?)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record PendingOffer(DateTime At, string ItemKey, int N, string RawLine);
    private readonly Dictionary<string, List<PendingOffer>> _pendingOffers
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An offer older than this when the trade completes belongs to
    /// an earlier, abandoned trade window — never committed.</summary>
    private const double TradeWindowSec = 300;

    /// <summary>Watch the log: offers to an NPC buffer as a pending trade;
    /// "You complete the trade" commits them — held counts drop and a quest
    /// completes once every item reached its own NPC. Cancelled trades
    /// simply never commit. Reward-receipt lines still complete as backup.</summary>
    public void ProcessLine(string rawLine)
    {
        string body = TimestampPrefix.Replace(rawLine, "", 1);

        if (body.StartsWith("You offered ", StringComparison.Ordinal)
            && OfferRx.Match(body) is { Success: true } om)
        {
            string itemKey = LootTracker.ItemKey(om.Groups["item"].Value);
            if (!_questItemKeys.Contains(itemKey)) return;
            // Already committed in an earlier session/replay — the LAW: every
            // retroactive consumer dedupes.
            if (_offerSeen.Contains(rawLine)) return;

            string npc = om.Groups["npc"].Value;
            var when = LineTime(rawLine);
            if (!_pendingOffers.TryGetValue(npc, out var list))
                _pendingOffers[npc] = list = new List<PendingOffer>();
            list.RemoveAll(p => (when - p.At).TotalSeconds > TradeWindowSec); // stale window
            if (list.All(p => p.RawLine != rawLine))
                list.Add(new PendingOffer(when, itemKey,
                    Math.Max(1, int.Parse(om.Groups["n"].Value)), rawLine));
            return;
        }

        if (body.StartsWith("You complete the trade with ", StringComparison.Ordinal)
            && TradeDoneRx.Match(body) is { Success: true } tm)
        {
            CommitTrade(tm.Groups["npc"].Value, LineTime(rawLine));
            return;
        }

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
            _tracked.Remove(q.Key); // a finished hunt un-tracks itself
            SaveProgress();
            Log.Info($"Sky quest complete: {q.Name} -> {q.Reward}");
            QuestCompleted?.Invoke(q);
            Changed?.Invoke();
            return;
        }
    }

    /// <summary>The completed trade seals its pending offers: held counts
    /// drop (tracked apart from looted so the loot-reconcile can't resurrect
    /// them), and a quest completes once every item reached its own NPC.</summary>
    private void CommitTrade(string npc, DateTime when)
    {
        if (!_pendingOffers.Remove(npc, out var list)) return;
        bool any = false;
        foreach (var p in list)
        {
            if ((when - p.At).TotalSeconds > TradeWindowSec) continue; // earlier, abandoned window
            if (!_offerSeen.Add(p.RawLine)) continue;                  // replay dedupe
            any = true;
            _offered[p.ItemKey] = _offered.GetValueOrDefault(p.ItemKey) + p.N;

            foreach (var q in _quests)
            {
                if (_completed.Contains(q.Key)) continue;
                if (!q.Giver.Equals(npc, StringComparison.OrdinalIgnoreCase)) continue;
                if (q.Items.All(i => LootTracker.ItemKey(i.Name) != p.ItemKey)) continue;

                if (!_questOffers.TryGetValue(q.Key, out var offered))
                    _questOffers[q.Key] = offered = new HashSet<string>();
                offered.Add(p.ItemKey);

                if (q.Items.All(i => offered.Contains(LootTracker.ItemKey(i.Name))))
                {
                    _completed.Add(q.Key);
                    _tracked.Remove(q.Key);
                    Log.Info($"Sky quest complete (trade with {npc}): {q.Name} -> {q.Reward}");
                    QuestCompleted?.Invoke(q);
                }
            }
        }
        if (!any) return;
        SaveProgress();
        Changed?.Invoke();
    }

    private static DateTime LineTime(string rawLine)
    {
        var m = Regex.Match(rawLine, @"^\[(?<ts>.+?)\]");
        return m.Success && DateTime.TryParseExact(m.Groups["ts"].Value,
            new[] { "ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" },
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t)
            ? t : DateTime.Now;
    }

    /// <summary>Wipe item counts + completions (Data page reset — a following
    /// reparse rebuilds counts from the loot events it replays).</summary>
    public void ResetProgress()
    {
        _counts.Clear();
        _completed.Clear();
        _offered.Clear();
        _questOffers.Clear();
        _offerSeen.Clear();
        SaveProgress();
        Changed?.Invoke();
    }

    /// <summary>Looted minus turned in — what's actually still in your bags.</summary>
    public int HeldCount(SkyItem item)
    {
        string key = LootTracker.ItemKey(item.Name);
        return Math.Max(0, _counts.GetValueOrDefault(key) - _offered.GetValueOrDefault(key));
    }

    public bool IsCompleted(SkyQuest q) => _completed.Contains(q.Key);

    /// <summary>Manual ✓ toggle from the Sky window.</summary>
    public void SetCompleted(SkyQuest q, bool done)
    {
        bool changed = done ? _completed.Add(q.Key) : _completed.Remove(q.Key);
        if (done) changed |= _tracked.Remove(q.Key); // a finished hunt un-tracks itself
        if (!changed) return;
        SaveProgress();
        Changed?.Invoke();
    }

    // ---- tracking (the helper panel's Hunting list) --------------------------

    public bool IsTracked(SkyQuest q) => _tracked.Contains(q.Key);

    public int TrackedCount => _tracked.Count;

    /// <summary>The ★ toggle: a tracked quest stands in the helper panel's
    /// Hunting list until completed (which un-tracks it) or un-starred.</summary>
    public void SetTracked(SkyQuest q, bool tracked)
    {
        bool changed = tracked ? _tracked.Add(q.Key) : _tracked.Remove(q.Key);
        if (!changed) return;
        SaveProgress();
        Changed?.Invoke();
    }

    /// <summary>Tracked quests in data order.</summary>
    public IReadOnlyList<SkyQuest> TrackedQuests() =>
        _quests.Where(q => _tracked.Contains(q.Key)).ToList();

    // ---- shopping list & housekeeping -----------------------------------------

    /// <summary>One still-needed item, aggregated across ACTIVE quests: a
    /// shared rune needed by five open quests with none held is "missing 5".</summary>
    public sealed record IsleNeed(string Isle, string Item, int Missing, int Needed,
        string Who, List<string> Quests);

    /// <summary>An item the ledger says nothing active still wants — spare
    /// copies safe to hand to a guildie (or the vendor).</summary>
    public sealed record SurplusItem(string Item, int Surplus);

    private int HeldByKey(string key) =>
        Math.Max(0, _counts.GetValueOrDefault(key) - _offered.GetValueOrDefault(key));

    /// <summary>The per-isle shopping list: everything ACTIVE quests still
    /// need beyond what you hold, grouped by where it drops.</summary>
    public IReadOnlyList<IsleNeed> MissingByIsle(string classFilter = "")
    {
        var agg = new Dictionary<string, (int Needed, SkyItem Sample, SortedSet<string> Quests)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var q in _quests)
        {
            if (_completed.Contains(q.Key)) continue;
            if (classFilter.Length > 0
                && !q.Class.Equals(classFilter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var it in q.Items)
            {
                if (!agg.TryGetValue(it.Name, out var a))
                    a = (0, it, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                a.Needed += it.Count;
                a.Quests.Add(q.Name);
                agg[it.Name] = a;
            }
        }

        var rows = new List<IsleNeed>();
        foreach (var (name, a) in agg)
        {
            int missing = a.Needed - HeldByKey(LootTracker.ItemKey(name));
            if (missing <= 0) continue;
            string isle = a.Sample.Where.Length > 0 ? a.Sample.Where : "Any isle · random Sky drop";
            string who = a.Sample.Mobs.Count > 0 ? string.Join(", ", a.Sample.Mobs) : a.Sample.Who;
            rows.Add(new IsleNeed(isle, name, missing, a.Needed, who, a.Quests.ToList()));
        }
        return rows.OrderBy(r => IsleOrder(r.Isle)).ThenBy(r => r.Isle, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.Missing).ThenBy(r => r.Item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int IsleOrder(string isle)
    {
        var m = Regex.Match(isle, @"\d+");
        return m.Success ? int.Parse(m.Value) : 99; // "Any isle" and oddballs sink
    }

    /// <summary>Held copies no ACTIVE quest still wants (per the loot ledger):
    /// every quest for the item is done, or you hold more than they need.</summary>
    public IReadOnlyList<SurplusItem> Surplus()
    {
        var needed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in _quests)
        {
            if (_completed.Contains(q.Key)) continue;
            foreach (var it in q.Items)
            {
                string key = LootTracker.ItemKey(it.Name);
                needed[key] = needed.GetValueOrDefault(key) + it.Count;
            }
        }

        var rows = new List<SurplusItem>();
        foreach (var key in _counts.Keys)
        {
            int surplus = HeldByKey(key) - needed.GetValueOrDefault(key);
            if (surplus > 0)
                rows.Add(new SurplusItem(_keyToName.GetValueOrDefault(key, key), surplus));
        }
        return rows.OrderByDescending(r => r.Surplus)
            .ThenBy(r => r.Item, StringComparer.OrdinalIgnoreCase).ToList();
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
            foreach (var k in doc.Tracked) _tracked.Add(k);
            foreach (var (k, v) in doc.Offered) _offered[k] = v;
            foreach (var (k, v) in doc.QuestOffers)
                _questOffers[k] = new HashSet<string>(v);
            foreach (var s in doc.OfferSeen) _offerSeen.Add(s);
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
                Tracked = _tracked.ToList(),
                Offered = new Dictionary<string, int>(_offered),
                QuestOffers = _questOffers.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                OfferSeen = _offerSeen.ToList(),
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
