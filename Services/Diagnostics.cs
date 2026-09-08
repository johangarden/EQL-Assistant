using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The diagnostics bundle (owner request, 8 Sep — the crash on the gaming
/// rig had no log within reach): one zip with the app log, the config, the
/// window placements, an about-file, and the last N minutes of the game log
/// with chat and tells SCRUBBED — combat, spells, loot and NPC speech stay,
/// what people said to each other goes. Companion's feedback slice, ours.
/// </summary>
public static class Diagnostics
{
    private static readonly Regex TimestampRx = new(@"^\[(?<ts>[^\]]+)\]\s?", RegexOptions.Compiled);

    // Player-to-player speech. A player name is one capitalised word; NPCs
    // carry articles or several words ("a rat says", "Klok Lagnoz says",
    // "The Kerran Sha`rr says") and keep their lines — quest text matters.
    private static readonly Regex[] ChatRx =
    {
        new(@"^[A-Za-z`]+ tells (?:you|the group|the raid|the guild|[A-Za-z]+:\d+|[A-Za-z]+), '", RegexOptions.Compiled),
        new(@"^You (?:told|tell) [A-Za-z`:0-9 ]+, '", RegexOptions.Compiled),
        new(@"^You (?:say|shout|auction|say out of character), '", RegexOptions.Compiled),
        new(@"^[A-Z][a-z]+ (?:shouts|auctions|says out of character), '", RegexOptions.Compiled),
        new(@"^[A-Z][a-z]+ says, '", RegexOptions.Compiled),
        new(@"^\[[A-Za-z ]+\] [A-Za-z]+ tells", RegexOptions.Compiled), // "[Guild] Name tells …" variants
    };

    /// <summary>True when the line is chat between people (dropped from the slice).</summary>
    public static bool IsChat(string rawLine)
    {
        string body = TimestampRx.Replace(rawLine, "", 1);
        foreach (var rx in ChatRx)
            if (rx.IsMatch(body)) return true;
        return false;
    }

    /// <summary>The lines from the last <paramref name="minutes"/> before the
    /// newest timestamp in the file (not the wall clock — a log that ended an
    /// hour ago still yields its last N minutes), chat removed. The first
    /// line states how many were removed.</summary>
    public static List<string> Slice(IEnumerable<string> lines, int minutes)
    {
        var stamped = new List<(DateTime At, string Line)>();
        DateTime last = DateTime.MinValue;
        foreach (var line in lines)
        {
            var m = TimestampRx.Match(line);
            if (!m.Success) { if (stamped.Count > 0) stamped.Add((last, line)); continue; }
            if (DateTime.TryParseExact(m.Groups["ts"].Value,
                    new[] { "ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" },
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var t))
            {
                last = t;
                stamped.Add((t, line));
            }
        }
        if (stamped.Count == 0) return new List<string> { "(no timestamped lines)" };
        var cutoff = last.AddMinutes(-minutes);
        int removed = 0;
        var outLines = new List<string>();
        foreach (var (at, line) in stamped)
        {
            if (at < cutoff) continue;
            if (IsChat(line)) { removed++; continue; }
            outLines.Add(line);
        }
        outLines.Insert(0, $"# last {minutes} min before {last:ddd MMM dd HH:mm:ss yyyy} — {outLines.Count} lines kept, {removed} chat lines removed");
        return outLines;
    }

    /// <summary>The newest eqlog in the followed folder, like the tailer picks it.</summary>
    public static string? NewestGameLog(string directory, string pattern)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
            return Directory.EnumerateFiles(directory, string.IsNullOrWhiteSpace(pattern) ? "eqlog_*.txt" : pattern)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>Write the zip. Returns a one-paragraph summary of what went in.</summary>
    public static string BuildBundle(string outZip, ConfigService config, string? gameLogPath, int minutes, string about)
    {
        var parts = new List<string>();
        if (File.Exists(outZip)) File.Delete(outZip);
        using (var zip = ZipFile.Open(outZip, ZipArchiveMode.Create))
        {
            // The app's own log (+ the rotated previous one).
            foreach (var (path, name) in new[] { (Log.Path, "app.log"), (Path.ChangeExtension(Log.Path, ".prev.log"), "app.prev.log") })
                if (File.Exists(path)) { AddFile(zip, path, name); parts.Add(name); }

            // Config + placements: every small json in the config folder.
            try
            {
                foreach (var f in Directory.EnumerateFiles(config.ConfigDirectory, "*.json"))
                {
                    string n = Path.GetFileName(f);
                    if (n.StartsWith("config", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("dialog-", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("window-", StringComparison.OrdinalIgnoreCase))
                    {
                        AddFile(zip, f, "config/" + n);
                    }
                }
                parts.Add("config + window placements");
            }
            catch (Exception ex) { about += $"\nconfig folder unreadable: {ex.Message}"; }

            // The game log slice, scrubbed.
            if (gameLogPath is not null && File.Exists(gameLogPath))
            {
                var slice = Slice(File.ReadLines(gameLogPath), minutes);
                var e = zip.CreateEntry($"game-log-last-{minutes}min.txt", CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                foreach (var l in slice) w.WriteLine(l);
                parts.Add($"last {minutes} min of {Path.GetFileName(gameLogPath)} ({slice.Count - 1} lines, chat removed)");
            }
            else parts.Add("no game log found");

            var ab = zip.CreateEntry("about.txt", CompressionLevel.Optimal);
            using (var w = new StreamWriter(ab.Open(), new UTF8Encoding(false))) w.Write(about);
        }
        return string.Join(" · ", parts);
    }

    private static void AddFile(ZipArchive zip, string path, string entryName)
    {
        // Copy through a shared-read stream: the app log is open for append.
        var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = e.Open();
        src.CopyTo(dst);
    }

    /// <summary>The about-file: what a reader needs to place the bundle.</summary>
    public static string About(ConfigService config, Models.AppConfig cfg, string? gameLogPath, string extra)
    {
        var sb = new StringBuilder();
        var asm = Assembly.GetExecutingAssembly();
        sb.AppendLine($"EQL Assistant {asm.GetName().Version} — diagnostics bundle written {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"exe: {Environment.ProcessPath}");
        sb.AppendLine($"os: {Environment.OSVersion} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")} · machine {Environment.MachineName}");
        sb.AppendLine($"culture: {System.Globalization.CultureInfo.CurrentCulture.Name} · uptime {Environment.TickCount64 / 3600000.0:0.0} h");
        sb.AppendLine($"config dir: {config.ConfigDirectory}");
        sb.AppendLine($"log dir: {cfg.Log.Directory} · pattern {cfg.Log.FilePattern} · poll {cfg.Log.PollIntervalMs} ms");
        sb.AppendLine($"game log: {gameLogPath ?? "(none)"}");
        sb.AppendLine($"loadout: {cfg.ActiveLoadout} · {cfg.Triggers.Count} triggers");
        sb.AppendLine($"overlay: opacity {cfg.Overlay.Opacity} · locked {cfg.Overlay.Locked} · hideWhenGameAway {cfg.Overlay.HideWhenGameAway} · cursorRing {cfg.Overlay.CursorRingVisible} · voice '{cfg.Overlay.VoiceName}' · muted {cfg.Overlay.Muted}");
        if (extra.Length > 0) sb.AppendLine(extra);
        return sb.ToString();
    }
}
