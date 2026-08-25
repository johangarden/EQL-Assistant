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

    /// <summary>LEGACY: the pre-learning typed time. Owner ruling (22 Aug
    /// 2026): typed times are gone — every mob learns. This field exists only
    /// so old files migrate (see <see cref="MigrateTypedTime"/>).</summary>
    public double Seconds { get; set; }

    /// <summary>Observed gaps, newest first, capped at <see cref="MaxGaps"/>.</summary>
    public List<RespawnGap> Gaps { get; set; } = new();

    public const int MaxGaps = 8;

    [System.Text.Json.Serialization.JsonIgnore]
    public double? LearnedSeconds => Gaps.Count > 0 ? Gaps.Min(g => g.Seconds) : null;

    /// <summary>The estimate: the learned minimum, or nothing (null = no
    /// evidence yet — the timer counts UP, learning).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? EffectiveSeconds => LearnedSeconds;

    public void AddGap(double seconds, DateTime when)
    {
        Gaps.Insert(0, new RespawnGap { Seconds = seconds, When = when });
        if (Gaps.Count > MaxGaps) Gaps.RemoveRange(MaxGaps, Gaps.Count - MaxGaps);
    }

    /// <summary>A legacy typed time becomes the FIRST gap sample — an upper
    /// bound the learner can tighten — and clears. Idempotent; entries that
    /// already learned keep their evidence and just drop the typed number.</summary>
    public void MigrateTypedTime(DateTime now)
    {
        if (Seconds <= 0) return;
        if (Gaps.Count == 0) AddGap(Seconds, now);
        Seconds = 0;
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
