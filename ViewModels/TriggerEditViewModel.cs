using System.Windows.Media;
using EQLOverlay.Models;

namespace EQLOverlay.ViewModels;

/// <summary>Editable wrapper around a <see cref="TriggerDefinition"/> for the manager UI.</summary>
public sealed class TriggerEditViewModel : ViewModelBase
{
    private string _id = "";
    public string Id
    {
        get => _id;
        set { if (SetField(ref _id, value)) OnPropertyChanged(nameof(SourceBadge)); }
    }

    /// <summary>Came from the spell library (lib-/libfade- ids) vs hand-made —
    /// library triggers arrive with correct patterns, so the manual tooling
    /// (live log capture) only shows for the M side.</summary>
    public bool IsLibrary =>
        Id.StartsWith("lib-", StringComparison.Ordinal)
        || Id.StartsWith("libfade-", StringComparison.Ordinal);

    /// <summary>List chip: [L]ibrary or [M]anual.</summary>
    public string SourceBadge => IsLibrary ? "L" : "M";

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            string prevWarn = AlertConfig.DefaultWarnPhrase(_name);
            string prevFaded = AlertConfig.DefaultFadedPhrase(_name, _category);
            if (!SetField(ref _name, value)) return;
            OnPropertyChanged(nameof(Display));
            // Prefilled alert phrases follow the name until hand-edited.
            if (WarnSpeak.Length == 0 || WarnSpeak == prevWarn)
                WarnSpeak = AlertConfig.DefaultWarnPhrase(value);
            if (FadedSpeak.Length == 0 || FadedSpeak == prevFaded)
                FadedSpeak = AlertConfig.DefaultFadedPhrase(value, _category);
        }
    }

    private string _category = "Buffs";
    public string Category
    {
        get => _category;
        set
        {
            string prevFaded = AlertConfig.DefaultFadedPhrase(_name, _category);
            if (!SetField(ref _category, value)) return;
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(PreviewBrush));
            OnPropertyChanged(nameof(GroupLabel));
            // Cooldowns say "is ready" instead of "faded" — keep a default
            // phrase in step when the type changes.
            if (FadedSpeak.Length == 0 || FadedSpeak == prevFaded)
                FadedSpeak = AlertConfig.DefaultFadedPhrase(_name, value);
        }
    }

    private string _panel = Panels.Bars;
    /// <summary>"bars", "selfBuffs", "targetDebuffs", "timerAuto", or "flash".</summary>
    public string Panel
    {
        get => _panel;
        set
        {
            if (!SetField(ref _panel, value)) return;
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(PreviewBrush));
            OnPropertyChanged(nameof(GroupLabel));
        }
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { if (SetField(ref _enabled, value)) OnPropertyChanged(nameof(ListOpacity)); }
    }

    /// <summary>Disabled triggers gray out in the list instead of wearing a "○".</summary>
    public double ListOpacity => Enabled ? 1.0 : 0.4;

    /// <summary>List group header: the panel for non-bar triggers, the category for bars.</summary>
    public string GroupLabel => Panel switch
    {
        Panels.SelfBuffs => "SELF-BUFF MATRIX",
        Panels.TargetDebuffs => "TARGET DEBUFFS",
        Panels.TimerAuto => "REPOP TIMERS",
        Panels.Flash => "FLASH ALERTS",
        _ => string.IsNullOrWhiteSpace(Category) ? "BARS" : Category.Trim().ToUpperInvariant(),
    };

    /// <summary>Stable ordering for the grouped list (bars types first, then panels).</summary>
    public int GroupRank => Panel switch
    {
        Panels.SelfBuffs => 6,
        Panels.TargetDebuffs => 7,
        Panels.TimerAuto => 8,
        Panels.Flash => 9,
        _ => Services.TriggerColors.ForCategory(Category) switch
        {
            Services.TriggerColors.Buff => 0,
            Services.TriggerColors.Heal => 1,
            Services.TriggerColors.Dot => 2,
            Services.TriggerColors.Debuff => 3,
            Services.TriggerColors.Cooldown => 4,
            _ => 5,
        },
    };

    private string _startPattern = "";
    public string StartPattern { get => _startPattern; set => SetField(ref _startPattern, value); }

    private string _endPattern = "";
    public string EndPattern { get => _endPattern; set => SetField(ref _endPattern, value); }

    private double _durationSeconds = 60;
    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (!SetField(ref _durationSeconds, value)) return;
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(DurationText));
        }
    }

    /// <summary>Friendly face of <see cref="DurationSeconds"/>: shows "9m12s",
    /// accepts "660", "11m", "9m12s" or "9:12". Invalid input snaps back.</summary>
    public string DurationText
    {
        get => Services.DurationText.Compact(DurationSeconds);
        set
        {
            if (Services.DurationText.Parse(value) is double s) DurationSeconds = s;
            OnPropertyChanged(nameof(DurationText)); // normalize ("660" -> "11m") or snap back
        }
    }

    private bool _durationAuto = true;
    public bool DurationAuto { get => _durationAuto; set => SetField(ref _durationAuto, value); }

    private bool? _castAnchored;
    /// <summary>null = automatic (library triggers with a shared landing sentence
    /// anchor themselves); true/false = the user's explicit choice.</summary>
    public bool? CastAnchored { get => _castAnchored; set => SetField(ref _castAnchored, value); }

    private string _color = "#4FC3F7";
    /// <summary>Legacy passthrough — colors derive from the type since 2.9.</summary>
    public string Color { get => _color; set => SetField(ref _color, value); }

    private bool _refreshOnRetrigger = true;
    public bool RefreshOnRetrigger { get => _refreshOnRetrigger; set => SetField(ref _refreshOnRetrigger, value); }

    // Alert fields (flattened): two notices, each a phrase OR a sound.

    private bool _warnOn;
    /// <summary>"Notify before it fades" toggle.</summary>
    public bool WarnOn { get => _warnOn; set => SetField(ref _warnOn, value); }

    private double _warnSeconds = 15;
    /// <summary>Seconds left when the pre-fade notice fires.</summary>
    public double WarnSeconds { get => _warnSeconds; set => SetField(ref _warnSeconds, value); }

    private string _warnMode = AlertConfig.ModeSpeak;
    public string WarnMode { get => _warnMode; set => SetField(ref _warnMode, value); }

    private string _warnSpeak = "";
    public string WarnSpeak { get => _warnSpeak; set => SetField(ref _warnSpeak, value); }

    private string _warnSound = "";
    public string WarnSound { get => _warnSound; set => SetField(ref _warnSound, value); }

    private bool _fadedOn;
    /// <summary>"Notify when it faded" toggle (doubles as cooldown 'ready').</summary>
    public bool FadedOn { get => _fadedOn; set => SetField(ref _fadedOn, value); }

    private string _fadedMode = AlertConfig.ModeSpeak;
    public string FadedMode { get => _fadedMode; set => SetField(ref _fadedMode, value); }

    private string _fadedSpeak = "";
    public string FadedSpeak { get => _fadedSpeak; set => SetField(ref _fadedSpeak, value); }

    private string _fadedSound = "";
    public string FadedSound { get => _fadedSound; set => SetField(ref _fadedSound, value); }

    private string _flashText = "";
    public string FlashText { get => _flashText; set => SetField(ref _flashText, value); }

    private bool _remindWhenMissing;
    public bool RemindWhenMissing { get => _remindWhenMissing; set => SetField(ref _remindWhenMissing, value); }

    // Cooldown reducer (bars only).
    private string _reducePattern = "";
    public string ReducePattern { get => _reducePattern; set => SetField(ref _reducePattern, value); }

    private double _reduceSeconds;
    public double ReduceSeconds { get => _reduceSeconds; set => SetField(ref _reduceSeconds, value); }

    /// <summary>Label shown in the trigger list — just "Name · 9m12s"; the
    /// type lives in the group header above the row.</summary>
    public string Display =>
        Panel == Panels.Flash
            ? Name
            : $"{Name}  ·  {Services.DurationText.Compact(DurationSeconds)}";

    /// <summary>The list swatch: the TYPE's fixed color (buff blue, heal green…).</summary>
    public Brush PreviewBrush
    {
        get
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                    Services.TriggerColors.For(Panel, Category)));
            }
            catch { return Brushes.Gray; }
        }
    }

    public static TriggerEditViewModel FromDefinition(TriggerDefinition d)
    {
        // Migrate pre-2.11 single-payload alerts before flattening, so the
        // editor always sees (and saves back) the two-notice model.
        Services.ConfigService.NormalizeAlert(d);
        return new()
        {
            Id = d.Id,
            Name = d.Name,
            Category = d.Category,
            Panel = string.IsNullOrWhiteSpace(d.Panel) ? Panels.Bars : d.Panel,
            Enabled = d.Enabled,
            StartPattern = d.StartPattern,
            EndPattern = d.EndPattern ?? "",
            DurationSeconds = d.DurationSeconds,
            DurationAuto = d.DurationAuto,
            CastAnchored = d.CastAnchored,
            Color = d.Color,
            RefreshOnRetrigger = d.RefreshOnRetrigger,
            WarnOn = d.Alert?.WarnEnabled ?? false,
            WarnSeconds = d.Alert is { AtSeconds: > 0 } a1 ? a1.AtSeconds : 15,
            WarnMode = d.Alert?.WarnMode is AlertConfig.ModeSound
                ? AlertConfig.ModeSound : AlertConfig.ModeSpeak,
            WarnSpeak = string.IsNullOrWhiteSpace(d.Alert?.Speak)
                ? AlertConfig.DefaultWarnPhrase(d.Name) : d.Alert!.Speak!,
            WarnSound = d.Alert?.Sound ?? "",
            FadedOn = d.Alert?.FadedEnabled ?? false,
            FadedMode = d.Alert?.FadedMode is AlertConfig.ModeSound
                ? AlertConfig.ModeSound : AlertConfig.ModeSpeak,
            FadedSpeak = string.IsNullOrWhiteSpace(d.Alert?.FadedSpeak)
                ? AlertConfig.DefaultFadedPhrase(d.Name, d.Category) : d.Alert!.FadedSpeak!,
            FadedSound = d.Alert?.FadedSound ?? "",
            FlashText = d.Alert?.FlashText ?? "",
            RemindWhenMissing = d.RemindWhenMissing,
            ReducePattern = d.ReducePattern ?? "",
            ReduceSeconds = d.ReduceSeconds,
        };
    }

    public TriggerDefinition ToDefinition()
    {
        bool hasAlert = WarnOn || FadedOn || !string.IsNullOrWhiteSpace(FlashText);

        return new TriggerDefinition
        {
            Id = string.IsNullOrWhiteSpace(Id) ? MakeId(Name) : Id.Trim(),
            Name = Name.Trim(),
            Category = string.IsNullOrWhiteSpace(Category) ? "Buffs" : Category.Trim(),
            Panel = string.IsNullOrWhiteSpace(Panel) ? Panels.Bars : Panel,
            Enabled = Enabled,
            StartPattern = StartPattern,
            EndPattern = string.IsNullOrWhiteSpace(EndPattern) ? null : EndPattern,
            DurationSeconds = DurationSeconds,
            DurationAuto = DurationAuto,
            CastAnchored = CastAnchored,
            Color = Color,
            RefreshOnRetrigger = RefreshOnRetrigger,
            RemindWhenMissing = RemindWhenMissing,
            ReducePattern = string.IsNullOrWhiteSpace(ReducePattern) ? null : ReducePattern,
            ReduceSeconds = ReduceSeconds,
            Alert = hasAlert ? new AlertConfig
            {
                WarnEnabled = WarnOn,
                AtSeconds = WarnSeconds > 0 ? WarnSeconds : 15,
                WarnMode = WarnMode,
                Speak = string.IsNullOrWhiteSpace(WarnSpeak) ? null : WarnSpeak,
                Sound = string.IsNullOrWhiteSpace(WarnSound) ? null : WarnSound,
                FadedEnabled = FadedOn,
                FadedMode = FadedMode,
                FadedSpeak = string.IsNullOrWhiteSpace(FadedSpeak) ? null : FadedSpeak,
                FadedSound = string.IsNullOrWhiteSpace(FadedSound) ? null : FadedSound,
                FlashText = string.IsNullOrWhiteSpace(FlashText) ? null : FlashText,
                // Legacy flags stay coherent for anything old reading the file.
                SpeakEnabled = WarnOn && WarnMode == AlertConfig.ModeSpeak,
                OnExpire = FadedOn,
            } : null,
        };
    }

    private static string MakeId(string name)
    {
        string slug = new string((name ?? "").ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(slug) ? "trigger-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }
}
