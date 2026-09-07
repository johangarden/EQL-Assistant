using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLOverlay.Views;

/// <summary>
/// "Pin above game" — the small-caps pill at the far right of a page
/// window's title row. Unpinned (default) the page is an ordinary window
/// the game covers on its next click; pinned it is Topmost like the HUD
/// panels. Reads the window's Topmost as its truth so a restore from the
/// bounds file needs no extra wiring. Themed by hand: no stock controls.
/// </summary>
public sealed class PagePin : Border
{
    private static readonly Brush OnFg = Freeze("#E8C15A");
    private static readonly Brush OnBg = Freeze("#2A2614");
    private static readonly Brush OffFg = Freeze("#7F93AD");
    private static readonly Brush OffBorder = Freeze("#2A3447");

    private readonly Window _window;
    private readonly Action _persist;
    private readonly TextBlock _label;

    public PagePin(Window window, Action persist)
    {
        _window = window;
        _persist = persist;
        _label = new TextBlock
        {
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _label;
        Padding = new Thickness(8, 3, 8, 3);
        Margin = new Thickness(12, 0, 0, 0);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Hand;
        ToolTip = "Keep this window above the game, like the panels — clicking your bags no longer buries it. Click again to let the game cover it.";
        MouseLeftButtonDown += (_, e) => { Toggle(); e.Handled = true; };
        MouseEnter += (_, _) => { if (!IsPinned) _label.Foreground = OnFg; };
        MouseLeave += (_, _) => Paint();
        Paint();
    }

    public bool IsPinned => _window.Topmost;

    public void Toggle()
    {
        _window.Topmost = !_window.Topmost;
        if (_window.Topmost) _window.Activate();
        Paint();
        _persist();
    }

    private void Paint()
    {
        bool on = IsPinned;
        _label.Text = on ? "PINNED ABOVE GAME" : "PIN ABOVE GAME";
        _label.Foreground = on ? OnFg : OffFg;
        Background = on ? OnBg : Brushes.Transparent;
        BorderBrush = on ? OnFg : OffBorder;
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
