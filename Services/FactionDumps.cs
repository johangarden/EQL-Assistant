using System.IO;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The two game dumps behind race unlocks (owner, 22 Sep): <c>/outputfile
/// faction</c> → <c>&lt;char&gt;_&lt;server&gt;-&lt;CLASS&gt;-Factions.txt</c>
/// (ID · Name · StandingValue · PointsToMax, tab-separated) and
/// <c>/outputfile achievements</c> → <c>&lt;char&gt;_&lt;server&gt;-Achievements.txt</c>
/// (sections like "Untapped Potential: Races"; C/I + one tab = an
/// achievement, C/I + two tabs = one of its conditions). Parsers and file
/// finders only — <see cref="RaceBook"/> puts them together.
/// </summary>
public static class FactionDumps
{
    /// <summary>One faction's standing; Max = Value + ToMax (2,000 for the race factions).</summary>
    public sealed record Standing(string Name, int Value, int ToMax)
    {
        public int Max => Value + ToMax;
    }

    public sealed record RaceReq(string Faction, bool Done);

    /// <summary>One race's unlock recipe as the achievement lists it.</summary>
    public sealed class Race
    {
        public string Name { get; set; } = "";
        public bool Done { get; set; }
        public List<RaceReq> Factions { get; } = new();
        /// <summary>Kerran: a task instead of factions.</summary>
        public string? Task { get; set; }
        public bool TaskDone { get; set; }
        /// <summary>Half Elf: "autocomplete when you unlock Human or Wood Elf".</summary>
        public string? DependsOn { get; set; }
        /// <summary>The "created as" line is Complete — this is the race you are.</summary>
        public bool BornAs { get; set; }
    }

    public static Dictionary<string, Standing> ParseFactions(string text)
    {
        var map = new Dictionary<string, Standing>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var cols = raw.TrimEnd('\r').Split('\t');
            if (cols.Length < 4 || !int.TryParse(cols[2], out int value) || !int.TryParse(cols[3], out int toMax)) continue;
            string name = cols[1].Trim();
            if (name.Length == 0) continue;
            map[name] = new Standing(name, value, toMax);
        }
        return map;
    }

    private static readonly Regex TopRx = new(@"^([CI])\t([^\t].*)$", RegexOptions.Compiled);
    private static readonly Regex SubRx = new(@"^([CI])\t\t(.*)$", RegexOptions.Compiled);
    private static readonly Regex FactionRx = new(@"^Get maximum faction with (.+?)\.?$", RegexOptions.Compiled);
    private static readonly Regex TaskRx = new(@"^Complete the '(.+?)' Task\.?$", RegexOptions.Compiled);
    private static readonly Regex BornRx = new(@"autocomplete if your character was created as an? (.+?)\.?$", RegexOptions.Compiled);
    private static readonly Regex DependsRx = new(@"autocomplete when you unlock (.+?) as a race", RegexOptions.Compiled);

    /// <summary>The "Untapped Potential: Races" section → one <see cref="Race"/> per "Race Unlock - X".</summary>
    public static List<Race> ParseRaces(string achievementsText) => ParseUnlocks(achievementsText, "Untapped Potential: Races", "Race Unlock - ");

    /// <summary>The same shape for "Untapped Potential: Classes" (a Classes tab later).</summary>
    public static List<Race> ParseClasses(string achievementsText) => ParseUnlocks(achievementsText, "Untapped Potential: Classes", "Class Unlock - ");

    private static List<Race> ParseUnlocks(string text, string section, string prefix)
    {
        var list = new List<Race>();
        bool inSection = false;
        Race? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var top = TopRx.Match(line);
            var sub = SubRx.Match(line);
            if (!top.Success && !sub.Success)
            {
                // a section header
                if (inSection) break;
                inSection = line.Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            if (top.Success)
            {
                string name = top.Groups[2].Value.Trim();
                cur = null;
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                cur = new Race { Name = name[prefix.Length..].Trim(), Done = top.Groups[1].Value == "C" };
                list.Add(cur);
                continue;
            }
            if (cur is null) continue;
            bool done = sub.Groups[1].Value == "C";
            string body = sub.Groups[2].Value.Trim();
            Match m;
            if ((m = FactionRx.Match(body)).Success) cur.Factions.Add(new RaceReq(m.Groups[1].Value.Trim(), done));
            else if ((m = TaskRx.Match(body)).Success) { cur.Task = m.Groups[1].Value; cur.TaskDone = done; }
            else if ((m = BornRx.Match(body)).Success) { if (done) cur.BornAs = true; }
            else if ((m = DependsRx.Match(body)).Success) cur.DependsOn = m.Groups[1].Value.Trim();
        }
        return list;
    }

    /// <summary>Newest "&lt;char&gt;_&lt;server&gt;-*-Factions.txt" (the class rides in the
    /// name), falling back to any factions dump in the folder.</summary>
    public static string? FindFactionsFile(string eqRoot, string charName, string server) =>
        FindNewest(eqRoot, p => p.EndsWith("-Factions.txt", StringComparison.OrdinalIgnoreCase), charName, server);

    public static string? FindAchievementsFile(string eqRoot, string charName, string server) =>
        FindNewest(eqRoot, p => p.EndsWith("-Achievements.txt", StringComparison.OrdinalIgnoreCase), charName, server);

    private static string? FindNewest(string eqRoot, Func<string, bool> suffix, string charName, string server)
    {
        if (eqRoot.Length == 0 || !Directory.Exists(eqRoot)) return null;
        var candidates = Directory.EnumerateFiles(eqRoot, "*.txt")
            .Where(p => suffix(Path.GetFileName(p)))
            .Select(p => (Path: p, Mtime: File.GetLastWriteTimeUtc(p)))
            .OrderByDescending(c => c.Mtime)
            .ToList();
        if (candidates.Count == 0) return null;
        string prefix = charName.Length > 0 && server.Length > 0 ? $"{charName}_{server}-" : charName.Length > 0 ? charName + "_" : "";
        var mine = prefix.Length > 0
            ? candidates.FirstOrDefault(c => Path.GetFileName(c.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            : default;
        return mine.Path ?? candidates[0].Path;
    }
}
