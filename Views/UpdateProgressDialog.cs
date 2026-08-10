using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLOverlay.Interop;

namespace EQLOverlay.Views;

/// <summary>Small dark download-progress window for the self-updater.</summary>
public sealed class UpdateProgressDialog : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Cancelled when the user closes the window mid-download.</summary>
    public CancellationToken Cancellation => _cts.Token;

    public UpdateProgressDialog(string tag)
    {
        Title = $"EQL Assistant — updating to {tag}";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        Topmost = true;
        ShowInTaskbar = false;

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Themes/Controls.xaml", UriKind.Relative),
        });
        Background = (Brush)Resources["Brush.Window"];
        WindowTheme.ApplyDark(this);

        var panel = new StackPanel { Margin = new Thickness(16) };
        _status = new TextBlock { Text = "Starting download…" };
        _bar = new ProgressBar
        {
            Height = 10,
            Margin = new Thickness(0, 10, 0, 0),
            Minimum = 0,
            Maximum = 100,
            Foreground = (Brush)Resources["Brush.Accent"],
            Background = (Brush)Resources["Brush.Field"],
            BorderBrush = (Brush)Resources["Brush.Border"],
        };
        panel.Children.Add(_status);
        panel.Children.Add(_bar);
        Content = panel;

        Closed += (_, _) => _cts.Cancel();
    }

    public void SetProgress(double fraction)
    {
        _bar.Value = Math.Clamp(fraction, 0, 1) * 100;
        _status.Text = $"Downloading… {fraction:P0}";
    }

    public void SetStatus(string text) => _status.Text = text;
}
