using System.IO;
using System.Text.Json;

namespace EQLOverlay.Services;

/// <summary>
/// Item stats for the character sheet: AC, stats, saves, flags, classes and
/// effect lines per item, keyed by normalized base name. Data:
/// data\item-stats.json — eqlwiki item pages via Companion's scrape (MIT),
/// slimmed to display-ready strings (~11,400 items, ~2 MB). Values are the
/// wiki's BASE item — the game states nowhere what a +N uplift changes, so
/// the sheet shows base numbers and says so.
/// </summary>
public sealed class ItemStats
{
    public sealed class Record
    {
        public string Name { get; set; } = "";
        public string Slot { get; set; } = "";
        public int? Ac { get; set; }
        public string Stats { get; set; } = "";
        public string Saves { get; set; } = "";
        public string Flags { get; set; } = "";
        public string Weight { get; set; } = "";
        public string Size { get; set; } = "";
        public string Classes { get; set; } = "";
        public string Races { get; set; } = "";
        public string Effects { get; set; } = "";
        public string Extras { get; set; } = "";
    }

    private sealed class FileShape
    {
        public Dictionary<string, Record>? Items { get; set; }
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
