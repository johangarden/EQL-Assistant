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
        /// <summary>An item to right-click. The owner's switch proves it — or <see cref="ClickText"/> does.</summary>
        public string Click { get; set; } = "";
        /// <summary>The line the game prints on the right-click (its opening words) — the souls do print one
        /// (owner, 7 Oct: "As you stare into the fading embers within your palm, you are haunted by the visage
        /// of Brother Hayle…"); when set, the log proves the click and no switch is needed.</summary>
        public string ClickText { get; set; } = "";
        public string Say { get; set; } = "";
        public string Reply { get; set; } = "";
        public string Faction { get; set; } = "";

        // ---- epics (25 Nov): what a twenty-to-forty-step class quest needs beyond a weapon line ----
        /// <summary>The named part of the quest this step belongs to ("The Dark Shroud"). An epic's
        /// parts run side by side, so a proven step vouches only for the steps of its own part.</summary>
        public string Part { get; set; } = "";
        /// <summary>Not required: never counted, never implied by a later step.</summary>
        public bool Optional { get; set; }
        /// <summary>The thing that goes wrong here ("con indifferent first") — its own amber line.</summary>
        public string Caution { get; set; } = "";
        /// <summary>Any ONE of these kills will do (Fright, Dread or Terror).</summary>
        public List<string> KillAny { get; set; } = new();
        /// <summary>What a combine produces, when the wiki names it — proven by the game's own
        /// "You have fashioned the items together to create something new: X." line.</summary>
        public List<string> Make { get; set; } = new();
        [JsonIgnore] public IEnumerable<string> NpcNames => Npcs.Count > 0 ? Npcs : new[] { Npc };
        [JsonIgnore] public bool HasTrade => Handin.Count > 0 || Coins.Length > 0;
        [JsonIgnore] public IEnumerable<string> KillNames => Kill.Concat(KillAny);
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
        /// <summary>"" = the Notable quests pack, "epics" = the class epics (25 Nov).</summary>
        public string Pack { get; set; } = "";
        /// <summary>A caution for the WHOLE quest (the shadow knight's "con indifferent on every hand-in").</summary>
        public string Warning { get; set; } = "";
        /// <summary>Who starts it and where ("Kurron Ni · The Overthere").</summary>
        public string Start { get; set; } = "";
        /// <summary>The quest ends in TWO rewards that finish apart (the ranger's Swiftwind and
        /// Earthcaller): no step vouches for the whole quest, only for what made its items.</summary>
        public bool Parallel { get; set; }
        public List<Step> Steps { get; set; } = new();
    }

    private sealed class Doc
    {
        public string Credit { get; set; } = "";
        public string Pack { get; set; } = "";
        public string Notice { get; set; } = "";
        public string Footer { get; set; } = "";
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
    private static readonly Regex OfferRx = new(@"^You offered (?<n>[\d,]+) (?<item>.+?) to (?<npc>.+?)\.$", RegexOptions.Compiled); // "1,000 Platinum" too
    private static readonly Regex TradeDoneRx = new(@"^You complete the trade with (?<npc>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex DestroyRx = new(@"^You successfully destroyed (?<n>\d+) (?<item>.+?)\.$", RegexOptions.Compiled);
    // A combine's own line (the tradeskill helper reads the same one): the product is named.
    private static readonly Regex MakeRx = new(
        @"^You have fashioned the items together to create (?:something new|an alternate product): (?<item>.+?)\.$", RegexOptions.Compiled);

    private const double TradeWindowSec = 300;

    /// <summary>An item name as the log and the wiki disagree on it (owner, 6 Oct — two SoulFires
    /// and a Fiery Avenger the tracker lost): lower-case, the +N tier off, backtick and curly
    /// apostrophes as ', hyphens as spaces, one space between words, and no leading article —
    /// the wiki's "A Spider Venom Sac" is the game's "Spider Venom Sac", its "Torn, Frost covered
    /// book" the game's "Torn, Frost-Covered Book".</summary>
    public static string Key(string item)
    {
        string k = LootTracker.ItemKey(item).Replace('`', '\'').Replace('\u2019', '\'').Replace('-', ' ');
        k = Regex.Replace(k, @"\s+", " ").Trim();
        return Regex.Replace(k, @"^(?:a|an|the) ", "");
    }

    /// <summary>A mob or NPC name as the log prints it against the wiki's: the game marks quest
    /// NPCs with a leading * ("*Inte Akera", "You have slain *Guard Willia!") and writes
    /// apostrophes as backticks ("Sir Lucan D`Lere", "Rysva To`Biath").</summary>
    public static string Who(string name)
    {
        string w = name.Trim().TrimStart('*').Trim().Replace('`', '\'').Replace('\u2019', '\'');
        return Regex.Replace(w, @"\s+", " ").ToLowerInvariant();
    }
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
    private readonly List<string> _clickOpeners = new(); // the first words shared by the click lines — a cheap gate
    private readonly LootTracker? _loot;

    public IReadOnlyList<Quest> Quests => _quests;
    public string Credit { get; private set; } = "";

    private readonly Dictionary<string, (string Notice, string Footer)> _packNotes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>One pack's quests: "" = Notable quests, "epics" = the class epics.</summary>
    public IReadOnlyList<Quest> QuestsIn(string pack) => _quests.Where(q => q.Pack.Equals(pack, StringComparison.OrdinalIgnoreCase)).ToList();
    /// <summary>The pack's standing notice (the epics' "unverified before Kunark" line) and its list footer.</summary>
    public string NoticeOf(string pack) => _packNotes.GetValueOrDefault(pack).Notice ?? "";
    public string FooterOf(string pack) => _packNotes.GetValueOrDefault(pack).Footer ?? "";

    public event Action? Changed;
    /// <summary>A step just proved itself from the log (never on ticks or replays of known marks).</summary>
    public event Action<Quest, Step>? StepDone;

    /// <summary>A quest line's loot item just dropped into your bags — whether
    /// or not the line is started (owner, 17 Sep: he destroyed the Glowing
    /// Sword Hilt with the junk; the app should have said what it was).</summary>
    public event Action<Quest, Step, string>? ItemLooted;
    /// <summary>You destroyed an item an unfinished step still wants.</summary>
    public event Action<Quest, Step, string>? ItemDestroyed;

    /// <summary>The first unfinished step wanting this item as loot or hand-in.</summary>
    public (Quest Quest, Step Step, int Index)? StepWanting(string item)
    {
        string key = Key(item);
        foreach (var q in _quests)
            for (int i = 0; i < q.Steps.Count; i++)
            {
                var s = q.Steps[i];
                if (IsDone(q, s)) continue;
                if (s.Loot.Any(l => Key(l) == key) || s.Handin.Any(h => Key(h.Name) == key))
                    return (q, s, i);
            }
        return null;
    }

    public QuestLines(ConfigService config, LootTracker? loot = null, string? progressPathOverride = null)
    {
        _loot = loot;
        _progressPath = progressPathOverride ?? Path.Combine(config.ConfigDirectory, "quest-lines-progress.json");
        LoadQuests();
        foreach (var q in _quests)
            foreach (var s in q.Steps)
            {
                foreach (var n in s.Handin)
                    _handinKeys.Add(Key(n.Name));
                if (s.ClickText.Length > 0)
                {
                    string opener = s.ClickText.Length > 24 ? s.ClickText[..24] : s.ClickText;
                    if (!_clickOpeners.Contains(opener)) _clickOpeners.Add(opener);
                }
            }
        LoadProgress();
        Sanitize();
    }

    // ---------------------------------------------------------------- data

    private void LoadQuests()
    {
        LoadFile("quest-lines.json");
        LoadFile("epic-quests.json"); // the class epics — their own pack (25 Nov)
    }

    private void LoadFile(string name)
    {
        try
        {
            string diskPath = Path.Combine(AppContext.BaseDirectory, "data", name);
            string? json = null;
            if (File.Exists(diskPath)) json = File.ReadAllText(diskPath);
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                string? res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
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
            if (Credit.Length == 0) Credit = doc.Credit;
            _packNotes[doc.Pack] = (doc.Notice, doc.Footer);
            foreach (var q in doc.Quests.Where(q => q.Key.Length > 0 && q.Steps.Count > 0))
            {
                if (q.Pack.Length == 0) q.Pack = doc.Pack;
                if (_quests.All(x => x.Key != q.Key)) _quests.Add(q);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{name} failed to load: {ex.Message}");
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

    /// <summary>The steps that count: an OPTIONAL step is never part of the tally.</summary>
    public int RequiredCount(Quest q) => q.Steps.Count(s => !s.Optional);
    public int DoneCount(Quest q) => q.Steps.Count(s => !s.Optional && IsDone(q, s));
    public bool IsComplete(Quest q) => q.Steps.All(s => s.Optional || IsDone(q, s));
    public Step? NextStep(Quest q) => q.Steps.FirstOrDefault(s => !s.Optional && !IsDone(q, s));

    /// <summary>A step whose proof is an act of quest INTENT: a sealed hand-in
    /// of quest items, or a said keyword. Kills and drops happen for a hundred
    /// other reasons — Xicotl and the Glowing Sword Hilt are a Plane of Hate
    /// evening, Dark Reavers drop all night in Guk — so they vouch for nothing
    /// but themselves, and only once the line is started (owner, 17 Sep: two
    /// lines "in progress" he had never begun).</summary>
    public static bool Anchors(Step s) => s.Handin.Count > 0 || s.Say.Length > 0 || s.Make.Count > 0; // a combine of quest items is intent too

    /// <summary>The line was begun on purpose: an owner tick anywhere, or the
    /// log proving an anchoring step.</summary>
    public bool Started(Quest q) => q.Steps.Any(s => _marks.TryGetValue(StepKey(q, s), out var m)
        && (m.How == "you" || (m.How == "auto" && (Anchors(s) || s.Click.Length > 0)))); // an auto mark on a click step came from the item's own line

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
        ReproveWaiting(q, when ?? DateTime.Now); // the tick started the line: drops already in hand now count
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
        string key = Key(item);
        int looted = _loot.Entries.Where(e => e.Kind == LootTracker.LootKind.Kept && Key(e.Item) == key)
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
            changed = Satisfy(cond: "kill:", value: Who(sm.Groups["mob"].Value), evidence: body, when, MatchKill);
        else if (body.EndsWith("!", StringComparison.Ordinal) && SlainByRx.Match(body) is { Success: true } sbm)
            changed = Satisfy("kill:", Who(sbm.Groups["mob"].Value), body, when, MatchKill);
        else if ((body.StartsWith("You looted ", StringComparison.Ordinal) || body.StartsWith("--You have looted ", StringComparison.Ordinal))
                 && LootRx.Match(body) is { Success: true } lm)
        {
            string item = Regex.Replace(lm.Groups["item"].Value, @"^\d+ ", "");
            if (StepWanting(item) is { } want) ItemLooted?.Invoke(want.Quest, want.Step, item);
            changed = Satisfy("loot:", Key(item), body, when, MatchLoot);
        }
        else if (_clickOpeners.Any(o => body.StartsWith(o, StringComparison.Ordinal)))
            changed = Satisfy("click", body, body, when, MatchClick);
        else if (body.StartsWith("You say, '", StringComparison.Ordinal) && SayRx.Match(body) is { Success: true } saym)
            changed = Satisfy("say", saym.Groups["t"].Value.Trim().TrimEnd('.', '!', '?'), body, when, MatchSay);
        else if (body.StartsWith("You offered ", StringComparison.Ordinal) && OfferRx.Match(body) is { Success: true } om)
        {
            string itemKey = Key(om.Groups["item"].Value);
            // A seen offer still goes to the pending list: the ledger counts it once (CommitTrade),
            // but a replay must be able to prove a step the first pass couldn't match (owner, 7 Oct:
            // the offers to *Inte Akera were "seen" by the old matching and the reparse skipped them).
            if (!_handinKeys.Contains(itemKey)) return;
            string npc = Who(om.Groups["npc"].Value);
            if (!_pending.TryGetValue(npc, out var list)) _pending[npc] = list = new List<PendingOffer>();
            list.RemoveAll(p => (when - p.At).TotalSeconds > TradeWindowSec);
            if (list.All(p => p.RawLine != rawLine))
                list.Add(new PendingOffer(when, itemKey, Math.Max(1, int.Parse(om.Groups["n"].Value.Replace(",", ""))), rawLine));
            return;
        }
        else if (body.StartsWith("You complete the trade with ", StringComparison.Ordinal) && TradeDoneRx.Match(body) is { Success: true } tm)
            changed = CommitTrade(Who(tm.Groups["npc"].Value), body, when);
        else if (body.StartsWith("You have fashioned the items together", StringComparison.Ordinal) && MakeRx.Match(body) is { Success: true } mk)
            changed = Satisfy("make:", Key(mk.Groups["item"].Value), body, when, MatchMake);
        else if (body.StartsWith("You successfully destroyed ", StringComparison.Ordinal) && DestroyRx.Match(body) is { Success: true } dm)
        {
            string key = Key(dm.Groups["item"].Value);
            bool fresh = !_destroySeen.Contains(rawLine);
            if (fresh && StepWanting(dm.Groups["item"].Value) is { } gone) ItemDestroyed?.Invoke(gone.Quest, gone.Step, dm.Groups["item"].Value);
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
        s.KillNames.Any(k => Bare(Who(k)) == Bare(Who(mob)));

    /// <summary>A mob's name without its leading article: the wiki's "Spiroc Lord" is the game's
    /// "The Spiroc Lord", its "Amygdalan warrior" the log's "an amygdalan warrior" (25 Nov).</summary>
    private static string Bare(string who) => Regex.Replace(who, @"^(?:a|an|the) ", "");

    /// <summary>Has the log seen this mob die for the step? (Article-blind; the stored key keeps the log's own spelling.)</summary>
    public static bool KillSeen(IEnumerable<string> partial, string mob)
    {
        string want = Bare(Who(mob));
        foreach (var c in partial)
            if (c.StartsWith("kill:", StringComparison.OrdinalIgnoreCase) && Bare(c[5..]) == want) return true;
        return false;
    }
    private static bool MatchMake(Step s, string itemKey) =>
        s.Make.Any(m => Key(m) == itemKey);
    private static bool MatchLoot(Step s, string itemKey) =>
        s.Loot.Any(l => Key(l) == itemKey);
    private static bool MatchClick(Step s, string line) =>
        s.ClickText.Length > 0 && line.StartsWith(s.ClickText, StringComparison.OrdinalIgnoreCase);
    private static bool MatchSay(Step s, string said) =>
        s.Say.Length > 0 && s.Say.Trim().TrimEnd('.', '!', '?').Equals(said, StringComparison.OrdinalIgnoreCase);

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
        bool changed = false;
        if (list is not null)
            foreach (var p in list)
            {
                if ((when - p.At).TotalSeconds > TradeWindowSec) continue;
                // The ledger arithmetic counts each offer line once; the step proof below is a set and dedupes itself.
                if (_offerSeen.Add(p.RawLine)) { _offered[p.ItemKey] = _offered.GetValueOrDefault(p.ItemKey) + p.N; changed = true; }
                sealedKeys.Add(p.ItemKey);
            }
        foreach (var q in _quests)
            foreach (var s in q.Steps)
            {
                if (IsDone(q, s) || !s.HasTrade) continue;
                if (!s.NpcNames.Any(n => Who(n) == npc)) continue;
                string key = StepKey(q, s);
                if (!_partial.TryGetValue(key, out var set)) _partial[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool any = false;
                foreach (var k in sealedKeys)
                    if (s.Handin.Any(h => Key(h.Name) == k) && set.Add("offer:" + k)) any = true;
                // Coins-only: the trade itself is the proof — but coins leave
                // no offer line, and both swords pay the same Dason Goldblade,
                // so it only counts for a line you have already started.
                if (s.Handin.Count == 0 && Started(q) && set.Add("trade")) any = true;
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
        foreach (var k in s.Kill) if (!KillSeen(set, k)) return;
        if (s.KillAny.Count > 0 && !s.KillAny.Any(k => KillSeen(set, k))) return; // any one of them
        foreach (var l in s.Loot) if (!set.Contains("loot:" + Key(l))) return;
        foreach (var mk in s.Make) if (!set.Contains("make:" + Key(mk))) return;
        if (s.Say.Length > 0 && !set.Contains("say")) return;
        if (s.Handin.Count > 0)
        {
            foreach (var h in s.Handin)
                if (!set.Contains("offer:" + Key(h.Name))) return;
        }
        else if (s.Coins.Length > 0 && !set.Contains("trade")) return; // coins-only: the trade line itself
        if (s.Click.Length > 0 && !set.Contains("click")) return; // the switch flips it — or the item's own line
        // A kill or a drop alone never starts a line — the evidence waits in
        // the partial set until an anchoring step (or a tick) begins it. A
        // right-click proven by the item's own line IS intent: you clicked the quest item.
        bool intent = Anchors(s) || s.Click.Length > 0;
        if (!intent && !Started(q)) return;
        _marks[key] = new Mark(when, "auto", string.Join(" · ", _evidence.GetValueOrDefault(key) ?? new List<string>()));
        bool last = s.Part.Length > 0 && ReferenceEquals(s, q.Steps.LastOrDefault(x => !x.Optional));
        if (intent || last) ImplyEarlier(q, s, when); // only intent vouches for the chain before it — or an epic's final step
        StepDone?.Invoke(q, s);
        if (intent) ReproveWaiting(q, when);
    }

    /// <summary>The line just started: steps whose kills and drops were already
    /// in the log get their marks now.</summary>
    private void ReproveWaiting(Quest q, DateTime when)
    {
        foreach (var o in q.Steps.ToList())
            if (!IsDone(q, o) && _partial.ContainsKey(StepKey(q, o)))
                TryProve(q, o, when);
    }

    /// <summary>Progress written under the old rule (any proven step implied
    /// the whole chain before it) is re-judged on load: implied marks that a
    /// non-anchoring, un-ticked step made are withdrawn, then auto marks on
    /// non-anchoring steps of lines never started. Logged, saved once.</summary>
    private void Sanitize()
    {
        int removed = 0;
        foreach (var q in _quests)
        {
            foreach (var s in q.Steps)
            {
                string key = StepKey(q, s);
                if (_marks.GetValueOrDefault(key) is not { How: "implied" } m) continue;
                var mm = Regex.Match(m.Evidence, @"implied by step (\d+)");
                if (!mm.Success || !int.TryParse(mm.Groups[1].Value, out int n) || n < 1 || n > q.Steps.Count) continue;
                var src = q.Steps[n - 1];
                bool byTick = _marks.GetValueOrDefault(StepKey(q, src)) is { How: "you" };
                bool lastOfEpic = src.Part.Length > 0 && ReferenceEquals(src, q.Steps.LastOrDefault(x => !x.Optional));
                if (!Anchors(src) && src.Click.Length == 0 && !byTick && !lastOfEpic) { _marks.Remove(key); removed++; }
            }
            if (Started(q)) continue;
            foreach (var s in q.Steps)
            {
                string key = StepKey(q, s);
                if (_marks.GetValueOrDefault(key) is { How: "auto" } && !Anchors(s)) { _marks.Remove(key); removed++; }
            }
        }
        if (removed == 0) return;
        Log.Info($"[lines] {removed} step mark(s) withdrawn on load — kills and drops alone no longer start a line");
        SaveProgress();
    }

    /// <summary>A proven step vouches for everything before it.</summary>
    private void ImplyEarlier(Quest q, Step s, DateTime when)
    {
        int ix = q.Steps.IndexOf(s);
        // An epic's parts run side by side, and inside a part several chains merge (ink, pen and
        // paper for one hand-in) — so a step of a PARTED quest vouches only for the steps that
        // MADE what it consumed: whoever looted, was given or combined the items it hands in or
        // uses, and in turn what those consumed. The quest's last step vouches for everything.
        if (s.Part.Length > 0 && (q.Parallel || !ReferenceEquals(s, q.Steps.LastOrDefault(x => !x.Optional))))
        {
            var want = new Queue<string>(s.Handin.Concat(s.Bring).Select(n => Key(n.Name)));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (want.Count > 0)
            {
                string item = want.Dequeue();
                if (!seen.Add(item)) continue;
                for (int i = 0; i < ix; i++)
                {
                    var maker = q.Steps[i];
                    if (!maker.Loot.Concat(maker.Get).Concat(maker.Make).Any(x => Key(x) == item)) continue;
                    foreach (var n in maker.Handin.Concat(maker.Bring)) want.Enqueue(Key(n.Name));
                    string mk = StepKey(q, maker);
                    if (!_marks.ContainsKey(mk))
                        _marks[mk] = new Mark(when, "implied", $"implied by step {ix + 1}: {s.Title}");
                }
            }
            return;
        }
        for (int i = 0; i < ix; i++)
        {
            var earlier = q.Steps[i];
            if (earlier.Optional) continue; // nobody can know an optional step was done
            string key = StepKey(q, earlier);
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
        return s.Kill.All(k => KillSeen(set, k))
            && (s.KillAny.Count == 0 || s.KillAny.Any(k => KillSeen(set, k)))
            && s.Loot.All(l => set.Contains("loot:" + Key(l)));
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
