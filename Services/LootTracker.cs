using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Loot history from the log's three loot line forms:
///   upgrade — "You looted a Platinum Ring +1 from Gynok Moltor's corpse to create a Platinum Ring +4"
///   kept    — "--You have looted 2 Bone Chips from an elf skeleton's corpse.--"
///   sold    — "You looted a Rusty Short Sword +2 from ... and sold it for 2 silver and 1 copper."
/// Entries persist to loot.json. Every entry stores the LINE's timestamp, so a
/// catch-up replay of lines already seen live dedupes exactly (same second +
/// item + mob), with no fuzzy time windows.
/// </summary>
public sealed class LootTracker
{
    public enum LootKind { Upgrade, Kept, Sold, Currency }

    public sealed record LootEntry(DateTime When, string Item, string Mob, string Zone, LootKind Kind,
        string Result = "", long Copper = 0, int Count = 1);

    private const int MaxEntries = 5000;

    private readonly string _path;
    private readonly List<LootEntry> _entries = new(); // newest first
    private string _zone = "";

    /// <summary>All entries, newest first.</summary>
    public IReadOnlyList<LootEntry> Entries => _entries;

    /// <summary>Raised on the caller's thread when a loot line was recorded.</summary>
    public event Action? Changed;

    /// <summary>Raised with each newly recorded entry (fires before the cap trims
    /// old rows — consumers keeping running totals never miss one).</summary>
    public event Action<LootEntry>? Added;

    public long TotalVendorCopper
    {
        get
        {
            long sum = 0;
            foreach (var e in _entries)
                if (e.Kind == LootKind.Sold) sum += e.Copper;
            return sum;
        }
    }

    public int UpgradeCount => _entries.Count(e => e.Kind == LootKind.Upgrade);

    // ---- line formats (confirmed from the real EQ Legends log) ---------------

    private static readonly Regex UpgradeRx = new(
        @"^You looted (?:an?|the) (?<item>.+?) from (?<mob>.+?)'s corpse to create (?:an?|the) (?<result>.+?)\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SoldRx = new(
        @"^You looted (?:an?|the) (?<item>.+?) from (?<mob>.+?)'s corpse and sold it for (?<money>.+?)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeptRx = new(
        @"^--You have looted (?<item>.+?) from (?<mob>.+?)'s corpse\.--$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Currency pickups (motes, wind runes) print their own form, confirmed:
    // "You looted a Mote of Minor Potential from a shin ghoul knight's corpse
    //  and stored it in your currency" — no trailing period.
    private static readonly Regex CurrencyRx = new(
        @"^You looted (?:an?|the) (?<item>.+?) from (?<mob>.+?)'s corpse and stored it in your currency\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CoinRx = new(
        @"(?<n>\d+) (?<unit>platinum|gold|silver|copper)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimestampPrefix =
        new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);

    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    private const string ZonePrefix = "You have entered ";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public LootTracker(ConfigService config, string? pathOverride = null)
    {
        // pathOverride: selftests bring their own file — the REAL loot.json
        // must never collect synthetic test entries.
        _path = pathOverride ?? Path.Combine(config.ConfigDirectory, "loot.json");
        Load();
    }

    public void ProcessLine(string rawLine)
    {
        DateTime when = ExtractTimestamp(rawLine, out string body);

        if (body.StartsWith(ZonePrefix, StringComparison.Ordinal) && body.EndsWith('.'))
        {
            _zone = body[ZonePrefix.Length..^1];
            return;
        }

        if (!TryParseLoot(body, out var kind, out string item, out string mob, out string result,
                out long copper, out int count))
            return;

        // Exact-second dedupe: a re-run catch-up feeds the very same lines.
        foreach (var e in _entries)
        {
            if (e.When == when && e.Kind == kind
                && e.Item.Equals(item, StringComparison.OrdinalIgnoreCase)
                && e.Mob.Equals(mob, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var entry = new LootEntry(when, item, mob, _zone, kind, result, copper, count);
        _entries.Insert(0, entry);
        Added?.Invoke(entry);
        if (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Parse a loot line body into its parts (public for the self-tests).
    /// Kept stacks split their count out: "2 Bone Chips" → count 2, item "Bone Chips".</summary>
    public static bool TryParseLoot(string body, out LootKind kind, out string item,
        out string mob, out string result, out long copper, out int count)
    {
        kind = LootKind.Kept; item = ""; mob = ""; result = ""; copper = 0; count = 1;

        var m = UpgradeRx.Match(body);
        if (m.Success)
        {
            kind = LootKind.Upgrade;
            item = m.Groups["item"].Value;
            mob = m.Groups["mob"].Value;
            result = m.Groups["result"].Value.TrimEnd('.');
            return true;
        }

        m = SoldRx.Match(body);
        if (m.Success)
        {
            kind = LootKind.Sold;
            item = m.Groups["item"].Value;
            mob = m.Groups["mob"].Value;
            copper = ParseCoins(m.Groups["money"].Value);
            return true;
        }

        m = CurrencyRx.Match(body);
        if (m.Success)
        {
            kind = LootKind.Currency;
            item = m.Groups["item"].Value;
            mob = m.Groups["mob"].Value;
            return true;
        }

        m = KeptRx.Match(body);
        if (m.Success)
        {
            kind = LootKind.Kept;
            // "a Raw-Hide Gorget +2" → strip the article; "2 Bone Chips" → count 2.
            item = Regex.Replace(m.Groups["item"].Value, @"^(?:an?|the) ", "");
            var stack = Regex.Match(item, @"^(?<n>\d+) (?<rest>.+)$");
            if (stack.Success)
            {
                count = int.Parse(stack.Groups["n"].Value, CultureInfo.InvariantCulture);
                item = stack.Groups["rest"].Value;
            }
            mob = m.Groups["mob"].Value;
            return true;
        }

        return false;
    }

    /// <summary>Canonical key for "how many of X do I hold" — "+N" upgrade suffixes
    /// collapse ("Sphinx Claw +1" counts as "Sphinx Claw").</summary>
    public static string ItemKey(string item) =>
        Regex.Replace(item.Trim(), @"\s\+\d+$", "").ToLowerInvariant();

    /// <summary>"2 platinum, 2 gold, 1 silver and 4 copper" → total copper.</summary>
    public static long ParseCoins(string money)
    {
        long copper = 0;
        foreach (Match c in CoinRx.Matches(money))
        {
            long n = long.Parse(c.Groups["n"].Value, CultureInfo.InvariantCulture);
            copper += c.Groups["unit"].Value switch
            {
                "platinum" => n * 1000,
                "gold" => n * 100,
                "silver" => n * 10,
                _ => n,
            };
        }
        return copper;
    }

    /// <summary>Total copper → "2p 2g 1s 4c" (zero denominations skipped).</summary>
    public static string FormatCoins(long copper)
    {
        if (copper <= 0) return "0c";
        long p = copper / 1000, g = copper % 1000 / 100, s = copper % 100 / 10, c = copper % 10;
        var parts = new List<string>(4);
        if (p > 0) parts.Add($"{p}p");
        if (g > 0) parts.Add($"{g}g");
        if (s > 0) parts.Add($"{s}s");
        if (c > 0) parts.Add($"{c}c");
        return string.Join(" ", parts);
    }

    // ---- persistence ----------------------------------------------------------

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var entries = JsonSerializer.Deserialize<List<LootEntry>>(File.ReadAllText(_path), JsonOpts);
            if (entries is null) return;
            _entries.AddRange(entries
                .Select(e =>
                {
                    // Pre-2.4 kept stacks embedded the count in the name.
                    if (e.Kind != LootKind.Kept || e.Count != 1) return e;
                    var stack = Regex.Match(e.Item, @"^(?<n>\d+) (?<rest>.+)$");
                    return stack.Success
                        ? e with
                        {
                            Count = int.Parse(stack.Groups["n"].Value, CultureInfo.InvariantCulture),
                            Item = stack.Groups["rest"].Value,
                        }
                        : e;
                })
                .OrderByDescending(e => e.When));
        }
        catch { /* corrupt file -> start empty rather than crash */ }
    }

    /// <summary>Wipe the loot history (Data page reset — followed by a reparse).</summary>
    public void ResetAll()
    {
        _entries.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOpts)); }
        catch { /* best-effort */ }
    }

    private static DateTime ExtractTimestamp(string line, out string body)
    {
        var m = TimestampPrefix.Match(line);
        if (m.Success)
        {
            body = line.Substring(m.Length);
            string ts = Regex.Replace(m.Groups["ts"].Value.Trim(), @"\s+", " ");
            if (DateTime.TryParseExact(ts, TimestampFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed;
        }
        else
        {
            body = line;
        }
        return DateTime.Now;
    }
}
