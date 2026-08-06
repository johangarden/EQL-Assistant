using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EQLOverlay.Interop;

namespace EQLOverlay.Views;

/// <summary>Minimal dark yes/no dialog (system MessageBox can't be themed).</summary>
public static class ConfirmDialog
{
    public static bool Show(Window? owner, string title, string message,
        string yesText = "Yes", string noText = "No")
    {
        var win = new Window
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true, // the overlay is topmost; the question must be above it
            ShowInTaskbar = false,
        };

        win.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Themes/Controls.xaml", UriKind.Relative),
        });
        win.Background = (Brush)win.Resources["Brush.Window"];
        WindowTheme.ApplyDark(win);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var yes = new Button { Content = yesText, MinWidth = 86, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        yes.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryBtn");
        var no = new Button { Content = noText, MinWidth = 86, IsCancel = true };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        panel.Children.Add(buttons);

        win.Content = panel;
        yes.Click += (_, _) => win.DialogResult = true;

        win.Loaded += (_, _) =>
        {
            NativeMethods.ForceForeground(new WindowInteropHelper(win).Handle);
            win.Activate();
        };

        return win.ShowDialog() == true;
    }
}
