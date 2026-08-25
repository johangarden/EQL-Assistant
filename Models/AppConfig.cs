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

    // (Catch-up modes removed in 2.7: today's log is ALWAYS caught up on start —
    // log data is the app's foundation and every consumer dedupes. Old configs'
    // catchUpMode/catchUpOnStart JSON properties are simply ignored on load.)

    /// <summary>
    /// The active loadout's triggers, loaded at runtime from a loadout file.
    /// Not stored in config.json (triggers live in loadouts/*.json).
    /// </summary>
    [JsonIgnore] public List<TriggerDefinition> Triggers { get; set; } = new();
}

public sealed class LogConfig
{
    /// <summary>Folder that holds eqlog_*.txt files.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Glob-ish pattern; the newest matching file is followed automatically.</summary>
    public string FilePattern { get; set; } = "eqlog_*.txt";

    /// <summary>LEGACY (pre-2.7): an exact file to follow. On load its folder is
    /// migrated into <see cref="Directory"/> — newest-file following covers it,
    /// and auto-catch-up made "start at end of file" moot too.</summary>
    public string ExplicitFile { get; set; } = "";

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

    /// <summary>Whether the repop timer watch is shown (toggled from ⏱ / tray / Ctrl+Alt+R).</summary>
    public bool TimerVisible { get; set; } = true;

    /// <summary>Whether the DPS meter panel is shown (toggled from toolbar / tray / Ctrl+Alt+D).</summary>
    public bool MeterVisible { get; set; } = true;

    /// <summary>Meter scope: solo (your abilities ranked, pet collapsible) vs
    /// group (everyone the log shows, ranked). EQL is solo-first.</summary>
    public bool MeterSoloMode { get; set; } = true;

    /// <summary>Your pet's name — enables the pet line in the DPS meter's incoming footer.</summary>
    public string PetName { get; set; } = "";

    /// <summary>Whether the skill tracker panel is shown.</summary>
    public bool SkillTrackerVisible { get; set; } = false;

    /// <summary>Abilities the skill tracker watches (backstab, reave, Smite, …).</summary>
    public List<string> SkillTrackerSkills { get; set; } = new();

    /// <summary>Whether the meter shows the proc watcher section (procs + PPM).</summary>
    public bool ProcWatcherVisible { get; set; } = false;

    /// <summary>Whether the automatic Enemy DoTs panel is shown (it only
    /// materializes on screen while it has rows, or while unlocked).</summary>
    public bool EnemyDotsVisible { get; set; } = true;

    /// <summary>Enemy DoTs grouping: by MOB name (default — the tab-target
    /// view: one header per mob, its dots under it) or by SPELL.</summary>
    public bool EnemyDotsGroupByMob { get; set; } = true;

    /// <summary>Whether the big stun/fear/charm/mez condition badges are shown
    /// (materializes only while a condition is active, or unlocked).</summary>
    public bool ConditionsVisible { get; set; } = true;

    /// <summary>Whether the Sky quest helper panel is enabled (materializes
    /// only when a dropper is sighted or quests are tracked).</summary>
    public bool SkyHelperVisible { get; set; } = true;

    /// <summary>Helper panel: admit items whose quest is already completed.</summary>
    public bool SkyHelperShowCompleted { get; set; }

    // ---- interrupt / resist notices: the badge flashes regardless; these add
    // sound. Default = a short SOUND, not speech — too many spoken phrases
    // clutter (owner ruling); Phrase mode speaks "Interrupted!" / "Resisted!".

    public bool InterruptNoticeEnabled { get; set; } = true;
    /// <summary>"sound" | "speak".</summary>
    public string InterruptNoticeMode { get; set; } = "sound";
    public string InterruptNoticeSpeak { get; set; } = "";
    public string InterruptNoticeSound { get; set; } = @"C:\Windows\Media\Windows Exclamation.wav";

    public bool ResistNoticeEnabled { get; set; } = true;
    public string ResistNoticeMode { get; set; } = "sound";
    public string ResistNoticeSpeak { get; set; } = "";
    public string ResistNoticeSound { get; set; } = @"C:\Windows\Media\Windows Ding.wav";

    /// <summary>Whether the Session stats panel (XP/AA/motes per hour) is shown.</summary>
    public bool SessionStatsVisible { get; set; } = false;

    /// <summary>Session stats slice: "zoneSession" (default), "session", "zone", "all".</summary>
    public string SessionStatsSlice { get; set; } = "zoneSession";

    /// <summary>Session stats tier scoping: count only the exact zone spelling
    /// ("Befallen 3 (Fused)") vs every tier of the camp.</summary>
    public bool SessionStatsExactTier { get; set; } = true;

    /// <summary>Session stats rate denominator: elapsed (default — your evening)
    /// vs active (minus 5+ minute silences — the camp's pace).</summary>
    public bool SessionStatsActiveBasis { get; set; } = false;

    /// <summary>Panels-menu master toggles: every panel that can appear gets
    /// one. Matrices still need cells, reminders still need a missing buff.</summary>
    public bool SelfMatrixVisible { get; set; } = true;
    public bool TargetMatrixVisible { get; set; } = true;
    public bool RemindersVisible { get; set; } = true;

    /// <summary>Whether the flash-alert area is shown (flash triggers stay quiet when off).</summary>
    public bool FlashVisible { get; set; } = true;

    /// <summary>Pop the death recap window automatically when you die.</summary>
    public bool DeathRecapAuto { get; set; } = true;

    /// <summary>Whether the detached toolbar (command strip) is shown.</summary>
    public bool ToolbarVisible { get; set; } = true;

    /// <summary>Whether the buff-bars panel is shown.</summary>
    public bool BarsVisible { get; set; } = true;

    /// <summary>Font size of screen flash alerts.</summary>
    public double FlashFontSize { get; set; } = 54;

    /// <summary>Width of the flash-alert area (text wraps inside it).</summary>
    public double FlashWidth { get; set; } = 900;

    // ---- scrolling combat text ------------------------------------------------

    /// <summary>Master switch for scrolling combat text (toolbar ⚡ / tray / Ctrl+Alt+C).</summary>
    public bool SctVisible { get; set; } = true;

    /// <summary>Which SCT lanes exist (each is its own movable panel).</summary>
    public bool SctIncoming { get; set; } = true;
    public bool SctOutgoing { get; set; } = true;
    public bool SctHeals { get; set; } = true;
    public bool SctHealsIn { get; set; } = true;
    public bool SctPetIncoming { get; set; } = false;
    public bool SctPetOutgoing { get; set; } = false;

    /// <summary>XP gains, faction adjustments and AA points as floating text.</summary>
    public bool SctProgress { get; set; } = true;

    /// <summary>SCT number size; hits at/above the big-hit threshold render 40% larger.</summary>
    public double SctFontSize { get; set; } = 18;
    public double SctBigHit { get; set; } = 200;

    /// <summary>Size of each SCT lane.</summary>
    public double SctLaneWidth { get; set; } = 170;
    public double SctLaneHeight { get; set; } = 300;

    /// <summary>How long an XP/faction float takes to rise and fade (seconds).
    /// These are rare and worth reading, so they run far slower than the
    /// 2.6s combat lanes.</summary>
    public double SctXpLifetime { get; set; } = 10;
}
