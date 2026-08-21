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

    private double _seconds; // 0 = auto: the learner's estimate stands in
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

    /// <summary>Friendly face of <see cref="Seconds"/>: shows "6m40s" (or
    /// "auto" when unset), accepts "400", "15m", "6m40s", "6:40" — and
    /// "auto" or empty to hand the number back to the learner.</summary>
    public string SecondsText
    {
        get => Seconds > 0 ? Services.DurationText.Compact(Seconds) : "auto";
        set
        {
            string t = value.Trim();
            if (t.Length == 0 || t.Equals("auto", StringComparison.OrdinalIgnoreCase))
                Seconds = 0;
            else if (Services.DurationText.Parse(t) is double s) Seconds = s;
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

    public string Display => $"{(Enabled ? "" : "○ ")}{Name}  ·  " + (Seconds > 0
        ? Services.DurationText.Compact(Seconds)
        : LearnedSeconds is { } l ? $"≤ {Services.DurationText.Compact(l)} auto" : "auto");

    // ---- the learner's evidence (death → next-appearance gaps) ----

    private bool _learnOn = true;
    public bool LearnOn { get => _learnOn; set => SetField(ref _learnOn, value); }

    /// <summary>Observed gaps, newest first — carried through so a Manager
    /// save never throws away what the learner measured.</summary>
    public List<RespawnGap> Gaps { get; set; } = new();

    public double? LearnedSeconds => Gaps.Count > 0 ? Gaps.Min(g => g.Seconds) : null;

    public bool HasGaps => Gaps.Count > 0;

    /// <summary>The evidence list: every gap is an upper bound ("≤"), and the
    /// minimum of them is the learned estimate.</summary>
    public string GapsText => string.Join("\n", Gaps.Select(g =>
        $"≤ {Services.DurationText.Compact(g.Seconds)}   ·   {g.When:d MMM HH:mm}"));

    public void ClearGaps()
    {
        Gaps.Clear();
        OnPropertyChanged(nameof(GapsText));
        OnPropertyChanged(nameof(HasGaps));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(SecondsText));
    }

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
        LearnOn = e.Learn,
        Gaps = e.Gaps.Select(g => new RespawnGap { Seconds = g.Seconds, When = g.When }).ToList(),
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
        Learn = LearnOn,
        Gaps = Gaps.Select(g => new RespawnGap { Seconds = g.Seconds, When = g.When }).ToList(),
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
