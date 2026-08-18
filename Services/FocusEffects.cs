using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The focus-effect families and their tiered members, with the items that
/// carry each tier — the data behind the Inventory window's Focus effects
/// audit ("do I own the best tier of each effect, and where is it?").
/// Data: data\focus-effects.json, built from eqlwiki.com Category:Focus_Effects
/// pages plus jmoyers/everquest-companion items.json (MIT). Tier ORDER inside
/// a family is the observed decay-clause level cap (the percent is identical
/// across a family's tiers); Jedah's states no caps and rides the
/// Lesser &lt; base &lt; Greater &lt; Superior convention its three sibling
/// families all follow.
/// </summary>
public sealed class FocusEffects
{
    public sealed class Tier
    {
        public string Effect { get; set; } = "";
        // The JSON spells it "tier" — an unmapped rename here once made every
        // tier deserialize as 0 and the audit read "none owned" forever.
        [System.Text.Json.Serialization.JsonPropertyName("tier")]
        public int TierNum { get; set; }
        public string Description { get; set; } = "";
        public int? LevelCap { get; set; }
        public List<string> Items { get; set; } = new();
    }

    public sealed class Family
    {
        [System.Text.Json.Serialization.JsonPropertyName("family")]
        public string Name { get; set; } = "";
        /// <summary>"spell" or "song" (the bard instrument resonances).</summary>
        public string Group { get; set; } = "spell";
        /// <summary>Short what-it-does label ("cast speed", "DoT damage").</summary>
        public string Kind { get; set; } = "";
        public List<Tier> Tiers { get; set; } = new();
    }

    /// <summary>One family's audit row: the best tier among the items you
    /// hold, and the item + place granting it. BestTier is 0 when you own
    /// none. Status: 2 = best available (green), 1 = something but better
    /// exists (orange), 0 = nothing (red).</summary>
    public sealed record AuditRow(Family Family, int BestTier, string BestEffect,
        string BestItem, string BestPlace, IReadOnlyList<bool> OwnedTiers)
    {
        public int Status => BestTier == 0 ? 0 : BestTier == Family.Tiers.Count ? 2 : 1;
    }

    public IReadOnlyList<Family> Families { get; }

    // normalized item name -> every (family, tier) it grants. A few items
    // carry two focus effects; a list keeps both.
    private readonly Dictionary<string, List<(Family Fam, Tier Tier)>> _byItem = new(StringComparer.Ordinal);

    private sealed class FileShape
    {
        public List<Family>? Families { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public FocusEffects() : this(Load()) { }

    public FocusEffects(IReadOnlyList<Family> families)
    {
        Families = families;
        foreach (var fam in families)
            foreach (var tier in fam.Tiers)
                foreach (string item in tier.Items)
                {
                    string key = ItemKey(item);
                    if (!_byItem.TryGetValue(key, out var list))
                        _byItem[key] = list = new List<(Family, Tier)>();
                    list.Add((fam, tier));
                }
    }

    private static IReadOnlyList<Family> Load()
    {
        try
        {
            // A data\focus-effects.json next to the exe wins (user-tweakable);
            // otherwise the copy embedded in the assembly.
            string path = Path.Combine(AppContext.BaseDirectory, "data", "focus-effects.json");
            string json;
            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }
            else
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("focus-effects.json", StringComparison.OrdinalIgnoreCase));
                if (resName is null) return Array.Empty<Family>();
                using var stream = asm.GetManifestResourceStream(resName)!;
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
            var file = JsonSerializer.Deserialize<FileShape>(json, JsonOpts);
            return file?.Families ?? new List<Family>();
        }
        catch (Exception ex)
        {
            Log.Warn("Focus effects failed to load: " + ex.Message);
            return Array.Empty<Family>();
        }
    }

    private static readonly Regex TierSuffixRx = new(@" \+\d+$", RegexOptions.Compiled);

    /// <summary>The join key for an item name: the dump's spelling folded to
    /// the wiki's — " (Exaltation)" and " +N" and the trailing "*" stripped,
    /// the apostrophe/backtick coin-flip settled, lowercased. The exaltation
    /// suffix folds because in EQ Legends the exaltation IS the focus
    /// carrier — "Runed Mithril Bracer (Exaltation)" grants what the wiki
    /// lists under "Runed Mithril Bracer".</summary>
    public static string ItemKey(string name)
    {
        string s = name.Trim();
        const string exalt = " (Exaltation)";
        if (s.EndsWith(exalt, StringComparison.Ordinal)) s = s[..^exalt.Length];
        if (s.EndsWith("*", StringComparison.Ordinal)) s = s[..^1];
        s = TierSuffixRx.Replace(s, "");
        return s.Replace('`', '\'').ToLowerInvariant();
    }

    /// <summary>Audit the dump's rows: for every family, the best tier among
    /// the items owned anywhere (worn, bags, bank, depot, key ring), and
    /// where the granting item sits. Preference among equal tiers: worn
    /// first, then the key ring, then storage — the "and are you WEARING
    /// it" half of the question.</summary>
    public List<AuditRow> Audit(IEnumerable<InventoryStore.CarryRow> rows)
    {
        // place preference: lower sorts first (worn = active; the keyring
        // collections are owned-but-not-worn, like any other storage)
        static int PlaceRank(string lane) => lane switch
        {
            "worn" => 0,
            "activated" => 1,
            "storage" or "keyring" => 2,
            _ => 3,
        };
        static string PlaceLabel(string lane) => lane switch
        {
            "worn" => "worn",
            "activated" => "activated item",
            "storage" => "in storage",
            "keyring" => "key ring",
            "bags" => "in bags",
            "bank" => "in bank",
            "depot" => "in depot",
            "hoard" => "in hoard",
            _ => lane,
        };

        // family -> tierNum -> best-ranked (item, lane)
        var owned = new Dictionary<Family, Dictionary<int, (string Item, string Lane)>>();
        foreach (var row in rows)
        {
            // "(Exaltation)" rows COUNT: EQ Legends delivers the classic
            // focus items as exaltations — the socketed copy in your worn
            // gear IS the active focus (measured on Thorrak's 2026-08-18
            // dump: Reagent Conservation III lives in a Wrist-Slot7 socket),
            // and the key ring's Augmentation category is the collected
            // pool. ItemKey folds the suffix, and a socket row inherits its
            // host's lane, so places read right on their own.
            if (!_byItem.TryGetValue(ItemKey(row.Name), out var hits)) continue;
            foreach (var (fam, tier) in hits)
            {
                if (!owned.TryGetValue(fam, out var tiers))
                    owned[fam] = tiers = new Dictionary<int, (string, string)>();
                if (!tiers.TryGetValue(tier.TierNum, out var cur)
                    || PlaceRank(row.Lane) < PlaceRank(cur.Lane))
                    tiers[tier.TierNum] = (row.Name, row.Lane);
            }
        }

        var result = new List<AuditRow>();
        foreach (var fam in Families)
        {
            owned.TryGetValue(fam, out var tiers);
            var ownedFlags = fam.Tiers.Select(t => tiers?.ContainsKey(t.TierNum) == true).ToList();
            int best = 0;
            for (int i = fam.Tiers.Count - 1; i >= 0; i--)
                if (ownedFlags[i]) { best = fam.Tiers[i].TierNum; break; }
            string effect = "", item = "", place = "";
            if (best > 0 && tiers is not null)
            {
                var hit = tiers[best];
                effect = fam.Tiers.First(t => t.TierNum == best).Effect;
                item = hit.Item;
                place = PlaceLabel(hit.Lane);
            }
            result.Add(new AuditRow(fam, best, effect, item, place, ownedFlags));
        }
        return result;
    }
}
