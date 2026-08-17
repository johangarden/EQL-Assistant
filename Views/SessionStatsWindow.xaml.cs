using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Session stats panel: XP lvl/hr, AA/hr, next-level ETA and mote drop rates
/// over a chosen slice of the record (see <see cref="SessionStats"/>). The
/// slice picker doubles as the title; the pills below scope the tier and the
/// rate's denominator. Scope changes are persisted by MainWindow via
/// <see cref="SettingsChanged"/>.
/// </summary>
public partial class SessionStatsWindow : Window
{
    private static readonly (SessionStats.Slice Slice, string Label)[] Slices =
    {
        (SessionStats.Slice.ZoneSession, "Zone + Session"),
        (SessionStats.Slice.Session, "Session"),
        (SessionStats.Slice.Zone, "Zone"),
        (SessionStats.Slice.All, "All"),
    };

    private static readonly Brush SegOnBg = Freeze("#16283E");
    private static readonly Brush SegOnFg = Freeze("#4FC3F7");
    private static readonly Brush SegOffFg = Freeze("#7F93AD");

    private readonly SessionStats _stats;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;
    private bool _loading;

    private SessionStats.Slice _slice;
    private bool _exactTier;
    private SessionStats.Basis _basis;

    /// <summary>Raised when the user changes slice / tier / basis — the host persists.</summary>
    public event Action<SessionStats.Slice, bool, SessionStats.Basis>? SettingsChanged;

    public SessionStatsWindow(SessionStats stats, ConfigService configService, double opacity,
        SessionStats.Slice slice, bool exactTier, SessionStats.Basis basis)
    {
        InitializeComponent();
        _stats = stats;
        _slice = slice;
        _exactTier = exactTier;
        _basis = basis;
        Title = "EQL Assistant — Session stats";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "sessionstats", Anchor.TopRight, 60, 320);

        _loading = true;
        foreach (var (_, label) in Slices) SliceBox.Items.Add(label);
        int idx = Array.FindIndex(Slices, s => s.Slice == _slice);
        SliceBox.SelectedIndex = idx >= 0 ? idx : 0;
        _loading = false;

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _tick.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { _placement.Attach(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        Closed += (_, _) => _tick.Stop();
    }

    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        Refresh();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ApplyClickThrough();
        // The picker and pills only respond while unlocked (click-through
        // covers the mouse; enabling tracks it for keyboard/visual honesty).
        SliceBox.IsEnabled = !locked;
        Refresh();
    }

    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();

    public void Refresh()
    {
        bool show = !_hidden;
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) { Hide(); return; }
        if (!show) return;

        var view = _stats.Snapshot(DateTime.Now, _slice, _exactTier, _basis);
        RowsControl.ItemsSource = view.Rows;
        LevelText.Text = view.LevelText;
        LevelText.ToolTip = view.LevelTip.Length > 0 ? view.LevelTip : null;
        string caption = view.Rows.Count == 0 ? view.Caption : $"{view.Caption} · {view.Span}";
        if (view.Rows.Count > 0 && !view.Measurable) caption += " · too short to rate";
        CaptionText.Text = caption;

        PaintPill(TierBtn, TierBtnText, _exactTier ? "THIS TIER" : "EVERY TIER", _exactTier);
        TierBtn.ToolTip = _exactTier
            ? "Counting only the tier you are standing in — visits under any other spelling of the zone name are left out."
            : "Counting every visit to this camp at any tier — difficulty and instance are folded away.";
        PaintPill(BasisBtn, BasisBtnText, _basis == SessionStats.Basis.Active ? "ACTIVE" : "ELAPSED",
            _basis == SessionStats.Basis.Active);
        BasisBtn.ToolTip = _basis == SessionStats.Basis.Active
            ? "Active time: the stretch minus every 5+ minute silence and every logout — how fast the camp pays while you work it."
            : "Elapsed time: the whole stretch minus only logouts — medding, banking and travel stay in, because you spent them.";
    }

    private static void PaintPill(Border pill, TextBlock text, string label, bool on)
    {
        text.Text = label;
        pill.Background = on ? SegOnBg : Brushes.Transparent;
        text.Foreground = on ? SegOnFg : SegOffFg;
    }

    private void Slice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SliceBox.SelectedIndex < 0) return;
        _slice = Slices[SliceBox.SelectedIndex].Slice;
        SettingsChanged?.Invoke(_slice, _exactTier, _basis);
        Refresh();
    }

    private void Tier_Click(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        _exactTier = !_exactTier;
        SettingsChanged?.Invoke(_slice, _exactTier, _basis);
        Refresh();
    }

    private void Basis_Click(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        _basis = _basis == SessionStats.Basis.Active
            ? SessionStats.Basis.Elapsed : SessionStats.Basis.Active;
        SettingsChanged?.Invoke(_slice, _exactTier, _basis);
        Refresh();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero)
            NativeMethods.SetClickThrough(_hwnd, _locked);
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
