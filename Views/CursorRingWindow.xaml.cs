using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;

namespace EQLOverlay.Views;

/// <summary>
/// The cursor ring: a small always-on-top, click-through window that follows
/// the mouse, drawing a ring around it — so the cursor is findable in the
/// heat of battle. Optional (Manager → General → Cursor ring card): size,
/// stroke and color are settings; the ring only shows while the game (or
/// this app) is the foreground window, so it never haunts the browser.
/// </summary>
public partial class CursorRingWindow : Window
{
    private readonly DispatcherTimer _tick;
    private Matrix _fromDevice = Matrix.Identity;
    private Func<bool>? _overGame;
    private int _frame;

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

    /// <summary>Size = ring diameter in px; the halo sits just outside it.
    /// <paramref name="overGame"/> answers "is the game (or this app) the
    /// window you're in?" — null shows the ring everywhere.</summary>
    public void ApplySettings(int size, double thickness, string color, Func<bool>? overGame)
    {
        size = Math.Clamp(size, 16, 200);
        thickness = Math.Clamp(thickness, 1, 12);
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(color); }
        catch { c = (Color)ColorConverter.ConvertFromString("#E8C15A"); }
        var ring = new SolidColorBrush(Color.FromArgb(0xCC, c.R, c.G, c.B));
        var halo = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        ring.Freeze(); halo.Freeze();

        double haloStroke = Math.Max(4, thickness * 2.5);
        Ring.Width = Ring.Height = size;
        Ring.StrokeThickness = thickness;
        Ring.Stroke = ring;
        Halo.Width = Halo.Height = size + haloStroke;
        Halo.StrokeThickness = haloStroke;
        Halo.Stroke = halo;
        Width = Height = size + haloStroke * 2 + 4;
        _overGame = overGame;
        Follow();
    }

    public double RingSize => Ring.Width;
    public double RingThickness => Ring.StrokeThickness;
    public Color RingColor => ((SolidColorBrush)Ring.Stroke).Color;

    private void Follow()
    {
        // The foreground check costs a process query — twice a second is plenty.
        if (_overGame is not null && (_frame++ % 30) == 0)
        {
            bool show = _overGame();
            var v = show ? Visibility.Visible : Visibility.Hidden;
            if (Visibility != v) Visibility = v;
        }
        if (!NativeMethods.GetCursorPos(out var p)) return;
        var dip = _fromDevice.Transform(new Point(p.X, p.Y));
        Left = dip.X - Width / 2;
        Top = dip.Y - Height / 2;
    }
}
