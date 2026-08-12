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
/// Automatic enemy-DoT panel: one row per (spell, mob name) tracked by
/// <see cref="CombatParser.EnemyDots"/> — no trigger configuration. Rows show
/// "Curse — a froglok urd shaman ×2 · 14s" (the ×N chip counts same-named
/// mobs via tick frequency; the time counts down when the duration is known,
/// up when it isn't). The window only shows itself while it has rows (or
/// while unlocked, so it can be placed).
/// </summary>
public partial class EnemyDotsWindow : Window
{
    private sealed record RowVm(string Label, string TimeText, bool IsOverrun);
    private sealed record GroupVm(string Spell, List<RowVm> Rows);

    private readonly CombatParser _parser;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    public EnemyDotsWindow(CombatParser parser, ConfigService configService, double opacity)
    {
        InitializeComponent();
        _parser = parser;
        Title = "EQL Assistant — Enemy DoTs";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "enemyDots", Anchor.TopLeft, 420, 140);

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
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
        var rows = _parser.EnemyDots(DateTime.Now);
        GroupsControl.ItemsSource = rows
            .GroupBy(r => r.Spell, StringComparer.OrdinalIgnoreCase)
            .Select(grp => new GroupVm(grp.Key.ToUpperInvariant(), grp.Select(r => new RowVm(
                $"{r.Target} {r.Ordinal:00}",
                r.Overrun
                    ? $"+{r.OverrunSeconds:0}s" // past the estimate, fade unwitnessed
                    : r.RemainingSeconds is double rem
                        ? TimeSpan.FromSeconds(rem) is { TotalMinutes: >= 1 } ts
                            ? $"{(int)ts.TotalMinutes}:{ts.Seconds:00}"
                            : $"{rem:0}s"
                        : $"↑{r.SinceSeconds:0}s",
                r.Overrun)).ToList()))
            .ToList();

        Placeholder.Visibility = !_locked && rows.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Visible only when useful: rows exist, or unlocked for placement.
        bool show = !_hidden && (rows.Count > 0 || !_locked);
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
}
