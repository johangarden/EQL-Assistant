using EQLOverlay.Models;

namespace EQLOverlay.Services;

/// <summary>
/// Fixed per-type trigger colors (2.9 UX rework): the user picks a TYPE, the
/// type owns the color — buffs blue, heals green, debuffs yellow, DoTs red,
/// cooldowns purple. Applies everywhere a trigger shows color (bars, flash
/// text, the manager list swatch). TriggerDefinition.Color still round-trips
/// through JSON for old configs but nothing reads it anymore.
/// </summary>
public static class TriggerColors
{
    public const string Buff = "#4FC3F7";      // blue
    public const string Heal = "#81C784";      // green
    public const string Debuff = "#FFD54F";    // yellow
    public const string Dot = "#E57373";       // red
    public const string Cooldown = "#BA68C8";  // purple
    public const string Flash = "#FFCC33";     // amber
    public const string Repop = "#4DB6AC";     // teal
    public const string Other = "#90A4AE";     // slate

    public static string For(TriggerDefinition t) => For(t.Panel, t.Category);

    public static string For(string panel, string category) => panel switch
    {
        Panels.Flash => Flash,
        Panels.TimerAuto => Repop,
        Panels.SelfBuffs => Buff,
        Panels.TargetDebuffs => Debuff,
        _ => ForCategory(category),
    };

    /// <summary>Keyword match on the (free-form) category. Order matters:
    /// "Debuffs" contains "buff", so debuff must win first.</summary>
    public static string ForCategory(string? category)
    {
        string c = (category ?? "").Trim();
        if (c.Contains("dot", StringComparison.OrdinalIgnoreCase)) return Dot;
        if (c.Contains("debuff", StringComparison.OrdinalIgnoreCase)) return Debuff;
        if (c.Contains("hot", StringComparison.OrdinalIgnoreCase)
            || c.Contains("heal", StringComparison.OrdinalIgnoreCase)) return Heal;
        if (c.Contains("cool", StringComparison.OrdinalIgnoreCase)
            || c.Equals("cd", StringComparison.OrdinalIgnoreCase)) return Cooldown;
        if (c.Contains("buff", StringComparison.OrdinalIgnoreCase)) return Buff;
        return Other;
    }
}
