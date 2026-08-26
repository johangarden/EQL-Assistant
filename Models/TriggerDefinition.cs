using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQLOverlay.Models;

/// <summary>Where a trigger is displayed.</summary>
public static class Panels
{
    public const string Bars = "bars";                   // Area 1: countdown bars
    public const string SelfBuffs = "selfBuffs";         // Area 3: self-buff present/missing matrix
    public const string TargetDebuffs = "targetDebuffs"; // Area 2: target-debuff matrix
    public const string TimerAuto = "timerAuto";         // start the repop timer when the pattern matches
    public const string Flash = "flash";                 // screen-center flash text on match (no bar)
}

/// <summary>
/// One user-defined rule: when a log line matches <see cref="StartPattern"/>,
/// start (or refresh) a countdown bar of <see cref="DurationSeconds"/>. An
/// optional <see cref="EndPattern"/> clears the bar early (e.g. "worn off").
/// </summary>
public sealed class TriggerDefinition
{
    /// <summary>Stable id. Used as the timer key so re-casts refresh the same bar.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name shown on the bar.</summary>
    public string Name { get; set; } = "";

    /// <summary>Free-form grouping label, e.g. "Buffs", "HoTs", "DoTs", "Cooldowns".</summary>
    public string Category { get; set; } = "Buffs";

    /// <summary>Which panel renders this: "bars" (default), "selfBuffs", "targetDebuffs".</summary>
    public string Panel { get; set; } = Panels.Bars;

    /// <summary>Master on/off switch for this trigger.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Regex matched against each log line to START the timer.</summary>
    public string StartPattern { get; set; } = "";

    /// <summary>Optional regex to END the timer early. Empty = only expires by time.</summary>
    public string? EndPattern { get; set; }

    /// <summary>How long the bar counts down, in seconds. With
    /// <see cref="DurationAuto"/> this is the STARTING value (library/base);
    /// observed fades refine it. Without, it is enforced exactly.</summary>
    public double DurationSeconds { get; set; } = 60;

    /// <summary>Auto-learn the duration from observed fades in the log
    /// (default). Off = enforce DurationSeconds exactly; learning still
    /// happens in the background either way.</summary>
    public bool DurationAuto { get; set; } = true;

    /// <summary>Cast-anchor: the start pattern only counts when it follows YOUR
    /// own "You begin casting &lt;Name&gt;." within a few seconds — the fix for
    /// landing sentences shared by several spells (Quickness, Alacrity, Celerity
    /// and Swift Like The Wind all print "You feel much faster."). null = auto:
    /// library triggers whose landing text is shared anchor themselves; every
    /// other trigger fires on any match.</summary>
    public bool? CastAnchored { get; set; }

    /// <summary>LEGACY (pre-2.9): colors are derived from the trigger's type
    /// now (see TriggerColors) — kept only so old config files round-trip.</summary>
    public string Color { get; set; } = "#3FA9F5";

    /// <summary>If true, a fresh match resets a running bar to full duration.</summary>
    public bool RefreshOnRetrigger { get; set; } = true;

    /// <summary>Optional audible/spoken alert configuration.</summary>
    public AlertConfig? Alert { get; set; }

    /// <summary>
    /// If true, once this buff has been active at least once this session, a
    /// "REBUFF" indicator + spoken nudge appears whenever it isn't active.
    /// </summary>
    public bool RemindWhenMissing { get; set; }

    /// <summary>A PERMANENT buff (Vampiric Embrace): once up it never expires
    /// — the bar shows ∞ until death, a wear-off line, or a loadout switch.
    /// Duration and auto-learn don't apply. Pairs well with RemindWhenMissing.</summary>
    public bool Permanent { get; set; }

    /// <summary>
    /// Optional cooldown-reducer regex: while this bar is running, every line
    /// that matches cuts <see cref="ReduceSeconds"/> off the remaining time
    /// (e.g. SK Reave landing shaves 60s off the Harm Touch cooldown).
    /// </summary>
    public string? ReducePattern { get; set; }

    /// <summary>Seconds cut per reducer match (0 = feature off).</summary>
    public double ReduceSeconds { get; set; }

    /// <summary>Compiled once at load. Not serialized.</summary>
    [JsonIgnore] public Regex? StartRegex { get; set; }
    [JsonIgnore] public Regex? EndRegex { get; set; }
    [JsonIgnore] public Regex? ReduceRegex { get; set; }
}

/// <summary>When/how to alert for a trigger — two independent notices ("before
/// it fades" and "when it faded"), each playing EITHER a spoken phrase OR a
/// notification sound, never both.</summary>
public sealed class AlertConfig
{
    public const string ModeSpeak = "speak";
    public const string ModeSound = "sound";

    // ---- notice 1: "Notify before it fades" ---------------------------------

    /// <summary>Master toggle for the pre-fade notice. null = pre-2.11 config;
    /// ConfigService.NormalizeAlert derives it from the legacy fields below.</summary>
    public bool? WarnEnabled { get; set; }

    /// <summary>Seconds left when the pre-fade notice fires (default 15).</summary>
    public double AtSeconds { get; set; }

    /// <summary>"speak" or "sound" — which channel the pre-fade notice uses.</summary>
    public string WarnMode { get; set; } = "";

    /// <summary>Pre-fade phrase (Windows TTS), default "&lt;Name&gt; is about to end".</summary>
    public string? Speak { get; set; }

    /// <summary>Pre-fade .wav (a Windows Media preset).</summary>
    public string? Sound { get; set; }

    // ---- notice 2: "Notify when it faded" -----------------------------------

    /// <summary>Master toggle for the faded notice (doubles as "cooldown ready").</summary>
    public bool? FadedEnabled { get; set; }

    /// <summary>"speak" or "sound" — which channel the faded notice uses.</summary>
    public string FadedMode { get; set; } = "";

    /// <summary>Faded phrase, default "&lt;Name&gt; faded" ("… is ready" for Cooldowns).</summary>
    public string? FadedSpeak { get; set; }

    /// <summary>Faded .wav (a Windows Media preset).</summary>
    public string? FadedSound { get; set; }

    // ---- shared -------------------------------------------------------------

    /// <summary>Text to flash big in the screen centre when the trigger matches (optional).</summary>
    public string? FlashText { get; set; }

    // ---- legacy (pre-2.11) — read for migration, kept coherent on save ------

    /// <summary>LEGACY: voice on/off for the single old speak phrase.</summary>
    public bool SpeakEnabled { get; set; } = true;

    /// <summary>LEGACY: fire when the bar reaches 0 (the old model shared one
    /// speak/sound payload between the timed warning and the expiry alert).</summary>
    public bool OnExpire { get; set; }

    /// <summary>Default pre-fade phrase for a trigger name.</summary>
    public static string DefaultWarnPhrase(string name) => name + " is about to end";

    /// <summary>Default faded phrase — cooldown-flavoured triggers announce "ready".</summary>
    public static string DefaultFadedPhrase(string name, string category) =>
        (category ?? "").Contains("cool", StringComparison.OrdinalIgnoreCase)
            ? name + " is ready"
            : name + " faded";
}
