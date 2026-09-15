using System.IO;

namespace EQLOverlay.Services;

/// <summary>
/// Replays whole log files through a FRESH loot ledger and Sky tracker on
/// scratch files — what the log alone proves — so the live, persisted quest
/// ledger can be checked against it (owner, 15 Sep: three quests read
/// "ready" on items the log showed handed in; the saved ledger had drifted
/// and a plain Reparse, which dedupes and never lowers a count, could not
/// undo it). Shared by the Data page's "Audit quest ledger" button and the
/// <c>--sky-audit</c> switch.
/// </summary>
public static class SkyAudit
{
    public sealed record Result(SkyQuests Sky, LootTracker Loot, int Lines, IReadOnlyList<string> Files, string Report);

    public static string ReportPath => Path.Combine(Path.GetTempPath(), "eql_sky_audit.txt");

    /// <summary>Replay <paramref name="paths"/> in order. <paramref name="yield"/>
    /// runs every 2000 lines (the UI passes a dispatcher yield so the progress
    /// card paints; the CLI passes null).</summary>
    public static async Task<Result> ReplayAsync(ConfigService cs, IReadOnlyList<string> paths, string filter,
        IProgress<ReparseProgress>? progress, Func<Task>? yield)
    {
        string lootPath = Path.Combine(Path.GetTempPath(), "eql_audit_loot.json");
        string skyPath = Path.Combine(Path.GetTempPath(), "eql_audit_sky.json");
        try { File.Delete(lootPath); } catch { /* fresh */ }
        try { File.Delete(skyPath); } catch { /* fresh */ }
        var loot = new LootTracker(cs, lootPath);
        var sky = new SkyQuests(cs, loot, skyPath);

        var names = sky.Quests.SelectMany(q => q.Items).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var lines = names.ToDictionary(n => n, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        int total = 0;
        for (int f = 0; f < paths.Count; f++)
        {
            string path = paths[f];
            if (!File.Exists(path)) continue;
            string name = Path.GetFileName(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, true, 1 << 16);
            progress?.Report(new ReparseProgress(name, f + 1, paths.Count, 0, size, 0, Verb: "Auditing"));
            int n = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                total++;
                if (++n % 2000 == 0)
                {
                    progress?.Report(new ReparseProgress(name, f + 1, paths.Count, fs.Position, size, n, Verb: "Auditing"));
                    if (yield is not null) await yield();
                }
                loot.ProcessLine(line);
                sky.ProcessLine(line);
                if (line.Contains("You have looted", StringComparison.Ordinal) || line.Contains("You offered", StringComparison.Ordinal)
                    || line.Contains("successfully destroyed", StringComparison.Ordinal) || line.Contains("You looted", StringComparison.Ordinal))
                    foreach (var item in names)
                        if (line.Contains(item, StringComparison.OrdinalIgnoreCase)) lines[item].Add(line);
            }
            progress?.Report(new ReparseProgress(name, f + 1, paths.Count, size, size, n, Done: true, Verb: "Auditing"));
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine($"sky audit: {string.Join(" + ", paths.Select(Path.GetFileName))} — {total:N0} lines; {names.Count} quest items{(filter.Length > 0 ? $" matching '{filter}'" : "")}");
        report.AppendLine($"completed quests: {sky.Quests.Count(sky.IsCompleted)} of {sky.Quests.Count}");
        foreach (var item in names)
        {
            var a = sky.Audit(item);
            if (filter.Length == 0 && a.Looted == 0 && a.Offered == 0) continue;
            report.AppendLine();
            report.AppendLine($"{item}: looted {a.Looted} · offered {a.Offered} · destroyed {a.Destroyed} → held {a.Held}"
                + $"  [{string.Join("; ", sky.Quests.Where(q => q.Items.Any(i => i.Name.Equals(item, StringComparison.OrdinalIgnoreCase))).Select(q => q.Name + (sky.IsCompleted(q) ? " ✓" : "")))}]");
            foreach (var l in lines[item]) report.AppendLine("    " + l);
        }
        return new Result(sky, loot, total, paths, report.ToString());
    }
}
