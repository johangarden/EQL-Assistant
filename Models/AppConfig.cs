using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EQLOverlay.Models;

/// <summary>Global settings, stored in config.json.</summary>
public sealed class AppConfig
{
    public LogConfig Log { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();

    /// <summary>Optional; lets patterns use {char} if you templatize them later.</summary>
    public string CharacterName { get; set; } = "";

    /// <summary>Name of the loadout to load on startup.</summary>
    public string ActiveLoadout { get; set; } = "Default";

    /// <summary>
    /// The active loadout's triggers, loaded at runtime from a loadout file.
    /// Not stored in config.json (triggers live in loadouts/*.json).
    /// </summary>
    [JsonIgnore] public List<TriggerDefinition> Triggers { get; set; } = new();
}

public sealed class LogConfig
{
    /// <summary>Folder that holds eqlog_*.txt files. Leave empty to use ExplicitFile.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Glob-ish pattern; the newest matching file is followed automatically.</summary>
    public string FilePattern { get; set; } = "eqlog_*.txt";

    /// <summary>If set, this exact file is followed and Directory/FilePattern are ignored.</summary>
    public string ExplicitFile { get; set; } = "";

    /// <summary>Start at the end of the file (ignore history) on launch.</summary>
    public bool StartAtEndOfFile { get; set; } = true;

    /// <summary>How often to poll the log for new lines.</summary>
    public int PollIntervalMs { get; set; } = 200;
}

/// <summary>App-managed window state, persisted separately in window-state.json.</summary>
public sealed class OverlayConfig
{
    public double Left { get; set; } = 60;
    public double Top { get; set; } = 140;
    public double Width { get; set; } = 320;

    public double BarHeight { get; set; } = 24;
    public double Spacing { get; set; } = 4;
    public double FontSize { get; set; } = 13;

    /// <summary>Locked = click-through, no chrome. Unlocked = movable, shows toolbar.</summary>
    public bool Locked { get; set; } = false;

    public bool ShowCategoryHeaders { get; set; } = true;

    /// <summary>Bars pulse/turn red when remaining time drops below this.</summary>
    public double WarnSeconds { get; set; } = 6;

    /// <summary>Seconds between spoken "missing buff" reminders while a buff is absent.</summary>
    public double RemindIntervalSeconds { get; set; } = 20;

    /// <summary>Global mute for all sound/TTS alerts.</summary>
    public bool Muted { get; set; } = false;

    /// <summary>Start locked (click-through, no toolbar) so it's game-ready immediately.</summary>
    public bool StartLocked { get; set; } = false;

    /// <summary>Whole-overlay opacity, 0.1–1.0 (lower = more see-through).</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Number of columns in the present/missing matrix panels.</summary>
    public int MatrixColumns { get; set; } = 4;

    /// <summary>Last-used repop/respawn timer duration, in seconds (default 6:40).</summary>
    public double TimerSeconds { get; set; } = 400;
}
