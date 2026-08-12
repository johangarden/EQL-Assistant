using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;
using EQLOverlay.ViewModels;

namespace EQLOverlay.Views;

/// <summary>
/// The REBUFF nags in their own panel (they used to squat inside the buff
/// bars): red rows bound live to <see cref="TriggerEngine.Reminders"/>. Only
/// materializes while it has rows, or while unlocked so it can be placed.
/// </summary>
public partial class RemindersWindow : Window
{
    private readonly ObservableCollection<TimerBarViewModel> _reminders;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    public RemindersWindow(ObservableCollection<TimerBarViewModel> reminders,
        ConfigService configService, double opacity)
    {
        InitializeComponent();
        _reminders = reminders;
        Title = "EQL Assistant — Rebuff Reminders";
        RowsControl.ItemsSource = reminders;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "reminders", Anchor.TopLeft, 60, 640);

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _tick.Tick += (_, _) => UpdateVisibility();

        Loaded += (_, _) => { _placement.Attach(); ApplyLockVisual(); UpdateVisibility(); _tick.Start(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        Closed += (_, _) => _tick.Stop();
    }

    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        UpdateVisibility();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ApplyClickThrough();
        ApplyLockVisual();
        UpdateVisibility();
    }

    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();

    private void UpdateVisibility()
    {
        Placeholder.Visibility = !_locked && _reminders.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        bool show = !_hidden && (_reminders.Count > 0 || !_locked);
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) Hide();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero)
            NativeMethods.SetClickThrough(_hwnd, _locked);
    }

    private void ApplyLockVisual()
    {
        Header.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        RootBorder.Background = _locked ? Brushes.Transparent : UnlockedBackdrop;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked)
            DragMove();
    }
}
