using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The best-in-slot finder: every equippable item you OWN (per the
/// inventory dump — worn, bags, bank, depot, hoard), joined to the wiki
/// item table, stats scaled to the item's +N tier, kept when the chosen
/// class combo can wear it, and ranked per slot by three weighted
/// priorities (3·p1 + 2·p2 + 1·p3). Pure computation; no persistence.
/// </summary>
public static class BisFinder
{
    public static readonly string[] AllClasses =
    {
        "WAR", "CLR", "PAL", "RNG", "SHD", "DRU", "MNK", "BRD",
        "ROG", "SHM", "NEC", "WIZ", "MAG", "ENC", "BST", "BER",
    };

    /// <summary>Slots in paper-doll order; Count = how many you wear.</summary>
    public static readonly (string Key, string Label, int Count)[] Slots =
    {
        ("HEAD", "Head", 1), ("FACE", "Face", 1), ("EAR", "Ear", 2), ("NECK", "Neck", 1),
        ("SHOULDERS", "Shoulders", 1), ("ARMS", "Arms", 1), ("BACK", "Back", 1),
        ("WRIST", "Wrist", 2), ("HANDS", "Hands", 1), ("CHEST", "Chest", 1),
        ("LEGS", "Legs", 1), ("FEET", "Feet", 1), ("WAIST", "Waist", 1), ("FINGER", "Finger", 2),
        ("PRIMARY", "Primary", 1), ("SECONDARY", "Secondary", 1), ("RANGE", "Range", 1),
        ("AMMO", "Ammo", 1),
    };

    /// <summary>What a priority can be: normalized stat keys, plus two
    /// synthetics — RESISTS (every SV summed) and DMG_DLY (weapon ratio ×100).</summary>
    public static readonly (string Key, string Label)[] Priorities =
    {
        ("AC", "AC"), ("HP", "HP"), ("MP", "Mana"), ("STR", "STR"), ("STA", "STA"),
        ("AGI", "AGI"), ("DEX", "DEX"), ("WIS", "WIS"), ("INT", "INT"), ("CHA", "CHA"),
        ("SV_FIRE", "SV Fire"), ("SV_COLD", "SV Cold"), ("SV_MAGIC", "SV Magic"),
        ("SV_POISON", "SV Poison"), ("SV_DISEASE", "SV Disease"), ("RESISTS", "Resists (sum)"),
        ("HASTE", "Haste"), ("DMG_DLY", "DMG/DLY (weapons)"),
    };

    public static readonly int[] Weights = { 3, 2, 1 };

    /// <summary>Lanes the finder searches — what you carry and what you stash.</summary>
    public static readonly string[] SearchLanes = { "worn", "bags", "bank", "depot", "hoard" };

    private static readonly Regex TierRx = new(@" \+(\d+)$", RegexOptions.Compiled);

    /// <summary>The +N tier stated by a dump name (0 when bare).</summary>
    public static int TierOf(string name) =>
        TierRx.Match(name.Trim()) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    /// <summary>Can this combo wear it? The wiki spells classes as "ALL",
    /// "NONE", "ALL except NEC WIZ MAG ENC" or an explicit list. Owner's
    /// default rule: ANY class in the combo being allowed is enough. An
    /// empty field (the wiki didn't say) is allowed but flagged by caller.</summary>
    public static bool ClassAllowed(string classesField, IReadOnlyCollection<string> combo)
    {
        string f = (classesField ?? "").Trim();
        if (f.Length == 0 || f.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return true;
        if (f.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return false;
        if (combo.Count == 0) return true;
        const string except = "ALL except ";
        if (f.StartsWith(except, StringComparison.OrdinalIgnoreCase))
        {
            var banned = f[except.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return combo.Any(c => !banned.Contains(c));
        }
        var allowed = f.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return combo.Any(allowed.Contains);
    }

    /// <summary>Every integer stat the item grants at its tier, by
    /// normalized key — AC included, saves included, the synthetics added.</summary>
    public static Dictionary<string, int> ScaledStats(ItemStats.Record rec, int tier)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rec.Ac is { } ac && ac != 0) d["AC"] = ItemUpgrade.ScalePrimary(ac, tier);
        foreach (var p in rec.Stats.Concat(rec.Saves))
        {
            if (p.Length < 2 || ItemUpgrade.StatInteger(p[1]) is not { } n) continue;
            string key = ItemUpgrade.NormalizeKey(p[0]);
            int scaled = ItemUpgrade.ClassOf(p[0]) switch
            {
                ItemUpgrade.StatClass.Primary => ItemUpgrade.ScalePrimary(n, tier),
                ItemUpgrade.StatClass.Flat => ItemUpgrade.ScaleFlat(n, tier),
                _ => n,
            };
            d[key] = d.GetValueOrDefault(key) + scaled;
        }
        int resists = d.Where(kv => kv.Key.StartsWith("SV_", StringComparison.Ordinal)).Sum(kv => kv.Value);
        if (resists != 0) d["RESISTS"] = resists;
        if (rec.Dmg is { } dmg && rec.Delay is { } dly && dmg > 0 && dly > 0)
        {
            d["DMG"] = ItemUpgrade.ScaleDamage(dmg, tier);
            d["DELAY"] = dly;
            d["DMG_DLY"] = (int)Math.Round(100.0 * d["DMG"] / dly);
        }
        return d;
    }

    public static double Score(IReadOnlyDictionary<string, int> stats, IReadOnlyList<string> prio)
    {
        double s = 0;
        for (int i = 0; i < prio.Count && i < Weights.Length; i++)
            if (prio[i].Length > 0) s += Weights[i] * stats.GetValueOrDefault(prio[i]);
        return s;
    }

    public sealed record Candidate(string Name, int Tier, string Location, string Lane,
        bool Worn, bool Allowed, bool ClassesUnknown, bool TwoHanded, int Copies,
        double Score, IReadOnlyDictionary<string, int> Stats, ItemStats.Record Rec)
    {
        public string BaseName => TierRx.Replace(Name, "");
    }

    public sealed record SlotResult(string Key, string Label, int Count,
        List<Candidate> Ranked, List<Candidate> Foreign)
    {
        /// <summary>The Count best allowed items — the picks.</summary>
        public IEnumerable<Candidate> Picks => Ranked.Take(Count);

        /// <summary>A pick that isn't on you, outscoring what is (or filling
        /// an empty slot) = an upgrade sitting in storage.</summary>
        public IEnumerable<Candidate> Upgrades
        {
            get
            {
                var wornScores = Ranked.Where(c => c.Worn).Select(c => c.Score)
                    .OrderByDescending(s => s).ToList();
                int wornCount = wornScores.Count;
                foreach (var p in Picks)
                {
                    if (p.Worn) continue;
                    // It displaces the weakest worn item, or fills a vacancy.
                    if (wornCount < Count || p.Score > wornScores[^1]) yield return p;
                }
            }
        }
    }

    public sealed record Result(List<SlotResult> Slots, List<string> Unknown, int Considered);

    /// <summary>Worn dump locations → slot key ("Fingers" → FINGER, "Wrist 2" → WRIST).</summary>
    private static string WornSlotKey(string location)
    {
        string b = InventoryStore.SplitBase(location).Trim();
        b = Regex.Replace(b, @"\s*\d+$", "");
        return b.ToUpperInvariant() switch
        {
            "FINGERS" => "FINGER",
            "EARS" => "EAR",
            "WRISTS" => "WRIST",
            var x => x,
        };
    }

    /// <summary>The board for one combo + priority set over the dump's rows.</summary>
    public static Result Build(IEnumerable<InventoryStore.CarryRow> rows, ItemStats stats,
        IReadOnlyCollection<string> combo, IReadOnlyList<string> prio,
        IReadOnlyCollection<string>? lanes = null)
    {
        var laneSet = new HashSet<string>(lanes ?? SearchLanes, StringComparer.Ordinal);
        var unknown = new List<string>();
        int considered = 0;
        // (slot key) → candidates; a physical copy per row, folded by name+tier.
        var bySlot = Slots.ToDictionary(s => s.Key, _ => new Dictionary<string, Candidate>(StringComparer.Ordinal));

        foreach (var r in rows)
        {
            if (!laneSet.Contains(r.Lane)) continue;
            if (r.IsContainer || r.Name.EndsWith("(Exaltation)", StringComparison.Ordinal)) continue;
            if (r.Name.Equals("Empty", StringComparison.OrdinalIgnoreCase)) continue;
            var rec = stats.Lookup(r.Name);
            if (rec is null)
            {
                if (!unknown.Contains(r.Name)) unknown.Add(r.Name);
                continue;
            }
            if (stats.IsContainer(r.Name)) continue;
            var slotKeys = rec.Slot.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToUpperInvariant()).Where(bySlot.ContainsKey).ToList();
            if (slotKeys.Count == 0) continue; // not wearable
            considered++;

            int tier = TierOf(r.Name);
            var scaled = ScaledStats(rec, tier);
            bool allowed = ClassAllowed(rec.Classes, combo);
            bool worn = r.Lane == "worn";
            string wornKey = worn ? WornSlotKey(r.Location) : "";
            bool twoHanded = rec.Skill.StartsWith("2H", StringComparison.OrdinalIgnoreCase);
            double score = Score(scaled, prio);

            foreach (var key in slotKeys)
            {
                // A worn item only claims the slot it's IN (a 1H weapon worn
                // in Secondary shouldn't read as worn under Primary too).
                bool wornHere = worn && (wornKey == key
                    || (wornKey == "" && slotKeys.Count == 1));
                string fold = FocusEffects.ItemKey(r.Name) + "|" + tier + "|" + (wornHere ? "w" : "s");
                var dict = bySlot[key];
                if (dict.TryGetValue(fold, out var have))
                {
                    dict[fold] = have with { Copies = have.Copies + Math.Max(1, r.Count) };
                    continue;
                }
                dict[fold] = new Candidate(r.Name, tier, r.Location, r.Lane, wornHere, allowed,
                    string.IsNullOrWhiteSpace(rec.Classes), twoHanded, Math.Max(1, r.Count),
                    score, scaled, rec);
            }
        }

        var slots = new List<SlotResult>();
        foreach (var (key, label, count) in Slots)
        {
            var all = bySlot[key].Values.ToList();
            var ranked = all.Where(c => c.Allowed)
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Stats.GetValueOrDefault("AC"))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var foreign = all.Where(c => !c.Allowed)
                .OrderByDescending(c => c.Score).ToList();
            slots.Add(new SlotResult(key, label, count, ranked, foreign));
        }
        return new Result(slots, unknown, considered);
    }
}
