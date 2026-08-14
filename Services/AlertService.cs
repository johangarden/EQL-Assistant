using System.IO;
using System.Runtime.InteropServices;

namespace EQLOverlay.Services;

/// <summary>
/// Plays alert sounds and speaks phrases. Uses the built-in Windows SAPI voice
/// (via COM, no NuGet dependency) and winmm for .wav playback.
/// </summary>
public sealed class AlertService
{
    private readonly dynamic? _voice;

    public bool Muted { get; set; }

    public AlertService()
    {
        try
        {
            var t = Type.GetTypeFromProgID("SAPI.SpVoice");
            _voice = t is null ? null : Activator.CreateInstance(t);
        }
        catch
        {
            _voice = null; // no TTS available; sound-only still works
        }
        if (_voice is null) Log.Warn("TTS unavailable: SAPI.SpVoice could not be created.");
    }

    /// <summary>Fire an alert: play the sound (if any) and speak the phrase (if any).</summary>
    public void Fire(string? speak, string? sound)
    {
        if (Muted) return;
        if (!string.IsNullOrWhiteSpace(sound)) PlayFile(sound!);
        if (!string.IsNullOrWhiteSpace(speak)) Speak(speak!);
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (Muted) { Log.Info($"Speak suppressed (muted): '{text}'"); return; }
        if (_voice is null) { Log.Warn($"Speak skipped (no TTS voice): '{text}'"); return; }
        try
        {
            _voice.Speak(text, 1u /* SVSFlagsAsync */);
            Log.Info($"Speak: '{text}'");
        }
        catch (Exception ex)
        {
            Log.Error($"Speak failed: '{text}'", ex);
        }
    }

    private void PlayFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            PlaySound(path, nint.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }
        catch { /* ignore playback errors */ }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, nint hmod, uint fdwSound);

    private const uint SND_ASYNC     = 0x0001;
    private const uint SND_NODEFAULT  = 0x0002;
    private const uint SND_FILENAME  = 0x00020000;
}
