using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQLOverlay.Models;

namespace EQLOverlay.Services;

/// <summary>
/// The prebaked spell library: ~1400 spells with their exact cast / wear-off
/// log messages, class levels and buff durations, used to generate ready-made
/// triggers with one click. Also tracks which spells have appeared in YOUR
/// log ("seen"), via exact-message lookup on every line.
/// Data: data\spell-library.json, converted from jmoyers/everquest-companion (MIT).
/// </summary>
public sealed class SpellLibrary
{
    public sealed class Spell
    {
        public string Name { get; set; } = "";
        public string Bucket { get; set; } = "Buff";   // "Buff" | "Debuff"
        public string Type { get; set; } = "";
        public string Classes { get; set; } = "";      // "RNG 28 · DRU 10"
        public List<string> ClassList { get; set; } = new();
        public string CastOnYou { get; set; } = "";
        public string CastOnOther { get; set; } = "";
        public string WearsOff { get; set; } = "";
        public double DurationSec { get; set; }
        public bool Illusion { get; set; }
    }

    private sealed class LibraryFile
    {
        public int Count { get; set; }
        public List<Spell> Spells { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex TimestampPrefix = new(@"^\[.+?\]\s?", RegexOptions.Compiled);

    private readonly string _seenPath;
    // Some spells share a message (e.g. Pack Spirit and Spirit of Wolf both cast
    // "You feel the spirit of wolf enter you.") — a hit marks all of them seen.
    private readonly Dictionary<string, List<Spell>> _byCastOnYou = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Spell>> _byWearsOff = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private bool _seenDirty;

    public IReadOnlyList<Spell> Spells { get; }
    public int SeenCount => _seen.Count;

    public SpellLibrary(ConfigService config)
    {
        _seenPath = Path.Combine(config.ConfigDirectory, "seen-spells.json");
        Spells = LoadLibrary();

        static void Index(Dictionary<string, List<Spell>> dict, string msg, Spell s)
        {
            if (msg.Length == 0) return;
            if (!dict.TryGetValue(msg, out var list)) dict[msg] = list = new List<Spell>();
            list.Add(s);
        }
        foreach (var s in Spells)
        {
            Index(_byCastOnYou, s.CastOnYou, s);
            Index(_byWearsOff, s.WearsOff, s);
        }

        try
        {
            if (File.Exists(_seenPath))
            {
                var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_seenPath), JsonOpts);
                if (names is not null) foreach (var n in names) _seen.Add(n);
            }
        }
        catch { /* corrupt seen file — start fresh */ }
    }

    private static List<Spell> LoadLibrary()
    {
        try
        {
            // A data\spell-library.json next to the exe wins (user-tweakable);
            // otherwise fall back to the copy embedded in the assembly, so a
            // lone copied exe still has the full library.
            string path = Path.Combine(AppContext.BaseDirectory, "data", "spell-library.json");
            string json;
            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }
            else
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("spell-library.json", StringComparison.OrdinalIgnoreCase));
                if (resName is null) return new();
                using var stream = asm.GetManifestResourceStream(resName)!;
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
            var file = JsonSerializer.Deserialize<LibraryFile>(json, JsonOpts);
            return file?.Spells ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn("Spell library failed to load: " + ex.Message);
            return new();
        }
    }

    public bool IsSeen(Spell s) => _seen.Contains(s.Name);

    /// <summary>O(1) exact-message check on every log line to build the "seen in your log" set.</summary>
    public void MarkSeenFromLine(string rawLine)
    {
        string body = TimestampPrefix.Replace(rawLine, "", 1).Trim();
        if (!_byCastOnYou.TryGetValue(body, out var hits)
            && !_byWearsOff.TryGetValue(body, out hits))
            return;
        foreach (var s in hits!)
            if (_seen.Add(s.Name))
                _seenDirty = true;
    }

    public void SaveSeenIfDirty()
    {
        if (!_seenDirty) return;
        try
        {
            File.WriteAllText(_seenPath, JsonSerializer.Serialize(_seen.OrderBy(x => x).ToList(), JsonOpts));
            _seenDirty = false;
        }
        catch { /* best-effort */ }
    }

    // ---- search / filter ------------------------------------------------------

    /// <summary>Filter: "" = all, "seen", "Buff", "Debuff", "Illusion".</summary>
    public List<Spell> Search(string query, string filter = "", string cls = "")
    {
        query = query.Trim();
        IEnumerable<Spell> src = Spells;

        src = filter switch
        {
            "seen" => src.Where(IsSeen),
            "Buff" => src.Where(s => s.Bucket == "Buff" && !s.Illusion),
            "Debuff" => src.Where(s => s.Bucket == "Debuff"),
            "Illusion" => src.Where(s => s.Illusion),
            _ => src,
        };

        if (cls.Length > 0)
            src = src.Where(s => s.ClassList.Contains(cls));

        if (query.Length > 0)
            src = src.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.CastOnYou.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.WearsOff.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Type.Contains(query, StringComparison.OrdinalIgnoreCase));

        return src.OrderByDescending(IsSeen).ThenBy(s => s.Name).ToList();
    }

    // ---- one-click trigger generation ----------------------------------------

    /// <summary>Countdown bar: starts on the cast message, cleared by the wear-off line.</summary>
    public static TriggerDefinition? BarTrigger(Spell s, bool spokenWarning)
    {
        if (s.CastOnYou.Length == 0) return null;
        var t = new TriggerDefinition
        {
            Id = "lib-" + Slug(s.Name),
            Name = s.Name,
            Category = s.Bucket == "Debuff" ? "Debuffs" : "Buffs",
            Panel = Panels.Bars,
            StartPattern = Regex.Escape(s.CastOnYou),
            EndPattern = s.WearsOff.Length > 0 ? Regex.Escape(s.WearsOff) : null,
            DurationSeconds = s.DurationSec > 0 ? s.DurationSec : 60,
            Color = s.Bucket == "Debuff" ? "#E57373" : "#4FC3F7",
            RefreshOnRetrigger = true,
            Alert = spokenWarning
                ? new AlertConfig { AtSeconds = 20, Speak = s.Name + " is fading" }
                : null,
        };
        ConfigService.CompileOne(t);
        return t;
    }

    /// <summary>Screen flash the moment the wear-off line appears in the log.</summary>
    public static TriggerDefinition? FadeFlashTrigger(Spell s)
    {
        if (s.WearsOff.Length == 0) return null;
        var t = new TriggerDefinition
        {
            Id = "libfade-" + Slug(s.Name),
            Name = s.Name + " faded",
            Panel = Panels.Flash,
            StartPattern = Regex.Escape(s.WearsOff),
            DurationSeconds = 0,
            Color = "#FFCC33",
            Alert = new AlertConfig { FlashText = s.Name + " FADED!" },
        };
        ConfigService.CompileOne(t);
        return t;
    }

    private static string Slug(string name)
    {
        string s = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length == 0 ? "spell" : s;
    }
}
