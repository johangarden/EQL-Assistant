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
/// The mez panel: the enemy DoTs' cousin — a row per mezzed mob from the
/// <see cref="CrowdControl"/> engine. Locked it is click-through and hides
/// while the list is empty; unlocked it keeps a placeholder. Repaints on
/// every engine change and ticks the clocks four times a second.
/// </summary>
public partial class MezWindow : Window
{
    public sealed record RowVm(string Label, string TimeText, double FillWidth, string SubText, string RightText,
        bool Due, bool Assumed, bool Broke, bool First);

    private const double TrackWidth = 296;
    private static readonly Brush UnlockedBackdrop = Freeze("#F0141A24");
    private static readonly Brush LockedBackdrop = Freeze("#E0141A24");
    private static readonly Brush Amber = Freeze("#FFB74D");
    private static readonly Brush Red = Freeze("#FF8A80");
    private static readonly Brush Faint = Freeze("#5C6B82");

    private readonly CrowdControl _cc;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    public MezWindow(CrowdControl cc, ConfigService configService, double opacity)
    {
        InitializeComponent();
        _cc = cc;
        Title = "EQL Assistant — Mez";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "mez", Anchor.TopLeft, 740, 620);

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Refresh();
        _cc.Changed += OnChanged;
        Closed += (_, _) => { _tick.Stop(); _cc.Changed -= OnChanged; }; // panel law

        Loaded += (_, _) => { _placement.Attach(); ApplyLockVisual(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        Refresh();
    }

    private void OnChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Send);
    }

    public void ApplySettings(double opacity)
    {
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        Refresh();
    }

    public void SetHidden(bool hidden) { _hidden = hidden; Refresh(); }
    public void SetLocked(bool locked) { _locked = locked; ApplyClickThrough(); ApplyLockVisual(); Refresh(); }
    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();

    /// <summary>Rows last painted — selftest.</summary>
    public IReadOnlyList<RowVm> Rows { get; private set; } = Array.Empty<RowVm>();

    /// <summary>One row's texts and fill, pure for the selftest.</summary>
    public static RowVm Row(CrowdControl.MezView m, bool first)
    {
        string time = m.Broke ? "BROKE" : m.Duration > 0 ? CharmWindow.Clock(Math.Max(0, m.Left)) + (m.Assumed ? "?" : "")
            : CharmWindow.Clock(m.Held) + "↑";
        double frac = m.Broke ? 1 : m.Duration > 0 ? Math.Clamp(m.Left / m.Duration, 0, 1) : 1;
        string sub = m.Broke ? $"{m.BrokeBy} hit it for {m.BrokeAmount:N0} · {m.SinceBreak:0} s ago"
            : m.Assumed ? "assumed — landing line not yet seen"
            : m.Due ? "re-mez now"
            : $"landed {m.Held:0} s ago";
        string right = m.Broke ? $"held {CharmWindow.Clock(m.Held)}"
            : m.Duration > 0 ? $"of {CharmWindow.Clock(m.Duration)}" : "no known clock";
        return new RowVm(m.Label, time, Math.Round(TrackWidth * frac), sub, right, m.Due, m.Assumed, m.Broke, first);
    }

    public void Refresh()
    {
        var s = _cc.Take(DateTime.Now);
        bool any = s.Mez.Count > 0;
        Rows = s.Mez.Select((m, i) => Row(m, i == 0)).ToList();
        RowsControl.ItemsSource = Rows;
        RowsControl.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = !any && !_locked ? Visibility.Visible : Visibility.Collapsed;

        int held = s.Held, broken = s.Broken;
        string spell = s.Mez.Select(m => m.Spell).Distinct().Count() == 1 ? " · " + s.Mez[0].Spell : "";
        SummaryText.Text = !any ? "nothing held"
            : (held > 0 ? $"{held} held" : "") + (broken > 0 ? (held > 0 ? " · " : "") + $"{broken} broken" : "") + spell;
        if (broken > 0) { NextText.Text = "broke!"; NextText.Foreground = Red; }
        else if (s.Next is { } n) { NextText.Text = n.Due ? "re-mez now" : $"re-mez in {Math.Max(0, n.Left - CrowdControl.LastStretch(n.Duration)):0} s"; NextText.Foreground = Amber; }
        else { NextText.Text = any ? "—" : ""; NextText.Foreground = Faint; }

        bool show = !_hidden && (any || !_locked);
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) Hide();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero) NativeMethods.SetClickThrough(_hwnd, _locked);
    }

    private void ApplyLockVisual()
    {
        RootBorder.Background = _locked ? LockedBackdrop : UnlockedBackdrop;
        HeaderRow.Cursor = _locked ? Cursors.Arrow : Cursors.SizeAll;
        HeaderRow.ToolTip = _locked ? null : "Drag to place — lock the overlay when done";
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked) DragMove();
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
