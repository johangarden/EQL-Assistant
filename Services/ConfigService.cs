using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQLOverlay.Models;

namespace EQLOverlay.Services;

/// <summary>
/// Loads/saves global settings (config.json) and per-loadout trigger files
/// (loadouts/*.json). Window position/lock live in window-state.json so
/// autosaving them never disturbs the other files.
/// </summary>
public sealed class ConfigService
{
    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string WindowStatePath { get; }
    public string LoadoutsDirectory { get; }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ConfigService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string newDir = Path.Combine(appData, "EQL_Assistant");
        string oldDir = Path.Combine(appData, "EQLOverlay");

        // One-time migration from the app's old name: move the whole folder
        // (config, loadouts, panel positions, kept fights) to EQL_Assistant.
        if (!Directory.Exists(newDir) && Directory.Exists(oldDir))
        {
            try { Directory.Move(oldDir, newDir); }
            catch { newDir = oldDir; /* couldn't move — keep using the old folder */ }
        }

        ConfigDirectory = newDir;
        Directory.CreateDirectory(ConfigDirectory);
        LoadoutsDirectory = Path.Combine(ConfigDirectory, "loadouts");
        Directory.CreateDirectory(LoadoutsDirectory);
        ConfigPath = Path.Combine(ConfigDirectory, "config.json");
        WindowStatePath = Path.Combine(ConfigDirectory, "window-state.json");
    }

    // ---- last-seen marker (last-seen.json) ----------------------------------
    // How far into which log the app has parsed — lets the startup catch-up
    // prompt show what's actually missing (and stay quiet after a quick restart).

    public sealed record LastSeen(string File, DateTime Time);

    private string LastSeenPath => Path.Combine(ConfigDirectory, "last-seen.json");

    public LastSeen? LoadLastSeen()
    {
        try
        {
            if (!File.Exists(LastSeenPath)) return null;
            return JsonSerializer.Deserialize<LastSeen>(File.ReadAllText(LastSeenPath), ReadOptions);
        }
        catch { return null; }
    }

    public void SaveLastSeen(string logFileName, DateTime time)
    {
        try
        {
            File.WriteAllText(LastSeenPath,
                JsonSerializer.Serialize(new LastSeen(logFileName, time), WriteOptions));
        }
        catch { /* best-effort */ }
    }

    // ---- settings (config.json) --------------------------------------------

    public AppConfig LoadSettings()
    {
        if (!File.Exists(ConfigPath))
            File.WriteAllText(ConfigPath, DefaultConfigJson);

        AppConfig config;
        try
        {
            string json = File.ReadAllText(ConfigPath);
            config = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions) ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"config.json couldn't be parsed:\n{ex.Message}\n\nFile: {ConfigPath}");
        }

        // Pre-2.7 migration: an explicit-file config becomes its folder, so
        // newest-file following takes over seamlessly.
        if (!string.IsNullOrWhiteSpace(config.Log.ExplicitFile))
        {
            if (string.IsNullOrWhiteSpace(config.Log.Directory))
                config.Log.Directory =
                    Path.GetDirectoryName(config.Log.ExplicitFile) ?? "";
            config.Log.ExplicitFile = "";
        }

        ApplyWindowState(config.Overlay);
        return config;
    }

    /// <summary>Write global settings. Triggers are [JsonIgnore] so they aren't included.</summary>
    public void SaveSettings(AppConfig config)
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, WriteOptions));
        Log.Info($"Settings saved -> {ConfigPath} (activeLoadout='{config.ActiveLoadout}')");
    }

    // ---- loadouts (loadouts/*.json) ----------------------------------------

    /// <summary>Ensure at least one loadout exists; migrate legacy config triggers if needed.</summary>
    public void EnsureDefaultLoadout()
    {
        if (Directory.EnumerateFiles(LoadoutsDirectory, "*.json").Any())
            return;

        var migrated = TryReadLegacyTriggers();
        Loadout def;
        if (migrated is { Count: > 0 })
        {
            def = new Loadout { Name = "Default", Triggers = migrated };
        }
        else
        {
            def = JsonSerializer.Deserialize<Loadout>(DefaultLoadoutJson, ReadOptions)
                  ?? new Loadout { Name = "Default" };
        }
        SaveLoadout(def);
    }

    /// <summary>All loadouts on disk, sorted by name. Triggers are NOT compiled here.</summary>
    public List<Loadout> ListLoadouts()
    {
        var list = new List<Loadout>();
        foreach (var path in Directory.EnumerateFiles(LoadoutsDirectory, "*.json"))
        {
            try
            {
                var lo = JsonSerializer.Deserialize<Loadout>(File.ReadAllText(path), ReadOptions);
                if (lo is null) continue;
                if (string.IsNullOrWhiteSpace(lo.Name))
                    lo.Name = Path.GetFileNameWithoutExtension(path);
                lo.FilePath = path;
                list.Add(lo);
            }
            catch { /* skip an unreadable/corrupt loadout file */ }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Load one loadout by name and compile its regexes (throws on a bad pattern).</summary>
    public Loadout? LoadLoadout(string name)
    {
        var lo = ListLoadouts().FirstOrDefault(l =>
            string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (lo is null) return null;
        CompileTriggers(lo.Triggers);
        return lo;
    }

    public void SaveLoadout(Loadout loadout)
    {
        string path = Path.Combine(LoadoutsDirectory, Slug(loadout.Name) + ".json");
        loadout.FilePath = path;
        File.WriteAllText(path, JsonSerializer.Serialize(loadout, WriteOptions));
        Log.Info($"Loadout saved -> {path} ({loadout.Triggers.Count} triggers)");
    }

    /// <summary>Delete any loadout file whose name isn't in <paramref name="keepNames"/>.</summary>
    public void SyncDeleteLoadouts(IEnumerable<string> keepNames)
    {
        var keep = new HashSet<string>(keepNames.Select(Slug), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(LoadoutsDirectory, "*.json"))
        {
            string slug = Path.GetFileNameWithoutExtension(path);
            if (!keep.Contains(slug))
            {
                try { File.Delete(path); } catch { /* ignore */ }
            }
        }
    }

    // ---- trigger compilation -----------------------------------------------

    public static void CompileTriggers(IEnumerable<TriggerDefinition> triggers)
    {
        var errors = new List<string>();
        foreach (var t in triggers)
        {
            try { CompileOne(t); }
            catch (ArgumentException ex) { errors.Add($"  • \"{t.Name}\" ({t.Id}): {ex.Message}"); }
        }
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "One or more triggers have an invalid regex pattern:\n\n" + string.Join("\n", errors));
    }

    /// <summary>Validate + (re)compile a trigger's regexes; throws on a bad pattern.</summary>
    public static void CompileOne(TriggerDefinition t)
    {
        t.StartRegex = string.IsNullOrWhiteSpace(t.StartPattern)
            ? null
            : new Regex(t.StartPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        t.EndRegex = string.IsNullOrWhiteSpace(t.EndPattern)
            ? null
            : new Regex(t.EndPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        t.ReduceRegex = string.IsNullOrWhiteSpace(t.ReducePattern)
            ? null
            : new Regex(t.ReducePattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

        NormalizeAlert(t);
    }

    /// <summary>Fill the two-notice alert fields (2.11 model), migrating pre-2.11
    /// configs where one speak/sound payload served both the timed warning and
    /// the expiry alert: the payload follows whichever notice actually fired.
    /// Idempotent — reruns leave a migrated config untouched.</summary>
    public static void NormalizeAlert(TriggerDefinition t)
    {
        if (t.Alert is not { } a) return;

        bool hadPayload = (a.SpeakEnabled && !string.IsNullOrWhiteSpace(a.Speak))
                          || !string.IsNullOrWhiteSpace(a.Sound);
        string legacyMode = !a.SpeakEnabled && !string.IsNullOrWhiteSpace(a.Sound)
            ? AlertConfig.ModeSound : AlertConfig.ModeSpeak;

        if (a.FadedEnabled is null)
        {
            // Old expiry alert — explicit OnExpire, or implied by a payload
            // with no timing ("Quickness faded" with AtSeconds 0 clearly
            // meant "say it when the bar runs out").
            a.FadedEnabled = a.OnExpire || (a.AtSeconds <= 0 && hadPayload);
            if (a.FadedEnabled == true)
            {
                a.FadedSpeak ??= a.Speak;
                a.FadedSound ??= a.Sound;
            }
        }
        a.WarnEnabled ??= a.AtSeconds > 0 && hadPayload;
        if (string.IsNullOrEmpty(a.WarnMode)) a.WarnMode = legacyMode;
        if (string.IsNullOrEmpty(a.FadedMode)) a.FadedMode = legacyMode;

        if (a.AtSeconds <= 0) a.AtSeconds = 15;

        // Prefill the phrases so the editor never shows an empty box (bars and
        // matrices only — a flash trigger's name is "X faded" already).
        if (t.Panel is Panels.Bars or Panels.SelfBuffs or Panels.TargetDebuffs)
        {
            if (string.IsNullOrWhiteSpace(a.Speak))
                a.Speak = AlertConfig.DefaultWarnPhrase(t.Name);
            if (string.IsNullOrWhiteSpace(a.FadedSpeak))
                a.FadedSpeak = AlertConfig.DefaultFadedPhrase(t.Name, t.Category);
        }

        // Keep the legacy flags coherent so an old exe reading this file stays
        // quiet about disabled notices.
        a.SpeakEnabled = a.WarnEnabled == true && a.WarnMode == AlertConfig.ModeSpeak;
        a.OnExpire = a.FadedEnabled == true;
    }

    private List<TriggerDefinition>? TryReadLegacyTriggers()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("triggers", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<TriggerDefinition>>(arr.GetRawText(), ReadOptions);
            }
        }
        catch { /* no legacy triggers */ }
        return null;
    }

    private static string Slug(string name)
    {
        string s = new string((name ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return string.IsNullOrEmpty(s) ? "loadout" : s;
    }

    // ---- window state -------------------------------------------------------

    private void ApplyWindowState(OverlayConfig overlay)
    {
        if (!File.Exists(WindowStatePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<WindowState>(
                File.ReadAllText(WindowStatePath), ReadOptions);
            if (state is null) return;
            if (state.Left is { } l) overlay.Left = l;
            if (state.Top is { } tp) overlay.Top = tp;
            if (state.Width is { } w) overlay.Width = w;
            if (state.Locked is { } locked) overlay.Locked = locked;
        }
        catch { /* ignore a corrupt state file; defaults win */ }
    }

    // ---- per-panel positions (matrix windows) -------------------------------

    // ---- dialog window bounds (dialog-<name>.json) --------------------------
    // The info windows (Inventory, Loot, Raid kills…) remember the size and
    // position the player dragged them to — resizing every open is meh.

    public sealed record DialogBounds(double Left, double Top, double Width, double Height, bool Maximized);

    public DialogBounds? LoadDialogBounds(string name)
    {
        string path = System.IO.Path.Combine(ConfigDirectory, $"dialog-{name}.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<DialogBounds>(File.ReadAllText(path), ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    public void SaveDialogBounds(string name, DialogBounds bounds)
    {
        try
        {
            File.WriteAllText(System.IO.Path.Combine(ConfigDirectory, $"dialog-{name}.json"),
                JsonSerializer.Serialize(bounds, WriteOptions));
        }
        catch { /* remembering the size is a convenience, never an error */ }
    }

    // The inventory dump only carries a storage while its window was open in
    // game — remember, per character, WHEN each storage was last captured so
    // the Inventory window can say how stale each one is.

    private string SectionTimesPath => System.IO.Path.Combine(ConfigDirectory, "inventory-sections.json");

    public Dictionary<string, DateTime> LoadSectionTimes(string charKey)
    {
        try
        {
            if (File.Exists(SectionTimesPath)
                && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DateTime>>>(
                    File.ReadAllText(SectionTimesPath), ReadOptions) is { } all
                && all.TryGetValue(charKey, out var times))
                return times;
        }
        catch { /* stale freshness metadata is never worth an error */ }
        return new Dictionary<string, DateTime>(StringComparer.Ordinal);
    }

    public void SaveSectionTimes(string charKey, Dictionary<string, DateTime> times)
    {
        try
        {
            Dictionary<string, Dictionary<string, DateTime>> all;
            try
            {
                all = File.Exists(SectionTimesPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DateTime>>>(
                        File.ReadAllText(SectionTimesPath), ReadOptions) ?? new()
                    : new();
            }
            catch
            {
                all = new();
            }
            all[charKey] = times;
            File.WriteAllText(SectionTimesPath, JsonSerializer.Serialize(all, WriteOptions));
        }
        catch { /* ibid. */ }
    }

    public (double Left, double Top)? LoadPanelPos(string name)
    {
        string path = System.IO.Path.Combine(ConfigDirectory, $"window-{name}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<PanelPos>(File.ReadAllText(path), ReadOptions);
            if (s?.Left is { } l && s.Top is { } t) return (l, t);
        }
        catch { /* ignore */ }
        return null;
    }

    public void SavePanelPos(string name, double left, double top)
    {
        try
        {
            File.WriteAllText(System.IO.Path.Combine(ConfigDirectory, $"window-{name}.json"),
                JsonSerializer.Serialize(new PanelPos { Left = left, Top = top }, WriteOptions));
        }
        catch { /* best-effort */ }
    }

    private sealed class PanelPos
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
    }

    // ---- per-panel anchored placement (Phase 3) -----------------------------

    public sealed record Placement(Models.Anchor Anchor, double OffX, double OffY);

    public Placement? LoadPlacement(string name)
    {
        string path = System.IO.Path.Combine(ConfigDirectory, $"window-{name}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<PlacementDto>(File.ReadAllText(path), ReadOptions);
            if (dto?.Anchor is { } a && Enum.TryParse<Models.Anchor>(a, ignoreCase: true, out var anchor))
                return new Placement(anchor, dto.OffX ?? 0, dto.OffY ?? 0);
        }
        catch { /* ignore */ }
        return null;
    }

    public void SavePlacement(string name, Models.Anchor anchor, double offX, double offY)
    {
        try
        {
            File.WriteAllText(System.IO.Path.Combine(ConfigDirectory, $"window-{name}.json"),
                JsonSerializer.Serialize(new PlacementDto
                {
                    Anchor = anchor.ToString(),
                    OffX = Math.Round(offX, 1),
                    OffY = Math.Round(offY, 1),
                }, WriteOptions));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Change just the anchor for a panel, preserving its offsets (defaults if none).</summary>
    public void SetPanelAnchor(string name, Models.Anchor anchor)
    {
        var p = LoadPlacement(name);
        SavePlacement(name, anchor, p?.OffX ?? 40, p?.OffY ?? 40);
    }

    private sealed class PlacementDto
    {
        public string? Anchor { get; set; }
        public double? OffX { get; set; }
        public double? OffY { get; set; }
    }

    public void SaveWindowState(OverlayConfig overlay)
    {
        var state = new WindowState
        {
            Left = overlay.Left,
            Top = overlay.Top,
            Width = overlay.Width,
            Locked = overlay.Locked,
        };
        try
        {
            File.WriteAllText(WindowStatePath, JsonSerializer.Serialize(state, WriteOptions));
        }
        catch { /* best-effort */ }
    }

    private sealed class WindowState
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public bool? Locked { get; set; }
    }

    // ---- per-character world state (last confirmed stance) --------------------
    // The log only prints a stance on CHANGE, so a restart would forget it —
    // this remembers the last "You assume a ... stance." per character.

    public string CharStatePath => Path.Combine(ConfigDirectory, "char-state.json");

    public Dictionary<string, string> LoadLastStances()
    {
        if (!File.Exists(CharStatePath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(CharStatePath), ReadOptions);
            return d is null ? new(StringComparer.OrdinalIgnoreCase)
                : new(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public void SaveLastStance(string character, string stance)
    {
        if (string.IsNullOrWhiteSpace(character)
            || character.Equals("You", StringComparison.OrdinalIgnoreCase)) return;
        var d = LoadLastStances();
        d[character] = stance;
        try { File.WriteAllText(CharStatePath, JsonSerializer.Serialize(d, WriteOptions)); }
        catch { /* best-effort */ }
    }

    // ---- known pet names (per character) --------------------------------------
    // Every summon gets a new random name; remembering them all is what keeps
    // last week's dead pets from reading as group members in old fights.

    public string KnownPetsPath => Path.Combine(ConfigDirectory, "known-pets.json");

    public HashSet<string> LoadKnownPets(string character)
    {
        var all = LoadKnownPetsFile();
        return all.TryGetValue(character, out var list)
            ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public void AddKnownPet(string character, string pet)
    {
        if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(pet)
            || character.Equals("You", StringComparison.OrdinalIgnoreCase)) return;
        var all = LoadKnownPetsFile();
        if (!all.TryGetValue(character, out var list))
            all[character] = list = new List<string>();
        if (list.Contains(pet, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(pet);
        try { File.WriteAllText(KnownPetsPath, JsonSerializer.Serialize(all, WriteOptions)); }
        catch { /* best-effort */ }
    }

    private Dictionary<string, List<string>> LoadKnownPetsFile()
    {
        if (!File.Exists(KnownPetsPath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                       File.ReadAllText(KnownPetsPath), ReadOptions)
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    // ---- global named respawns (repop timer) ---------------------------------

    public string RespawnsPath => Path.Combine(ConfigDirectory, "respawns.json");

    public List<RespawnEntry> LoadRespawns()
    {
        if (!File.Exists(RespawnsPath)) return new();
        try
        {
            var list = JsonSerializer.Deserialize<List<RespawnEntry>>(
                File.ReadAllText(RespawnsPath), ReadOptions) ?? new();
            // Typed times retired (owner ruling): each becomes the first gap
            // sample. In-memory until the next save; idempotent either way.
            foreach (var r in list) r.MigrateTypedTime(DateTime.Now);
            return list;
        }
        catch { return new(); }
    }

    public void SaveRespawns(List<RespawnEntry> respawns)
    {
        try
        {
            File.WriteAllText(RespawnsPath, JsonSerializer.Serialize(respawns, WriteOptions));
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// One-time migration: respawn timers used to live inside loadouts as
    /// "timerAuto" triggers, which made them vanish on loadout switches. Move
    /// them all into the global respawns.json.
    /// </summary>
    public void MigrateRespawnsFromLoadouts()
    {
        if (File.Exists(RespawnsPath)) return;

        var respawns = new List<RespawnEntry>();
        foreach (var lo in ListLoadouts())
        {
            var timers = lo.Triggers.Where(t => t.Panel == Panels.TimerAuto).ToList();
            if (timers.Count == 0) continue;
            foreach (var t in timers)
            {
                if (respawns.Any(r => r.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                respawns.Add(new RespawnEntry
                {
                    Name = t.Name,
                    Seconds = t.DurationSeconds,
                    Pattern = t.StartPattern,
                    Enabled = t.Enabled,
                });
            }
            lo.Triggers.RemoveAll(t => t.Panel == Panels.TimerAuto);
            SaveLoadout(lo);
        }
        SaveRespawns(respawns); // writes the file even when empty, so this runs once
    }

    /// <summary>A respawn entry compiled into the engine's timerAuto trigger form.
    /// Duration = the learned minimum gap; 0 = no evidence yet — the death
    /// still starts a counting-UP "learning" row on the watch.</summary>
    public static TriggerDefinition? BuildRespawnTrigger(RespawnEntry r)
    {
        if (!r.Enabled || string.IsNullOrWhiteSpace(r.Name)) return null;
        string n = Regex.Escape(r.Name.Trim());
        var t = new TriggerDefinition
        {
            Id = "respawn-" + Slug(r.Name),
            Name = r.Name.Trim(),
            Panel = Panels.TimerAuto,
            DurationSeconds = r.EffectiveSeconds ?? 0,
            StartPattern = string.IsNullOrWhiteSpace(r.Pattern)
                ? $@"(?:{n} has been slain by|You have slain {n})"
                : r.Pattern,
        };
        try { CompileOne(t); return t; }
        catch { return null; /* bad user pattern — skip rather than crash */ }
    }

    /// <summary>All enabled respawns as compiled triggers, ready to merge into the engine.</summary>
    public List<TriggerDefinition> BuildRespawnTriggers() =>
        LoadRespawns().Select(BuildRespawnTrigger).Where(t => t is not null).Select(t => t!).ToList();

    // ---- merged log copies -----------------------------------------------------
    // "Merge in another log file…" stores a timestamped COPY inside the config
    // folder, so Reset & rebuild can replay merged history even after the
    // original file is deleted or moved.

    public string MergedLogsDirectory => Path.Combine(ConfigDirectory, "merged-logs");

    /// <summary>Stored merge copies, oldest first (empty when none).</summary>
    public List<string> ListMergedLogs()
    {
        try
        {
            return Directory.Exists(MergedLogsDirectory)
                ? Directory.GetFiles(MergedLogsDirectory, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new();
        }
        catch { return new(); }
    }

    /// <summary>"eqlog_X.txt" + 2026-08-12 20:30:15 → "eqlog_X-20260812-203015.txt".</summary>
    public static string MergedCopyName(string sourceFileName, DateTime when) =>
        $"{Path.GetFileNameWithoutExtension(sourceFileName)}-{when:yyyyMMdd-HHmmss}{Path.GetExtension(sourceFileName)}";

    /// <summary>Copy a picked log into merged-logs; returns the copy's path.</summary>
    public string StoreMergedLogCopy(string sourcePath)
    {
        Directory.CreateDirectory(MergedLogsDirectory);
        string dest = Path.Combine(MergedLogsDirectory,
            MergedCopyName(Path.GetFileName(sourcePath), DateTime.Now));
        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
    }

    // ---- kept fights (DPS meter history) -------------------------------------

    public string SavedFightsPath => Path.Combine(ConfigDirectory, "fights.json");

    private List<CombatParser.FightRecord>? _savedFights;

    /// <summary>THE kept-fights list — one shared in-memory copy, so the history
    /// window's ★ Keep and the raid auto-keep write through the same instance
    /// instead of clobbering each other's file.</summary>
    public List<CombatParser.FightRecord> SavedFights => _savedFights ??= LoadSavedFights();

    /// <summary>Persist the shared kept-fights list.</summary>
    public void SaveSavedFights() => SaveSavedFights(SavedFights);

    /// <summary>Fights the user chose to keep ("★ Keep" in the history window).</summary>
    public List<CombatParser.FightRecord> LoadSavedFights()
    {
        if (!File.Exists(SavedFightsPath)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<CombatParser.FightRecord>>(
                File.ReadAllText(SavedFightsPath), ReadOptions) ?? new();
        }
        catch { return new(); /* corrupt file — start over rather than crash */ }
    }

    public void SaveSavedFights(List<CombatParser.FightRecord> fights)
    {
        _savedFights = fights; // whoever saves last defines the shared copy
        try
        {
            File.WriteAllText(SavedFightsPath, JsonSerializer.Serialize(fights, WriteOptions));
        }
        catch { /* best-effort */ }
    }

    // ---- default files ------------------------------------------------------

    private const string DefaultConfigJson = """
    {
      // ==========================================================================
      //  EQL Assistant settings.  Triggers live in the loadouts\ folder next to
      //  this file (one .json per loadout) and are managed from the in-app
      //  Trigger Manager (Ctrl+Alt+M).
      // ==========================================================================

      "log": {
        // Folder containing your eqlog_*.txt files, e.g. "C:\\Games\\EQLegends\\Logs".
        "directory": "",
        "filePattern": "eqlog_*.txt",
        "explicitFile": "",
        "startAtEndOfFile": true,
        "pollIntervalMs": 200
      },

      "overlay": {
        "width": 320,
        "barHeight": 24,
        "spacing": 4,
        "fontSize": 13,
        "showCategoryHeaders": true,
        "warnSeconds": 6,
        "remindIntervalSeconds": 20,
        "muted": false,
        "startLocked": false,
        "opacity": 1.0,
        "matrixColumns": 4,
        "timerSeconds": 400,
        "timerVisible": true,
        "meterVisible": true,
        // Your pet's name — enables the pet line in the DPS meter's incoming footer.
        "petName": "",
        "flashFontSize": 54,
        "flashWidth": 900,
        // Scrolling combat text: master switch + one movable lane per enabled type.
        "sctVisible": true,
        "sctIncoming": true,
        "sctOutgoing": true,
        "sctHeals": true,
        "sctHealsIn": true,
        "sctPetIncoming": false,
        "sctPetOutgoing": false,
        "sctFontSize": 18,
        "sctBigHit": 200,
        "sctLaneWidth": 170,
        "sctLaneHeight": 300
      },

      "characterName": "",
      "activeLoadout": "Default",
      // Replay today's log lines on startup (fight history, raid kills, seen
      // spells) — useful when the app is started mid-session.
      "catchUpOnStart": false
    }
    """;

    private const string DefaultLoadoutJson = """
    {
      "name": "Default",
      "triggers": [
        {
          "id": "sow", "name": "Spirit of Wolf", "category": "Buffs",
          "startPattern": "You feel the spirit of wolf enter you\\.",
          "endPattern": "Your Spirit of Wolf spell has worn off\\.",
          "durationSeconds": 1800, "color": "#4FC3F7", "refreshOnRetrigger": true
        },
        {
          "id": "clarity", "name": "Clarity", "category": "Buffs",
          "startPattern": "Your mind fades into a euphoric state\\.",
          "endPattern": "Your Clarity spell has worn off\\.",
          "durationSeconds": 1620, "color": "#BA68C8", "refreshOnRetrigger": true
        },
        {
          "id": "regen-self", "name": "Regeneration (HoT)", "category": "HoTs",
          "startPattern": "You feel the beat of the wild rush through you\\.",
          "endPattern": "Your Regeneration spell has worn off\\.",
          "durationSeconds": 90, "color": "#81C784", "refreshOnRetrigger": true
        },
        {
          "id": "hot-on-other", "name": "HoT", "category": "HoTs",
          "startPattern": "(?<target>\\w+) begins to regenerate\\.",
          "durationSeconds": 60, "color": "#AED581", "refreshOnRetrigger": true
        }
      ]
    }
    """;
}
