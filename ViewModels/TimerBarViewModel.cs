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
        // HoTs are the bars you actively watch to stay alive — render taller.
        HeightScale = Services.TriggerColors.ForCategory(category) == Services.TriggerColors.Heal
            ? 1.4 : 1.0;
    }

    /// <summary>Multiplier on the configured bar height (HoTs 1.4×, rest 1×).</summary>
    public double HeightScale { get; }

    /// <summary>A normal countdown bar. <paramref name="waitsForFade"/> = the
    /// trigger has an end pattern: on expiry the bar OVERRUNS (grays out and
    /// counts up) until the fade line arrives, instead of vanishing while the
    /// buff is demonstrably still up — the "still there, still learning" state.</summary>
    public static TimerBarViewModel CreateTimer(
        string key, string name, string category, double totalSeconds,
        DateTime endTimeLocal, Brush fill,
        double alertAtSeconds, bool alertOnExpire, string? alertSpeak, string? alertSound,
        string? alertFadedSpeak = null, string? alertFadedSound = null,
        bool waitsForFade = false, bool learnsDuration = false)
    {
        var vm = new TimerBarViewModel(key, name, category, fill)
        {
            TotalSeconds = totalSeconds <= 0 ? 1 : totalSeconds,
            EndTimeLocal = endTimeLocal,
            AlertAtSeconds = alertAtSeconds,
            AlertOnExpire = alertOnExpire,
            AlertSpeak = alertSpeak,
            AlertSound = alertSound,
            AlertFadedSpeak = alertFadedSpeak,
            AlertFadedSound = alertFadedSound,
            WaitsForFade = waitsForFade,
            LearnsDuration = learnsDuration,
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

    // Alert settings copied from the trigger at creation. Speak/Sound are the
    // pre-fade payload; the Faded pair plays when the bar reaches 0.
    public double AlertAtSeconds { get; private init; }
    public bool AlertOnExpire { get; private init; }
    public string? AlertSpeak { get; private init; }
    public string? AlertSound { get; private init; }
    public string? AlertFadedSpeak { get; private init; }
    public string? AlertFadedSound { get; private init; }

    /// <summary>Engine flag so a "fading" alert only fires once per fill.</summary>
    public bool FadeAlertFired { get; set; }

    /// <summary>True when the trigger has an end pattern (see CreateTimer).</summary>
    public bool WaitsForFade { get; private init; }

    /// <summary>Auto-learn is on: an overrun means the estimate was short and
    /// the coming fade line will teach the real duration.</summary>
    public bool LearnsDuration { get; private init; }

    private bool _isOverrun;
    /// <summary>Expired without a witnessed fade: gray, counting up.</summary>
    public bool IsOverrun
    {
        get => _isOverrun;
        private set => SetField(ref _isOverrun, value);
    }

    /// <summary>Seconds past the estimate while overrunning (engine cull cap).</summary>
    public double OverrunSeconds { get; private set; }

    public void EnterOverrun() => IsOverrun = true;

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

    public bool IsExpired => !IsMissing && !IsOverrun && RemainingSeconds <= 0;

    public void Restart(double totalSeconds, DateTime endTimeLocal)
    {
        TotalSeconds = totalSeconds <= 0 ? 1 : totalSeconds;
        EndTimeLocal = endTimeLocal;
        FadeAlertFired = false;
        IsOverrun = false;
        OverrunSeconds = 0;
    }

    /// <summary>Cut time off a running bar (cooldown reducers) — the bar visibly
    /// jumps; hitting 0 expires it through the normal tick path.</summary>
    public void ReduceBy(double seconds)
    {
        if (!IsMissing && seconds > 0)
            EndTimeLocal = EndTimeLocal.AddSeconds(-seconds);
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

        if (IsOverrun)
        {
            OverrunSeconds = Math.Max(0, (now - EndTimeLocal).TotalSeconds);
            RemainingSeconds = 0;
            Fraction = 1; // full gray bar: "still up, past the estimate"
            RemainingText = LearnsDuration
                ? $"learning +{OverrunSeconds:0}s"
                : $"+{OverrunSeconds:0}s";
            IsWarning = false;
            return;
        }

        double remaining = (EndTimeLocal - now).TotalSeconds;
        if (remaining < 0) remaining = 0;

        RemainingSeconds = remaining;
        Fraction = Math.Clamp(remaining / TotalSeconds, 0, 1);
        RemainingText = Format(remaining);
        IsWarning = remaining > 0 && remaining <= warnSeconds;
    }

    private static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }
}
