namespace EQLOverlay.Services;

/// <summary>
/// The focus planner (owner, 30 Sep: "with the current gear, what focus
/// effects should be in what slots to have the best and highest focus
/// effects — only instrument focus if the combo includes bard; some effects
/// share a slot, so a way of picking need-to-have and nice-to-have").
///
/// The rules, from eqlwiki's Exaltations page: a worn item's Focus socket
/// opens at +1 (one per item); an exaltation "carries the equipment slot
/// restrictions of its source item" — a Face item's focus fits a Face socket;
/// the same focus never stacks, so only its best tier counts. Assumed, not
/// stated: an exaltation keeps its source item's CLASS restrictions too, and
/// a tier past its level cap runs at part strength (the wiki's "decays on
/// spells over level N"). An exaltation fits an ITEM whose own slot matches
/// its source item's — a Neck item worn in Any Slot takes a Neck exaltation,
/// never a Secondary one (owner, 2 Oct, the game's own refusal). The plan
/// is the EXACT best assignment — one
/// exaltation per family, one per socket, a Need outweighing any number of
/// nice-to-haves — so a Need never loses its socket to a stack of Nices.
/// </summary>
public static class FocusPlanner
{
    public enum Want { Off, Nice, Need }

    /// <summary>A worn item's open focus socket. Slots = the ITEM's own wiki slots
    /// (what an exaltation must match — a Neck item in Any Slot is still Neck);
    /// SlotKey = where it is worn. Fixed = a +0 item's own, unmovable focus (no
    /// socket yet — the effect still sits on the item).</summary>
    /// <summary>Classes = the item's own class list (what an exaltation's list is overlapped with).</summary>
    public sealed record Socket(string Label, string SlotKey, string Item, string Location, int Line, bool AnySlot, bool Fixed = false, IReadOnlyList<string>? Slots = null, string Classes = "")
    {
        /// <summary>The slots an exaltation must share: the item's own, else the worn slot.</summary>
        public IReadOnlyList<string> Accepts => Slots is { Count: > 0 } ? Slots : new[] { SlotKey };
        public string AcceptsText => string.Join("/", Accepts.Select(a => a.ToLowerInvariant()));
    }

    /// <summary>One focus exaltation you own, and where it sits now.</summary>
    /// <summary>Host = the item it sits inside (worn or stored), Where = that item's place in plain words.</summary>
    public sealed record Exalt(string Name, string Key, FocusEffects.Family Family, FocusEffects.Tier Tier, IReadOnlyList<string> Slots,
        string Classes, string Place, string? InSocket, int Line, bool Fixed = false, string Host = "", string Where = "")
    {
        /// <summary>The name without the " (Exaltation)" tail.</summary>
        public string Short => Name.Replace(" (Exaltation)", "");
        public string TierLabel => FocusEffects.TierShort(Family, Tier.TierNum);
        public string Effect => Tier.Effect;
    }

    public sealed record Placement(Socket Socket, Exalt? Now, Exalt? Plan);

    public sealed record FamilyRow(FocusEffects.Family Family, Want Want, Exalt? Best, Exalt? Placed, bool Shown);

    public sealed record Hunt(FocusEffects.Family Family, FocusEffects.Tier Tier, string Item, string Slot, string OpenSocket, string Why);

    public sealed record Conflict(Socket Socket, List<Exalt> Wanting, Exalt? Winner);

    /// <summary>An exaltation that fits a socket's slot but not its classes: socketed
    /// there the item would keep only Left (the two lists' overlap), which your combo
    /// can't wear — so the planner never puts it there (owner, 2 Oct: Rokyl's
    /// Channelling Crystal in a Bladestopper left the shield BRD-only).</summary>
    public sealed record Blocked(Exalt E, Socket S, string Left);

    public sealed record Plan(List<Placement> Sockets, List<FamilyRow> Families, List<Hunt> Hunts, List<Conflict> Conflicts,
        List<Exalt> Foreign, List<Exalt> Unplaceable, List<Exalt> Pool, int Moves, bool Exact, List<Blocked> ClassBlocked)
    {
        public int Needs => Families.Count(f => f.Shown && f.Want == Want.Need && f.Best is not null);
        public int NeedsPlaced => Families.Count(f => f.Shown && f.Want == Want.Need && f.Placed is not null);
        public int Nices => Families.Count(f => f.Shown && f.Want == Want.Nice && f.Best is not null);
        public int NicesPlaced => Families.Count(f => f.Shown && f.Want == Want.Nice && f.Placed is not null);
    }

    private const string FocusSocketSuffix = "-Slot7";
    private static readonly string[] Casters = { "BRD", "BST", "PAL", "CLR", "DRU", "ENC", "MAG", "NEC", "RNG", "SHD", "SHM", "WIZ" };
    private static readonly string[] Priests = { "CLR", "DRU", "SHM" };

    /// <summary>A sensible first marking for a combo; the player edits from here.</summary>
    public static Dictionary<string, Want> DefaultWants(IReadOnlyCollection<string> combo)
    {
        bool Has(params string[] c) => combo.Any(x => c.Contains(x, StringComparer.OrdinalIgnoreCase));
        bool caster = Has(Casters);
        var d = new Dictionary<string, Want>(StringComparer.OrdinalIgnoreCase);
        Want C(bool need, bool nice) => need ? Want.Need : nice ? Want.Nice : Want.Off;
        d["Improved Damage"] = C(caster, false);
        d["Mana Preservation"] = C(caster, false);
        d["Spell Haste"] = C(caster, false);
        d["Burning Affliction"] = C(false, caster);
        d["Affliction Efficiency"] = C(false, caster);
        d["Affliction Haste"] = C(false, caster);
        d["Extended Enhancement"] = C(false, caster);
        d["Enhancement Haste"] = C(false, caster);
        d["Extended Range"] = C(false, false);
        d["Improved Healing"] = C(Has(Priests), Has("PAL", "RNG", "BST", "BRD", "SHD"));
        d["Reagent Conservation"] = C(false, Has("MAG", "WIZ", "ENC", "NEC", "CLR", "DRU", "SHM"));
        d["Summoning Efficiency"] = C(false, Has("MAG"));
        d["Summoning Haste"] = C(false, Has("MAG"));
        d["Reanimation Efficiency"] = C(false, Has("NEC"));
        d["Reanimation Haste"] = C(false, Has("NEC"));
        foreach (var song in new[] { "String Resonance", "Percussion Resonance", "Brass Resonance", "Wind Resonance" })
            d[song] = C(false, Has("BRD"));
        return d;
    }

    /// <summary>Instrument foci show only with a bard in the combo; the summoned
    /// (conjured) families only when you hold one.</summary>
    public static bool Shown(FocusEffects.Family fam, IReadOnlyCollection<string> combo, bool owned) => fam.Group switch
    {
        "song" => combo.Contains("BRD", StringComparer.OrdinalIgnoreCase),
        "summoned" => owned,
        _ => true,
    };

    /// <summary>Full strength at or under the tier's level cap; past it the wiki
    /// says the bonus decays — read as a straight fall to a 30% floor over 30 levels.</summary>
    public static double Strength(FocusEffects.Tier tier, int level) =>
        tier.LevelCap is not { } cap || level <= 0 || level <= cap ? 1 : Math.Max(0.3, 1 - (level - cap) / 30.0);

    /// <summary>"Wrist 1", "Fingers 2", "Any slot 1" — the sockets in dump order, each
    /// with its item's own slots from the wiki table.</summary>
    public static List<Socket> Sockets(IEnumerable<InventoryStore.CarryRow> rows, FocusEffects focus, ItemStats? stats = null)
    {
        var list = new List<(string Base, string Key, string Item, string Loc, int Line, bool Fixed)>();
        foreach (var r in rows.OrderBy(r => r.Line))
        {
            if (r.Lane != "worn" || r.Host.Length > 0 || r.IsContainer || InventoryStore.IsExaltation(r.Name)) continue;
            if (r.Name.Equals("Empty", StringComparison.OrdinalIgnoreCase)) continue;
            int tier = BisFinder.TierOf(r.Name);
            bool fixedFocus = tier < 1 && focus.EffectsOf(r.Name).Count > 0;
            if (tier < 1 && !fixedFocus) continue; // the Focus socket opens at +1
            string b = InventoryStore.SplitBase(r.Location);
            list.Add((b, SlotFinder.WornKey(r.Location), r.Name, r.Location, r.Line, fixedFocus));
        }
        var counts = list.GroupBy(s => s.Base).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        var result = new List<Socket>();
        foreach (var s in list)
        {
            int n = seen[s.Base] = seen.GetValueOrDefault(s.Base) + 1;
            string label = s.Base == "Any Slot" ? "Any slot" : s.Base;
            if (counts[s.Base] > 1) label += " " + n;
            var rec = stats?.Lookup(s.Item);
            var own = rec is not null ? SlotFinder.SlotKeys(rec) : new List<string>();
            result.Add(new Socket(label, s.Key, s.Item, s.Loc, s.Line, s.Key == "ANY", s.Fixed, own.Count > 0 ? own : null, rec?.Classes ?? ""));
        }
        return result;
    }

    /// <summary>Every focus exaltation you own — socketed in worn or stored items,
    /// or loose on the key ring — plus the fixed foci of worn +0 items.</summary>
    public static List<Exalt> PoolOf(IEnumerable<InventoryStore.CarryRow> rows, FocusEffects focus, ItemStats stats, List<Socket> sockets)
    {
        var all = rows.OrderBy(r => r.Line).ToList();
        var pool = new List<Exalt>();
        foreach (var r in all)
        {
            bool fixedFocus = false;
            if (!InventoryStore.IsExaltation(r.Name))
            {
                // A worn +0 item's own focus: unmovable, but it counts.
                var fs = sockets.FirstOrDefault(s => s.Fixed && s.Line == r.Line);
                if (fs is null) continue;
                fixedFocus = true;
            }
            var hits = focus.EffectsOf(r.Name);
            if (hits.Count == 0) continue; // a click / worn / proc exaltation
            var rec = stats.Lookup(r.Name);
            var slots = rec is not null ? SlotFinder.SlotKeys(rec) : new List<string>();
            string classes = rec?.Classes ?? "";
            foreach (var (fam, tier) in hits)
            {
                var iref = tier.Items.FirstOrDefault(i => FocusEffects.ItemKey(i.Name) == FocusEffects.ItemKey(r.Name));
                if (slots.Count == 0 && iref is not null && iref.Slot.Length > 0)
                    slots = SlotFinder.SlotKeys(new ItemStats.Record { Slot = iref.Slot });
                if (classes.Length == 0 && iref is not null) classes = iref.Classes;
            }
            string? inSocket = null; string place, hostName = "", where = "";
            if (fixedFocus)
            {
                var s = sockets.First(s => s.Fixed && s.Line == r.Line);
                inSocket = s.Label; place = $"on {r.Name} (worn, +0)"; hostName = r.Name; where = "worn";
            }
            else if (r.Lane == "worn" && r.Host.Length > 0 && r.Location.EndsWith(FocusSocketSuffix, StringComparison.Ordinal))
            {
                var host = sockets.LastOrDefault(s => !s.Fixed && s.Line < r.Line && s.Item == r.Host && r.Location == s.Location + FocusSocketSuffix);
                inSocket = host?.Label; place = host is not null ? $"in {host.Label}" : $"in {r.Host} (worn)"; hostName = r.Host; where = "worn";
            }
            else if (r.Lane == "keyring") place = "the key ring";
            else if (r.Host.Length > 0)
            {
                hostName = r.Host; where = SlotFinder.PrettyLocation(InventoryStore.SplitBase(r.Location) + HostChain(r.Location), r.Lane);
                place = $"in {hostName} · {where}";
            }
            else { where = SlotFinder.PrettyLocation(r.Location, r.Lane); place = where; }
            var first = hits[0];
            pool.Add(new Exalt(r.Name, FocusEffects.ItemKey(r.Name), first.Fam, first.Tier, slots, classes, place, inSocket, r.Line, fixedFocus, hostName, where));
            // A rare item carrying two foci: the same physical exaltation, a row per family.
            foreach (var (fam, tier) in hits.Skip(1))
                pool.Add(new Exalt(r.Name, FocusEffects.ItemKey(r.Name), fam, tier, slots, classes, place, inSocket, r.Line, fixedFocus, hostName, where));
        }
        return pool;
    }

    /// <summary>"Bank5-Slot7-Slot7" → the host's own chain "-Slot7" (drop the socket).</summary>
    private static string HostChain(string location)
    {
        string b = InventoryStore.SplitBase(location);
        string chain = location[b.Length..];
        int cut = chain.LastIndexOf("-Slot", StringComparison.Ordinal);
        return cut >= 0 ? chain[..cut] : chain;
    }

    /// <summary>The classes an item keeps once an exaltation sits in it: the overlap of
    /// the two class lists (the game's rule, 2 Oct). An unknown list reads as ALL.</summary>
    public static List<string> ClassesWith(string hostClasses, string exaltClasses)
    {
        var h = BisFinder.ClassSet(hostClasses); var e = BisFinder.ClassSet(exaltClasses);
        return BisFinder.AllClasses.Where(c => h.Contains(c) && e.Contains(c)).ToList();
    }

    /// <summary>Could your combo still wear the item with that exaltation in it?</summary>
    public static bool Wearable(string hostClasses, string exaltClasses, IReadOnlyCollection<string> combo) =>
        combo.Count == 0 || ClassesWith(hostClasses, exaltClasses).Any(c => combo.Contains(c, StringComparer.OrdinalIgnoreCase));

    /// <summary>The slot rule alone: the exaltation's source slots meet the ITEM's own slots.</summary>
    private static bool SlotFits(Exalt e, Socket s) =>
        e.Fixed ? s.Fixed && s.Label == e.InSocket : !s.Fixed && e.Slots.Any(x => s.Accepts.Contains(x));

    /// <summary>Slot AND class: what the planner may actually do.</summary>
    private static bool Fits(Exalt e, Socket s, IReadOnlyCollection<string> combo) =>
        SlotFits(e, s) && (e.Fixed || Wearable(s.Classes, e.Classes, combo));

    private const double NeedWeight = 100, NiceWeight = 10, StayBonus = 0.5;
    private const int SearchBudget = 400_000;

    public static Plan Build(IEnumerable<InventoryStore.CarryRow> rows, FocusEffects focus, ItemStats stats,
        IReadOnlyCollection<string> combo, int level, IReadOnlyDictionary<string, Want> wants)
    {
        var all = rows.ToList();
        var sockets = Sockets(all, focus, stats);
        var pool = PoolOf(all, focus, stats, sockets);
        Want WantOf(FocusEffects.Family f) => wants.TryGetValue(f.Name, out var w) ? w : Want.Off;
        var ownedFams = pool.Select(e => e.Family).ToHashSet();
        bool ShownFam(FocusEffects.Family f) => Shown(f, combo, ownedFams.Contains(f));

        // Foreign = your combo can't wear its source item (a hidden family — a bard's lute without a bard — is no news).
        var foreign = pool.Where(e => !e.Fixed && ShownFam(e.Family) && !BisFinder.ClassAllowed(e.Classes, combo)).Distinct().ToList();
        var unplaceable = pool.Where(e => !e.Fixed && e.Slots.Count == 0 && !foreign.Contains(e)).Distinct().ToList();
        var usable = pool.Where(e => !foreign.Contains(e) && (e.Fixed || e.Slots.Count > 0) && ShownFam(e.Family) && WantOf(e.Family) != Want.Off).ToList();

        // Edges: (exaltation, socket) pairs that fit, weighted by want × strength.
        var edges = new List<(Exalt E, Socket S, double W)>();
        var blocked = new List<Blocked>(); var blockedSeen = new HashSet<(int, string)>();
        foreach (var e in usable)
            foreach (var s in sockets)
            {
                // The exaltation's source slots must meet the ITEM's own slots — and the
                // item keeps only the classes both lists share, which must include yours.
                if (!SlotFits(e, s)) continue;
                if (!e.Fixed && !Wearable(s.Classes, e.Classes, combo))
                {
                    if (blockedSeen.Add((e.Line, s.Label))) blocked.Add(new Blocked(e, s, string.Join("/", ClassesWith(s.Classes, e.Classes))));
                    continue;
                }
                double w = (WantOf(e.Family) == Want.Need ? NeedWeight : NiceWeight) * Strength(e.Tier, level)
                           + (e.InSocket == s.Label ? StayBonus : 0);
                edges.Add((e, s, w));
            }
        var famList = edges.Select(x => x.E.Family).Distinct()
            .OrderByDescending(f => edges.Where(x => x.E.Family == f).Max(x => x.W)).ToList();
        var byFam = famList.ToDictionary(f => f, f => edges.Where(x => x.E.Family == f).OrderByDescending(x => x.W).ToList());
        var maxW = famList.Select(f => byFam[f][0].W).ToArray();

        // The exact search: each family takes one fitting exaltation into one free socket, or goes without.
        var best = new List<(Exalt E, Socket S, double W)>(); double bestW = -1; bool exact = true; int nodes = 0;
        var usedS = new HashSet<string>(); var usedE = new HashSet<int>(); var pick = new List<(Exalt, Socket, double)>();
        void Dfs(int i, double w)
        {
            if (++nodes > SearchBudget) { exact = false; return; }
            double bound = w; for (int j = i; j < famList.Count; j++) bound += maxW[j];
            if (bound <= bestW) return;
            if (i == famList.Count) { bestW = w; best = pick.ToList(); return; }
            foreach (var x in byFam[famList[i]])
            {
                if (usedS.Contains(x.S.Label) || usedE.Contains(x.E.Line)) continue;
                usedS.Add(x.S.Label); usedE.Add(x.E.Line); pick.Add(x);
                Dfs(i + 1, w + x.W);
                pick.RemoveAt(pick.Count - 1); usedS.Remove(x.S.Label); usedE.Remove(x.E.Line);
                if (!exact) return;
            }
            Dfs(i + 1, w);
        }
        Dfs(0, 0);
        if (!exact)
        {
            // Over budget: greedy by weight, the same shape.
            best.Clear(); usedS.Clear(); usedE.Clear(); var doneF = new HashSet<FocusEffects.Family>();
            foreach (var x in edges.OrderByDescending(x => x.W))
            {
                if (doneF.Contains(x.E.Family) || usedS.Contains(x.S.Label) || usedE.Contains(x.E.Line)) continue;
                best.Add(x); doneF.Add(x.E.Family); usedS.Add(x.S.Label); usedE.Add(x.E.Line);
            }
        }
        var planBySocket = best.ToDictionary(x => x.S.Label, x => x.E);
        var planByFam = best.ToDictionary(x => x.E.Family, x => x.E);

        var placements = new List<Placement>();
        int moves = 0;
        foreach (var s in sockets)
        {
            var now = pool.FirstOrDefault(e => e.InSocket == s.Label);
            planBySocket.TryGetValue(s.Label, out var plan);
            if (plan is not null && (now is null || now.Line != plan.Line)) moves++;
            placements.Add(new Placement(s, now, plan));
        }

        var families = new List<FamilyRow>();
        foreach (var fam in focus.Families)
        {
            var owned = pool.Where(e => e.Family == fam && !foreign.Contains(e)).OrderByDescending(e => Strength(e.Tier, level)).ThenByDescending(e => e.Tier.TierNum).ToList();
            planByFam.TryGetValue(fam, out var placed);
            families.Add(new FamilyRow(fam, WantOf(fam), owned.FirstOrDefault(), placed, ShownFam(fam)));
        }

        // Conflicts: a socket several wanted foci fit, and one of them went without.
        var conflicts = new List<Conflict>();
        foreach (var s in sockets.Where(s => !s.Fixed))
        {
            var wanting = usable.Where(e => !e.Fixed && Fits(e, s, combo)).GroupBy(e => e.Family).Select(g => g.OrderByDescending(e => e.Tier.TierNum).First()).ToList();
            if (wanting.Select(e => e.Family).Distinct().Count() < 2) continue;
            if (!wanting.Any(e => !planByFam.ContainsKey(e.Family))) continue;
            planBySocket.TryGetValue(s.Label, out var winner);
            conflicts.Add(new Conflict(s, wanting, winner));
        }

        // Hunts: the next tier up for every wanted family whose best is short or decays.
        var hunts = new List<Hunt>();
        foreach (var fr in families.Where(f => f.Shown && f.Want != Want.Off))
        {
            int have = fr.Best?.Tier.TierNum ?? 0;
            bool decays = fr.Best is not null && Strength(fr.Best.Tier, level) < 1;
            var next = fr.Family.Tiers.Where(t => !t.SummonedOnly && t.Items.Count > 0 && (t.TierNum > have || (decays && t.TierNum != have && Strength(t, level) > Strength(fr.Best!.Tier, level))))
                .OrderBy(t => t.TierNum).FirstOrDefault();
            if (next is null) continue;
            string why = fr.Best is null ? "you own none" : decays ? $"yours decays past level {fr.Best.Tier.LevelCap}" : $"a tier up from your {fr.Best.TierLabel}";
            foreach (var item in next.Items.Where(i => !i.Name.StartsWith("Summoned:", StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                var rec = stats.Lookup(item.Name);
                var slots = rec is not null ? SlotFinder.SlotKeys(rec) : SlotFinder.SlotKeys(new ItemStats.Record { Slot = item.Slot });
                string classes = rec?.Classes ?? item.Classes;
                if (classes.Length > 0 && !BisFinder.ClassAllowed(classes, combo)) continue;
                var open = placements.FirstOrDefault(p => !p.Socket.Fixed && p.Plan is null && slots.Any(x => p.Socket.Accepts.Contains(x)) && Wearable(p.Socket.Classes, classes, combo));
                hunts.Add(new Hunt(fr.Family, next, item.Name, string.Join("/", slots.Select(Pretty)).ToLowerInvariant(), open?.Socket.Label ?? "", why));
            }
        }
        return new Plan(placements, families, hunts, conflicts, foreign, unplaceable, pool, moves, exact, blocked);
    }

    /// <summary>The move in plain words (owner, 30 Sep: "make the move note more explanatory"):
    /// what to take out, where it is now, what it goes into.</summary>
    public static string MoveText(Placement p)
    {
        if (p.Plan is null) return "";
        var e = p.Plan;
        string into = $"socket it into {p.Socket.Item} ({p.Socket.Label})";
        string move = e.InSocket is not null ? $"Take the {e.Short} exaltation out of {e.Host} ({e.InSocket}, worn) and {into}."
            : e.Place == "the key ring" ? $"Take the {e.Short} exaltation from the key ring and {into}."
            : e.Host.Length > 0 ? $"Pull the {e.Short} exaltation out of {e.Host} ({e.Where}) and {into}."
            : $"Take the {e.Short} exaltation from {e.Where} and {into}.";
        return p.Now is null ? move : $"First take the {p.Now.Short} exaltation out of {p.Socket.Item} — it goes back to the key ring. Then: {char.ToLowerInvariant(move[0])}{move[1..]}";
    }

    private static string Pretty(string key) => BisFinder.Slots.FirstOrDefault(s => s.Key == key).Label ?? key;

    /// <summary>"Improved Damage=need;Spell Haste=nice" ↔ the marks (for the prefs file).</summary>
    public static string Pack(IReadOnlyDictionary<string, Want> wants) =>
        string.Join(";", wants.Select(kv => $"{kv.Key}={kv.Value.ToString().ToLowerInvariant()}"));

    public static Dictionary<string, Want> Unpack(string packed, Dictionary<string, Want> defaults)
    {
        var d = new Dictionary<string, Want>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var part in packed.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && Enum.TryParse<Want>(part[(eq + 1)..], true, out var w)) d[part[..eq]] = w;
        }
        return d;
    }
}
