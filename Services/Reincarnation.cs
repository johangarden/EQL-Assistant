using System.IO;
using System.Text.Json;

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

        var runs = new List<PlanRun>();
        var rankThree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(int r, int[] t, bool done)
        {
            int pen = Penalty(cc[t[0]], cc[t[1]], cc[t[2]], rc[r]);
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
            runs.Add(new PlanRun(runs.Count + 1, Races[r], t.Select(c => Classes[c]).ToList(), pen, by, use, raceUse, done, !done && lockedRaces.Contains(Races[r])));
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
                            int pen = Penalty(cc[i], cc[j], cc[k], rc[r]);
                            double gain = (rc[r] < Ranks ? wR[r] : 0) + (cc[i] < Ranks ? wC[i] : 0) + (cc[j] < Ranks ? wC[j] : 0) + (cc[k] < Ranks ? wC[k] : 0);
                            double score = gain - beta * pen + 0.02 * (wC[i] + wC[j] + wC[k]) - 0.4 * (cc[i] + cc[j] + cc[k]);
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
        return new Plan(runs, runs.Count == 0 ? 0 : runs.Average(x => (double)x.Penalty), runs.Count == 0 ? 0 : runs.Max(x => x.Penalty), rankThree, allAt, trioAt);
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
