using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EQLOverlay.Views;

/// <summary>
/// A segmented control (owner request, 7 Sep — "This week | All time"):
/// one pill holding two or more mutually exclusive options; the filled
/// segment SLIDES to the pick instead of hard-switching. For settings-shaped
/// choices (which weapon style, DPS or Stat, how strict a dial) — lenses
/// and menus keep their own grammar. Themed by hand: no stock controls.
/// </summary>
public sealed class Segmented : Border
{
    public sealed record Option(string Id, string Label, string? Tip = null);

    private static readonly Brush TrackBg = Freeze("#1B2130");
    private static readonly Brush TrackLine = Freeze("#3A4560");
    private static readonly Brush OffFg = Freeze("#7F93AD");
    private static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(180);

    private readonly Grid _stage = new();
    private readonly Border _thumb;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly List<(Border Cell, TextBlock Text, Option Opt)> _cells = new();
    private readonly TranslateTransform _shift = new();
    private readonly Brush _onFg;
    private bool _placed;
    private readonly System.Windows.Threading.DispatcherTimer _after = new() { Interval = Slide + TimeSpan.FromMilliseconds(20) };
    private string? _pending;

    public string Selected { get; private set; }

    /// <summary>Fires after a click's slide has landed — never on <see cref="Select"/>.</summary>
    public event Action<string>? Changed;

    /// <param name="accent">Text color of the picked segment; the thumb is a
    /// dark tint of it.</param>
    public Segmented(IEnumerable<Option> options, string selected, string accent = "#4FC3F7")
    {
        _onFg = Freeze(accent);
        var c = (Color)ColorConverter.ConvertFromString(accent);
        var thumbBg = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        var thumbLine = new SolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B));
        thumbBg.Freeze(); thumbLine.Freeze();

        CornerRadius = new CornerRadius(11);
        Background = TrackBg;
        BorderBrush = TrackLine;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(2);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 0, 6, 4);
        SnapsToDevicePixels = true;

        _thumb = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = thumbBg,
            BorderBrush = thumbLine,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _shift,
            Width = 0,
        };
        _stage.Children.Add(_thumb);
        _stage.Children.Add(_row);
        Child = _stage;

        foreach (var opt in options)
        {
            var text = new TextBlock
            {
                Text = opt.Label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = OffFg,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Border
            {
                Child = text,
                Padding = new Thickness(11, 2, 11, 3),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = opt.Tip,
            };
            string id = opt.Id;
            cell.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (id == Selected) return;
                Select(id);
                // The pick's consequences (a board rebuild, say) can block
                // the UI thread longer than the slide — so the slide goes
                // first and the handler runs once it has landed. A second
                // click inside that window just retargets.
                _pending = id;
                _after.Stop();
                _after.Start();
            };
            cell.MouseEnter += (_, _) => { if (id != Selected) text.Foreground = _onFg; };
            cell.MouseLeave += (_, _) => { if (id != Selected) text.Foreground = OffFg; };
            _row.Children.Add(cell);
            _cells.Add((cell, text, opt));
        }
        Selected = selected;
        _after.Tick += (_, _) =>
        {
            _after.Stop();
            if (_pending is { } id) { _pending = null; Changed?.Invoke(id); }
        };
        Loaded += (_, _) => Place(animate: false);
        SizeChanged += (_, _) => Place(animate: false);
    }

    /// <summary>Move the pick (sliding) without raising <see cref="Changed"/>.</summary>
    public void Select(string id)
    {
        Selected = id;
        Place(animate: _placed && IsLoaded);
    }

    private void Place(bool animate)
    {
        foreach (var (_, text, opt) in _cells)
            text.Foreground = opt.Id == Selected ? _onFg : OffFg;
        var hit = _cells.FirstOrDefault(c => c.Opt.Id == Selected);
        if (hit.Cell is null || hit.Cell.ActualWidth <= 0) return;
        double x = hit.Cell.TranslatePoint(new Point(0, 0), _stage).X;
        double w = hit.Cell.ActualWidth;
        _thumb.Height = hit.Cell.ActualHeight;
        if (!animate)
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _thumb.BeginAnimation(WidthProperty, null);
            _shift.X = x;
            _thumb.Width = w;
        }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, Slide) { EasingFunction = ease });
            _thumb.BeginAnimation(WidthProperty,
                new DoubleAnimation(w, Slide) { EasingFunction = ease });
        }
        _placed = true;
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
