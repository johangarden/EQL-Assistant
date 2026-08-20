using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLOverlay.Services;

/// <summary>
/// Item stats for the character sheet: AC, structured stat/save pairs, weapon
/// numbers, flags, classes, effect lines and the wiki icon id per item, keyed
/// by normalized base name. Data: data\item-stats.json — eqlwiki item pages
/// via Companion's scrape (MIT), ~11,400 items. Values are the wiki's BASE
/// item; ItemUpgrade scales them to a worn +N tier with the wiki's own
/// slider rules.
/// </summary>
public sealed class ItemStats
{
    public sealed class Record
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("slot")] public string Slot { get; set; } = "";
        [JsonPropertyName("ac")] public int? Ac { get; set; }
        [JsonPropertyName("dmg")] public int? Dmg { get; set; }
        [JsonPropertyName("delay")] public int? Delay { get; set; }
        [JsonPropertyName("dmgBon")] public int? DmgBonus { get; set; }
        [JsonPropertyName("backstab")] public int? Backstab { get; set; }
        [JsonPropertyName("skill")] public string Skill { get; set; } = "";
        [JsonPropertyName("range")] public string Range { get; set; } = "";
        /// <summary>[["STR","+3"], ["Haste","36%"], …] — source order.</summary>
        [JsonPropertyName("stats")] public List<string[]> Stats { get; set; } = new();
        /// <summary>The SV * subset, same pair shape.</summary>
        [JsonPropertyName("saves")] public List<string[]> Saves { get; set; } = new();
        [JsonPropertyName("flags")] public string Flags { get; set; } = "";
        [JsonPropertyName("weight")] public string Weight { get; set; } = "";
        [JsonPropertyName("size")] public string Size { get; set; } = "";
        [JsonPropertyName("classes")] public string Classes { get; set; } = "";
        [JsonPropertyName("races")] public string Races { get; set; } = "";
        [JsonPropertyName("effects")] public string Effects { get; set; } = "";
        [JsonPropertyName("extras")] public string Extras { get; set; } = "";
        /// <summary>The wiki icon id — data\item-icons\item-{Icon}.png.</summary>
        [JsonPropertyName("icon")] public int? Icon { get; set; }
    }

    private sealed class FileShape
    {
        [JsonPropertyName("items")] public Dictionary<string, Record>? Items { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, Record> _items;

    public int Count => _items.Count;

    public ItemStats()
    {
        _items = Load();
    }

    /// <summary>The wiki record for an item name (any spelling the dump
    /// uses — +N, "(Exaltation)", backticks and apostrophes all fold), or
    /// null when the wiki has no page for it.</summary>
    public Record? Lookup(string itemName) =>
        _items.TryGetValue(FocusEffects.ItemKey(itemName), out var rec) ? rec : null;

    /// <summary>In-window label for a stat key ("STR" → "Strength",
    /// "SV FIRE" → "SV Fire").</summary>
    public static string StatLabel(string key)
    {
        string k = key.ToUpperInvariant();
        string? known = k switch
        {
            "STR" => "Strength", "STA" => "Stamina", "AGI" => "Agility",
            "DEX" => "Dexterity", "WIS" => "Wisdom", "INT" => "Intelligence",
            "CHA" => "Charisma", "HP" => "HP", "MANA" => "Mana",
            "END" or "ENDURANCE" => "Endurance", "AC" => "AC",
            "HASTE" => "Haste", "ATTACK" => "Attack", "REGEN" => "Regen",
            _ => null,
        };
        if (known is not null) return known;
        if (k.StartsWith("SV ", StringComparison.Ordinal))
            return "SV " + TitleCase(k[3..]);
        return TitleCase(k);
    }

    private static string TitleCase(string s) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    private static Dictionary<string, Record> Load()
    {
        try
        {
            // A data\item-stats.json next to the exe wins (user-tweakable);
            // otherwise the copy embedded in the assembly.
            string path = Path.Combine(AppContext.BaseDirectory, "data", "item-stats.json");
            string json;
            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }
            else
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("item-stats.json", StringComparison.OrdinalIgnoreCase));
                if (resName is null) return new();
                using var stream = asm.GetManifestResourceStream(resName)!;
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
            var file = JsonSerializer.Deserialize<FileShape>(json, JsonOpts);
            return file?.Items ?? new Dictionary<string, Record>();
        }
        catch (Exception ex)
        {
            Log.Warn("Item stats failed to load: " + ex.Message);
            return new Dictionary<string, Record>();
        }
    }
}
