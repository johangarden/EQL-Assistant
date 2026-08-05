using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// GINA-style flash-alert area: big text that fades in/out inside a movable,
/// anchored panel. Unlocked = titled frame you can drag (like the matrices);
/// locked = invisible and click-through, only the flashes show.
/// </summary>
public partial class FlashWindow : Window
{
    private readonly PanelPlacement _placement;
    private readonly Storyboard _fade;
    private nint _hwnd;
    private bool _locked;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    public FlashWindow(ConfigService config, double opacity, double fontSize, double areaWidth)
    {
        InitializeComponent();

        fontSize = Math.Clamp(fontSize <= 0 ? 54 : fontSize, 10, 200);
        areaWidth = Math.Clamp(areaWidth <= 0 ? 900 : areaWidth, 200, 3000);

        Width = areaWidth;
        FlashText.FontSize = fontSize;
        Placeholder.FontSize = fontSize;
        Area.MinHeight = fontSize * 1.6;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);

        // Default: horizontally centered, upper third of the primary work area.
        var wa = SystemParameters.WorkArea;
        _placement = new PanelPlacement(this, config, "flash", Anchor.TopLeft,
            Math.Max(0, (wa.Width - areaWidth) / 2), wa.Height * 0.30);

        // Fade in fast, hold, fade out.
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.12))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.1))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.7))));
        Storyboard.SetTarget(anim, FlashText);
        Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
        _fade = new Storyboard();
        _fade.Children.Add(anim);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _placement.Attach();
        ApplyLockVisual();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyClickThrough();
    }

    public void Flash(string text, Brush color)
    {
        FlashText.Text = text;
        FlashText.Foreground = color;
        _fade.Begin();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ApplyClickThrough();
        ApplyLockVisual();
    }

    public void ResetPosition() => _placement.ResetToDefault();

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero)
            NativeMethods.SetClickThrough(_hwnd, _locked);
    }

    private void ApplyLockVisual()
    {
        Header.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        RootBorder.Background = _locked ? Brushes.Transparent : UnlockedBackdrop;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked)
            DragMove();
    }
}
