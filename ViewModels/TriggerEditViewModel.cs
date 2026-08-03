using System.Windows.Media;
using EQLOverlay.Models;

namespace EQLOverlay.ViewModels;

/// <summary>Editable wrapper around a <see cref="TriggerDefinition"/> for the manager UI.</summary>
public sealed class TriggerEditViewModel : ViewModelBase
{
    private string _id = "";
    public string Id { get => _id; set => SetField(ref _id, value); }

    private string _name = "";
    public string Name { get => _name; set { if (SetField(ref _name, value)) OnPropertyChanged(nameof(Display)); } }

    private string _category = "Buffs";
    public string Category { get => _category; set { if (SetField(ref _category, value)) OnPropertyChanged(nameof(Display)); } }

    private string _panel = Panels.Bars;
    /// <summary>"bars", "selfBuffs", or "targetDebuffs".</summary>
    public string Panel { get => _panel; set => SetField(ref _panel, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set { if (SetField(ref _enabled, value)) OnPropertyChanged(nameof(Display)); } }

    private string _startPattern = "";
    public string StartPattern { get => _startPattern; set => SetField(ref _startPattern, value); }

    private string _endPattern = "";
    public string EndPattern { get => _endPattern; set => SetField(ref _endPattern, value); }

    private double _durationSeconds = 60;
    public double DurationSeconds { get => _durationSeconds; set { if (SetField(ref _durationSeconds, value)) OnPropertyChanged(nameof(Display)); } }

    private string _color = "#4FC3F7";
    public string Color
    {
        get => _color;
        set { if (SetField(ref _color, value)) OnPropertyChanged(nameof(PreviewBrush)); }
    }

    private bool _refreshOnRetrigger = true;
    public bool RefreshOnRetrigger { get => _refreshOnRetrigger; set => SetField(ref _refreshOnRetrigger, value); }

    // Alert fields (flattened).
    private string _alertSpeak = "";
    public string AlertSpeak { get => _alertSpeak; set => SetField(ref _alertSpeak, value); }

    private string _alertSound = "";
    public string AlertSound { get => _alertSound; set => SetField(ref _alertSound, value); }

    private double _alertAtSeconds;
    public double AlertAtSeconds { get => _alertAtSeconds; set => SetField(ref _alertAtSeconds, value); }

    private bool _alertOnExpire;
    public bool AlertOnExpire { get => _alertOnExpire; set => SetField(ref _alertOnExpire, value); }

    private bool _remindWhenMissing;
    public bool RemindWhenMissing { get => _remindWhenMissing; set => SetField(ref _remindWhenMissing, value); }

    /// <summary>Label shown in the trigger list.</summary>
    public string Display =>
        $"{(Enabled ? "" : "○ ")}{Name}  ·  {Category}  ·  {DurationSeconds:0}s";

    public Brush PreviewBrush
    {
        get
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Color)); }
            catch { return Brushes.Gray; }
        }
    }

    public static TriggerEditViewModel FromDefinition(TriggerDefinition d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Category = d.Category,
        Panel = string.IsNullOrWhiteSpace(d.Panel) ? Panels.Bars : d.Panel,
        Enabled = d.Enabled,
        StartPattern = d.StartPattern,
        EndPattern = d.EndPattern ?? "",
        DurationSeconds = d.DurationSeconds,
        Color = d.Color,
        RefreshOnRetrigger = d.RefreshOnRetrigger,
        AlertSpeak = d.Alert?.Speak ?? "",
        AlertSound = d.Alert?.Sound ?? "",
        AlertAtSeconds = d.Alert?.AtSeconds ?? 0,
        AlertOnExpire = d.Alert?.OnExpire ?? false,
        RemindWhenMissing = d.RemindWhenMissing,
    };

    public TriggerDefinition ToDefinition()
    {
        bool hasAlert = !string.IsNullOrWhiteSpace(AlertSpeak)
                        || !string.IsNullOrWhiteSpace(AlertSound)
                        || AlertAtSeconds > 0
                        || AlertOnExpire;

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
            Color = Color,
            RefreshOnRetrigger = RefreshOnRetrigger,
            RemindWhenMissing = RemindWhenMissing,
            Alert = hasAlert ? new AlertConfig
            {
                Speak = string.IsNullOrWhiteSpace(AlertSpeak) ? null : AlertSpeak,
                Sound = string.IsNullOrWhiteSpace(AlertSound) ? null : AlertSound,
                AtSeconds = AlertAtSeconds,
                OnExpire = AlertOnExpire,
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
