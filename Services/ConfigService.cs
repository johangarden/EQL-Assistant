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

        ApplyWindowState(config.Overlay);
        return config;
    }

    /// <summary>Write global settings. Triggers are [JsonIgnore] so they aren't included.</summary>
    public void SaveSettings(AppConfig config)
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, WriteOptions));
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

    // ---- kept fights (DPS meter history) -------------------------------------

    public string SavedFightsPath => Path.Combine(ConfigDirectory, "fights.json");

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
        "sctPetIncoming": false,
        "sctPetOutgoing": false,
        "sctFontSize": 18,
        "sctBigHit": 200,
        "sctLaneWidth": 170,
        "sctLaneHeight": 300
      },

      "characterName": "",
      "activeLoadout": "Default"
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
