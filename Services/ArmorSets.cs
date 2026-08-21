using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLOverlay.Services;

/// <summary>
/// The planar class armor sets — data\armor-sets.json, Johan's scrape of
/// eqlwiki's Category:Armor_Sets (21 Aug 2026): every class's classic planar
/// set (Iksar alternates included), the Hate 2.0 / revamp sets whose wiki
/// pages don't list pieces yet, and the two MULTI-CLASS planar sets
/// (chain &amp; plate, cloth &amp; leather) with per-piece class lists.
/// Ownership is judged elsewhere (the Character window matches piece names
/// against the dump via the focus join key); this class only carries the
/// wiki's knowledge of what a set IS.
/// </summary>
public sealed class ArmorSets
{
    /// <summary>One piece. Classes is empty for single-class sets (the set's
    /// class carries) and per-piece for the multi-class sets — Lustrous
    /// Russet's Breastplate admits BER, its Helm does not.</summary>
    public sealed record Piece(string Name, string Slot, IReadOnlyList<string> Classes);

    public sealed class Set
    {
        public string Name = "";
        /// <summary>Class abbreviations the set serves (multi-class: the
        /// union over its pieces).</summary>
        public IReadOnlyList<string> Classes = Array.Empty<string>();
        /// <summary>Short who-is-it-for text: "SHD", or "chain &amp; plate".</summary>
        public string Kind = "";
        public IReadOnlyList<string> Zones = Array.Empty<string>();
        public string RaceNote = "";
        /// <summary>Non-empty when the wiki page doesn't list pieces yet.</summary>
        public string Note = "";
        public IReadOnlyList<Piece> Pieces = Array.Empty<Piece>();
        public bool Multiclass;
    }

    public IReadOnlyList<Set> Sets { get; }

    public ArmorSets() => Sets = Load();

    /// <summary>Does this set serve any of the player's classes ("SHD/ROG/SHM"
    /// from /who, split)? Unknown classes = everything is relevant.</summary>
    public static bool Relevant(Set s, IReadOnlyCollection<string> classes) =>
        classes.Count == 0
        || s.Classes.Any(c => classes.Contains(c, StringComparer.OrdinalIgnoreCase));

    /// <summary>Display order for piece slots, head to toe.</summary>
    public static int SlotRank(string slot) => slot.ToUpperInvariant() switch
    {
        "HEAD" => 0, "FACE" => 1, "SHOULDERS" => 2, "CHEST" => 3, "ARMS" => 4,
        "WRIST" => 5, "HANDS" => 6, "LEGS" => 7, "FEET" => 8, _ => 9,
    };

    // ---- JSON shapes (the scrape's own field names; extras are ignored) -----

    private sealed class RootJson
    {
        [JsonPropertyName("classes")] public List<ClassJson>? Classes { get; set; }
        [JsonPropertyName("multiclass_planar")] public List<SetJson>? Multiclass { get; set; }
    }

    private sealed class ClassJson
    {
        [JsonPropertyName("abbr")] public string Abbr { get; set; } = "";
        [JsonPropertyName("classic_planar")] public List<SetJson>? Classic { get; set; }
        [JsonPropertyName("hate_2_0")] public List<SetJson>? Hate20 { get; set; }
    }

    private sealed class SetJson
    {
        [JsonPropertyName("set")] public string Name { get; set; } = "";
        [JsonPropertyName("zones")] public List<string>? Zones { get; set; }
        [JsonPropertyName("race_restriction")] public string? Race { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("pieces")] public List<PieceJson>? Pieces { get; set; }
        [JsonPropertyName("pieces_note")] public string? PiecesNote { get; set; }
    }

    private sealed class PieceJson
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("slot")] public string Slot { get; set; } = "";
        [JsonPropertyName("classes")] public List<string>? Classes { get; set; }
    }

    private static List<Set> Load()
    {
        try
        {
            // A data\armor-sets.json next to the exe wins (user-tweakable);
            // otherwise the copy embedded in the assembly.
            string path = Path.Combine(AppContext.BaseDirectory, "data", "armor-sets.json");
            string json;
            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }
            else
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("armor-sets.json", StringComparison.OrdinalIgnoreCase));
                if (resName is null) return new();
                using var stream = asm.GetManifestResourceStream(resName)!;
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
            var root = JsonSerializer.Deserialize<RootJson>(json);
            if (root is null) return new();

            var sets = new List<Set>();
            foreach (var cls in root.Classes ?? new())
            {
                foreach (var sj in (cls.Classic ?? new()).Concat(cls.Hate20 ?? new()))
                    sets.Add(Build(sj, new[] { cls.Abbr }, cls.Abbr, multiclass: false));
            }
            foreach (var sj in root.Multiclass ?? new())
            {
                var union = (sj.Pieces ?? new())
                    .SelectMany(p => p.Classes ?? new())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string kind = sj.Category?.Contains("chain", StringComparison.OrdinalIgnoreCase) == true
                    ? "chain & plate"
                    : sj.Category?.Contains("cloth", StringComparison.OrdinalIgnoreCase) == true
                        ? "cloth & leather"
                        : "multi-class";
                sets.Add(Build(sj, union, kind, multiclass: true));
            }
            return sets;
        }
        catch (Exception ex)
        {
            Log.Info("Armor sets failed to load: " + ex.Message);
            return new();
        }
    }

    private static Set Build(SetJson sj, IReadOnlyList<string> classes, string kind, bool multiclass) => new()
    {
        Name = sj.Name,
        Classes = classes,
        Kind = kind,
        Zones = sj.Zones ?? new List<string>(),
        RaceNote = sj.Race ?? "",
        Note = sj.PiecesNote ?? "",
        Pieces = (sj.Pieces ?? new())
            .Where(p => p.Name.Length > 0)
            .Select(p => new Piece(p.Name, p.Slot,
                (IReadOnlyList<string>?)p.Classes ?? Array.Empty<string>()))
            .ToList(),
        Multiclass = multiclass,
    };
}
