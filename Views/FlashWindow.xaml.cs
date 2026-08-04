using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EQLOverlay.Interop;

namespace EQLOverlay.Views;

/// <summary>
/// A full-screen, click-through overlay that flashes big text in the centre and
/// fades out — for GINA-style mechanic/emote warnings.
/// </summary>
public partial class FlashWindow : Window
{
    private readonly Storyboard _fade;

    public FlashWindow()
    {
        InitializeComponent();

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
        var wa = SystemParameters.WorkArea; // primary monitor
        Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Always click-through + no-activate + off Alt-Tab.
        NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, true);
    }

    public void Flash(string text, Brush color)
    {
        FlashText.Text = text;
        FlashText.Foreground = color;
        _fade.Begin();
    }
}
