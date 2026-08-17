using System.Windows.Media;

namespace EQLOverlay.Views;

/// <summary>
/// Self-made vector class icons for the Plane of Sky badge strip — sixteen
/// archetype silhouettes in the RaidGlyphs style (24×24 viewbox, EvenOdd
/// holes), inspired by the classic class-selection iconography without using
/// any game art: lute, paw, axes, ankh, eye, fist, skull, helm, bow, dagger,
/// horned helm, leaf, feather, flame, orb, shield.
/// </summary>
public static class ClassGlyphs
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        // Crested helm, front-on, visor slit cut out.
        ["Warrior"] =
            "M12 3.5 C7.5 3.5 4.5 7 4.5 11.5 L4.5 20.5 L8.5 20.5 L8.5 16.5 L15.5 16.5 L15.5 20.5 " +
            "L19.5 20.5 L19.5 11.5 C19.5 7 16.5 3.5 12 3.5 Z " +
            "M11 1 L13 1 L13 4.2 L11 4.2 Z " +
            "M7.2 11 L16.8 11 L16.8 13.4 L7.2 13.4 Z",

        // Ankh: looped cross with the loop's hole.
        ["Cleric"] =
            "M12 1.5 C15 1.5 17.2 3.6 17.2 6.2 C17.2 8.6 15 10.8 13.1 11.9 L17.5 11.9 L17.5 14.5 " +
            "L13.3 14.5 L13.3 22.5 L10.7 22.5 L10.7 14.5 L6.5 14.5 L6.5 11.9 L10.9 11.9 " +
            "C9 10.8 6.8 8.6 6.8 6.2 C6.8 3.6 9 1.5 12 1.5 Z " +
            "M12 4 C10.4 4 9.4 5 9.4 6.3 C9.4 7.7 10.6 9.2 12 10.1 C13.4 9.2 14.6 7.7 14.6 6.3 " +
            "C14.6 5 13.6 4 12 4 Z",

        // Heater shield with a cross cut out.
        ["Paladin"] =
            "M12 1.8 L20.8 4.8 L20.8 12 C20.8 17.3 17 21 12 22.8 C7 21 3.2 17.3 3.2 12 L3.2 4.8 Z " +
            "M10.9 7 L13.1 7 L13.1 10.4 L16.4 10.4 L16.4 12.6 L13.1 12.6 L13.1 17 L10.9 17 " +
            "L10.9 12.6 L7.6 12.6 L7.6 10.4 L10.9 10.4 Z",

        // Bow, strung, arrow nocked and pointing right.
        ["Ranger"] =
            "M7.2 1.6 C15 5 15 19 7.2 22.4 L9.6 22.4 C17.4 18 17.4 6 9.6 1.6 Z " +
            "M7.4 2 L8.6 2 L8.6 22 L7.4 22 Z " +
            "M8.5 10.9 L17.4 10.9 L15.2 8.7 L16.6 8 L21.8 12 L16.6 16 L15.2 15.3 L17.4 13.1 L8.5 13.1 Z",

        // Horned helm with a glowering visor slit.
        ["Shadow Knight"] =
            "M12 5.6 C8.6 5.6 6.4 8.2 6.4 12 L6.4 20.5 L9.8 20.5 L9.8 16.8 L14.2 16.8 L14.2 20.5 " +
            "L17.6 20.5 L17.6 12 C17.6 8.2 15.4 5.6 12 5.6 Z " +
            "M7.4 7.2 C4.8 5.8 3.2 3.4 3.2 1.2 C6.4 2.2 8.6 4 9.4 6.2 Z " +
            "M16.6 7.2 C19.2 5.8 20.8 3.4 20.8 1.2 C17.6 2.2 15.4 4 14.6 6.2 Z " +
            "M8.6 11 L15.4 11 L15.4 13.2 L8.6 13.2 Z",

        // Leaf with a vein slit.
        ["Druid"] =
            "M11.4 22.4 C3.6 16.4 4.2 5.6 20.6 1.8 C22.6 13.6 18.4 20.6 11.4 22.4 Z " +
            "M10.8 20.4 C11.6 14 14 8 18.6 4 L17.6 3.6 C12.8 8 10.4 14 9.8 20 Z",

        // Yin-yang: ring, S-half, and the two dots (a fist never survives 24px).
        ["Monk"] =
            "M12 1.2 A10.8 10.8 0 1 0 12.01 1.2 Z M12 2.6 A9.4 9.4 0 1 1 11.99 2.6 Z " +
            "M12 3.4 A8.6 8.6 0 0 0 12 20.6 A4.3 4.3 0 0 1 12 12 A4.3 4.3 0 0 0 12 3.4 Z " +
            "M12 15 A1.7 1.7 0 1 0 12.01 15 Z " +
            "M12 5.6 A1.7 1.7 0 1 0 12.01 5.6 Z",

        // Lute at a jaunty angle: round body, sound-hole, neck and tuning head.
        ["Bard"] =
            "M8 10.6 C5.4 13.2 5 17 7.2 19.2 C9.4 21.4 13.2 21 15.8 18.4 C17.6 16.6 17.4 14 15.6 11.4 " +
            "L13 8.8 C10.4 7 7.8 6.8 8 10.6 Z " +
            "M11.2 13.4 C12.1 13.4 12.8 14.1 12.8 15 C12.8 15.9 12.1 16.6 11.2 16.6 " +
            "C10.3 16.6 9.6 15.9 9.6 15 C9.6 14.1 10.3 13.4 11.2 13.4 Z " +
            "M14 9.8 L19.4 4.4 L21 6 L15.6 11.4 Z " +
            "M19.2 2.6 L22.4 5.8 L23.4 4.8 L20.2 1.6 Z",

        // Dagger, point down: blade, crossguard, grip, pommel ring.
        ["Rogue"] =
            "M12 22.6 L9.3 12.4 L14.7 12.4 Z " +
            "M7.8 10.4 L16.2 10.4 L16.2 12.4 L7.8 12.4 Z " +
            "M10.7 6.6 L13.3 6.6 L13.3 10.4 L10.7 10.4 Z " +
            "M12 3.2 C13.1 3.2 14 4.1 14 5.2 C14 6.3 13.1 7.2 12 7.2 C10.9 7.2 10 6.3 10 5.2 " +
            "C10 4.1 10.9 3.2 12 3.2 Z",

        // Spirit feather with a bare quill stem — the stem is what keeps it
        // apart from the druid leaf at badge size.
        ["Shaman"] =
            "M11.2 19.2 C9.6 13.4 11.4 6.4 19.6 1.2 C20.8 9.2 17.6 16 12.4 18.8 Z " +
            "M12.2 17.2 C12.7 12.9 14.3 8.7 17.2 4.9 L16.5 4.5 C13.4 8.6 11.9 12.9 11.4 17 Z " +
            "M8.8 23 L11 18.2 L12.2 18.8 L10.2 23.6 Z",

        // Skull — the raid glyph, reused (death answers to both callings).
        ["Necromancer"] = RaidGlyphs.RawPath("skull"),

        // Flame with an inner lick cut out.
        ["Wizard"] =
            "M12 1.6 C13 5.6 17.4 8 17.4 13 C17.4 17.6 15 21.2 11.6 22.2 C8.2 21.6 6.2 18.6 6.2 15.2 " +
            "C6.2 12.4 7.6 10.4 9 8.8 C9.2 10.8 10.2 12 11.2 12.4 C10.6 9.2 11 5.2 12 1.6 Z " +
            "M11.8 15 C12.9 15 13.8 16 13.8 17.4 C13.8 18.8 12.9 19.8 11.8 19.8 " +
            "C10.7 19.8 9.8 18.8 9.8 17.4 C9.8 16 10.7 15 11.8 15 Z",

        // Summoning orb: core with four rays.
        ["Magician"] =
            "M12 7.4 C14.5 7.4 16.6 9.5 16.6 12 C16.6 14.5 14.5 16.6 12 16.6 " +
            "C9.5 16.6 7.4 14.5 7.4 12 C7.4 9.5 9.5 7.4 12 7.4 Z " +
            "M11 1 L13 1 L13 5.4 L11 5.4 Z M11 18.6 L13 18.6 L13 23 L11 23 Z " +
            "M1 11 L5.4 11 L5.4 13 L1 13 Z M18.6 11 L23 11 L23 13 L18.6 13 Z",

        // Almond eye — the raid glyph, reused (the enchanter's third eye).
        ["Enchanter"] = RaidGlyphs.RawPath("eye"),

        // Paw print: pad plus four toes.
        ["Beastlord"] =
            "M12 12.4 C15.6 12.4 18 14.8 18 17.4 C18 20.2 15.2 21.8 12 21.8 C8.8 21.8 6 20.2 6 17.4 " +
            "C6 14.8 8.4 12.4 12 12.4 Z " +
            "M4.6 7.6 C5.8 7.6 6.8 8.6 6.8 9.8 C6.8 11 5.8 12 4.6 12 C3.4 12 2.4 11 2.4 9.8 C2.4 8.6 3.4 7.6 4.6 7.6 Z " +
            "M9.2 3.4 C10.4 3.4 11.4 4.4 11.4 5.6 C11.4 6.8 10.4 7.8 9.2 7.8 C8 7.8 7 6.8 7 5.6 C7 4.4 8 3.4 9.2 3.4 Z " +
            "M14.8 3.4 C16 3.4 17 4.4 17 5.6 C17 6.8 16 7.8 14.8 7.8 C13.6 7.8 12.6 6.8 12.6 5.6 C12.6 4.4 13.6 3.4 14.8 3.4 Z " +
            "M19.4 7.6 C20.6 7.6 21.6 8.6 21.6 9.8 C21.6 11 20.6 12 19.4 12 C18.2 12 17.2 11 17.2 9.8 C17.2 8.6 18.2 7.6 19.4 7.6 Z",

        // Crossed axes, blades sweeping up and outward.
        ["Berserker"] =
            "M4.2 21.6 L15.2 6.4 L16.8 7.6 L5.8 22.8 Z " +
            "M13.6 2 C17 1.2 20.4 2.4 22.4 5.2 C20.4 8 17.2 9.4 13.8 8.9 C13.1 6.6 13 4.3 13.6 2 Z " +
            "M19.8 21.6 L8.8 6.4 L7.2 7.6 L18.2 22.8 Z " +
            "M10.4 2 C7 1.2 3.6 2.4 1.6 5.2 C3.6 8 6.8 9.4 10.2 8.9 C10.9 6.6 11 4.3 10.4 2 Z",
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The class's silhouette, or null for a name we don't draw.</summary>
    public static Geometry? For(string className)
    {
        if (!Paths.TryGetValue(className.Trim(), out string? data)) return null;
        if (!Cache.TryGetValue(className, out var g))
        {
            g = Geometry.Parse(data);
            g.Freeze();
            Cache[className] = g;
        }
        return g;
    }

    public static IEnumerable<string> ClassNames => Paths.Keys;
}
