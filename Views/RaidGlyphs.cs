using System.Windows;
using System.Windows.Media;

namespace EQLOverlay.Views;

/// <summary>
/// Self-made vector badges for the Raid Kills page: ~9 archetype silhouettes
/// (dragon, skull, demon, golem, bird, wasp, eye, spirit, claw) mapped over
/// the default target list, each with its own tint. No game assets — the
/// wiki's screenshots are Daybreak's copyright and look muddy at 24px anyway.
/// A target we don't know (user-edited raid-targets.json) falls back to a
/// monogram badge colored by a stable name hash.
/// </summary>
public static class RaidGlyphs
{
    public sealed record Badge(Geometry? Glyph, Color Tint, string? Monogram);

    // ---- archetype silhouettes (24x24 viewbox, FillRule=EvenOdd) --------------

    private static readonly Dictionary<string, string> GlyphPaths = new(StringComparer.Ordinal)
    {
        // Wyvern in flight: two spread scalloped wings meeting at the body,
        // barbed tail sweeping down — the shape that reads "dragon" at 24px.
        ["dragon"] =
            "M2 3.4 C7.2 3.8 10.6 5.8 12 9 C13.4 5.8 16.8 3.8 22 3.4 " +
            "C19.6 5.6 18.4 7.8 18.2 10 C17 9.4 15.8 9.2 14.6 9.4 C15 10.6 15.1 11.8 14.8 13 " +
            "C13.9 12.3 13.2 12 12.6 12 C12.9 14.8 12.5 17.6 10.2 21.2 C9.8 18.8 9 17.2 7.8 16 " +
            "L10.4 15.6 C11.2 14.4 11.5 13.2 11.4 12 C10.7 12.1 10 12.4 9.2 13 " +
            "C8.9 11.8 9 10.6 9.4 9.4 C8.2 9.2 7 9.4 5.8 10 C5.6 7.8 4.4 5.6 2 3.4 Z",

        // Classic skull: cranium, eye holes, nose notch, jaw.
        ["skull"] =
            "M12 2 C6.5 2 3 5.8 3 10.5 C3 13.3 4.3 15.4 6.2 16.8 L6.2 19.5 C6.2 20.9 7.3 22 8.7 22 " +
            "L15.3 22 C16.7 22 17.8 20.9 17.8 19.5 L17.8 16.8 C19.7 15.4 21 13.3 21 10.5 C21 5.8 17.5 2 12 2 Z " +
            "M8.6 9.2 C9.9 9.2 10.9 10.3 10.9 11.6 C10.9 12.9 9.9 14 8.6 14 C7.3 14 6.3 12.9 6.3 11.6 C6.3 10.3 7.3 9.2 8.6 9.2 Z " +
            "M15.4 9.2 C16.7 9.2 17.7 10.3 17.7 11.6 C17.7 12.9 16.7 14 15.4 14 C14.1 14 13.1 12.9 13.1 11.6 C13.1 10.3 14.1 9.2 15.4 9.2 Z " +
            "M12 14.6 L13.3 17.4 L10.7 17.4 Z",

        // Horned face with slit eyes.
        ["demon"] =
            "M4 3.5 C6 5.8 7.4 6.4 8.9 6.4 C9.9 5.7 10.9 5.4 12 5.4 C13.1 5.4 14.1 5.7 15.1 6.4 " +
            "C16.6 6.4 18 5.8 20 3.5 C19.8 6.8 19 8.9 17.8 10.1 C18.4 11.3 18.7 12.7 18.7 14.1 " +
            "C18.7 18.4 15.7 21.5 12 21.5 C8.3 21.5 5.3 18.4 5.3 14.1 C5.3 12.7 5.6 11.3 6.2 10.1 C5 8.9 4.2 6.8 4 3.5 Z " +
            "M8.4 12 L11.2 13.2 L8.2 14.6 Z M15.6 12 L15.8 14.6 L12.8 13.2 Z " +
            "M12 16.2 L13.4 18.6 L12 17.9 L10.6 18.6 Z",

        // Hulking blocky brute: broad shoulders, small head, heavy arms.
        ["golem"] =
            "M9.4 3 L14.6 3 L15.4 5.6 L19.8 7 L21.6 12.4 L19 16.4 L17 14.4 L17.6 20.8 L13.4 21.6 L13 17 " +
            "L11 17 L10.6 21.6 L6.4 20.8 L7 14.4 L5 16.4 L2.4 12.4 L4.2 7 L8.6 5.6 Z " +
            "M9.6 8.2 L11 9.4 L8.8 10.2 Z M14.4 8.2 L15.2 10.2 L13 9.4 Z",

        // Spiroc: parrot head profile facing left — hooked open beak, crest, eye.
        ["bird"] =
            "M14 2.6 C12 2.2 10.4 2.6 9.2 3.6 C10 3.7 10.7 4 11.3 4.5 C8.6 5.3 6.7 7.2 5.9 9.8 " +
            "L2.2 10.4 C3.3 11.3 4.5 11.8 5.7 11.9 L2.8 14.2 C4.4 14.6 5.9 14.4 7.2 13.7 " +
            "C8.3 17.7 11.7 20.6 15.8 21.4 C14.6 19.8 13.9 18 13.8 16.2 C17.9 16.6 21.4 13.6 21.6 9.7 " +
            "C21.8 5.9 18.4 2.9 14 2.6 Z " +
            "M14.8 7 C15.7 7 16.4 7.7 16.4 8.6 C16.4 9.5 15.7 10.2 14.8 10.2 C13.9 10.2 13.2 9.5 13.2 8.6 C13.2 7.7 13.9 7 14.8 7 Z",

        // Wasp: paired wings, round head, striped abdomen tapering to a sting.
        ["wasp"] =
            "M9.2 4.4 C6.6 2.8 3.6 3.2 2.4 4.8 C3.6 6.8 6.6 7.6 9.2 6.8 Z " +
            "M14.8 4.4 C17.4 2.8 20.4 3.2 21.6 4.8 C20.4 6.8 17.4 7.6 14.8 6.8 Z " +
            "M12 4.6 C13.5 4.6 14.6 5.7 14.6 7.1 C14.6 8.5 13.5 9.6 12 9.6 C10.5 9.6 9.4 8.5 9.4 7.1 C9.4 5.7 10.5 4.6 12 4.6 Z " +
            "M12 10.2 C14.3 10.2 16.1 11.9 16.1 14 C16.1 16.9 14.1 19.8 12.6 21.4 L12 22.2 L11.4 21.4 " +
            "C9.9 19.8 7.9 16.9 7.9 14 C7.9 11.9 9.7 10.2 12 10.2 Z " +
            "M9 13.2 L15 13.2 L15 14.4 L9 14.4 Z M9.6 16.4 L14.4 16.4 L14.4 17.6 L9.6 17.6 Z",

        // Almond eye with iris ring and filled pupil.
        ["eye"] =
            "M12 5.4 C6.2 5.4 2.2 10.3 1.2 12 C2.2 13.7 6.2 18.6 12 18.6 C17.8 18.6 21.8 13.7 22.8 12 " +
            "C21.8 10.3 17.8 5.4 12 5.4 Z " +
            "M12 8 C14.2 8 16 9.8 16 12 C16 14.2 14.2 16 12 16 C9.8 16 8 14.2 8 12 C8 9.8 9.8 8 12 8 Z " +
            "M12 10.2 C13 10.2 13.8 11 13.8 12 C13.8 13 13 13.8 12 13.8 C11 13.8 10.2 13 10.2 12 C10.2 11 11 10.2 12 10.2 Z",

        // Ghost/wisp with wavy hem and hollow eyes.
        ["spirit"] =
            "M12 2.8 C7.4 2.8 4.2 6.2 4.2 10.8 L4.2 21 L6.8 18.8 L9.4 21.2 L12 18.8 L14.6 21.2 L17.2 18.8 " +
            "L19.8 21 L19.8 10.8 C19.8 6.2 16.6 2.8 12 2.8 Z " +
            "M9.1 8.6 C10 8.6 10.7 9.5 10.7 10.6 C10.7 11.7 10 12.6 9.1 12.6 C8.2 12.6 7.5 11.7 7.5 10.6 C7.5 9.5 8.2 8.6 9.1 8.6 Z " +
            "M14.9 8.6 C15.8 8.6 16.5 9.5 16.5 10.6 C16.5 11.7 15.8 12.6 14.9 12.6 C14 12.6 13.3 11.7 13.3 10.6 C13.3 9.5 14 8.6 14.9 8.6 Z",

        // Three raking talon slashes, tapering to points.
        ["claw"] =
            "M3.4 3.2 C7.4 6.6 9.8 10.8 10.6 15.8 C6.8 13 4.4 8.8 3.4 3.2 Z " +
            "M10.4 2.2 C15 6.6 17.4 12 17.6 18.4 C13.2 14.6 10.8 9.2 10.4 2.2 Z " +
            "M17.6 3.4 C20.4 6.8 21.8 10.8 21.6 15.2 C18.8 12.4 17.4 8.4 17.6 3.4 Z",
    };

    private static readonly Dictionary<string, Geometry> GeometryCache = new(StringComparer.Ordinal);

    /// <summary>All glyph keys (the --render-glyphs sheet iterates these).</summary>
    public static IEnumerable<string> GlyphKeys => GlyphPaths.Keys;

    public static Geometry GlyphFor(string key)
    {
        if (!GeometryCache.TryGetValue(key, out var g))
        {
            g = Geometry.Parse("F0 " + GlyphPaths[key]);
            if (g is PathGeometry pg) pg.FillRule = FillRule.EvenOdd;
            g.Freeze();
            GeometryCache[key] = g;
        }
        return g;
    }

    // ---- the default target list, mapped ----------------------------------------

    private static readonly Dictionary<string, (string Glyph, string Tint)> ByTarget =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Open World
        ["Lord Nagafen"] = ("dragon", "#EF5350"),          // red dragon
        ["Lady Vox"] = ("dragon", "#81D4FA"),              // ice dragon
        ["Master Yael"] = ("skull", "#E6DBC8"),            // bone golem

        // Plane of Fear
        ["Cazic Thule"] = ("demon", "#66BB6A"),            // the god of fear
        ["Dread"] = ("golem", "#E57373"),
        ["Fright"] = ("golem", "#FFB74D"),
        ["Terror"] = ("golem", "#9575CD"),
        ["A dracoliche"] = ("dragon", "#D7CCC8"),          // bone dragon

        // Plane of Hate
        ["Innoruuk"] = ("demon", "#BA68C8"),               // the god of hate
        ["Maestro of Rancor"] = ("spirit", "#CE93D8"),
        ["Lord of Loathing"] = ("demon", "#AB47BC"),
        ["Lord of Ire"] = ("demon", "#8E24AA"),
        ["Master of Spite"] = ("demon", "#BA68C8"),
        ["Mistress of Scorn"] = ("demon", "#F48FB1"),
        ["High Priest M'kari"] = ("spirit", "#B39DDB"),
        ["Magi P'tasa"] = ("spirit", "#9FA8DA"),
        ["Coercer T'vala"] = ("eye", "#CE93D8"),
        ["Grandmaster R'Tal"] = ("claw", "#A1887F"),
        ["Ashenbone Broodmaster"] = ("skull", "#B0A695"),  // skeletal dragon
        ["Avatar of Abhorrence"] = ("demon", "#7E57C2"),

        // Plane of Sky
        ["Noble Dojorn"] = ("spirit", "#FFB74D"),          // efreeti
        ["Thunder Spirit Princess"] = ("spirit", "#FFF176"),
        ["Protector of Sky"] = ("bird", "#4FC3F7"),
        ["Gorgalosk"] = ("claw", "#8D6E63"),
        ["Keeper of Souls"] = ("skull", "#90A4AE"),
        ["The Spiroc Lord"] = ("bird", "#26C6DA"),
        ["Bazzt Zzzt"] = ("wasp", "#FFD54F"),
        ["Sister of the Spire"] = ("bird", "#F48FB1"),     // spiroc too
        ["Eye of Veeshan"] = ("eye", "#4DD0E1"),
        ["Overseer of Air"] = ("spirit", "#B0BEC5"),
        ["The Hand of Veeshan"] = ("claw", "#7986CB"),
    };

    // Monogram fallback palette (stable pick by name hash).
    private static readonly string[] FallbackTints =
        { "#4FC3F7", "#81C784", "#E57373", "#BA68C8", "#FFB74D", "#64B5F6", "#4DB6AC", "#F06292" };

    /// <summary>Badge for a target: a mapped silhouette, or a monogram in a
    /// stable hash color for names we don't know.</summary>
    public static Badge For(string targetName)
    {
        if (ByTarget.TryGetValue(targetName.Trim(), out var hit))
            return new Badge(GlyphFor(hit.Glyph), ParseColor(hit.Tint), null);

        // Deterministic (string.GetHashCode is per-process-randomized).
        int h = 0;
        foreach (char c in targetName.Trim().ToUpperInvariant()) h = h * 31 + c;
        string tint = FallbackTints[Math.Abs(h) % FallbackTints.Length];
        string t = targetName.Trim();
        foreach (var art in new[] { "a ", "an ", "the " })
            if (t.StartsWith(art, StringComparison.OrdinalIgnoreCase) && t.Length > art.Length)
            { t = t[art.Length..]; break; }
        return new Badge(null, ParseColor(tint),
            t.Length > 0 ? char.ToUpperInvariant(t[0]).ToString() : "?");
    }

    /// <summary>True when the target resolves to a drawn silhouette (selftest).</summary>
    public static bool HasGlyph(string targetName) => ByTarget.ContainsKey(targetName.Trim());

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
