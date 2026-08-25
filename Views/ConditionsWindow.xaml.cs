using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Big-badge crowd-control panel: while you are stunned / feared / charmed /
/// mesmerized, a large glyph badge sits on screen for the whole duration
/// (landing line to wear-off line — see <see cref="ConditionWatcher"/>).
/// Materializes only while a condition is active, or while unlocked so it
/// can be placed.
/// </summary>
public partial class ConditionsWindow : Window
{
    private sealed record BadgeVm(string Kind, string Elapsed, Brush Stroke, Geometry Glyph);

    private readonly ConditionWatcher _watcher;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    // Type-owned condition colors + hand-drawn glyphs (24×24 boxes).
    private static readonly Dictionary<string, (Brush Stroke, Geometry Glyph)> Badges = new()
    {
        [ConditionWatcher.Stunned] = (Freeze("#FFD54F"), Geometry.Parse(
            // 8-point starburst — the classic "seeing stars".
            "M12 0 L14.6 8.1 L22.4 4.4 L17 11 L24 12 L17 13 L22.4 19.6 L14.6 15.9 " +
            "L12 24 L9.4 15.9 L1.6 19.6 L7 13 L0 12 L7 11 L1.6 4.4 L9.4 8.1 Z")),
        [ConditionWatcher.Feared] = (Freeze("#E57373"), Geometry.Parse(
            // warning triangle with an exclamation cut out (even-odd holes)
            "M12 1 L23.5 21.5 L0.5 21.5 Z " +
            "M10.9 8 L13.1 8 L12.6 15 L11.4 15 Z " +
            "M10.8 17.6 A1.2 1.2 0 1 0 13.2 17.6 A1.2 1.2 0 1 0 10.8 17.6 Z")),
        [ConditionWatcher.Charmed] = (Freeze("#F06292"), Geometry.Parse(
            "M12 21 C5 14 2 10.5 2 7.3 C2 4.4 4.2 2.5 6.8 2.5 C8.8 2.5 10.8 3.7 12 5.7 " +
            "C13.2 3.7 15.2 2.5 17.2 2.5 C19.8 2.5 22 4.4 22 7.3 C22 10.5 19 14 12 21 Z")),
        [ConditionWatcher.Mezzed] = (Freeze("#64B5F6"), Geometry.Parse(
            // crescent moon
            "M14.5 2 A10 10 0 1 0 14.5 22 A8 8 0 1 1 14.5 2 Z")),
        [ConditionWatcher.Interrupted] = (Freeze("#FFB74D"), Geometry.Parse(
            // a cast bar snapped by a bolt
            "M1.5 10.5 L8.5 10.5 L8.5 13.5 L1.5 13.5 Z M15.5 10.5 L22.5 10.5 L22.5 13.5 L15.5 13.5 Z " +
            "M14 2 L9 11.5 L11.7 11.5 L10 22 L15 12.5 L12.3 12.5 Z")),
        [ConditionWatcher.Resisted] = (Freeze("#4DD0E1"), Geometry.Parse(
            // a shield, the spell turned away (even-odd X cut)
            "M12 1.5 L20.5 4.8 L20.5 11 C20.5 17 17 20.7 12 22.5 C7 20.7 3.5 17 3.5 11 L3.5 4.8 Z " +
            "M8.6 7.2 L12 10.6 L15.4 7.2 L16.8 8.6 L13.4 12 L16.8 15.4 L15.4 16.8 L12 13.4 " +
            "L8.6 16.8 L7.2 15.4 L10.6 12 L7.2 8.6 Z")),
    };

    public ConditionsWindow(ConditionWatcher watcher, ConfigService configService, double opacity)
    {
        InitializeComponent();
        _watcher = watcher;
        Title = "EQL Assistant — Conditions";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        // Default near screen centre — a stun badge must land where you look.
        _placement = new PanelPlacement(this, configService, "conditions", Anchor.TopLeft, 760, 300);

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _tick.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { _placement.Attach(); ApplyLockVisual(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        Closed += (_, _) => _tick.Stop();
    }

    /// <summary>Global show/hide (tray Panels toggle / hide-all).</summary>
    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        Refresh();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ApplyClickThrough();
        ApplyLockVisual();
        Refresh();
    }

    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();

    private void Refresh()
    {
        var active = _watcher.Active(DateTime.Now);
        BadgesControl.ItemsSource = active
            .Where(v => Badges.ContainsKey(v.Kind))
            // Moment badges (interrupt/resist) name the SPELL where a state
            // badge counts its seconds.
            .Select(v => new BadgeVm(v.Kind,
                v.Detail.Length > 0 ? v.Detail : $"+{v.ElapsedSeconds:0}s",
                Badges[v.Kind].Stroke, Badges[v.Kind].Glyph))
            .ToList();

        Placeholder.Visibility = !_locked && active.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        bool show = !_hidden && (active.Count > 0 || !_locked);
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) Hide();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero)
            NativeMethods.SetClickThrough(_hwnd, _locked);
    }

    private void ApplyLockVisual()
    {
        Header.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        RootBorder.Background = _locked ? Brushes.Transparent : UnlockedBackdrop;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked)
            DragMove();
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
