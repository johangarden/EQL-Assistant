using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// THE class-combo picker (owner, 30 Sep: "there should only be a class picker
/// like in the BiS finder that gets prepicked by /who — else the user picks
/// or changes it"): the 16 classes in a 4×4 grid, up to three lit, a fourth
/// pick evicting the oldest. Prefilled from /who when the game has said; a
/// hand pick is remembered by the page for the days it hasn't. One accent per
/// role — gold for you, teal for a combo you compare with. No stock controls.
/// </summary>
public sealed class ClassPicker : Border
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Card = F("#232B40"), Edge = F("#3A4560"), OffFg = F("#7F93AD"), Faint = F("#5C6B82"), Surface = F("#1B2130"), Line = F("#2A3347");

    private readonly List<string> _combo = new();
    private readonly UniformGrid _grid = new() { Columns = 4 };
    private readonly TextBlock _hint = new() { FontSize = 10.5, Foreground = Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 250 };
    private readonly Brush _onFg, _onBg, _onLine;

    /// <summary>Fires on every hand pick (never on <see cref="Set"/>).</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    public IReadOnlyList<string> Combo => _combo;

    /// <param name="accent">Text and edge of a lit chip; the fill is a dark tint of it.</param>
    public ClassPicker(string title, string accent = "#E8C15A")
    {
        _onFg = F(accent); _onLine = F(accent);
        var c = (Color)ColorConverter.ConvertFromString(accent);
        var bg = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)); bg.Freeze(); _onBg = bg;
        Background = Surface; BorderBrush = Line; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6);
        Padding = new Thickness(12, 8, 8, 6); Margin = new Thickness(0, 0, 12, 8); VerticalAlignment = VerticalAlignment.Top;
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 7) });
        foreach (var cls in BisFinder.AllClasses)
        {
            var chip = new Border
            {
                Tag = cls, Width = 58, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(0, 2, 0, 3), Margin = new Thickness(0, 0, 5, 5),
                Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = cls, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            };
            string id = cls;
            chip.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!_combo.Remove(id)) { _combo.Add(id); while (_combo.Count > 3) _combo.RemoveAt(0); }
                Paint();
                Changed?.Invoke(_combo);
            };
            _grid.Children.Add(chip);
        }
        sp.Children.Add(_grid);
        sp.Children.Add(_hint);
        Child = sp;
        Paint();
    }

    /// <summary>The picked combo, silently.</summary>
    public void Set(IEnumerable<string> combo)
    {
        _combo.Clear();
        _combo.AddRange(combo.Where(c => BisFinder.AllClasses.Contains(c, StringComparer.OrdinalIgnoreCase)).Select(c => c.ToUpperInvariant()).Distinct().Take(3));
        Paint();
    }

    /// <summary>The line under the chips ("Prefilled from /who at 21:31", "No /who yet — pick your classes").</summary>
    public string Hint { get => _hint.Text; set { _hint.Text = value; _hint.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed; } }

    /// <summary>Selftest: click a chip the way the mouse would.</summary>
    internal void ClickForTest(string cls)
    {
        var chip = _grid.Children.OfType<Border>().First(b => (string)b.Tag == cls);
        chip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
    }

    private void Paint()
    {
        foreach (var child in _grid.Children)
        {
            if (child is not Border chip || chip.Tag is not string cls) continue;
            bool on = _combo.Contains(cls);
            chip.Background = on ? _onBg : Card;
            chip.BorderBrush = on ? _onLine : Edge;
            if (chip.Child is TextBlock tb) tb.Foreground = on ? _onFg : OffFg;
        }
    }
}
