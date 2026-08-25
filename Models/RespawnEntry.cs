namespace EQLOverlay.Models;

/// <summary>One observed death→next-appearance gap. Every gap is an UPPER
/// BOUND on the respawn — you can't meet a mob before it spawns — so the
/// learner's estimate is the MINIMUM over these, never a mean.</summary>
public sealed class RespawnGap
{
    public double Seconds { get; set; }
    public DateTime When { get; set; }
}

/// <summary>
/// One named-mob respawn timer. Global (stored in respawns.json, not in a
/// loadout) — a mob's respawn time doesn't depend on your class combo.
/// </summary>
public sealed class RespawnEntry
{
    public string Name { get; set; } = "";

    /// <summary>Zone the mob lives in — used to group long respawn lists
    /// (Manager page + the watch's ☰ menu). Optional.</summary>
    public string Zone { get; set; } = "";

    /// <summary>Respawn time in seconds, typed by the user. 0 = none set:
    /// the learned estimate stands in (auto mode). A typed number outranks
    /// everything the learner measured — nothing outranks a user who camped
    /// the spot, and it's also the guard against duplicate-name mobs whose
    /// twins shrink the learned minimum below the real cycle.</summary>
    public double Seconds { get; set; } = 400;

    /// <summary>Collect death→next-appearance gaps for this mob (the
    /// RespawnLearner). Evidence accrues even while a typed number wins.</summary>
    public bool Learn { get; set; } = true;

    /// <summary>Observed gaps, newest first, capped at <see cref="MaxGaps"/>.</summary>
    public List<RespawnGap> Gaps { get; set; } = new();

    public const int MaxGaps = 8;

    [System.Text.Json.Serialization.JsonIgnore]
    public double? LearnedSeconds => Gaps.Count > 0 ? Gaps.Min(g => g.Seconds) : null;

    /// <summary>The estimate ladder: typed number > learned minimum > nothing
    /// (null = no estimate yet — the timer counts UP, learning).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? EffectiveSeconds => Seconds > 0 ? Seconds : LearnedSeconds;

    public void AddGap(double seconds, DateTime when)
    {
        Gaps.Insert(0, new RespawnGap { Seconds = seconds, When = when });
        if (Gaps.Count > MaxGaps) Gaps.RemoveRange(MaxGaps, Gaps.Count - MaxGaps);
    }

    /// <summary>
    /// Optional death-line regex. Empty = auto-match
    /// "&lt;Name&gt; has been slain by …" / "You have slain &lt;Name&gt;".
    /// </summary>
    public string Pattern { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The spawn-timer notices, GLOBAL since the Manager-page simplification
/// (owner sketch, 22 Aug 2026): one before-notice and one spawn-notice for
/// every watched mob — per-mob alert plumbing was manual clutter. A phrase
/// may carry "{mob}", replaced with the mob's name; empty falls back to the
/// defaults below. Config lives in OverlayConfig (Respawn* fields).
/// </summary>
public static class RespawnNotice
{
    public static string DefaultWarnPhrase(string name) => $"{name} spawning soon";
    public static string DefaultSpawnPhrase(string name) => $"{name} respawn";

    /// <summary>What a notice fires: (speak, sound) with exactly one set, or
    /// null when off (or a sound notice names no file).</summary>
    public static (string? Speak, string? Sound)? Payload(bool enabled, string mode,
        string phrase, string sound, string mob, Func<string, string> defaultPhrase)
    {
        if (!enabled) return null;
        if (mode == "sound")
            return string.IsNullOrWhiteSpace(sound) ? null : (null, sound);
        string text = string.IsNullOrWhiteSpace(phrase)
            ? defaultPhrase(mob)
            : phrase.Replace("{mob}", mob, StringComparison.OrdinalIgnoreCase);
        return (text, null);
    }
}
