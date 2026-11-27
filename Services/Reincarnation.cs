using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The reincarnation planner (26 Nov, ahead of Kunark on 8 Dec 2026). Every class and every race has
/// three reincarnation ranks; a run reincarnates ONE race and THREE classes, so all 96 ranks take at
/// least 48 runs. THE RULE (the official Kunark FAQ as the community planner at witty-waypoint reports
/// it — unverified in game): each finished reincarnation adds +10% to the XP that class or race needs,
/// the HIGHEST of the run's four components sets the rate (never the sum), capped at +300%; AA XP is
/// untouched; a fourth run earns no fourth rank but still counts.
///
/// What follows from it: 48 forced runs are 144 class slots over 16 classes — nine uses each however
/// you order them — so every balanced order averages about +41% and peaks at +80%. The order cannot
/// lower the bill; it only decides WHICH ranks come early, and that is what the owner's priorities
/// steer (most → least important classes and races, the first three classes being the priority trio).
///
/// The plan is greedy, one run at a time: score = the priority weight of every new rank the run earns
/// − β × its penalty, with β by mode (low 100 · balanced 1.2 · mine 0.05), a small pull toward
/// favourite classes on repeats and toward even use. No class runs past the even share (the ceiling of
/// all class slots over 16) unless nothing else fits. Races not unlocked yet wait until the unlocked
/// ones are finished. Progress is ticked by hand — no log line for a finished reincarnation is known.
///
/// Rulers of Kunark (the official FAQ, 27 Nov): you reincarnate at the level cap, 55. Levels are per class
/// and a loadout stands at its lowest class, so a class not yet reincarnated keeps a head start
/// (<see cref="State.Levels"/>) that only counts when all three of a run's classes have one — the plan
/// takes those trios whole (a run's <see cref="PlanRun.From"/>), and three classes at the cap are ready.
/// </summary>
public sealed class Reincarnation
{
    public static readonly string[] Classes = BisFinder.AllClasses;

    /// <summary>The sixteen races: the code on the chips and the name Race unlocks knows it by.</summary>
    public static readonly (string Code, string Name)[] RaceTable =
    {
        ("BAR", "Barbarian"), ("DEF", "Dark Elf"), ("DWF", "Dwarf"), ("ERU", "Erudite"), ("FRG", "Froglok"), ("GNM", "Gnome"),
        ("HDE", "Half Dark Elf"), ("HEF", "Half Elf"), ("HFL", "Halfling"), ("HIE", "High Elf"), ("HUM", "Human"), ("IKS", "Iksar"),
        ("KRN", "Kerran"), ("OGR", "Ogre"), ("TRL", "Troll"), ("WEF", "Wood Elf"),
    };
    public static readonly string[] Races = RaceTable.Select(r => r.Code).ToArray();

    public const int Ranks = 3, StepPercent = 10, CapPercent = 300;
    /// <summary>Rulers of Kunark (the official FAQ, 27 Nov): you reincarnate at the level cap, and the cap is 55.</summary>
    public const int LevelCap = 55;
    /// <summary>The FAQ: stat soft caps rise by a fifth of the levels a reincarnation gives up — 11 at 55.</summary>
    public const int SoftcapPerRun = LevelCap / 5;
    public const string RulesUrl = "https://witty-waypoint.pages.dev/reincarnation/";

    public static string RaceName(string code) => RaceTable.FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Name ?? code;
    public static string? RaceCode(string name) => RaceTable.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Code;

    public sealed class DoneRun
    {
        public string Race { get; set; } = "";
        public List<string> Classes { get; set; } = new();
        public DateTime At { get; set; }
    }

    /// <summary>One character's plan inputs: what is done, what came before tracking, what matters.</summary>
    public sealed class State
    {
        public List<string> ClassOrder { get; set; } = new();
        public List<string> RaceOrder { get; set; } = new();
        public string Mode { get; set; } = "balanced";
        public List<DoneRun> Done { get; set; } = new();
        /// <summary>Finished reincarnations from before the tracking began, by class or race code.</summary>
        public Dictionary<string, int> Base { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The level each CLASS stands at now, for as long as no ticked run has reincarnated it
        /// (owner, 27 Nov: "i have all the classes at lvl 50"). Levels are per class and a loadout stands
        /// at its LOWEST class, so this head start only counts when all three of a run's classes have one.
        /// A ticked run puts its classes back at 1 — unticking it gives the head start back.</summary>
        public Dictionary<string, int> Levels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int LevelOf(string code) => Math.Clamp(Levels.GetValueOrDefault(code, 1), 1, LevelCap);

        /// <summary>Finished reincarnations of a class or race: before tracking + ticked runs.</summary>
        public int Count(string code) =>
            Math.Max(0, Base.GetValueOrDefault(code))
            + Done.Count(d => d.Race.Equals(code, StringComparison.OrdinalIgnoreCase) || d.Classes.Contains(code, StringComparer.OrdinalIgnoreCase));

        /// <summary>Set the finished count by hand (the pips): only the before-tracking part moves.</summary>
        public void SetCount(string code, int total)
        {
            int ticked = Count(code) - Math.Max(0, Base.GetValueOrDefault(code));
            int b = Math.Max(0, total - ticked);
            if (b == 0) Base.Remove(code); else Base[code] = b;
        }
    }

    public sealed record PlanRun(int Number, string Race, IReadOnlyList<string> Classes, int Penalty, IReadOnlyList<string> SetBy,
        IReadOnlyList<int> ClassUse, int RaceUse, bool Done, bool Locked)
    {
        /// <summary>The level the loadout stands at before this run: its lowest class. 1 = a full levelling.</summary>
        public int From { get; init; } = 1;
        /// <summary>All three classes at the cap: nothing to level, the reincarnation can be taken as it is.</summary>
        public bool Ready => From >= LevelCap;
        /// <summary>"OGR 3 · SHD 1": the ranks this run earns (a use past the third earns none).</summary>
        public IEnumerable<string> Earns =>
            Classes.Select((c, i) => (c, n: ClassUse[i])).Where(x => x.n <= Ranks).Select(x => $"{x.c} {x.n}")
                .Concat(RaceUse <= Ranks ? new[] { $"{Race} {RaceUse}" } : Array.Empty<string>());
    }

    public sealed record Plan(IReadOnlyList<PlanRun> Runs, double Average, int Peak, IReadOnlyDictionary<string, int> RankThreeAt,
        int AllClassesAt, int TrioAt)
    {
        public PlanRun? Next => Runs.FirstOrDefault(r => !r.Done);
    }

    // ---- what the ranks are worth (eqlwiki's Special AAs table, data/reincarnation.json) ----

    /// <summary>A class's or race's reincarnation line: a passive that grows over the three ranks
    /// and — classes only — the activated ability the third rank grants.</summary>
    public sealed record Ability(string Code, string Kind, string Name, IReadOnlyList<string> Ranks, string Passive, string Active, string ActiveText)
    {
        public bool IsClass => Kind == "class";
    }

    public sealed record AbilityData(IReadOnlyDictionary<string, Ability> ByCode, int SharedCooldownSec, string Notice,
        IReadOnlyList<string> CompletionistRanks, string Completionist);

    private static readonly Lazy<AbilityData> AbilitiesLazy = new(LoadAbilities);
    public static AbilityData Abilities => AbilitiesLazy.Value;
    public static Ability? AbilityOf(string code) => Abilities.ByCode.GetValueOrDefault(code);

    private static AbilityData LoadAbilities()
    {
        var by = new Dictionary<string, Ability>(StringComparer.OrdinalIgnoreCase);
        int shared = 0; string notice = "", comp = ""; var compRanks = new List<string>();
        try
        {
            const string name = "reincarnation.json";
            string disk = Path.Combine(AppContext.BaseDirectory, "data", name);
            string? json = null;
            if (File.Exists(disk)) json = File.ReadAllText(disk);
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                if (asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("data." + name, StringComparison.OrdinalIgnoreCase)) is { } res)
                {
                    using var stream = asm.GetManifestResourceStream(res)!;
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
            }
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string Str(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                List<string> Arr(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                if (root.TryGetProperty("sharedCooldownSec", out var cd) && cd.ValueKind == JsonValueKind.Number) shared = cd.GetInt32();
                notice = Str(root, "notice");
                if (root.TryGetProperty("completionist", out var c) && c.ValueKind == JsonValueKind.Object) { comp = Str(c, "passive"); compRanks = Arr(c, "ranks"); }
                if (root.TryGetProperty("entries", out var entries))
                    foreach (var e in entries.EnumerateArray())
                    {
                        string code = Str(e, "code");
                        if (code.Length > 0) by[code] = new Ability(code, Str(e, "kind"), Str(e, "name"), Arr(e, "ranks"), Str(e, "passive"), Str(e, "active"), Str(e, "activeText"));
                    }
            }
        }
        catch (Exception ex) { Log.Warn("reincarnation abilities: " + ex.Message); }
        return new AbilityData(by, shared, notice, compRanks, comp);
    }

    // ---- what you have now (owner, 26 Nov: "the sum of bonus for the completed reincarnations") ----

    /// <summary>One earned passive at its current rank: "Increases … lifetap spells by 6%".</summary>
    public sealed record Earned(string Code, string Name, int Rank, string Value, string Effect, bool IsClass);

    /// <summary>Everything the finished reincarnations give right now: the passives at their rank,
    /// the activated abilities of classes at rank three, the few stats several races share added
    /// up, and the Completionist rank (every class at that rank or better).</summary>
    public sealed record Summary(IReadOnlyList<Earned> Passives, IReadOnlyList<Ability> Activated, IReadOnlyList<string> Totals, int CompletionistRank, string Completionist);

    /// <summary>A passive at a rank. The wiki words each one for rank I ("by 3%") and gives the three
    /// values beside it: a value that says what it is ("+4 HP Gen +10 AC") stands alone; a bare
    /// figure ("6", "20%") takes the place of the ONE number in the sentence — two numbers and a
    /// bare figure cannot be matched up, so the value is shown beside the wiki's words instead.</summary>
    public static string EffectAt(Ability a, int rank)
    {
        if (a.Ranks.Count == 0) return a.Passive;
        string value = a.Ranks[Math.Clamp(rank, 1, a.Ranks.Count) - 1].Trim();
        if (Regex.IsMatch(value, "[A-Za-z]")) return value;
        string sentence = a.Passive;
        int stop = sentence.IndexOf(". ", StringComparison.Ordinal);
        if (stop >= 0) sentence = sentence[..stop];
        sentence = sentence.Trim().TrimEnd('.');
        var lead = Regex.Match(sentence, @"^This passive ability (will )?(\w+)");
        if (lead.Success)
        {
            string verb = lead.Groups[2].Value;
            if (lead.Groups[1].Success && !verb.EndsWith('s')) verb += "s"; // "will increase" reads "Increases", like the rest
            sentence = char.ToUpperInvariant(verb[0]) + verb[1..] + sentence[lead.Length..];
        }
        var numbers = Regex.Matches(sentence, @"\d+%?");
        string figure = Regex.Match(value, @"\d+").Value;
        if (numbers.Count != 1 || figure.Length == 0) return $"{value} — {sentence}";
        var at = numbers[0];
        return sentence[..at.Index] + figure + (at.Value.EndsWith('%') ? "%" : "") + sentence[(at.Index + at.Length)..];
    }

    public static Summary Summarize(State s)
    {
        var passives = new List<Earned>();
        var activated = new List<Ability>();
        foreach (string code in s.ClassOrder.Concat(s.RaceOrder))
        {
            int have = Math.Min(Ranks, s.Count(code));
            if (have == 0 || AbilityOf(code) is not { } a) continue;
            passives.Add(new Earned(code, a.Name, have, a.Ranks.Count >= have ? a.Ranks[have - 1].Trim() : "", EffectAt(a, have), a.IsClass));
            if (a.IsClass && have >= Ranks && a.Active.Length > 0) activated.Add(a);
        }
        // The few gains that are the same stat from several races: worth one figure.
        var totals = new List<string>();
        void Total(string label, string pattern)
        {
            var parts = new List<(string Name, int N)>();
            foreach (var e in passives.Where(x => !x.IsClass))
                if (Regex.Match(e.Value + " | " + e.Effect, pattern, RegexOptions.IgnoreCase) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int n)) parts.Add((e.Name, n));
            if (parts.Count >= 2) totals.Add($"{label} +{parts.Sum(x => x.N)} in all ({string.Join(" · ", parts.Select(x => $"{x.Name} +{x.N}"))})");
        }
        Total("Hit point regeneration", @"\+(\d+) HP gen");
        Total("Base stat caps", @"base stat caps(?: and all resists)? by (\d+)");
        int comp = Classes.Min(c => Math.Min(Ranks, s.Count(c)));
        var data = Abilities;
        string compText = comp > 0 && data.CompletionistRanks.Count >= comp && data.Completionist.Length > 0
            ? EffectAt(new Ability("", "class", "Completionist", data.CompletionistRanks, data.Completionist, "", ""), comp) : "";
        return new Summary(passives, activated, totals, compText.Length > 0 ? comp : 0, compText);
    }

    // ---- the stat sheet (owner, 26 Nov: "list all the bonuses … highlight once gained … more like a stat
    // sheet than text") — one row per STAT, every giver added up, the figures read from the wiki's table ----

    /// <summary>The Completionist line's "source": every class together.</summary>
    public const string EveryClass = "ALL";

    /// <summary>A stat the reincarnation ranks move, and who gives it: a class or race code and which
    /// number of its rank value is this stat (Iksar's "+2 HP Gen +5 AC": 0 = HP regen, 1 = armor class).
    /// <paramref name="Less"/> = the stat goes DOWN (damage taken, costs, delays) — shown with a minus.</summary>
    public sealed record StatDef(string Group, string Label, string Unit, bool Less, IReadOnlyList<(string Code, int Index)> Sources)
    {
        public string Format(int n) => (Less ? "−" : "+") + n + Unit;
    }

    public sealed record StatPart(string Code, int Rank, int Have, int Max);

    public sealed record StatRow(StatDef Def, int Have, int Max, IReadOnlyList<StatPart> Parts)
    {
        public string HaveText => Def.Format(Have);
        public string MaxText => Def.Format(Max);
    }

    private static StatDef Up(string group, string label, string unit, params (string, int)[] from) => new(group, label, unit, false, from);
    private static StatDef Down(string group, string label, string unit, params (string, int)[] from) => new(group, label, unit, true, from);

    /// <summary>The sheet's rows, in reading order. The labels are ours — short names for what eqlwiki's
    /// sentences say; the figures are never typed here, they come from the table (<see cref="RankNumbers"/>).</summary>
    public static readonly IReadOnlyList<StatDef> StatSheet = new[]
    {
        Up("OFFENSE", "Melee damage", "%", (EveryClass, 0)),
        Up("OFFENSE", "Chance to hit", "%", ("RNG", 0)),
        Up("OFFENSE", "Skill ability damage", "%", ("ROG", 0)),
        Up("OFFENSE", "Lifetap spell damage", "%", ("SHD", 0)),
        Up("OFFENSE", "Pet damage", "%", ("MAG", 0)),
        Down("OFFENSE", "Pet attack delay", "%", ("BST", 0)),

        Down("DEFENSE", "Melee damage taken", "%", ("WAR", 0)),
        Up("DEFENSE", "Dodge chance", "%", ("MNK", 0)),
        Up("DEFENSE", "Incoming instant heals", "%", ("PAL", 0)),
        Up("DEFENSE", "Armor class", "", ("IKS", 1)),
        Down("DEFENSE", "Cold damage taken", "%", ("BAR", 0)),
        Down("DEFENSE", "Fire damage taken", "%", ("DEF", 0)),
        Down("DEFENSE", "Magic damage taken", "%", ("HIE", 0)),
        Up("DEFENSE", "All resists", "", ("HEF", 0)),

        Up("RESIST CHANCES", "Stun resist chance", "%", ("OGR", 0)),
        Up("RESIST CHANCES", "Fear resist chance", "%", ("DWF", 0)),
        Up("RESIST CHANCES", "Charm & mez resist chance", "%", ("GNM", 0)),
        Down("RESIST CHANCES", "Snares & movement slows", "%", ("HFL", 0)),

        Down("SPELLS & COSTS", "Spell cast time", "%", ("DRU", 0)),
        Down("SPELLS & COSTS", "Healing spell mana cost", "%", ("CLR", 0)),
        Down("SPELLS & COSTS", "Direct damage mana cost", "%", ("WIZ", 0)),
        Down("SPELLS & COSTS", "DoT spell cost", "%", ("NEC", 0)),
        Down("SPELLS & COSTS", "Buff mana cost (no heal)", "%", ("SHM", 0)),
        Down("SPELLS & COSTS", "Debuff mana cost (no damage)", "%", ("ENC", 0)),
        Down("SPELLS & COSTS", "Endurance costs", "%", ("BER", 0)),
        Up("SPELLS & COSTS", "Spell & ranged range", " units", ("WEF", 0)),

        Up("REGEN & STATS", "Hit point regeneration", "", ("FRG", 0), ("IKS", 0), ("TRL", 0)),
        Up("REGEN & STATS", "Mana regeneration", "", ("ERU", 0)),
        Up("REGEN & STATS", "Endurance regeneration", "", ("KRN", 0)),
        Up("REGEN & STATS", "Base stat caps", "", ("HUM", 0), ("HEF", 0)),

        Up("UTILITY", "Movement speed", "%", ("BRD", 0)),
        Up("UTILITY", "Jump power", "%", ("FRG", 1)),
        Up("UTILITY", "Safe Fall skill", "", ("KRN", 1)),
        Up("UTILITY", "Tracking & map range", "%", ("HDE", 0)),
    };

    /// <summary>The figure at rank I, II and III: the <paramref name="index"/>-th number in each of the
    /// wiki's three rank values ("+4 HP Gen +10 AC" → 4, 10). Zero where the table has none.</summary>
    public static int[] RankNumbers(string code, int index)
    {
        IReadOnlyList<string> ranks = code == EveryClass ? Abilities.CompletionistRanks : AbilityOf(code)?.Ranks ?? Array.Empty<string>();
        var figures = new int[Ranks];
        for (int i = 0; i < Ranks && i < ranks.Count; i++)
        {
            var m = Regex.Matches(ranks[i], @"\d+");
            if (m.Count > index && int.TryParse(m[index].Value, out int n)) figures[i] = n;
        }
        return figures;
    }

    /// <summary>The whole sheet for a character: every stat with what its givers add up to now and at
    /// most. Completionist stands at the rank every class has reached.</summary>
    public static IReadOnlyList<StatRow> Sheet(State s)
    {
        var rows = new List<StatRow>();
        foreach (var def in StatSheet)
        {
            var parts = new List<StatPart>();
            foreach (var (code, index) in def.Sources)
            {
                int rank = code == EveryClass ? Classes.Min(c => Math.Min(Ranks, s.Count(c))) : Math.Min(Ranks, s.Count(code));
                var figures = RankNumbers(code, index);
                parts.Add(new StatPart(code, rank, rank > 0 ? figures[rank - 1] : 0, figures[Ranks - 1]));
            }
            rows.Add(new StatRow(def, parts.Sum(x => x.Have), parts.Sum(x => x.Max), parts));
        }
        return rows;
    }

    private readonly string? _path;
    private readonly Dictionary<string, State> _all = new(StringComparer.OrdinalIgnoreCase);

    public Reincarnation(string? path)
    {
        _path = path;
        try
        {
            if (path is not null && File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, State>>(File.ReadAllText(path)) is { } d)
                foreach (var kv in d) if (kv.Value is not null) _all[kv.Key] = kv.Value;
        }
        catch { /* a broken file starts over — the plan is cheap to redo */ }
    }

    /// <summary>The character's state, with both orders made whole: a first visit leads with the
    /// classes /who names and with the races already unlocked.</summary>
    public State For(string charKey, IEnumerable<string>? whoClasses = null, IEnumerable<string>? unlockedFirst = null)
    {
        if (!_all.TryGetValue(charKey, out var s)) _all[charKey] = s = new State();
        s.Base = new Dictionary<string, int>(s.Base ?? new(), StringComparer.OrdinalIgnoreCase);
        s.Done ??= new();
        s.Levels = new Dictionary<string, int>((s.Levels ?? new()).Where(kv => kv.Value > 1 && Classes.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        // A ticked run is one race and three different classes — anything else never happened.
        s.Done.RemoveAll(d => d is null || !Races.Contains(d.Race, StringComparer.OrdinalIgnoreCase)
            || d.Classes is null || d.Classes.Where(c => Classes.Contains(c, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 || d.Classes.Count != 3);
        s.ClassOrder = Whole(s.ClassOrder, Classes, whoClasses);
        s.RaceOrder = Whole(s.RaceOrder, Races, unlockedFirst);
        if (s.Mode is not ("low" or "balanced" or "mine")) s.Mode = "balanced";
        return s;
    }

    /// <summary>What Race unlocks knows: the races still locked (planned last) and the ones you have
    /// (they lead a first visit's order). No dumps yet = nothing locked, nothing known.</summary>
    public static (HashSet<string> Locked, List<string> Unlocked) RacesFrom(RaceBook? book)
    {
        var views = book?.Views() ?? (IReadOnlyList<RaceBook.RaceView>)Array.Empty<RaceBook.RaceView>();
        return (new HashSet<string>(views.Where(v => !v.Done).Select(v => RaceCode(v.Name)).OfType<string>(), StringComparer.OrdinalIgnoreCase),
            views.Where(v => v.Done).Select(v => RaceCode(v.Name)).OfType<string>().ToList());
    }

    private static List<string> Whole(List<string>? have, string[] all, IEnumerable<string>? lead)
    {
        var kept = (have ?? new()).Select(x => x.ToUpperInvariant()).Where(all.Contains).Distinct().ToList();
        if (kept.Count == 0 && lead is not null) kept = lead.Select(x => x.ToUpperInvariant()).Where(all.Contains).Distinct().ToList();
        kept.AddRange(all.Where(x => !kept.Contains(x)));
        return kept;
    }

    public void Save()
    {
        try
        {
            if (_path is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    /// <summary>The XP penalty of a run, in percent: ten per finished reincarnation of its most-used
    /// component — the highest of the four, never their sum — capped at +300%.</summary>
    public static int Penalty(params int[] finished) => Math.Min(CapPercent, StepPercent * Math.Max(0, finished.Max()));

    /// <summary>Reincarnations done so far: every run has exactly one race, so the races' counts add up to
    /// it — unless ranks were set by hand on classes only, where three class ranks make at least one run.</summary>
    public static int RunsDone(State s) => Math.Max(Races.Sum(s.Count), (int)Math.Ceiling(Classes.Sum(s.Count) / 3.0));

    /// <summary>Move an entry of a priority list to a new place (a drag): everything else keeps its order.</summary>
    public static void Move(List<string> order, string code, int toIndex)
    {
        int from = order.FindIndex(x => x.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return;
        string item = order[from];
        order.RemoveAt(from);
        order.Insert(Math.Clamp(toIndex, 0, order.Count), item);
    }

    /// <summary>A picked priority trio leads the class order, in pick order; the rest keep theirs.</summary>
    public static void LeadWith(List<string> order, IEnumerable<string> trio)
    {
        var lead = trio.Select(x => x.ToUpperInvariant()).Where(order.Contains).Distinct().ToList();
        order.RemoveAll(lead.Contains);
        order.InsertRange(0, lead);
    }

    /// <summary>The whole road: the ticked runs as they were, then the runs still to do, best first.</summary>
    public static Plan Build(State s, IReadOnlySet<string>? lockedRaces = null)
    {
        lockedRaces ??= new HashSet<string>();
        int nC = Classes.Length, nR = Races.Length;
        var cc = new int[nC]; var rc = new int[nR];
        for (int i = 0; i < nC; i++) cc[i] = Math.Max(0, s.Base.GetValueOrDefault(Classes[i]));
        for (int i = 0; i < nR; i++) rc[i] = Math.Max(0, s.Base.GetValueOrDefault(Races[i]));
        var wC = new double[nC]; var wR = new double[nR];
        for (int i = 0; i < nC; i++) { int at = s.ClassOrder.IndexOf(Classes[i]); wC[i] = nC - (at < 0 ? nC : at); }
        for (int i = 0; i < nR; i++) { int at = s.RaceOrder.IndexOf(Races[i]); wR[i] = nR - (at < 0 ? nR : at); }
        double beta = s.Mode switch { "low" => 100, "mine" => 0.05, _ => 1.2 };
        // Where each class stands: its head start until a run reincarnates it, level 1 from then on.
        var lv = new int[nC];
        for (int i = 0; i < nC; i++) lv[i] = s.LevelOf(Classes[i]);

        var runs = new List<PlanRun>();
        var rankThree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(int r, int[] t, bool done)
        {
            int from = Math.Min(lv[t[0]], Math.Min(lv[t[1]], lv[t[2]]));
            int pen = from >= LevelCap ? 0 : Penalty(cc[t[0]], cc[t[1]], cc[t[2]], rc[r]); // a loadout at the cap levels nothing
            var by = new List<string>();
            if (pen > 0)
            {
                foreach (int c in t) if (Math.Min(CapPercent, cc[c] * StepPercent) == pen) by.Add(Classes[c]);
                if (Math.Min(CapPercent, rc[r] * StepPercent) == pen) by.Add(Races[r]);
            }
            var use = new int[3];
            for (int i = 0; i < 3; i++) { use[i] = ++cc[t[i]]; if (use[i] == Ranks) rankThree[Classes[t[i]]] = runs.Count + 1; }
            int raceUse = ++rc[r];
            if (raceUse == Ranks) rankThree[Races[r]] = runs.Count + 1;
            foreach (int c in t) lv[c] = 1;
            runs.Add(new PlanRun(runs.Count + 1, Races[r], t.Select(c => Classes[c]).ToList(), pen, by, use, raceUse, done, !done && lockedRaces.Contains(Races[r])) { From = from });
        }

        foreach (var d in s.Done)
        {
            int r = Array.FindIndex(Races, x => x.Equals(d.Race, StringComparison.OrdinalIgnoreCase));
            var t = d.Classes.Select(c => Array.FindIndex(Classes, x => x.Equals(c, StringComparison.OrdinalIgnoreCase))).Where(i => i >= 0).Distinct().ToArray();
            if (r >= 0 && t.Length == 3) Add(r, t, done: true);
        }

        for (int guard = 0; guard < 200 && (rc.Any(n => n < Ranks) || cc.Any(n => n < Ranks)); guard++)
        {
            var open = Enumerable.Range(0, nR).Where(r => rc[r] < Ranks).ToList();
            var pool = open.Count == 0 ? Enumerable.Range(0, nR).ToList()
                : open.Any(r => !lockedRaces.Contains(Races[r])) ? open.Where(r => !lockedRaces.Contains(Races[r])).ToList() : open;
            // The even share: every class slot still to come, spread over the sixteen.
            int left = Math.Max(rc.Sum(n => Math.Max(0, Ranks - n)), (int)Math.Ceiling(cc.Sum(n => Math.Max(0, Ranks - n)) / 3.0));
            int share = Math.Max(cc.Max(), (int)Math.Ceiling((cc.Sum() + 3.0 * left) / nC));
            var best = Pick(pool, share) ?? Pick(pool, int.MaxValue);
            if (best is null) break;
            Add(best.Value.Race, best.Value.Trio, done: false);
        }

        (int Race, int[] Trio)? Pick(List<int> pool, int share)
        {
            (int, int[])? best = null; double bestScore = double.NegativeInfinity;
            foreach (int r in pool)
                for (int i = 0; i < nC; i++)
                {
                    if (cc[i] >= share) continue;
                    for (int j = i + 1; j < nC; j++)
                    {
                        if (cc[j] >= share) continue;
                        for (int k = j + 1; k < nC; k++)
                        {
                            if (cc[k] >= share) continue;
                            int from = Math.Min(lv[i], Math.Min(lv[j], lv[k]));
                            int pen = from >= LevelCap ? 0 : Penalty(cc[i], cc[j], cc[k], rc[r]);
                            double gain = (rc[r] < Ranks ? wR[r] : 0) + (cc[i] < Ranks ? wC[i] : 0) + (cc[j] < Ranks ? wC[j] : 0) + (cc[k] < Ranks ? wC[k] : 0);
                            // the levels a head start skips, on the penalty's own scale: a whole levelling saved = 100 points
                            double score = gain - beta * pen + 0.02 * (wC[i] + wC[j] + wC[k]) - 0.4 * (cc[i] + cc[j] + cc[k])
                                + beta * 100.0 * (from - 1) / (LevelCap - 1);
                            if (score > bestScore) { bestScore = score; best = (r, new[] { i, j, k }); }
                        }
                    }
                }
            if (best is { } b) Array.Sort(b.Item2, (x, y) => wC[y].CompareTo(wC[x])); // the trio reads most important first
            return best;
        }

        int allAt = Classes.All(rankThree.ContainsKey) ? Classes.Max(c => rankThree[c]) : 0;
        var trio = s.ClassOrder.Take(3).ToList();
        int trioAt = trio.Count == 3 && trio.All(rankThree.ContainsKey) ? trio.Max(c => rankThree[c]) : 0;
        // The average is over the FULL levellings (from level 1); a ready run levels nothing and sets no peak.
        var full = runs.Where(x => x.From <= 1).ToList();
        var paid = runs.Where(x => !x.Ready).ToList();
        double average = full.Count > 0 ? full.Average(x => (double)x.Penalty) : paid.Count > 0 ? paid.Average(x => (double)x.Penalty) : 0;
        return new Plan(runs, average, paid.Count == 0 ? 0 : paid.Max(x => x.Penalty), rankThree, allAt, trioAt);
    }

    /// <summary>The community's 48-run sheet from scratch (three cycles of four trios run three times
    /// each, then the pet classes) under the same rule: what the plan is measured against.</summary>
    public static (double Average, int Peak) SheetOrder()
    {
        string[][] trios = { new[] { "SHD", "MNK", "SHM" }, new[] { "WAR", "BER", "DRU" }, new[] { "PAL", "ROG", "BRD" }, new[] { "CLR", "ENC", "WIZ" } };
        string[][] tail = { new[] { "NEC", "BST", "MAG" }, new[] { "NEC", "MAG", "RNG" }, new[] { "MAG", "BST", "RNG" }, new[] { "NEC", "BST", "RNG" } };
        string[][] races = { new[] { "IKS", "HIE", "HUM", "HEF", "KRN" }, new[] { "BAR", "OGR", "DEF", "DWF", "ERU" }, new[] { "HDE", "TRL", "GNM", "WEF", "FRG" } };
        var s = new State();
        foreach (var cycle in races)
        {
            for (int g = 0; g < 4; g++) for (int x = 0; x < 3; x++) s.Done.Add(new DoneRun { Race = cycle[g], Classes = trios[g].ToList() });
            for (int x = 0; x < 3; x++) s.Done.Add(new DoneRun { Race = cycle[4], Classes = tail[x].ToList() });
            s.Done.Add(new DoneRun { Race = "HFL", Classes = tail[3].ToList() });
        }
        var p = Build(s);
        return (p.Runs.Take(48).Average(r => (double)r.Penalty), p.Runs.Take(48).Max(r => r.Penalty));
    }
}
