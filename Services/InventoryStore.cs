using System.IO;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The game's own inventory dump, parsed: <c>/outputfile inventory</c> writes
/// "&lt;Char&gt;_&lt;server&gt;-Inventory.txt" into the EQ install root — a
/// tab-separated table of everything worn, bagged, banked and deposited, plus
/// a keyring table. This is Companion's measured grammar, ported:
///  • A section header is any row whose SECOND column is literally "Name";
///    its SHAPE comes from the columns it spells ("Name ID Count Slots" =
///    items, "Name ID" = keyring), so an unknown future table still parses.
///  • Nesting rides "-Slot&lt;n&gt;" suffixes ("General 1-Slot9-Slot7") — the
///    separator is the whole token, never a bare "-": "Personal-Depot1" keeps
///    its hyphen and is a top-level depot slot.
///  • Duplicate base tokens are REAL (two Ears, two Wrists): a child attaches
///    to the most recently seen row bearing its parent path.
///  • "Empty" means "the client enumerated this slot", not "this storage is
///    empty" — and a storage MISSING from a dump means "this dump does not
///    say": the file only covers windows that were open when it was typed.
/// Nothing here persists; the dump on disk is the record and re-reads in
/// milliseconds.
/// </summary>
public static class InventoryStore
{
    public sealed class Entry
    {
        public string Section = "";
        public string Location = "";   // verbatim — the spelling the player can search for
        public string Base = "";       // location minus the -Slot chain
        public string Name = "";       // verbatim (keeps " +N", "*", " (Exaltation)")
        public int ItemId;
        public int Count;
        public int Slots;              // child slots this item PROVIDES (bag size)
        public bool Empty;
        public bool Orphan;            // named a parent the file never showed
        public int Line;               // 1-based line in the dump
        public List<Entry> Children = new();
    }

    public sealed record KeyRingEntry(string Section, string Category, string Name, int ItemId, int Line);

    public sealed class Dump
    {
        public List<Entry> Items = new();
        public List<KeyRingEntry> KeyRing = new();
        public List<string> Sections = new();
        public int MalformedCount;
        public int UnknownSectionRows;

        /// <summary>Storages this dump gives EVIDENCE for — the row is the
        /// evidence, an "Empty" bank slot still proves the bank was dumped.
        /// Keys: worn, bags, bank, sharedBank, depot, keyring.</summary>
        public HashSet<string> Covered = new(StringComparer.Ordinal);

        /// <summary>A non-primary ITEM-shaped table exists (the never-sampled
        /// Dragon's Hoard would arrive as one).</summary>
        public bool HasExtraItemSection;
    }

    /// <summary>One row of the flattened, searchable ledger. Host is the item
    /// this row sits INSIDE (the bearer of an exaltation socket), "" at top
    /// level.</summary>
    public sealed record CarryRow(string Name, string SearchKey, string Location, int Count,
        string Lane, int Line, string Host = "", bool IsContainer = false);

    public const string PrimarySection = "Location";
    public const string LanePrefix = "section:";

    private static readonly string[] ItemColumns = { "Name", "ID", "Count", "Slots" };
    private static readonly string[] KeyRingColumns = { "Name", "ID" };

    /// <summary>Only "Equipment" keyring rows are things you hold; "Activated"
    /// is an unlocked appearance, not an item.</summary>
    private static readonly HashSet<string> HeldKeyRingCategories = new(StringComparer.Ordinal)
    {
        "Equipment",
    };

    private static readonly HashSet<string> EquipLocations = new(StringComparer.Ordinal)
    {
        "Any Slot", "Ammo", "Arms", "Back", "Chest", "Ear", "Face", "Feet", "Fingers",
        "Hands", "Head", "Held", "Legs", "Neck", "Primary", "Range", "Secondary",
        "Shoulders", "Waist", "Wrist",
    };

    // Note the client's own inconsistency: "General 1" has a space,
    // "Bank1" / "SharedBank1" do not, and the depot is a compound token.
    // The Dragon's Hoard (first sampled 2026-08-18, Thorrak's dump) rides
    // the PRIMARY table as "Hoard 1"… — spaced like General, nestable.
    private static readonly (Regex Rx, string Lane)[] ContainerLanes =
    {
        (new Regex(@"^General \d+$", RegexOptions.Compiled), "bags"),
        (new Regex(@"^Bank\d+$", RegexOptions.Compiled), "bank"),
        (new Regex(@"^SharedBank\d+$", RegexOptions.Compiled), "bank"), // one chip, deliberately
        (new Regex(@"^Personal-Depot\d+$", RegexOptions.Compiled), "depot"),
        (new Regex(@"^Hoard \d+$", RegexOptions.Compiled), "hoard"),
    };

    // END-anchored so "Personal-Depot1" keeps its hyphen and yields no sub-slots.
    private static readonly Regex SlotChainRx = new(@"^(?<base>.*?)(?<chain>(?:-Slot\d+)+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LastSlotRx = new(@"-Slot\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyDictionary<string, string> LaneLabels =
        new Dictionary<string, string>
        {
            // In-game vocabulary only — "keyring" is the dump's legacy word
            // from the old EQ client and never appears in the UI.
            ["worn"] = "Worn",
            ["bags"] = "Bags",
            ["storage"] = "Storage",           // KeyRing / Equipment — in-game "Storage"
            ["activated"] = "Activated items", // KeyRing / Activated
            ["keyring"] = "Exaltations",       // KeyRing / Augmentation — in-game "Exaltations"
            ["bank"] = "Bank",
            ["depot"] = "Depot",
            ["hoard"] = "Dragon Hoard",
            ["elsewhere"] = "Elsewhere",
        };

    // Chip order tells a story: what's ON you (worn → bags → the keyring
    // collections) first, what's STASHED (bank → depot → hoard) after.
    private static readonly string[] FixedLaneOrder =
        { "worn", "bags", "storage", "activated", "keyring", "bank", "depot", "hoard", "elsewhere" };

    /// <summary>Which visual family a lane chip belongs to: "carry" = on your
    /// character, "stash" = remote storage, "" = neither (Elsewhere, extra
    /// sections).</summary>
    public static string LaneGroup(string laneId) => laneId switch
    {
        "worn" or "bags" or "storage" or "activated" or "keyring" => "carry",
        "bank" or "depot" or "hoard" => "stash",
        _ => "",
    };

    // ---- parsing --------------------------------------------------------------

    public static Dump Parse(string text)
    {
        var dump = new Dump();
        string section = PrimarySection;
        string shape = "items";
        dump.Sections.Add(section);
        var byPath = new Dictionary<string, Entry>(StringComparer.Ordinal);

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            string[] cols = line.Split('\t');

            if (cols.Length >= 2 && cols[1].Trim() == "Name")
            {
                section = cols[0].Trim();
                shape = SectionShape(cols);
                if (!dump.Sections.Contains(section)) dump.Sections.Add(section);
                // The header itself is evidence: a keyring table with zero
                // rows still says "the keyring was dumped".
                if (shape == "keyRing") dump.Covered.Add("keyring");
                else if (shape == "items" && section != PrimarySection) dump.HasExtraItemSection = true;
                byPath.Clear(); // nothing nests across tables
                continue;
            }

            if (shape == "items") AddItemRow(dump, byPath, section, cols, i + 1);
            else if (shape == "keyRing") AddKeyRingRow(dump, section, cols, i + 1);
            else dump.UnknownSectionRows++;
        }
        return dump;
    }

    /// <summary>Shape by the columns the header SPELLS (trailing empty columns
    /// dropped — the real KeyRing header ends in a bare tab).</summary>
    private static string SectionShape(string[] cols)
    {
        var declared = cols.Skip(1).Select(c => c.Trim()).ToList();
        while (declared.Count > 0 && declared[^1].Length == 0) declared.RemoveAt(declared.Count - 1);
        if (declared.SequenceEqual(ItemColumns)) return "items";
        if (declared.SequenceEqual(KeyRingColumns)) return "keyRing";
        return "unknown";
    }

    private static void AddItemRow(Dump dump, Dictionary<string, Entry> byPath,
        string section, string[] cols, int line)
    {
        if (cols.Length < 4) { dump.MalformedCount++; return; }
        string location = cols[0].Trim();
        string name = cols[1].Trim();
        var entry = new Entry
        {
            Section = section,
            Location = location,
            Base = SplitBase(location),
            Name = name,
            ItemId = Int(cols, 2),
            Count = Int(cols, 3),
            Slots = Int(cols, 4),
            Empty = name.Length == 0 || name == "Empty",
            Line = line,
        };

        string stripped = LastSlotRx.Replace(location, "");
        string? parentKey = stripped == location ? null : stripped;
        if (parentKey is not null && byPath.TryGetValue(parentKey, out var parent))
        {
            parent.Children.Add(entry);
        }
        else
        {
            entry.Orphan = parentKey is not null; // top level is not an orphan
            dump.Items.Add(entry);
        }
        byPath[location] = entry; // last-writer-wins: duplicate Ears are real

        if (section == PrimarySection)
        {
            string storage = StorageOfBase(entry.Base);
            if (storage.Length > 0) dump.Covered.Add(storage);
        }
    }

    private static readonly Regex SharedBankRx = new(@"^SharedBank\d+$", RegexOptions.Compiled);
    private static readonly Regex BankRx = new(@"^Bank\d+$", RegexOptions.Compiled);
    private static readonly Regex GeneralRx = new(@"^General \d+$", RegexOptions.Compiled);
    private static readonly Regex DepotRx = new(@"^Personal-Depot\d+$", RegexOptions.Compiled);
    private static readonly Regex HoardRx = new(@"^Hoard \d+$", RegexOptions.Compiled);

    /// <summary>Like <see cref="LaneOfBase"/> but keeps sharedBank distinct —
    /// coverage speaks about the game's storages, not the ledger's chips.</summary>
    private static string StorageOfBase(string baseToken)
    {
        if (EquipLocations.Contains(baseToken)) return "worn";
        if (GeneralRx.IsMatch(baseToken)) return "bags";
        if (SharedBankRx.IsMatch(baseToken)) return "sharedBank";
        if (BankRx.IsMatch(baseToken)) return "bank";
        if (DepotRx.IsMatch(baseToken)) return "depot";
        if (HoardRx.IsMatch(baseToken)) return "hoard";
        return "";
    }

    private static void AddKeyRingRow(Dump dump, string section, string[] cols, int line)
    {
        if (cols.Length < 3) { dump.MalformedCount++; return; }
        dump.KeyRing.Add(new KeyRingEntry(section, cols[0].Trim(), cols[1].Trim(),
            Int(cols, 2), line));
    }

    private static int Int(string[] cols, int i) =>
        i < cols.Length && int.TryParse(cols[i].Trim(), out int n) && n >= 0 ? n : 0;

    /// <summary>The base token of a Location: everything before its
    /// "-Slot&lt;n&gt;" chain.</summary>
    public static string SplitBase(string location)
    {
        var m = SlotChainRx.Match(location);
        return m.Success ? m.Groups["base"].Value : location;
    }

    /// <summary>Which ledger lane a base token belongs to.</summary>
    public static string LaneOfBase(string baseToken)
    {
        if (EquipLocations.Contains(baseToken)) return "worn";
        foreach (var (rx, lane) in ContainerLanes)
            if (rx.IsMatch(baseToken)) return lane;
        return "elsewhere";
    }

    // ---- the flattened ledger -------------------------------------------------

    /// <summary>An exaltation row: the socketed copy living inside an item's
    /// exaltation socket ("Wicked Sallet (Exaltation)" under Head-Slot7).</summary>
    public static bool IsExaltation(string name) =>
        name.EndsWith(" (Exaltation)", StringComparison.Ordinal);

    // Worn rows in a dump come in the client's enumeration order (Any Slot,
    // Ear, Head, Face, Ear…) — a player thinks armor head-to-toe, then
    // jewelry, then weapons. This is the display order.
    private static readonly string[] WornOrder =
    {
        // armor
        "Head", "Face", "Chest", "Shoulders", "Arms", "Wrist", "Hands", "Legs", "Feet",
        // jewelry & drapes
        "Ear", "Neck", "Fingers", "Waist", "Back",
        // weapons & held
        "Primary", "Secondary", "Range", "Ammo", "Held",
        // the wildcards last
        "Any Slot",
    };

    /// <summary>Display rank of a worn base token (unknown tokens last).</summary>
    public static int WornRank(string baseToken)
    {
        int i = Array.IndexOf(WornOrder, baseToken);
        return i >= 0 ? i : WornOrder.Length;
    }

    /// <summary>Which display band a worn token sits in — armor(0),
    /// jewelry(1), weapons(2), wildcards(3) — for the thin rules between
    /// them. Unknown tokens ride the last band.</summary>
    public static int WornBand(string baseToken) => WornRank(baseToken) switch
    {
        <= 8 => 0,   // Head … Feet
        <= 13 => 1,  // Ear … Back
        <= 18 => 2,  // Primary … Held
        _ => 3,      // Any Slot + unknown
    };

    /// <summary>The game's slot types, correlated from an in-game item window
    /// (Aldryn, Blade of the Ocean: Ornamentation / Focus / Click / Worn /
    /// Proc Exaltation) against observed dump ladders (1|2, 7, 8, 9, 10):
    /// Slot7 occupants are always focus exaltations, Slot8 held the Golem
    /// Metal Wand clicky, Slot10 holds weapon procs — 9 and 1/2 follow from
    /// the window's order. An unmapped number stays a number.</summary>
    public static (string Short, string Name) SlotType(int n) => n switch
    {
        1 or 2 => ("O", "Ornamentation"),
        7 => ("F", "Focus Exaltation"),
        8 => ("C", "Click Exaltation"),
        9 => ("W", "Worn Exaltation"),
        10 => ("P", "Proc Exaltation"),
        _ => (n.ToString(), $"Slot {n}"),
    };

    /// <summary>A CONTAINER's children are its contents; an ITEM's children
    /// are its sockets. The observable tells: socket occupants are always
    /// "(Exaltation)" rows, and every ordinary item declares 10 child slots
    /// while bags declare their bag size (8, 24…). An all-empty 10-slot bag
    /// (Kavruul's) is indistinguishable and reads as sockets — the dump
    /// does not say.</summary>
    public static bool IsContainer(Entry e)
    {
        if (e.Children.Count == 0) return false;
        if (e.Children.Any(c => !c.Empty && !IsExaltation(c.Name))) return true;
        return e.Slots > 0 && e.Slots != 10;
    }

    /// <summary>Which window tab a ledger row belongs to: socketed
    /// "(Exaltation)" copies get their own tab, everything else — key ring
    /// rows included — is an item. (The Focus effects tab is not row-backed:
    /// it audits the whole dump against the focus-effect families.)</summary>
    public static string TabOf(CarryRow row)
    {
        return IsExaltation(row.Name) ? "exalt" : "items";
    }

    /// <summary>Every non-empty row of every table, in FILE ORDER, plus the
    /// lane chips that actually have rows (a chip that filters to nothing is
    /// a control that can only disappoint).</summary>
    public static (List<CarryRow> Rows, List<(string Id, string Label)> Lanes) CarryAll(Dump dump)
    {
        var rows = new List<CarryRow>();
        void Walk(Entry e, Entry? parent)
        {
            if (!e.Empty && e.Name.Length > 0)
            {
                string lane = e.Section != PrimarySection
                    ? LanePrefix + e.Section
                    : LaneOfBase(e.Base);
                string location = e.Section == PrimarySection
                    ? e.Location
                    : $"{e.Section} / {e.Location}";
                string host = parent is { Empty: false } ? parent.Name : "";
                rows.Add(new CarryRow(e.Name, e.Name.ToLowerInvariant(), location,
                    e.Count > 0 ? e.Count : 1, lane, e.Line, host, IsContainer(e)));
            }
            foreach (var c in e.Children) Walk(c, e);
        }
        foreach (var e in dump.Items) Walk(e, null);

        foreach (var k in dump.KeyRing)
        {
            if (k.Name.Length == 0 || k.Name == "Empty") continue;
            // The keyring is several in-game things: Equipment = "Storage",
            // Activated = "Activated items"; anything else stays generic.
            string lane = k.Category switch
            {
                "Equipment" => "storage",
                "Activated" => "activated",
                _ => "keyring",
            };
            rows.Add(new CarryRow(k.Name, k.Name.ToLowerInvariant(),
                $"{k.Section} / {k.Category}", 1, lane, k.Line));
        }

        rows.Sort((a, b) => a.Line.CompareTo(b.Line));
        return (rows, LanesOf(rows, dump));
    }

    /// <summary>The duplicate finder's set: item keys (tier-stripped, via
    /// <see cref="FocusEffects.ItemKey"/>) that appear as MORE THAN ONE
    /// physical row in the given rows. Two rows = two copies in two places
    /// (or two bag slots) — a single stack is one place and never counts.
    /// "+N" folds into its base, so the old +1 in the bank surfaces next to
    /// the worn +4. Call it per tab: items and socketed exaltations share
    /// names without being the same thing. Containers are furniture, not
    /// clutter — five Backpacks are five bags, so they never count.</summary>
    public static HashSet<string> DuplicateKeys(IEnumerable<CarryRow> rows) => rows
        .Where(r => !r.IsContainer)
        .GroupBy(r => FocusEffects.ItemKey(r.Name), StringComparer.Ordinal)
        .Where(g => g.Count() >= 2)
        .Select(g => g.Key)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>The lane chips a set of rows deserves: fixed lanes first, then
    /// any extra section, only ever lanes that actually have rows.</summary>
    public static List<(string Id, string Label)> LanesOf(IEnumerable<CarryRow> rows, Dump dump)
    {
        var present = rows.Select(r => r.Lane).ToHashSet(StringComparer.Ordinal);
        var lanes = new List<(string, string)>();
        foreach (string id in FixedLaneOrder)
            if (present.Contains(id)) lanes.Add((id, LaneLabels[id]));
        foreach (string section in dump.Sections)
        {
            string id = LanePrefix + section;
            if (present.Contains(id)) lanes.Add((id, section));
        }
        return lanes;
    }

    /// <summary>A dump older than this many days gets a stale warning — gear
    /// changes faster than memory admits.</summary>
    public const int DumpStaleDays = 3;

    /// <summary>Every storage a dump CAN carry, in the in-game ritual's
    /// order: Covered key + display label. Hoard coverage also accepts
    /// <see cref="Dump.HasExtraItemSection"/> (never sampled on old clients).</summary>
    public static readonly (string Key, string Label)[] StorageDefs =
    {
        ("worn", "Worn"),
        ("bags", "Bags"),
        ("bank", "Bank"),
        ("sharedBank", "Shared bank"),
        ("depot", "Depot"),
        ("hoard", "Dragon's Hoard"),
        ("keyring", "Exaltations & storage"),
    };

    /// <summary>The storages this dump does NOT speak for, in the order the
    /// in-game ritual visits them. "Missing" means "the dump does not say" —
    /// the game only writes a storage while its window is open. The Dragon's
    /// Hoard has never been sampled, so ANY extra item table counts as it.</summary>
    public static List<string> MissingStorages(Dump dump)
    {
        var missing = new List<string>();
        if (!dump.Covered.Contains("bank")) missing.Add("bank");
        else if (!dump.Covered.Contains("sharedBank")) missing.Add("shared bank");
        if (!dump.Covered.Contains("depot")) missing.Add("tradeskill depot");
        // The hoard is "Hoard N" rows in the primary table (sampled); an
        // unknown extra item table is still accepted as it, for older clients.
        if (!dump.Covered.Contains("hoard") && !dump.HasExtraItemSection) missing.Add("Dragon's Hoard");
        if (!dump.Covered.Contains("keyring")) missing.Add("exaltations & storage");
        return missing;
    }

    /// <summary>Flat name→count over everything held: every item row counts
    /// (bags and socketed exaltations included), "Equipment" keyring rows
    /// count one each. Keys are the raw name, lowercased.</summary>
    public static Dictionary<string, int> HeldCounts(Dump dump)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string name, int n)
        {
            string key = name.ToLowerInvariant();
            counts[key] = counts.TryGetValue(key, out int cur) ? cur + n : n;
        }
        void Walk(Entry e)
        {
            if (!e.Empty && e.Name.Length > 0) Add(e.Name, e.Count > 0 ? e.Count : 1);
            foreach (var c in e.Children) Walk(c);
        }
        foreach (var e in dump.Items) Walk(e);
        foreach (var k in dump.KeyRing)
            if (HeldKeyRingCategories.Contains(k.Category) && k.Name.Length > 0 && k.Name != "Empty")
                Add(k.Name, 1);
        return counts;
    }

    // ---- discovery ------------------------------------------------------------

    /// <summary>The freshest matching dump in the EQ install root: the exact
    /// "&lt;Char&gt;_&lt;server&gt;-Inventory.txt" wins, then the classic
    /// "&lt;Char&gt;-Inventory.txt", then any "*-Inventory.txt" newest first.</summary>
    public static string? FindDumpFile(string eqRoot, string charName, string server)
    {
        if (eqRoot.Length == 0 || !Directory.Exists(eqRoot)) return null;
        var candidates = Directory.EnumerateFiles(eqRoot, "*.txt")
            .Where(p => Path.GetFileName(p).EndsWith("-Inventory.txt", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path: p, Mtime: File.GetLastWriteTimeUtc(p)))
            .OrderByDescending(c => c.Mtime)
            .ToList();
        if (candidates.Count == 0) return null;

        foreach (string want in PreferredNames(charName, server))
        {
            var hit = candidates.FirstOrDefault(c =>
                Path.GetFileName(c.Path).Equals(want, StringComparison.OrdinalIgnoreCase));
            if (hit.Path is not null) return hit.Path;
        }
        return candidates[0].Path;
    }

    private static IEnumerable<string> PreferredNames(string charName, string server)
    {
        if (charName.Length > 0 && server.Length > 0) yield return $"{charName}_{server}-Inventory.txt";
        if (charName.Length > 0) yield return $"{charName}-Inventory.txt";
    }

    /// <summary>"eqlog_Thorrak_paineel.txt" → ("Thorrak", "paineel").</summary>
    public static (string Name, string Server) ParseLogName(string logPath)
    {
        var m = Regex.Match(Path.GetFileName(logPath), @"^eqlog_(?<name>.+?)_(?<server>.+?)\.txt$",
            RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups["name"].Value, m.Groups["server"].Value) : ("", "");
    }

    /// <summary>The EQ install root for a followed log path (the log lives in
    /// "&lt;root&gt;\Logs"), or "" when the layout does not match.</summary>
    public static string EqRootOf(string logPath)
    {
        try
        {
            var logsDir = Path.GetDirectoryName(logPath);
            if (logsDir is null) return "";
            if (!string.Equals(Path.GetFileName(logsDir), "Logs", StringComparison.OrdinalIgnoreCase))
                return "";
            return Path.GetDirectoryName(logsDir) ?? "";
        }
        catch
        {
            return "";
        }
    }
}
