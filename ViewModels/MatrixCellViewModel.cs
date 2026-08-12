namespace EQLOverlay.ViewModels;

/// <summary>
/// One persistent cell in a present/missing matrix (self-buffs or target-debuffs).
/// Unlike a bar, it always exists; it just toggles between active (green + seconds
/// left) and missing (red).
/// </summary>
public sealed class MatrixCellViewModel : ViewModelBase
{
    public MatrixCellViewModel(string key, string name, double durationSeconds,
        double alertAtSeconds, bool alertOnExpire, string? alertSpeak, string? alertSound,
        string? alertFadedSpeak = null, string? alertFadedSound = null)
    {
        Key = key;
        Name = name;
        DurationSeconds = durationSeconds <= 0 ? 1 : durationSeconds;
        AlertAtSeconds = alertAtSeconds;
        AlertOnExpire = alertOnExpire;
        AlertSpeak = alertSpeak;
        AlertSound = alertSound;
        AlertFadedSpeak = alertFadedSpeak;
        AlertFadedSound = alertFadedSound;
    }

    public string Key { get; }
    public string Name { get; }
    public double DurationSeconds { get; }

    // Speak/Sound = the pre-fade payload; the Faded pair plays at expiry.
    public double AlertAtSeconds { get; }
    public bool AlertOnExpire { get; }
    public string? AlertSpeak { get; }
    public string? AlertSound { get; }
    public string? AlertFadedSpeak { get; }
    public string? AlertFadedSound { get; }

    public bool FadeAlertFired { get; set; }

    private DateTime _endTimeLocal;

    /// <summary>When the buff runs out (used to carry state across a config reload).</summary>
    public DateTime EndTimeLocal => _endTimeLocal;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
    }

    private double _remainingSeconds;
    public double RemainingSeconds
    {
        get => _remainingSeconds;
        private set => SetField(ref _remainingSeconds, value);
    }

    private string _remainingText = "";
    public string RemainingText
    {
        get => _remainingText;
        private set => SetField(ref _remainingText, value);
    }

    private bool _isWarning;
    public bool IsWarning
    {
        get => _isWarning;
        private set => SetField(ref _isWarning, value);
    }

    public void Activate(DateTime endTimeLocal)
    {
        _endTimeLocal = endTimeLocal;
        IsActive = true;
        FadeAlertFired = false;
    }

    public void Deactivate()
    {
        IsActive = false;
        RemainingSeconds = 0;
        RemainingText = "";
        IsWarning = false;
    }

    /// <summary>Recompute display. Returns true if it just expired this tick.</summary>
    public bool Refresh(DateTime now, double warnSeconds)
    {
        if (!IsActive) return false;

        double remaining = (_endTimeLocal - now).TotalSeconds;
        if (remaining <= 0)
        {
            Deactivate();
            return true;
        }

        RemainingSeconds = remaining;
        RemainingText = Format(remaining);
        IsWarning = remaining <= warnSeconds;
        return false;
    }

    private static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}:{ts.Seconds:00}";
        return $"{ts.Seconds}s";
    }
}
