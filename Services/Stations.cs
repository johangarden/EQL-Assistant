using System.IO;
using System.Reflection;
using System.Text.Json;

namespace EQLOverlay.Services;

/// <summary>
/// Where the stationary tradeskill containers stand — forge, oven, brew barrel,
/// pottery wheel, kiln, loom — per zone, with the game's /loc (owner, 4 Oct: "the
/// helper should show the coords for that tradeskill in the current zone").
/// Data: <c>data/stations.json</c>, built by the scratch <c>build-stations.py</c>
/// from the Project 1999 wiki's container pages (classic zones match). Kits,
/// boxes, bowls and bags are carried, so they have no place here.
/// </summary>
public static class Stations
{
    /// <summary>Y, X (, Z) in the game's /loc order; null when the wiki names the spot without a /loc.</summary>
    public sealed record Station(string Kind, string Zone, double? Y, double? X, string Note, string Era, double? Z = null)
    {
        /// <summary>The waypoint command to paste in game, or "" without coordinates.</summary>
        public string Way => Y is { } y && X is { } x ? Stations.Way(y, x, Z) : "";
    }

    private sealed class Doc { public string Source { get; set; } = ""; public string Fetched { get; set; } = ""; public List<Station> Stations { get; set; } = new(); }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<List<Station>> All = new(Load);

    public static IReadOnlyList<Station> AllStations => All.Value;

    /// <summary>The station a recipe's container wording means, or null for the ones you carry
    /// (kits, toolboxes, mixing bowls, medicine bags, clay jars).</summary>
    public static string? KindOf(string container)
    {
        string c = (container ?? "").Trim().ToLowerInvariant();
        if (c.Length == 0) return null;
        if (c.Contains("forge")) return "Forge";
        if (c.Contains("oven") || c.Contains("spit")) return "Oven";
        if (c.Contains("brew")) return "Brew Barrel";
        if (c.Contains("pottery wheel")) return "Pottery Wheel";
        if (c.Contains("kiln")) return "Kiln";
        if (c.Contains("loom")) return "Loom";
        return null;
    }

    /// <summary>"The Ruins of Old Guk 1 (Awakened)" → "The Ruins of Old Guk": the zone the game names, less its instance tail.</summary>
    public static string FoldZone(string zone)
    {
        string z = (zone ?? "").Trim();
        z = System.Text.RegularExpressions.Regex.Replace(z, @"\s+\d+\s*\(Awakened\)$|\s*\(Awakened\)$", "");
        return z.Trim();
    }

    /// <summary>The stations of one kind in a zone — classic first, the wiki's order within.</summary>
    public static List<Station> In(string kind, string zone)
    {
        string z = FoldZone(zone);
        if (z.Length == 0) return new List<Station>();
        return All.Value.Where(s => s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && s.Zone.Equals(z, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Era.Length > 0 ? 1 : 0).ToList();
    }

    /// <summary>"/way X Y Z" from a /loc's Y, X, Z (owner, 4 Oct: the game's waypoint takes X first). Z left off when unknown.</summary>
    public static string Way(double y, double x, double? z = null)
    {
        static string N(double v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        return $"/way {N(x)} {N(y)}" + (z is { } zz ? " " + N(zz) : "");
    }

    private static List<Station> Load()
    {
        try
        {
            string diskPath = Path.Combine(AppContext.BaseDirectory, "data", "stations.json");
            string? json = null;
            if (File.Exists(diskPath)) json = File.ReadAllText(diskPath);
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("stations.json", StringComparison.OrdinalIgnoreCase));
                if (res is not null)
                {
                    using var stream = asm.GetManifestResourceStream(res)!;
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
            }
            if (json is null) return new List<Station>();
            return JsonSerializer.Deserialize<Doc>(json, JsonOpts)?.Stations ?? new List<Station>();
        }
        catch (Exception ex) { Log.Warn("stations.json unreadable: " + ex.Message); return new List<Station>(); }
    }
}
