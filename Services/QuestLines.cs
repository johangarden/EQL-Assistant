using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Notable quest LINES (owner request, 7 Sep 2026): multi-step weapon quests
/// from eqlwiki — Zimel's Blades (SoulFire), The Torrid Corruptor, The Fiery
/// Avenger — tracked step by step from the log. What the log proves:
/// kills ("You have slain X!"), loot lines, hand-ins (offers sealed by
/// "You complete the trade with NPC" — the Sky rule), and what you say to
/// NPCs ("You say, 'I still seek guidance'"). What it can't see — right-
/// clicking a soul — is a tick the owner flips. A later step proving itself
/// implies every earlier one, so a quest begun before the app still fills
/// in. State is SETS, never counters, so replay and merge are harmless.
/// </summary>
public sealed class QuestLines
{
    public sealed class Need
    {
        public string Name { get; set; } = "";
        public int Count { get; set; } = 1;
    }

    public sealed class Prereq
    {
        public string Quest { get; set; } = "";
        public string Item { get; set; } = "";
    }

    public sealed class Step
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>kill · loot · handin · say — the headline flavor.</summary>
        public string Kind { get; set; } = "";
        public string Npc { get; set; } = "";
        /// <summary>Alternative NPCs (either seals the trade).</summary>
        public List<string> Npcs { get; set; } = new();
        public string Zone { get; set; } = "";
        public string Note { get; set; } = "";
        public List<string> Kill { get; set; } = new();
        public List<string> Loot { get; set; } = new();
        public List<Need> Handin { get; set; } = new();
        /// <summary>Extra things to have in the bags for this step (no hand-in).</summary>
        public List<Need> Bring { get; set; } = new();
        public string Coins { get; set; } = "";
        public List<string> Get { get; set; } = new();
        /// <summary>An item to right-click — no log line; the owner ticks it.</summary>
        public string Click { get; set; } = "";
        public string Say { get; set; } = "";
        public string Reply { get; set; } = "";
        public string Faction { get; set; } = "";

        [JsonIgnore] public IEnumerable<string> NpcNames => Npcs.Count > 0 ? Npcs : new[] { Npc };
        [JsonIgnore] public bool HasTrade => Handin.Count > 0 || Coins.Length > 0;
    }

    public sealed class Quest
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Reward { get; set; } = "";
        public string RewardStats { get; set; } = "";
        public List<string> Classes { get; set; } = new();
        public int MinLevel { get; set; }
        public List<string> Requires { get; set; } = new();
        public List<Prereq> Prereqs { get; set; } = new();
        public string Wiki { get; set; } = "";
        public string Note { get; set; } = "";
        public List<Step> Steps { get; set; } = new();
    }

    private sealed class Doc
    {
        public string Credit { get; set; } = "";
        public List<Quest> Quests { get; set; } = new();
    }

    /// <summary>How a step got done: "auto" (the log), "you" (the tick), or
    /// "implied" (a later step proved itself).</summary>
    public sealed record Mark(DateTime When, string How, string Evidence);

    private sealed class ProgressDoc
    {
        public Dictionary<string, Mark> Steps { get; set; } = new();
        public Dictionary<string, List<string>> Partial { get; set; } = new();
        public Dictionary<string, List<string>> Evidence { get; set; } = new();
        public List<string> Tracked { get; set; } = new();
        public List<string> OfferSeen { get; set; } = new();
        public Dictionary<string, int> Offered { get; set; } = new();
        public Dictionary<string, int> Destroyed { get; set; } = new();
        public List<string> DestroySeen { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex TimestampPrefix = new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);
    private static readonly Regex SlainRx = new(@"^You have slain (?<mob>.+?)!$", RegexOptions.Compiled);
    private static readonly Regex SlainByRx = new(@"^(?<mob>.+?) has been slain by (?<who>.+?)!$", RegexOptions.Compiled);
    private static readonly Regex LootRx = new(
        @"^(?:--You have looted|You looted) (?:an? |the )?(?<item>.+?) from (?<mob>.+?)'s corpse",
        RegexOptions.Compiled);
    private static readonly Regex SayRx = new(@"^You say, '(?<t>.+)'$", RegexOptions.Compiled);
    private static readonly Regex OfferRx = new(@"^You offered (?<n>\d+) (?<item>.+?) to (?<npc>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex TradeDoneRx = new(@"^You complete the trade with (?<npc>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex DestroyRx = new(@"^You successfully destroyed (?<n>\d+) (?<item>.+?)\.$", RegexOptions.Compiled);

    private const double TradeWindowSec = 300;
    private sealed record PendingOffer(DateTime At, string ItemKey, int N, string RawLine);

    private readonly string _progressPath;
    private readonly List<Quest> _quests = new();
    private readonly Dictionary<string, Mark> _marks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _partial = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _evidence = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offerSeen = new();
    private readonly Dictionary<string, int> _offered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _destroyed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _destroySeen = new();
    private readonly Dictionary<string, List<PendingOffer>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _handinKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly LootTracker? _loot;

    public IReadOnlyList<Quest> Quests => _quests;
    public string Credit { get; private set; } = "";

    public event Action? Changed;
    /// <summary>A step just proved itself from the log (never on ticks or replays of known marks).</summary>
    public event Action<Quest, Step>? StepDone;

    public QuestLines(ConfigService config, LootTracker? loot = null, string? progressPathOverride = null)
    {
        _loot = loot;
        _progressPath = progressPathOverride ?? Path.Combine(config.ConfigDirectory, "quest-lines-progress.json");
        LoadQuests();
        foreach (var q in _quests)
            foreach (var s in q.Steps)
                foreach (var n in s.Handin)
                    _handinKeys.Add(LootTracker.ItemKey(n.Name));
        LoadProgress();
    }

    // ---------------------------------------------------------------- data

    private void LoadQuests()
    {
        try
        {
            string diskPath = Path.Combine(AppContext.BaseDirectory, "data", "quest-lines.json");
            string? json = null;
            if (File.Exists(diskPath)) json = File.ReadAllText(diskPath);
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                string? res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("quest-lines.json", StringComparison.OrdinalIgnoreCase));
                if (res is not null)
                {
                    using var stream = asm.GetManifestResourceStream(res)!;
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
            }
            if (json is null) return;
            var doc = JsonSerializer.Deserialize<Doc>(json, JsonOpts);
            if (doc is null) return;
            Credit = doc.Credit;
            _quests.AddRange(doc.Quests.Where(q => q.Key.Length > 0 && q.Steps.Count > 0));
        }
        catch (Exception ex)
        {
            Log.Warn($"quest-lines.json failed to load: {ex.Message}");
        }
    }

    private void LoadProgress()
    {
        try
        {
            if (!File.Exists(_progressPath)) return;
            var doc = JsonSerializer.Deserialize<ProgressDoc>(File.ReadAllText(_progressPath), JsonOpts);
            if (doc is null) return;
            foreach (var (k, m) in doc.Steps) _marks[k] = m;
            foreach (var (k, v) in doc.Partial) _partial[k] = new HashSet<string>(v, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in doc.Evidence) _evidence[k] = v;
            foreach (var t in doc.Tracked) _tracked.Add(t);
            foreach (var s in doc.OfferSeen) _offerSeen.Add(s);
            foreach (var (k, n) in doc.Offered) _offered[k] = n;
            foreach (var (k, n) in doc.Destroyed) _destroyed[k] = n;
            foreach (var s in doc.DestroySeen) _destroySeen.Add(s);
        }
        catch (Exception ex)
        {
            Log.Warn($"quest-lines progress failed to load: {ex.Message}");
        }
    }

    private void SaveProgress()
    {
        try
        {
            var doc = new ProgressDoc
            {
                Steps = new Dictionary<string, Mark>(_marks),
                Partial = _partial.ToDictionary(p => p.Key, p => p.Value.ToList()),
                Evidence = new Dictionary<string, List<string>>(_evidence),
                Tracked = _tracked.ToList(),
                OfferSeen = _offerSeen.ToList(),
                Offered = new Dictionary<string, int>(_offered),
                Destroyed = new Dictionary<string, int>(_destroyed),
                DestroySeen = _destroySeen.ToList(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_progressPath)!);
            File.WriteAllText(_progressPath, JsonSerializer.Serialize(doc, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warn($"quest-lines progress failed to save: {ex.Message}");
        }
    }

    public void ResetProgress()
    {
        _marks.Clear(); _partial.Clear(); _evidence.Clear(); _offerSeen.Clear(); _offered.Clear(); _pending.Clear();
        _destroyed.Clear(); _destroySeen.Clear();
        SaveProgress();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- state

    public static string StepKey(Quest q, Step s) => $"{q.Key}/{s.Id}";

    public bool IsDone(Quest q, Step s) => _marks.ContainsKey(StepKey(q, s));
    public Mark? MarkOf(Quest q, Step s) => _marks.GetValueOrDefault(StepKey(q, s));
    public IReadOnlyList<string> EvidenceOf(Quest q, Step s) =>
        _evidence.GetValueOrDefault(StepKey(q, s)) ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>Conditions the log has already satisfied on an UNFINISHED step
    /// ("kill:Lord Grimrot", "loot:burning soul…", "trade", "say").</summary>
    public IReadOnlyCollection<string> PartialOf(Quest q, Step s) =>
        _partial.GetValueOrDefault(StepKey(q, s)) ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    public int DoneCount(Quest q) => q.Steps.Count(s => IsDone(q, s));
    public bool IsComplete(Quest q) => q.Steps.All(s => IsDone(q, s));
    public Step? NextStep(Quest q) => q.Steps.FirstOrDefault(s => !IsDone(q, s));

    public bool IsTracked(Quest q) => _tracked.Contains(q.Key);
    public void SetTracked(Quest q, bool on)
    {
        if (on ? !_tracked.Add(q.Key) : !_tracked.Remove(q.Key)) return;
        SaveProgress();
        Changed?.Invoke();
    }

    /// <summary>The owner's tick: marks the step done ("you"); the right-click
    /// steps have no log line, and any step can be ticked over the log.</summary>
    public void Tick(Quest q, Step s, DateTime? when = null)
    {
        string key = StepKey(q, s);
        if (_marks.ContainsKey(key)) return;
        _marks[key] = new Mark(when ?? DateTime.Now, "you", s.Click.Length > 0 ? $"right-clicked {s.Click}" : "ticked");
        ImplyEarlier(q, s, when ?? DateTime.Now);
        SaveProgress();
        Changed?.Invoke();
    }

    /// <summary>Only a "you" or "implied" mark can be taken back — the log's
    /// own marks clear with Reset.</summary>
    public bool Untick(Quest q, Step s)
    {
        string key = StepKey(q, s);
        if (_marks.GetValueOrDefault(key) is not { } m || m.How == "auto") return false;
        _marks.Remove(key);
        SaveProgress();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Copies of a hand-in item the ledger says you looted and kept,
    /// minus what completed trades took — the same arithmetic as Sky. Quest
    /// REWARDS never appear in the ledger (no loot line), so the view also
    /// asks the inventory dump.</summary>
    public int LedgerHeld(string item)
    {
        if (_loot is null) return 0;
        string key = LootTracker.ItemKey(item);
        int looted = _loot.Entries.Where(e => e.Kind == LootTracker.LootKind.Kept && LootTracker.ItemKey(e.Item) == key)
            .Sum(e => Math.Max(1, e.Count));
        return Math.Max(0, looted - _offered.GetValueOrDefault(key) - _destroyed.GetValueOrDefault(key));
    }

    // ---------------------------------------------------------------- the log

    public void ProcessLine(string rawLine)
    {
        string body = TimestampPrefix.Replace(rawLine, "", 1);
        if (body.Length == 0) return;
        var when = LineTime(rawLine);
        bool changed = false;

        if (body.StartsWith("You have slain ", StringComparison.Ordinal) && SlainRx.Match(body) is { Success: true } sm)
            changed = Satisfy(cond: "kill:", value: sm.Groups["mob"].Value, evidence: body, when, MatchKill);
        else if (body.EndsWith("!", StringComparison.Ordinal) && SlainByRx.Match(body) is { Success: true } sbm)
            changed = Satisfy("kill:", sbm.Groups["mob"].Value, body, when, MatchKill);
        else if ((body.StartsWith("You looted ", StringComparison.Ordinal) || body.StartsWith("--You have looted ", StringComparison.Ordinal))
                 && LootRx.Match(body) is { Success: true } lm)
        {
            string item = Regex.Replace(lm.Groups["item"].Value, @"^\d+ ", "");
            changed = Satisfy("loot:", LootTracker.ItemKey(item), body, when, MatchLoot);
        }
        else if (body.StartsWith("You say, '", StringComparison.Ordinal) && SayRx.Match(body) is { Success: true } saym)
            changed = Satisfy("say", saym.Groups["t"].Value.Trim().TrimEnd('.', '!'), body, when, MatchSay);
        else if (body.StartsWith("You offered ", StringComparison.Ordinal) && OfferRx.Match(body) is { Success: true } om)
        {
            string itemKey = LootTracker.ItemKey(om.Groups["item"].Value);
            if (!_handinKeys.Contains(itemKey) || _offerSeen.Contains(rawLine)) return;
            string npc = om.Groups["npc"].Value;
            if (!_pending.TryGetValue(npc, out var list)) _pending[npc] = list = new List<PendingOffer>();
            list.RemoveAll(p => (when - p.At).TotalSeconds > TradeWindowSec);
            if (list.All(p => p.RawLine != rawLine))
                list.Add(new PendingOffer(when, itemKey, Math.Max(1, int.Parse(om.Groups["n"].Value)), rawLine));
            return;
        }
        else if (body.StartsWith("You complete the trade with ", StringComparison.Ordinal) && TradeDoneRx.Match(body) is { Success: true } tm)
            changed = CommitTrade(tm.Groups["npc"].Value, body, when);
        else if (body.StartsWith("You successfully destroyed ", StringComparison.Ordinal) && DestroyRx.Match(body) is { Success: true } dm)
        {
            string key = LootTracker.ItemKey(dm.Groups["item"].Value);
            if (_handinKeys.Contains(key) && _destroySeen.Add(rawLine))
            {
                _destroyed[key] = _destroyed.GetValueOrDefault(key) + Math.Max(1, int.Parse(dm.Groups["n"].Value));
                changed = true;
            }
        }

        if (changed)
        {
            SaveProgress();
            Changed?.Invoke();
        }
    }

    private static bool MatchKill(Step s, string mob) =>
        s.Kill.Any(k => k.Equals(mob, StringComparison.OrdinalIgnoreCase));
    private static bool MatchLoot(Step s, string itemKey) =>
        s.Loot.Any(l => LootTracker.ItemKey(l) == itemKey);
    private static bool MatchSay(Step s, string said) =>
        s.Say.Length > 0 && s.Say.Trim().TrimEnd('.', '!').Equals(said, StringComparison.OrdinalIgnoreCase);

    /// <summary>Record one satisfied condition on every unfinished step it
    /// fits, then see whether the step is now proven.</summary>
    private bool Satisfy(string cond, string value, string evidence, DateTime when, Func<Step, string, bool> fits)
    {
        bool changed = false;
        foreach (var q in _quests)
            foreach (var s in q.Steps)
            {
                if (IsDone(q, s) || !fits(s, value)) continue;
                string key = StepKey(q, s);
                if (!_partial.TryGetValue(key, out var set)) _partial[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string condKey = cond.EndsWith(':') ? cond + value.ToLowerInvariant() : cond;
                if (!set.Add(condKey)) continue; // replay: already counted
                AddEvidence(key, $"{evidence} · {when:d MMM HH:mm}");
                changed = true;
                TryProve(q, s, when);
            }
        return changed;
    }

    private bool CommitTrade(string npc, string evidence, DateTime when)
    {
        _pending.Remove(npc, out var list);
        var sealedKeys = new List<string>();
        if (list is not null)
            foreach (var p in list)
            {
                if ((when - p.At).TotalSeconds > TradeWindowSec) continue;
                if (!_offerSeen.Add(p.RawLine)) continue;
                _offered[p.ItemKey] = _offered.GetValueOrDefault(p.ItemKey) + p.N;
                sealedKeys.Add(p.ItemKey);
            }

        bool changed = sealedKeys.Count > 0;
        foreach (var q in _quests)
            foreach (var s in q.Steps)
            {
                if (IsDone(q, s) || !s.HasTrade) continue;
                if (!s.NpcNames.Any(n => n.Equals(npc, StringComparison.OrdinalIgnoreCase))) continue;
                string key = StepKey(q, s);
                if (!_partial.TryGetValue(key, out var set)) _partial[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool any = false;
                foreach (var k in sealedKeys)
                    if (s.Handin.Any(h => LootTracker.ItemKey(h.Name) == k) && set.Add("offer:" + k)) any = true;
                // Coins-only: the trade itself is the proof — but coins leave
                // no offer line, and both swords pay the same Dason Goldblade,
                // so it only counts for a line you have already started.
                if (s.Handin.Count == 0 && DoneCount(q) > 0 && set.Add("trade")) any = true;
                if (!any) continue;
                AddEvidence(key, $"{evidence} · {when:d MMM HH:mm}");
                changed = true;
                TryProve(q, s, when);
            }
        return changed;
    }

    private void AddEvidence(string key, string line)
    {
        if (!_evidence.TryGetValue(key, out var lines)) _evidence[key] = lines = new List<string>();
        if (lines.Count < 12 && !lines.Contains(line)) lines.Add(line);
    }

    /// <summary>All of a step's log-visible conditions met → done, unless it
    /// still waits on the owner's right-click tick.</summary>
    private void TryProve(Quest q, Step s, DateTime when)
    {
        string key = StepKey(q, s);
        var set = _partial.GetValueOrDefault(key);
        if (set is null) return;
        foreach (var k in s.Kill) if (!set.Contains("kill:" + k.ToLowerInvariant())) return;
        foreach (var l in s.Loot) if (!set.Contains("loot:" + LootTracker.ItemKey(l))) return;
        if (s.Say.Length > 0 && !set.Contains("say")) return;
        if (s.Handin.Count > 0)
        {
            foreach (var h in s.Handin)
                if (!set.Contains("offer:" + LootTracker.ItemKey(h.Name))) return;
        }
        else if (s.Coins.Length > 0 && !set.Contains("trade")) return; // coins-only: the trade line itself
        if (s.Click.Length > 0 && !set.Contains("click")) return; // the tick flips it
        _marks[key] = new Mark(when, "auto", string.Join(" · ", _evidence.GetValueOrDefault(key) ?? new List<string>()));
        ImplyEarlier(q, s, when);
        StepDone?.Invoke(q, s);
    }

    /// <summary>A proven step vouches for everything before it.</summary>
    private void ImplyEarlier(Quest q, Step s, DateTime when)
    {
        int ix = q.Steps.IndexOf(s);
        for (int i = 0; i < ix; i++)
        {
            string key = StepKey(q, q.Steps[i]);
            if (_marks.ContainsKey(key)) continue;
            _marks[key] = new Mark(when, "implied", $"implied by step {ix + 1}: {s.Title}");
        }
    }

    /// <summary>True once the log has proven everything but the right-click.</summary>
    public bool AwaitsClick(Quest q, Step s)
    {
        if (s.Click.Length == 0 || IsDone(q, s)) return false;
        var set = _partial.GetValueOrDefault(StepKey(q, s));
        if (set is null) return false;
        return s.Kill.All(k => set.Contains("kill:" + k.ToLowerInvariant()))
            && s.Loot.All(l => set.Contains("loot:" + LootTracker.ItemKey(l)));
    }

    private static DateTime LineTime(string rawLine)
    {
        var m = TimestampPrefix.Match(rawLine);
        return m.Success && DateTime.TryParseExact(m.Groups["ts"].Value,
            new[] { "ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" },
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t)
            ? t : DateTime.Now;
    }
}
