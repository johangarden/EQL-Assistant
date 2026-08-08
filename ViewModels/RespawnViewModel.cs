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

    public static RespawnViewModel FromEntry(RespawnEntry e) => new()
    {
        Name = e.Name,
        Zone = e.Zone,
        Seconds = e.Seconds,
        Pattern = e.Pattern,
        Enabled = e.Enabled,
    };

    public RespawnEntry ToEntry() => new()
    {
        Name = Name.Trim(),
        Zone = Zone.Trim(),
        Seconds = Seconds,
        Pattern = Pattern.Trim(),
        Enabled = Enabled,
    };
}
