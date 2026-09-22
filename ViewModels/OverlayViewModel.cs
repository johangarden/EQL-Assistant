using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.ViewModels;

/// <summary>Top-level bindings for the overlay window (bars + chrome + status).</summary>
public sealed class OverlayViewModel : ViewModelBase
{
    private readonly TriggerEngine _engine;
    private readonly DispatcherTimer _flashTimer;

    public OverlayViewModel(TriggerEngine engine, AppConfig config)
    {
        _engine = engine;
        BarHeight = config.Overlay.BarHeight;
        Spacing = config.Overlay.Spacing;
        FontSize = config.Overlay.FontSize;
        ShowCategoryHeaders = config.Overlay.ShowCategoryHeaders;
        _locked = config.Overlay.Locked;
        _muted = config.Overlay.Muted;

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            ToastVisible = false;
        };
    }

    public ObservableCollection<TimerBarViewModel> Bars => _engine.Bars;

    public double BarHeight { get; }
    public double Spacing { get; }
    public double FontSize { get; }
    public bool ShowCategoryHeaders { get; }

    /// <summary>Build version shown in the toolbar, e.g. "v2.5.1" (matches the Manager).</summary>
    public string Version { get; } = FormatVersion();

    private static string FormatVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    private bool _locked;
    public bool Locked
    {
        get => _locked;
        set
        {
            if (SetField(ref _locked, value))
                OnPropertyChanged(nameof(IsUnlocked));
        }
    }

    /// <summary>Convenience inverse for showing the toolbar/handle only when editable.</summary>
    public bool IsUnlocked => !_locked;

    /// <summary>The persistent log-following state (set by the log watcher).
    /// Shown on the Manager's Log source section, not on the toolbar.</summary>
    private string _logStatus = "Starting…";
    public string LogStatus
    {
        get => _logStatus;
        set { SetField(ref _logStatus, value); OnPropertyChanged(nameof(LogDot)); }
    }

    /// <summary>The toolbar's log dot (21 Sep): "on" = following a file,
    /// "catch" = a catch-up / reparse is running, "off" = nothing read.</summary>
    public string LogDot => _progress is not null ? "catch"
        : _logStatus.StartsWith("Following", StringComparison.Ordinal) ? "on" : "off";

    // Toast: a short-lived confirmation under the toolbar ("Switched to:
    // Raid", "Pet detected: Lober") that fades instead of squatting on a
    // permanent status line.
    private string _toast = "";
    public string Toast
    {
        get => _toast;
        private set => SetField(ref _toast, value);
    }

    private bool _toastVisible;
    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetField(ref _toastVisible, value);
    }

    /// <summary>Show a short-lived confirmation toast (~4s) under the toolbar.</summary>
    public void Flash(string message)
    {
        Toast = message;
        ToastVisible = true;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    // Progress card under the toolbar: the startup catch-up and the menu's
    // "Catch up from today's log" — owner request 14 Sep, a bar not a cursor.
    private ReparseProgress? _progress;
    public ReparseProgress? Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressVisible));
            OnPropertyChanged(nameof(ProgressTitle));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressDetail));
            OnPropertyChanged(nameof(ProgressFill));
            OnPropertyChanged(nameof(ProgressFraction));
            OnPropertyChanged(nameof(LogDot));
        }
    }
    public bool ProgressVisible => _progress is not null;
    public string ProgressTitle => _progress?.Title ?? "";
    public string ProgressPercent => _progress?.Percent ?? "";
    public string ProgressDetail => _progress?.Detail ?? "";
    /// <summary>The toolbar's track is 366 px (the toast's inner width).</summary>
    public const double ProgressTrack = 366;
    public double ProgressFill => ProgressTrack * (_progress?.Fraction ?? 0);
    public double ProgressFraction => _progress?.Fraction ?? 0;

    private string _loadoutName = "Default";
    public string LoadoutName
    {
        get => _loadoutName;
        set => SetField(ref _loadoutName, value);
    }

    private bool _panelsHidden;
    /// <summary>Drives the toolbar's eye: every panel hidden, toolbar stays
    /// (toolbar button or Ctrl+Alt+P) — owner request, 7 Sep.</summary>
    public bool PanelsHidden
    {
        get => _panelsHidden;
        set => SetField(ref _panelsHidden, value);
    }

    private bool _muted;
    /// <summary>Drives the little 🔇 indicator (mute is hotkey-only, Ctrl+Alt+S).</summary>
    public bool Muted
    {
        get => _muted;
        set => SetField(ref _muted, value);
    }

    // ---- the toolbar's card chrome (21 Sep) ----

    private bool _toolbarLabels;
    /// <summary>General → "labels under the toolbar keys": 34×32 keys with a word each.</summary>
    public bool ToolbarLabels
    {
        get => _toolbarLabels;
        set => SetField(ref _toolbarLabels, value);
    }

    private bool _tradeskillOpen;
    /// <summary>The anvil lights gold while the tradeskill card is shown.</summary>
    public bool TradeskillOpen
    {
        get => _tradeskillOpen;
        set => SetField(ref _tradeskillOpen, value);
    }

    // News badges on the doors: a small gold count.
    private int _questBadge, _raidBadge, _lootBadge;
    /// <summary>Sky quests ready to hand in.</summary>
    public int QuestBadge
    {
        get => _questBadge;
        set { SetField(ref _questBadge, value); OnPropertyChanged(nameof(QuestBadgeVisible)); OnPropertyChanged(nameof(QuestBadgeText)); }
    }
    /// <summary>Raid-target kills since the Raid kills window was last opened.</summary>
    public int RaidBadge
    {
        get => _raidBadge;
        set { SetField(ref _raidBadge, value); OnPropertyChanged(nameof(RaidBadgeVisible)); OnPropertyChanged(nameof(RaidBadgeText)); }
    }
    /// <summary>Drops since the Loot window was last opened.</summary>
    public int LootBadge
    {
        get => _lootBadge;
        set { SetField(ref _lootBadge, value); OnPropertyChanged(nameof(LootBadgeVisible)); OnPropertyChanged(nameof(LootBadgeText)); }
    }
    public bool QuestBadgeVisible => _questBadge > 0;
    public bool RaidBadgeVisible => _raidBadge > 0;
    public bool LootBadgeVisible => _lootBadge > 0;
    public string QuestBadgeText => BadgeText(_questBadge);
    public string RaidBadgeText => BadgeText(_raidBadge);
    public string LootBadgeText => BadgeText(_lootBadge);
    public static string BadgeText(int n) => n > 99 ? "99+" : n.ToString();
}
