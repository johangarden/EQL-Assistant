using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Win32;

namespace EQLOverlay.Services;

/// <summary>
/// Offline natural voices for the NaturalVoiceSAPIAdapter (owner report,
/// 9 Sep: "I downloaded the voice but it still said Online"). Windows 11's
/// own Narrator voice packs no longer work with the adapter (its changelog:
/// the packs' decryption key changed, v0.2.4 stopped loading them), so the
/// adapter reads unzipped voice packs from the folder named in
/// HKCU\Software\NaturalVoiceSAPIAdapter\Enumerator\NarratorVoicePath. The
/// maintainer's wiki lists the last working packs with direct links; this
/// downloads one, unzips it under the app's config folder, and points that
/// value at it — no admin. The adapter enumerates at process start, so the
/// app must be restarted before the voice appears (without "Online").
/// </summary>
public static class VoicePacks
{
    public sealed record Pack(string Id, string Label, string Language, string Url, string Folder, int ApproxMb);

    /// <summary>From the adapter wiki "Narrator natural voice download links" (9 Sep 2026).</summary>
    public static readonly IReadOnlyList<Pack> Catalog = new[]
    {
        new Pack("sonia-gb", "Sonia — English (United Kingdom), female", "en-GB",
            "https://dl.nvdacn.com/NVDA-Addons/TTS/NaturalVoices/MicrosoftWindows.Voice.en-GB.Sonia.1_1.0.3.0_x64__cw5n1h2txyewy.Msix",
            "MicrosoftWindows.Voice.en-GB.Sonia.1", 21),
        new Pack("jenny-us", "Jenny — English (United States), female", "en-US",
            "https://dl.nvdacn.com/NVDA-Addons/TTS/NaturalVoices/MicrosoftWindows.Voice.en-US.Jenny.1_1.0.8.0_x64__cw5n1h2txyewy.Msix",
            "MicrosoftWindows.Voice.en-US.Jenny.1", 12),
        new Pack("aria-us", "Aria — English (United States), female", "en-US",
            "https://dl.nvdacn.com/NVDA-Addons/TTS/NaturalVoices/MicrosoftWindows.Voice.en-US.Aria.1_1.0.8.0_x64__cw5n1h2txyewy.Msix",
            "MicrosoftWindows.Voice.en-US.Aria.1", 20),
        new Pack("guy-us", "Guy — English (United States), male", "en-US",
            "https://dl.nvdacn.com/NVDA-Addons/TTS/NaturalVoices/MicrosoftWindows.Voice.en-US.Guy.1_1.0.5.0_x64__cw5n1h2txyewy.Msix",
            "MicrosoftWindows.Voice.en-US.Guy.1", 20),
    };

    /// <summary>Overridable for the selftest — never touch the adapter's real key from a test.</summary>
    public static string EnumeratorKey { get; set; } = @"Software\NaturalVoiceSAPIAdapter\Enumerator";

    /// <summary>Where packs live: one sub folder per pack, nothing else (the adapter's rule).</summary>
    public static string Root(ConfigService config) => Path.Combine(config.ConfigDirectory, "voice-packs");

    /// <summary>Packs whose folder is present under the root.</summary>
    public static IReadOnlyList<Pack> Installed(ConfigService config)
    {
        string root = Root(config);
        return Catalog.Where(p => Directory.Exists(Path.Combine(root, p.Folder))
                                  && Directory.EnumerateFileSystemEntries(Path.Combine(root, p.Folder)).Any()).ToList();
    }

    /// <summary>True when the adapter has ever been installed (its user key exists).</summary>
    public static bool AdapterPresent()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\NaturalVoiceSAPIAdapter"); return k is not null; }
        catch { return false; }
    }

    /// <summary>The folder the adapter currently reads packs from ("" = its default).</summary>
    public static string CurrentPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(EnumeratorKey);
            return k?.GetValue("NarratorVoicePath") as string ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Point the adapter at <paramref name="root"/> and make sure local voices are on.</summary>
    public static void PointAdapterAt(string root)
    {
        using var k = Registry.CurrentUser.CreateSubKey(EnumeratorKey, writable: true)
            ?? throw new InvalidOperationException("Couldn't open the adapter's registry key.");
        k.SetValue("NarratorVoicePath", root, RegistryValueKind.String);
        k.SetValue("NoNarratorVoices", 0, RegistryValueKind.DWord);
    }

    /// <summary>Download, unzip and register one pack. <paramref name="progress"/>
    /// gets human lines ("Downloading … 42 MB"). Returns the pack's folder.</summary>
    public static async Task<string> InstallAsync(ConfigService config, Pack pack, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string root = Root(config);
        string target = Path.Combine(root, pack.Folder);
        Directory.CreateDirectory(root);
        string tmp = Path.Combine(root, pack.Folder + ".download");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EQL-Assistant");
            using var resp = await http.GetAsync(pack.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buf = new byte[1 << 16];
            long done = 0; int n; var lastReport = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if ((DateTime.Now - lastReport).TotalMilliseconds > 400)
                {
                    lastReport = DateTime.Now;
                    progress?.Report(total > 0
                        ? $"Downloading {pack.Label.Split(' ')[0]}… {done / 1048576.0:0} of {total / 1048576.0:0} MB"
                        : $"Downloading {pack.Label.Split(' ')[0]}… {done / 1048576.0:0} MB");
                }
            }
        }

        progress?.Report("Unpacking…");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Extract(tmp, target);
        try { File.Delete(tmp); } catch { /* tidy-up only */ }

        progress?.Report("Registering with the voice adapter…");
        PointAdapterAt(root);
        Log.Info($"Voice pack installed: {pack.Id} -> {target}; adapter NarratorVoicePath = {root}");
        return target;
    }

    /// <summary>An MSIX is a zip: unzip it whole into the pack folder.</summary>
    public static void Extract(string msixPath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        ZipFile.ExtractToDirectory(msixPath, targetDir, overwriteFiles: true);
    }

    /// <summary>The adapter names a local pack without "Online": what the picker will show.</summary>
    public static string ExpectedVoiceName(Pack p) =>
        $"Microsoft {p.Label.Split(' ')[0]} (Natural) - {(p.Language == "en-GB" ? "English (United Kingdom)" : "English (United States)")}";
}
