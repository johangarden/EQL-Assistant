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
        set => SetField(ref _logStatus, value);
    }

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

    private string _loadoutName = "Default";
    public string LoadoutName
    {
        get => _loadoutName;
        set => SetField(ref _loadoutName, value);
    }

    private bool _muted;
    /// <summary>Drives the little 🔇 indicator (mute is hotkey-only, Ctrl+Alt+S).</summary>
    public bool Muted
    {
        get => _muted;
        set => SetField(ref _muted, value);
    }
}
