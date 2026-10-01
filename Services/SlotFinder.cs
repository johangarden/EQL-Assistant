using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The slot finder (owner, 30 Sep: "a good view of what items across all my
/// bags / storage / bank I have for a specific slot, colour-coded for the
/// current /who combo — but also other combos"). Every wearable item in the
/// inventory dump, indexed by the slot it fits (a 1H weapon sits under Primary
/// AND Secondary), copies folded by name with every location kept, stats scaled
/// to the best copy's +N like the BiS finder. Pure computation.
/// </summary>
public static class SlotFinder
{
    /// <summary>Lanes the finder lists — what you carry, what you stash, and the
    /// key ring's Storage (real items you hold). Socketed exaltations stay out.</summary>
    public static readonly string[] Lanes = { "worn", "bags", "bank", "depot", "hoard", "storage" };

    public static string LaneLabel(string lane) => lane switch
    {
        "worn" => "Worn", "bags" => "Bags", "bank" => "Bank", "depot" => "Depot", "hoard" => "Hoard", "storage" => "Storage", _ => lane,
    };

    /// <summary>One physical copy: where it is, its +N, and the slot KEY it is
    /// worn in ("" when stored).</summary>
    public sealed record Copy(string Location, string Lane, int Tier, int Count, string WornKey);

    public sealed record Item(string Name, string Key, ItemStats.Record Rec, List<Copy> Copies)
    {
        public int BestTier => Copies.Max(c => c.Tier);
        public int Count => Copies.Sum(c => c.Count);
        public bool WornIn(string slotKey) => Copies.Any(c => c.WornKey == slotKey);
        public bool Worn => Copies.Any(c => c.Lane == "worn");
        public Dictionary<string, int> Stats => BisFinder.ScaledStats(Rec, BestTier);
    }

    public sealed record SlotList(string Key, string Label, List<Item> Items);

    /// <summary>How an item reads for the two combos on the page.</summary>
    public enum Fit { You, Compare, Neither, Unknown }

    private static readonly Regex TrailingNumber = new(@"\s*\d+$", RegexOptions.Compiled);

    /// <summary>The wiki's slot words → the finder's keys ("FINGERS" → FINGER, "SECONDAY" → SECONDARY).</summary>
    public static List<string> SlotKeys(ItemStats.Record rec)
    {
        var keys = new List<string>();
        foreach (var raw in rec.Slot.Split(new[] { ' ', ',', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string k = raw.Trim().ToUpperInvariant() switch
            {
                "FINGERS" => "FINGER", "EARS" => "EAR", "WRISTS" => "WRIST", "SHOULDER" => "SHOULDERS", "SECONDAY" => "SECONDARY",
                var x => x,
            };
            if (BisFinder.Slots.Any(s => s.Key == k) && !keys.Contains(k)) keys.Add(k);
        }
        return keys;
    }

    /// <summary>Worn dump locations → slot key ("Fingers" → FINGER, "Any Slot" → ANY).</summary>
    public static string WornKey(string location)
    {
        string b = TrailingNumber.Replace(InventoryStore.SplitBase(location).Trim(), "");
        return b.ToUpperInvariant() switch
        {
            "FINGERS" => "FINGER", "EARS" => "EAR", "WRISTS" => "WRIST", "ANY SLOT" => "ANY",
            var x => x,
        };
    }

    /// <summary>The dump's own words for a place, the way a player reads them:
    /// "Bank 3 · slot 1", "Shared bank 1 · slot 2", "Bag 8 · slot 6", "Depot 1", "Storage".</summary>
    public static string PrettyLocation(string location, string lane)
    {
        if (lane == "storage") return "Storage";
        if (lane == "worn") return InventoryStore.SplitBase(location);
        string b = InventoryStore.SplitBase(location);
        string slot = location.Length > b.Length ? Regex.Replace(location[b.Length..], @"-Slot(\d+)", " · slot $1").TrimStart(' ', '·') : "";
        string head = b switch
        {
            var s when s.StartsWith("SharedBank", StringComparison.Ordinal) => "Shared bank " + s["SharedBank".Length..],
            var s when s.StartsWith("Bank", StringComparison.Ordinal) => "Bank " + s["Bank".Length..],
            var s when s.StartsWith("General ", StringComparison.Ordinal) => "Bag " + s["General ".Length..],
            var s when s.StartsWith("Personal-Depot", StringComparison.Ordinal) => "Depot " + s["Personal-Depot".Length..],
            var s => s,
        };
        return slot.Length > 0 ? $"{head} · {slot}" : head;
    }

    /// <summary>Every slot with every item that fits it, from the dump's rows.</summary>
    public static List<SlotList> Build(IEnumerable<InventoryStore.CarryRow> rows, ItemStats stats)
    {
        var laneSet = new HashSet<string>(Lanes, StringComparer.Ordinal);
        var bySlot = BisFinder.Slots.ToDictionary(s => s.Key, _ => new Dictionary<string, Item>(StringComparer.Ordinal));
        foreach (var r in rows)
        {
            if (!laneSet.Contains(r.Lane) || r.IsContainer || InventoryStore.IsExaltation(r.Name)) continue;
            if (r.Name.Equals("Empty", StringComparison.OrdinalIgnoreCase)) continue;
            var rec = stats.Lookup(r.Name);
            if (rec is null || stats.IsContainer(r.Name)) continue;
            var keys = SlotKeys(rec);
            if (keys.Count == 0) continue;
            int tier = BisFinder.TierOf(r.Name);
            string wornKey = r.Lane == "worn" ? WornKey(r.Location) : "";
            string itemKey = FocusEffects.ItemKey(r.Name);
            foreach (var key in keys)
            {
                // A worn item is worn only in the slot it is IN (a 1H in Secondary is not
                // worn under Primary); an Any Slot item is worn, but in no listed slot.
                string wk = wornKey.Length == 0 ? "" : wornKey == "ANY" ? "ANY" : wornKey == key || keys.Count == 1 ? key : "";
                var copy = new Copy(r.Location, r.Lane, tier, Math.Max(1, r.Count), wk);
                var dict = bySlot[key];
                if (dict.TryGetValue(itemKey, out var have)) have.Copies.Add(copy);
                else dict[itemKey] = new Item(rec.Name.Length > 0 ? rec.Name : StripTier(r.Name), itemKey, rec, new List<Copy> { copy });
            }
        }
        var result = new List<SlotList>();
        foreach (var (key, label, _) in BisFinder.Slots)
        {
            var items = bySlot[key].Values.ToList();
            foreach (var it in items)
                it.Copies.Sort((a, b) => { int c = Rank(a.Lane).CompareTo(Rank(b.Lane)); return c != 0 ? c : b.Tier.CompareTo(a.Tier); });
            items.Sort((a, b) => { int w = b.Worn.CompareTo(a.Worn); return w != 0 ? w : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            result.Add(new SlotList(key, key == "FINGER" ? "Fingers" : label, items));
        }
        return result;
    }

    /// <summary>Items in the listed places the wiki table has no record for —
    /// shown, not hidden (owner, 1 Oct: "why isn't it part of the recommendation?").</summary>
    public static List<string> Unknown(IEnumerable<InventoryStore.CarryRow> rows, ItemStats stats)
    {
        var laneSet = new HashSet<string>(Lanes, StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var r in rows)
        {
            if (!laneSet.Contains(r.Lane) || r.IsContainer || InventoryStore.IsExaltation(r.Name)) continue;
            if (r.Name.Equals("Empty", StringComparison.OrdinalIgnoreCase) || stats.Lookup(r.Name) is not null) continue;
            string n = StripTier(r.Name);
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }
        return names;
    }

    private static int Rank(string lane) => Array.IndexOf(Lanes, lane) is var i && i >= 0 ? i : Lanes.Length;

    private static string StripTier(string name) => Regex.Replace(name.Trim(), @" \+\d+$", "");

    /// <summary>Which combo can wear it. The wiki's empty class field reads Unknown.</summary>
    public static Fit FitOf(Item item, IReadOnlyCollection<string> you, IReadOnlyCollection<string> compare)
    {
        if (string.IsNullOrWhiteSpace(item.Rec.Classes)) return Fit.Unknown;
        if (you.Count > 0 && BisFinder.ClassAllowed(item.Rec.Classes, you)) return Fit.You;
        if (compare.Count > 0 && BisFinder.ClassAllowed(item.Rec.Classes, compare)) return Fit.Compare;
        return Fit.Neither;
    }

    /// <summary>Can this one class wear it?</summary>
    public static bool ClassCan(ItemStats.Record rec, string cls) =>
        !string.IsNullOrWhiteSpace(rec.Classes) && BisFinder.ClassAllowed(rec.Classes, new[] { cls });

    /// <summary>Who could use the items neither combo can wear — copies per class, most first.</summary>
    public static List<(string Cls, int N)> Tally(IEnumerable<Item> items)
    {
        var t = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in items)
            foreach (var cls in BisFinder.AllClasses)
                if (ClassCan(it.Rec, cls)) t[cls] = t.GetValueOrDefault(cls) + it.Count;
        return t.OrderByDescending(kv => kv.Value).ThenBy(kv => Array.IndexOf(BisFinder.AllClasses, kv.Key)).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>"HP +5 · INT +2 · Haste 36%" — the tier-scaled stats, pools first, in the wiki's order.</summary>
    public static string StatLine(Item item)
    {
        var scaled = item.Stats;
        var parts = new List<string>();
        var order = new List<string>();
        foreach (var p in item.Rec.Stats.Concat(item.Rec.Saves))
            if (p.Length >= 2) { string k = ItemUpgrade.NormalizeKey(p[0]); if (!order.Contains(k)) order.Add(k); }
        foreach (var k in order.OrderByDescending(k => k is "HP" or "MP"))
        {
            if (!scaled.TryGetValue(k, out int v)) { var raw = item.Rec.Stats.FirstOrDefault(p => p.Length >= 2 && ItemUpgrade.NormalizeKey(p[0]) == k); if (raw is not null) parts.Add($"{raw[0]} {raw[1]}"); continue; }
            parts.Add($"{(k == "MP" ? "MANA" : k.Replace("SV_", "SV "))} {(v >= 0 ? "+" : "")}{v}");
        }
        if (scaled.TryGetValue("DMG", out int dmg) && scaled.TryGetValue("DELAY", out int dly)) parts.Insert(0, $"{dmg}/{dly}");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }
}
