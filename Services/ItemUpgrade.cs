namespace EQLOverlay.Services;

/// <summary>
/// What an item's stat block reads at a ` +N` upgrade tier. Ported verbatim
/// from Companion's shared/itemUpgrade.ts (MIT, © Josh Moyers), which is
/// itself the exact extraction of eqlwiki.com's own ItemLevelSlider module —
/// the script driving the item-level slider on every item page. Nothing here
/// is invented arithmetic; where the reference disagrees with clean decimal
/// math (the weight curve's IEEE754 artifact) the reference wins, because the
/// number a player reads on the wiki page is the number this has to show.
///
/// The state the reference models is (full tier, banked merge-exp fraction).
/// An inventory dump only states the full tier (the name's +N), so callers
/// here pass fraction 0 — a mid-tier item can read a touch higher in game.
///
/// Rules ("effective" = full + fraction/2^full; the +N% headline is ×10):
///  - PRIMARY (AC, STR/STA/AGI/DEX/WIS/INT/CHA, HP/Mana/End, every SV *):
///      base ≤ 10 → base + full (fraction ignored);
///      base > 10 → floor(base + round(base × effective / 10)) with the
///      increment rounded half-away-from-zero BEFORE the add;
///      negatives shrink toward zero (base + full, capped at 0).
///  - WEAPON DMG: base + floor(base × effective / 10). Delay NEVER scales —
///    that is why the ratio improves.
///  - FLAT (Haste, HP/Mana/End Regen): base + full.
///  - WEIGHT: base × (1 − 0.09 × log2(2^full + fraction)), ceiled away from
///    zero at one decimal, clamped at 0; feather-light bases (≤ 0.1) untouched.
///  - SV VOID: an upgraded item carrying ≥ 2 distinct attribute/save fields
///    gains a synthetic "SV VOID: +full" line (AC/HP/Mana never trigger it).
/// </summary>
public static class ItemUpgrade
{
    public const int MaxTier = 10;

    /// <summary>full + fraction / 2^full — the multiplier every stat reads.</summary>
    public static double EffectiveLevel(int full, int fraction = 0)
    {
        full = Math.Clamp(full, 0, MaxTier);
        if (full == 0 || full == MaxTier) fraction = 0;
        fraction = Math.Clamp(fraction, 0, (1 << full) - 1);
        return full + fraction / (double)(1 << full);
    }

    /// <summary>Round half AWAY FROM ZERO (Excel ROUND, not banker's).</summary>
    public static double ExcelRound(double value, int digits = 0)
    {
        double f = Math.Pow(10, digits);
        double x = value * f;
        return (x < 0 ? -Math.Round(-x, MidpointRounding.AwayFromZero)
                      : Math.Round(x, MidpointRounding.AwayFromZero)) / f;
    }

    /// <summary>Ceil AWAY FROM ZERO at `digits` decimals (Excel ROUNDUP).</summary>
    public static double ExcelRoundUp(double value, int digits = 0)
    {
        double f = Math.Pow(10, digits);
        return (value < 0 ? -Math.Ceiling(-value * f) : Math.Ceiling(value * f)) / f;
    }

    // Key aliases, longest-first so a compound key can never be swallowed by
    // one of its own words (MANA REGEN before MANA). Whole-key matches only.
    private static readonly (string From, string To)[] KeyAliases =
        new (string, string)[]
        {
            ("MANA_REGEN", "MANA_REGEN"),
            ("ENDURANCE", "END"),
            ("HP_REGEN", "HP_REGEN"),
            ("END_REGEN", "END_REGEN"),
            ("DISEASE", "SV_DISEASE"),
            ("POISON", "SV_POISON"),
            ("DAMAGE", "DMG"),
            ("ATK_DELAY", "DELAY"),
            ("REGEN", "HP_REGEN"),
            ("ENDUR", "END"),
            ("MAGIC", "SV_MAGIC"),
            ("MANA", "MP"),
            ("FIRE", "SV_FIRE"),
            ("COLD", "SV_COLD"),
            ("WT", "WEIGHT"),
        }.OrderByDescending(a => a.Item1.Length).ToArray();

    /// <summary>"sv magic" / "Mana Regen" → "SV_MAGIC" / "MANA_REGEN".</summary>
    public static string NormalizeKey(string key)
    {
        string k = System.Text.RegularExpressions.Regex
            .Replace(key.Trim().ToUpperInvariant(), @"[\s-]+", "_").TrimEnd(':');
        foreach (var (from, to) in KeyAliases)
            if (from == k) return to;
        return k;
    }

    private static readonly HashSet<string> PrimaryKeys = new(StringComparer.Ordinal)
        { "AC", "STR", "STA", "AGI", "DEX", "WIS", "INT", "CHA", "HP", "MP", "END" };
    private static readonly HashSet<string> FlatKeys = new(StringComparer.Ordinal)
        { "HP_REGEN", "MANA_REGEN", "END_REGEN", "HASTE" };

    public enum StatClass { Primary, Flat, Damage, Delay, Weight, Unchanged }

    /// <summary>Which scaling rule a raw stat key follows.</summary>
    public static StatClass ClassOf(string key)
    {
        string k = NormalizeKey(key);
        if (k == "DMG") return StatClass.Damage;
        if (k == "DELAY") return StatClass.Delay;
        if (k == "WEIGHT") return StatClass.Weight;
        if (FlatKeys.Contains(k)) return StatClass.Flat;
        if (PrimaryKeys.Contains(k) || k.StartsWith("SV_", StringComparison.Ordinal))
            return StatClass.Primary;
        return StatClass.Unchanged;
    }

    public static int ScalePrimary(int baseVal, int full, int fraction = 0)
    {
        full = Math.Clamp(full, 0, MaxTier);
        if (baseVal == 0) return 0;
        if (baseVal < 0) return Math.Min(0, baseVal + full);
        if (baseVal <= 10) return baseVal + full;
        return (int)Math.Floor(baseVal + ExcelRound(baseVal * EffectiveLevel(full, fraction) / 10.0));
    }

    public static int ScaleDamage(int baseVal, int full, int fraction = 0)
    {
        if (baseVal <= 0) return baseVal;
        return baseVal + (int)Math.Floor(baseVal * EffectiveLevel(full, fraction) / 10.0);
    }

    public static int ScaleFlat(int baseVal, int full)
    {
        if (baseVal <= 0) return baseVal;
        return baseVal + Math.Clamp(full, 0, MaxTier);
    }

    public static double ScaleWeight(double baseVal, int full, int fraction = 0)
    {
        full = Math.Clamp(full, 0, MaxTier);
        if (full == 0 || baseVal <= 0.1) return baseVal;
        if (full == MaxTier) fraction = 0;
        fraction = Math.Clamp(fraction, 0, (1 << full) - 1);
        double totalProgression = (1 << full) + fraction;
        return Math.Max(0, ExcelRoundUp(baseVal * (1 - 0.09 * Math.Log2(totalProgression)), 1));
    }

    // The SV VOID grant triggers on ≥ 2 distinct fields from this set —
    // AC, HP and Mana are deliberately NOT in it (the reference excludes them).
    private static readonly HashSet<string> VoidTriggerKeys = new(StringComparer.Ordinal)
    {
        "STR", "STA", "INT", "AGI", "DEX", "CHA", "WIS",
        "SV_FIRE", "SV_COLD", "SV_POISON", "SV_MAGIC", "SV_DISEASE",
    };

    /// <summary>Whether an upgraded item with these stat/save keys gains the
    /// synthetic "SV VOID: +full" line (never when it already states SV VOID).</summary>
    public static bool SynthesizesVoid(IEnumerable<string> statAndSaveKeys, int full)
    {
        if (full <= 0) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in statAndSaveKeys)
        {
            string k = NormalizeKey(key);
            if (k == "SV_VOID") return false;
            if (VoidTriggerKeys.Contains(k)) seen.Add(k);
        }
        return seen.Count >= 2;
    }

    /// <summary>"+15" → 15, "-5" → -5; null for anything non-integer — a "36%"
    /// must never fall into integer scaling or an integer sum.</summary>
    public static int? StatInteger(string value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^([+-]?\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>A percent value's number ("36%" → 36), or null.</summary>
    public static int? PercentInteger(string value)
    {
        var m = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^([+-]?\d+)\s*%$");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Scale one "KEY: value" stat pair's value text to a tier,
    /// keeping the source's own spelling (leading +, trailing %). Values that
    /// state no scalable number pass through verbatim.</summary>
    public static string ScaleValueText(string key, string value, int full)
    {
        var cls = ClassOf(key);
        if (cls != StatClass.Primary && cls != StatClass.Flat) return value;
        int? n = StatInteger(value) ?? PercentInteger(value);
        if (n is not { } baseVal) return value;
        int scaled = cls == StatClass.Primary
            ? ScalePrimary(baseVal, full)
            : ScaleFlat(baseVal, full);
        if (scaled == baseVal) return value;
        bool hadPlus = value.TrimStart().StartsWith('+');
        string suffix = value.TrimEnd().EndsWith('%') ? "%" : "";
        return (scaled > 0 && hadPlus ? "+" + scaled : scaled.ToString()) + suffix;
    }
}
