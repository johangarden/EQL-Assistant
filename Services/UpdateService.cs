using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace EQLOverlay.Services;

/// <summary>One published release, as read from the GitHub API.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string AssetName,
    string AssetUrl, long AssetSize, string PageUrl);

/// <summary>
/// Self-update via GitHub Releases. The running exe can't overwrite itself, so
/// the handoff trick: download the new exe to %TEMP%, start it with
/// --finish-update &lt;targetPath&gt; &lt;pid&gt;, and exit. The temp copy waits for this
/// process to die, plain-overwrites the original, relaunches it, and the next
/// normal start deletes stale temp updaters.
/// </summary>
public static class UpdateService
{
    public const string Repo = "johangarden/EQL-Assistant";
    private const string ApiLatest = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string TempPrefix = "EQLAssistant-update-";

    /// <summary>Running version, normalized to 4 fields so tag comparisons are exact.</summary>
    public static Version CurrentVersion { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    public static async Task<ReleaseInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        using var http = NewClient();
        string json = await http.GetStringAsync(ApiLatest, ct);
        return ParseRelease(json);
    }

    /// <summary>Latest-release JSON → the first .exe asset (null if none/unparsable).</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!TryParseVersion(root.TryGetProperty("tag_name", out var tn) ? tn.GetString() ?? "" : "",
                out var version))
            return null;
        string tag = tn.GetString()!;
        string page = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            return new ReleaseInfo(version, tag, name,
                a.GetProperty("browser_download_url").GetString() ?? "",
                a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                page);
        }
        return null;
    }

    /// <summary>"v2.4.0", "2.4" … → a normalized Version.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        tag = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var v) || tag.Length == 0) return false;
        version = Normalize(v);
        return true;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool IsNewer(Version latest) => latest > CurrentVersion;

    /// <summary>Download the release exe to %TEMP% with progress; size-checked.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo rel,
        IProgress<double>? progress, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{TempPrefix}{rel.Tag}.exe");
        using var http = NewClient();
        using var resp = await http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (rel.AssetSize > 0) progress?.Report((double)done / rel.AssetSize);
            }
        }

        long size = new FileInfo(path).Length;
        if (rel.AssetSize > 0 && size != rel.AssetSize)
            throw new InvalidOperationException(
                $"Download incomplete ({size:N0} of {rel.AssetSize:N0} bytes).");
        return path;
    }

    /// <summary>Start the downloaded exe as the update finisher, then the caller exits.</summary>
    public static void LaunchFinisher(string tempExe, string targetPath)
    {
        var psi = new ProcessStartInfo(tempExe) { UseShellExecute = false };
        psi.ArgumentList.Add("--finish-update");
        psi.ArgumentList.Add(targetPath);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>Runs INSIDE the downloaded temp exe: wait for the old app to exit,
    /// overwrite it, relaunch it. Returns an error message, or null on success.</summary>
    public static string? FinishUpdate(string targetPath, string pidText)
    {
        try
        {
            if (int.TryParse(pidText, out int pid))
            {
                try { Process.GetProcessById(pid).WaitForExit(20_000); }
                catch { /* already exited */ }
            }

            string? err = CopyWithRetry(Environment.ProcessPath!, targetPath);
            if (err is not null) return err;

            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Overwrite-copy with retries (~10s) while file locks/AV settle.</summary>
    public static string? CopyWithRetry(string source, string target)
    {
        Exception? last = null;
        for (int i = 0; i < 40; i++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return null;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(250);
            }
        }
        return last!.Message;
    }

    /// <summary>Delete stale downloaded updaters (a just-ran one is still locked —
    /// the run after next gets it).</summary>
    public static void CleanupTempUpdaters()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), TempPrefix + "*.exe"))
                try { File.Delete(f); } catch { /* still running */ }
        }
        catch { /* best-effort */ }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("EQL-Assistant", CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
