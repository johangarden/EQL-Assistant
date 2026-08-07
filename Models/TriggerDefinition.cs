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

    /// <summary>How long the bar counts down, in seconds.</summary>
    public double DurationSeconds { get; set; } = 60;

    /// <summary>Bar fill color, any WPF color string ("#3FA9F5", "DodgerBlue").</summary>
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

/// <summary>When/how to alert for a trigger.</summary>
public sealed class AlertConfig
{
    /// <summary>Text spoken via Windows TTS when the alert fires. Optional.</summary>
    public string? Speak { get; set; }

    /// <summary>Path to a .wav file played when the alert fires. Optional.</summary>
    public string? Sound { get; set; }

    /// <summary>Fire this many seconds before the bar expires (0 = don't).</summary>
    public double AtSeconds { get; set; }

    /// <summary>Fire when the bar reaches 0 (use for "cooldown ready").</summary>
    public bool OnExpire { get; set; }

    /// <summary>Text to flash big in the screen centre when the trigger matches (optional).</summary>
    public string? FlashText { get; set; }
}
