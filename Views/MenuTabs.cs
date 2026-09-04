using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLOverlay.Views;

/// <summary>
/// The MENU tier of the window header grammar (owner ruling, 4 Sep):
/// Title → Menu → Pills. Menu tabs are a window's SECTIONS — small caps,
/// dim, the active one gold with a gold underline over a hairline. A
/// "soon" item is a section that doesn't exist yet: dimmer, unclickable,
/// so the future has a place ("KUNARK · soon"). Deliberately a different
/// species from the pills that pick a lens INSIDE a section.
/// </summary>
public static class MenuTabs
{
    public sealed record Item(string Id, string Label, bool Soon = false, string? Tip = null);

    private static readonly Brush ActiveFg = Freeze("#E8C15A");
    private static readonly Brush IdleFg = Freeze("#7F93AD");
    private static readonly Brush SoonFg = Freeze("#5C6B82");
    private static readonly Brush Hairline = Freeze("#1F2637");

    /// <summary>Render the tabs into <paramref name="host"/> (replacing its
    /// children). <paramref name="pick"/> fires for a click on a non-active,
    /// non-soon item.</summary>
    public static void Render(Panel host, IEnumerable<Item> items, string active, Action<string> pick)
    {
        host.Children.Clear();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            bool on = item.Id == active;
            var text = new TextBlock
            {
                Text = item.Label.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = on ? ActiveFg : item.Soon ? SoonFg : IdleFg,
                Opacity = item.Soon ? 0.7 : 1,
            };
            var tab = new Border
            {
                Child = text,
                Padding = new Thickness(2, 2, 2, 6),
                Margin = new Thickness(0, 0, 18, 0),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = on ? ActiveFg : Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = item.Soon || on ? Cursors.Arrow : Cursors.Hand,
                ToolTip = item.Tip,
            };
            if (!item.Soon && !on)
            {
                string id = item.Id;
                tab.MouseLeftButtonDown += (_, _) => pick(id);
                tab.MouseEnter += (_, _) => text.Foreground = ActiveFg;
                tab.MouseLeave += (_, _) => text.Foreground = IdleFg;
            }
            row.Children.Add(tab);
        }
        host.Children.Add(new Border
        {
            Child = row,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
        });
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
