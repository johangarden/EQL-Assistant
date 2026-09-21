using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The incoming-damage panel: a HUD window like the enemy DoTs — locked it
/// is click-through and vanishes while nothing hits you; unlocked it keeps
/// a placeholder so it can be placed. Refreshes four times a second from
/// the <see cref="IncomingWatch"/>. The chart itself is the shared
/// <see cref="IncomingStrip"/> (21 Sep) — the Manager can move it onto the
/// DPS meter instead, in which case this window is never built.
/// </summary>
public partial class IncomingWindow : Window
{
    private static readonly Brush UnlockedBackdrop = Freeze("#F0141A24");
    private static readonly Brush LockedBackdrop = Freeze("#E0141A24");

    private readonly IncomingWatch _watch;
    private readonly Func<string> _stance;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    public IncomingWindow(IncomingWatch watch, Func<string> stance, ConfigService configService, double opacity, int windowSec)
    {
        InitializeComponent();
        _watch = watch;
        _stance = stance;
        _watch.WindowSec = windowSec;
        Title = "EQL Assistant — Incoming damage";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "incoming", Anchor.TopLeft, 420, 300);
        Strip.HeaderRow.MouseLeftButtonDown += Header_DragMove;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { _placement.Attach(); ApplyLockVisual(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        Closed += (_, _) => _tick.Stop(); // panel law: timers die with the window
        Refresh(); // a never-shown window (selftest) still renders honestly
    }

    public void ApplySettings(int windowSec, double opacity)
    {
        _watch.WindowSec = windowSec;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        Refresh();
    }

    public void SetHidden(bool hidden) { _hidden = hidden; Refresh(); }
    public void SetLocked(bool locked) { _locked = locked; ApplyClickThrough(); ApplyLockVisual(); Refresh(); }
    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();

    /// <summary>The verdict kind last painted ("switch" / "ok" / "mixed" / "") — selftest.</summary>
    public string LastKind => Strip.LastKind;
    /// <summary>The stance pill's text last painted ("DEFENSIVE ▸ MAGE HUNTER") — selftest.</summary>
    public string LastChip => Strip.LastChip;

    public void Refresh()
    {
        var s = _watch.Take(DateTime.Now, _stance());
        Strip.Paint(s);
        Placeholder.Text = $"No damage taken in the last {s.WindowSec} s.";
        Placeholder.Visibility = !s.Any && !_locked ? Visibility.Visible : Visibility.Collapsed;

        bool show = !_hidden && (s.Any || !_locked);
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) Hide();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero) NativeMethods.SetClickThrough(_hwnd, _locked);
    }

    private void ApplyLockVisual()
    {
        RootBorder.Background = _locked ? LockedBackdrop : UnlockedBackdrop;
        Strip.HeaderRow.Cursor = _locked ? Cursors.Arrow : Cursors.SizeAll;
        Strip.HeaderRow.ToolTip = _locked ? null : "Drag to place — lock the overlay when done";
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked) DragMove();
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
