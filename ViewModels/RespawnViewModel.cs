using EQLOverlay.Models;

namespace EQLOverlay.ViewModels;

/// <summary>Editable wrapper around a <see cref="RespawnEntry"/> for the Manager's repop page.</summary>
public sealed class RespawnViewModel : ViewModelBase
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) OnPropertyChanged(nameof(Display)); }
    }

    private double _seconds = 400;
    public double Seconds
    {
        get => _seconds;
        set
        {
            if (!SetField(ref _seconds, value)) return;
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(SecondsText));
        }
    }

    /// <summary>Friendly face of <see cref="Seconds"/>: shows "6m40s",
    /// accepts "400", "15m", "6m40s" or "6:40". Invalid input snaps back.</summary>
    public string SecondsText
    {
        get => Services.DurationText.Compact(Seconds);
        set
        {
            if (Services.DurationText.Parse(value) is double s) Seconds = s;
            OnPropertyChanged(nameof(SecondsText)); // normalize ("400" -> "6m40s") or snap back
        }
    }

    private string _zone = "";
    public string Zone
    {
        get => _zone;
        set { if (SetField(ref _zone, value)) OnPropertyChanged(nameof(ZoneGroup)); }
    }

    /// <summary>Group header for the Manager list (empty zones bucket together).</summary>
    public string ZoneGroup => string.IsNullOrWhiteSpace(Zone) ? "No zone set" : Zone.Trim();

    private string _pattern = "";
    public string Pattern { get => _pattern; set => SetField(ref _pattern, value); }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { if (SetField(ref _enabled, value)) OnPropertyChanged(nameof(Display)); }
    }

    public string Display => $"{(Enabled ? "" : "○ ")}{Name}  ·  {Seconds:0}s";

    // ---- the two alert notices (mirrors the trigger editor's model) ----

    private bool _warnOn;
    public bool WarnOn { get => _warnOn; set => SetField(ref _warnOn, value); }

    private double _warnSeconds = 15;
    public double WarnSeconds { get => _warnSeconds; set => SetField(ref _warnSeconds, value); }

    private string _warnMode = "speak";
    public string WarnMode { get => _warnMode; set => SetField(ref _warnMode, value); }

    private string _warnSpeak = "";
    public string WarnSpeak { get => _warnSpeak; set => SetField(ref _warnSpeak, value); }

    private string _warnSound = "";
    public string WarnSound { get => _warnSound; set => SetField(ref _warnSound, value); }

    private bool _spawnOn = true;
    public bool SpawnOn { get => _spawnOn; set => SetField(ref _spawnOn, value); }

    private string _spawnMode = "speak";
    public string SpawnMode { get => _spawnMode; set => SetField(ref _spawnMode, value); }

    private string _spawnSpeak = "";
    public string SpawnSpeak { get => _spawnSpeak; set => SetField(ref _spawnSpeak, value); }

    private string _spawnSound = "";
    public string SpawnSound { get => _spawnSound; set => SetField(ref _spawnSound, value); }

    public static RespawnViewModel FromEntry(RespawnEntry e) => new()
    {
        Name = e.Name,
        Zone = e.Zone,
        Seconds = e.Seconds,
        Pattern = e.Pattern,
        Enabled = e.Enabled,
        WarnOn = e.WarnEnabled,
        WarnSeconds = e.WarnSeconds,
        WarnMode = e.WarnMode,
        WarnSpeak = e.WarnSpeak,
        WarnSound = e.WarnSound,
        SpawnOn = e.SpawnEnabled,
        SpawnMode = e.SpawnMode,
        SpawnSpeak = e.SpawnSpeak,
        SpawnSound = e.SpawnSound,
    };

    public RespawnEntry ToEntry() => new()
    {
        Name = Name.Trim(),
        Zone = Zone.Trim(),
        Seconds = Seconds,
        Pattern = Pattern.Trim(),
        Enabled = Enabled,
        WarnEnabled = WarnOn,
        WarnSeconds = WarnSeconds,
        WarnMode = WarnMode,
        WarnSpeak = WarnSpeak.Trim(),
        WarnSound = WarnSound,
        SpawnEnabled = SpawnOn,
        SpawnMode = SpawnMode,
        SpawnSpeak = SpawnSpeak.Trim(),
        SpawnSound = SpawnSound,
    };
}
