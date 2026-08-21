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

    // ---- alerts (the trigger two-notice model, respawn-flavored): notify
    // BEFORE it spawns and/or WHEN it spawns, each a toggle + Phrase OR
    // Sound (one channel, never both). Empty phrase = the default below,
    // which follows renames for free.

    public bool WarnEnabled { get; set; }
    /// <summary>Lead time for the before-notice.</summary>
    public double WarnSeconds { get; set; } = 15;
    /// <summary>"speak" | "sound".</summary>
    public string WarnMode { get; set; } = "speak";
    public string WarnSpeak { get; set; } = "";
    public string WarnSound { get; set; } = "";

    /// <summary>On by default — the pre-2.20 behavior (spoken "&lt;Name&gt; respawn").</summary>
    public bool SpawnEnabled { get; set; } = true;
    public string SpawnMode { get; set; } = "speak";
    public string SpawnSpeak { get; set; } = "";
    public string SpawnSound { get; set; } = "";

    public static string DefaultWarnPhrase(string name) => $"{name} spawning soon";
    public static string DefaultSpawnPhrase(string name) => $"{name} respawn";

    /// <summary>What the before-notice fires: (speak, sound) with exactly one
    /// set, or null when the notice is off (or a sound notice names no file).</summary>
    public (string? Speak, string? Sound)? WarnPayload()
    {
        if (!WarnEnabled) return null;
        if (WarnMode == "sound")
            return string.IsNullOrWhiteSpace(WarnSound) ? null : (null, WarnSound);
        return (string.IsNullOrWhiteSpace(WarnSpeak) ? DefaultWarnPhrase(Name) : WarnSpeak, null);
    }

    /// <summary>Same for the spawn notice.</summary>
    public (string? Speak, string? Sound)? SpawnPayload()
    {
        if (!SpawnEnabled) return null;
        if (SpawnMode == "sound")
            return string.IsNullOrWhiteSpace(SpawnSound) ? null : (null, SpawnSound);
        return (string.IsNullOrWhiteSpace(SpawnSpeak) ? DefaultSpawnPhrase(Name) : SpawnSpeak, null);
    }
}
