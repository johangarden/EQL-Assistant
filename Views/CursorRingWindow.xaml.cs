using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;

namespace EQLOverlay.Views;

/// <summary>
/// The cursor ring: a small always-on-top, click-through window that follows
/// the mouse, drawing a gold ring around it — so the cursor is findable in
/// the heat of battle. Optional (Manager → General → Overlay).
/// </summary>
public partial class CursorRingWindow : Window
{
    private readonly DispatcherTimer _tick;
    private Matrix _fromDevice = Matrix.Identity;

    public CursorRingWindow()
    {
        InitializeComponent();

        // ~60 fps follow — moving a tiny layered window is cheap.
        _tick = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _tick.Tick += (_, _) => Follow();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetClickThrough(hwnd, true);
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
                _fromDevice = ct.TransformFromDevice;
            Follow();
            _tick.Start();
        };
        Closed += (_, _) => _tick.Stop(); // panel law: timers die with the window
    }

    private void Follow()
    {
        if (!NativeMethods.GetCursorPos(out var p)) return;
        var dip = _fromDevice.Transform(new Point(p.X, p.Y));
        Left = dip.X - Width / 2;
        Top = dip.Y - Height / 2;
    }
}
