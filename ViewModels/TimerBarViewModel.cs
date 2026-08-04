using System.Windows.Media;

namespace EQLOverlay.ViewModels;

/// <summary>
/// A single live bar. Either a countdown (buff/HoT/DoT/cooldown) or a static
/// "missing buff" reminder. Holds everything the XAML binds to plus the alert
/// settings the engine reads when deciding to fire audio.
/// </summary>
public sealed class TimerBarViewModel : ViewModelBase
{
    private TimerBarViewModel(string key, string name, string category, Brush fill)
    {
        Key = key;
        Name = name;
        Category = category;
        Fill = fill;
        Fill.Freeze();
    }

    /// <summary>A normal countdown bar.</summary>
    public static TimerBarViewModel CreateTimer(
        string key, string name, string category, double totalSeconds,
        DateTime endTimeLocal, Brush fill,
        double alertAtSeconds, bool alertOnExpire, string? alertSpeak, string? alertSound)
    {
        var vm = new TimerBarViewModel(key, name, category, fill)
        {
            TotalSeconds = totalSeconds <= 0 ? 1 : totalSeconds,
            EndTimeLocal = endTimeLocal,
            AlertAtSeconds = alertAtSeconds,
            AlertOnExpire = alertOnExpire,
            AlertSpeak = alertSpeak,
            AlertSound = alertSound,
        };
        vm.Refresh(DateTime.Now, double.MaxValue);
        return vm;
    }

    /// <summary>A manual repop/respawn countdown: warns at 10s and beeps on expire.</summary>
    public static TimerBarViewModel CreateManual(string key, string name, double totalSeconds, Brush fill)
    {
        var vm = new TimerBarViewModel(key, name, "Timers", fill)
        {
            TotalSeconds = totalSeconds <= 0 ? 1 : totalSeconds,
            EndTimeLocal = DateTime.Now.AddSeconds(totalSeconds <= 0 ? 1 : totalSeconds),
            WarnOverride = 10,
            BeepOnExpire = true,
        };
        vm.Refresh(DateTime.Now, double.MaxValue);
        return vm;
    }

    /// <summary>A static red "REBUFF" indicator for a missing buff.</summary>
    public static TimerBarViewModel CreateMissing(string key, string name, string category, Brush fill)
    {
        var vm = new TimerBarViewModel(key, name, category, fill)
        {
            IsMissing = true,
            TotalSeconds = 1,
            EndTimeLocal = DateTime.MaxValue,
        };
        vm.Refresh(DateTime.Now, double.MaxValue);
        return vm;
    }

    public string Key { get; }
    public string Name { get; }
    public string Category { get; }
    public Brush Fill { get; }

    public bool IsMissing { get; private init; }

    public double TotalSeconds { get; private set; }
    public DateTime EndTimeLocal { get; private set; }

    // Alert settings copied from the trigger at creation.
    public double AlertAtSeconds { get; private init; }
    public bool AlertOnExpire { get; private init; }
    public string? AlertSpeak { get; private init; }
    public string? AlertSound { get; private init; }

    /// <summary>Warn threshold that overrides the global one (repop timers use 10s).</summary>
    public double? WarnOverride { get; private init; }

    /// <summary>Play a short bip when this bar expires (repop timers).</summary>
    public bool BeepOnExpire { get; private init; }

    /// <summary>Engine flag so a "fading" alert only fires once per fill.</summary>
    public bool FadeAlertFired { get; set; }

    private double _remainingSeconds;
    public double RemainingSeconds
    {
        get => _remainingSeconds;
        private set => SetField(ref _remainingSeconds, value);
    }

    private double _fraction;
    public double Fraction
    {
        get => _fraction;
        private set => SetField(ref _fraction, value);
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

    public bool IsExpired => !IsMissing && RemainingSeconds <= 0;

    public void Restart(double totalSeconds, DateTime endTimeLocal)
    {
        TotalSeconds = totalSeconds <= 0 ? 1 : totalSeconds;
        EndTimeLocal = endTimeLocal;
        FadeAlertFired = false;
    }

    public void Refresh(DateTime now, double warnSeconds)
    {
        if (IsMissing)
        {
            Fraction = 1;
            RemainingText = "REBUFF";
            IsWarning = true; // pulse
            RemainingSeconds = 1;
            return;
        }

        double remaining = (EndTimeLocal - now).TotalSeconds;
        if (remaining < 0) remaining = 0;

        RemainingSeconds = remaining;
        Fraction = Math.Clamp(remaining / TotalSeconds, 0, 1);
        RemainingText = Format(remaining);
        IsWarning = remaining > 0 && remaining <= (WarnOverride ?? warnSeconds);
    }

    private static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }
}
