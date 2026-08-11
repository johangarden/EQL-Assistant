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

    /// <summary>True when a trigger's start pattern is a landing sentence that
    /// SEVERAL different spells print (e.g. "You feel much faster." is
    /// Quickness, Alacrity, Celerity AND Swift Like The Wind) — the case where
    /// an unanchored bar would be a coin flip. Ranks of one spell don't count
    /// as different. Non-escaped custom regexes simply return false.</summary>
    public bool IsSharedLanding(string startPattern)
    {
        if (string.IsNullOrEmpty(startPattern)) return false;
        string body;
        try { body = Regex.Unescape(startPattern); } catch { return false; }
        return _byCastOnYou.TryGetValue(body, out var hits)
            && hits.Select(s => SpellDurations.BaseKey(s.Name)).Distinct().Count() > 1;
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

    /// <summary>Wipe the seen-spells set (Data page reset).</summary>
    public void ResetSeen()
    {
        _seen.Clear();
        _seenDirty = true;
        SaveSeenIfDirty();
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

    // ---- trigger typing -------------------------------------------------------

    /// <summary>The trigger TYPE for a spell — drives list grouping and the
    /// type-owned color. The wiki's type field decides when it says something
    /// ("Heal Over Time", "Damage Over Time", …); classic landing-line wording
    /// fills the gaps (every poison/disease DoT says "You have been poisoned."
    /// -style lines but is typed just "Detrimental"); the bucket is the
    /// fallback.</summary>
    public static string TriggerCategory(Spell s)
    {
        string t = s.Type;
        if (t.Contains("Heal Over Time", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Regen", StringComparison.OrdinalIgnoreCase)) return "HoTs";
        if (t.Contains("Damage Over Time", StringComparison.OrdinalIgnoreCase)) return "DoTs";
        if (t.Equals("Heal", StringComparison.OrdinalIgnoreCase)) return "HoTs";

        string land = s.CastOnYou;
        bool debuff = s.Bucket == "Debuff";
        if (debuff && (land.Contains("poisoned", StringComparison.OrdinalIgnoreCase)
                       || land.Contains("diseased", StringComparison.OrdinalIgnoreCase)
                       || land.Contains("blood boils", StringComparison.OrdinalIgnoreCase)
                       || land.Contains("blood simmers", StringComparison.OrdinalIgnoreCase)
                       || land.Contains("plague", StringComparison.OrdinalIgnoreCase)
                       || land.Contains("withers", StringComparison.OrdinalIgnoreCase)))
            return "DoTs";
        if (!debuff && land.Contains("regenerate", StringComparison.OrdinalIgnoreCase))
            return "HoTs";

        return debuff ? "Debuffs" : "Buffs";
    }

    /// <summary>Case-insensitive spell lookup by exact name.</summary>
    public Spell? FindByName(string name) =>
        Spells.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>88 scraped spells carry junk landing text ("You .", empty) —
    /// ports, proc buffs, and the EQL-added heals the wiki has no emote for.</summary>
    public static bool JunkMessage(string message)
    {
        string t = Regex.Replace(message.Trim(), @"^(You|Someone)\b", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return t.Trim().TrimEnd('.').Trim().Length == 0;
    }

    /// <summary>The fallback start pattern when the landing text is junk: the
    /// begin-cast line always prints, is unique per spell, and needs no
    /// cast-anchor. The bar just starts at cast time instead of landing.
    /// Tolerates the RANK the game appends ("You begin casting Slugs Healing
    /// V.") and bard singing.</summary>
    public static string BeginCastPattern(string spellName) =>
        "^You begin (?:casting|singing) " + Regex.Escape(spellName.Trim()) + @"(?: [IVX]{1,7})?\.";

    /// <summary>Heal library-added triggers on load. Two repairs: (a) triggers
    /// still wearing the pre-2.9 two-bucket categories re-derive their type
    /// (Snails Healing becomes a HoT, Envenomed Bolt a DoT) — hand-typed ones
    /// are left alone; (b) triggers whose start pattern is a junk landing line
    /// ("You\ \.") switch to the begin-cast pattern. Returns how many changed.</summary>
    public int HealLibraryTriggers(IEnumerable<TriggerDefinition> triggers)
    {
        int changed = 0;
        foreach (var t in triggers)
        {
            if (!t.Id.StartsWith("lib-", StringComparison.Ordinal)) continue;
            if (FindByName(t.Name) is not { } s) continue;
            bool touched = false;

            if (t.Category is "Buffs" or "Debuffs" && TriggerCategory(s) is var cat && cat != t.Category)
            {
                t.Category = cat;
                touched = true;
            }

            // Repairable shapes: the escaped junk landing line, empty, or the
            // 2.9.0 fallback that missed the rank suffix ("… Slugs Healing V.").
            string legacyFallback = "^You begin casting " + Regex.Escape(s.Name.Trim()) + @"\.";
            if (JunkMessage(s.CastOnYou)
                && (t.StartPattern == Regex.Escape(s.CastOnYou) || t.StartPattern.Length == 0
                    || t.StartPattern == legacyFallback))
            {
                t.StartPattern = BeginCastPattern(s.Name);
                try { ConfigService.CompileOne(t); } catch { /* keep the text either way */ }
                touched = true;
            }

            if (touched) changed++;
        }
        return changed;
    }

    // ---- one-click trigger generation ----------------------------------------

    /// <summary>Countdown bar: starts on the cast message (or, when the scrape
    /// has no usable landing text, on the begin-cast line), cleared by the
    /// wear-off line.</summary>
    public static TriggerDefinition? BarTrigger(Spell s, bool spokenWarning)
    {
        if (s.Name.Length == 0) return null;
        var t = new TriggerDefinition
        {
            Id = "lib-" + Slug(s.Name),
            Name = s.Name,
            Category = TriggerCategory(s),
            Panel = Panels.Bars,
            StartPattern = JunkMessage(s.CastOnYou)
                ? BeginCastPattern(s.Name)
                : Regex.Escape(s.CastOnYou),
            EndPattern = s.WearsOff.Length > 0 ? Regex.Escape(s.WearsOff) : null,
            DurationSeconds = s.DurationSec > 0 ? s.DurationSec : 60,
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
