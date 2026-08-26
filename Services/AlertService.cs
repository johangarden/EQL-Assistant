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
    private readonly dynamic? _defaultToken; // the system voice, for "(default)"

    public bool Muted { get; set; }

    public AlertService()
    {
        try
        {
            var t = Type.GetTypeFromProgID("SAPI.SpVoice");
            _voice = t is null ? null : Activator.CreateInstance(t);
            if (_voice is not null) _defaultToken = _voice.Voice;
        }
        catch
        {
            _voice = null; // no TTS available; sound-only still works
        }
        if (_voice is null) Log.Warn("TTS unavailable: SAPI.SpVoice could not be created.");
    }

    /// <summary>Installed SAPI voice descriptions ("Microsoft Zira Desktop…").
    /// Voices from bridges like NaturalVoiceSAPIAdapter appear here too.</summary>
    public List<string> InstalledVoices()
    {
        var names = new List<string>();
        if (_voice is null) return names;
        try
        {
            var tokens = _voice.GetVoices();
            for (int i = 0; i < (int)tokens.Count; i++)
                names.Add((string)tokens.Item(i).GetDescription());
        }
        catch (Exception ex)
        {
            Log.Warn("Voice enumeration failed: " + ex.Message);
        }
        return names;
    }

    /// <summary>Pick the speaking voice by its description (empty = the system
    /// default) and the SAPI rate (-10 slow … 10 fast; 0 = normal).</summary>
    public void ApplyVoice(string? name, int rate)
    {
        if (_voice is null) return;
        try
        {
            _voice.Rate = Math.Clamp(rate, -10, 10);
            if (string.IsNullOrWhiteSpace(name))
            {
                if (_defaultToken is not null) _voice.Voice = _defaultToken;
                return;
            }
            var tokens = _voice.GetVoices();
            for (int i = 0; i < (int)tokens.Count; i++)
            {
                if (string.Equals((string)tokens.Item(i).GetDescription(), name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _voice.Voice = tokens.Item(i);
                    return;
                }
            }
            // An uninstalled name (other machine's config) keeps the default.
        }
        catch (Exception ex)
        {
            Log.Warn("ApplyVoice failed: " + ex.Message);
        }
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
