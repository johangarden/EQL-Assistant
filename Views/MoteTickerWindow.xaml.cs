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
/// The live mote-farming ticker: while a stint is running (a mote looted
/// within the last 15 minutes), a small card shows the stint's pace —
/// "14 motes · 26/h · 210 pts/h" with the grade breakdown and the clock.
/// The clock runs from the stint's FIRST drop to NOW, so the pace is what
/// you'd actually bank by keeping at it. Rates hold back for the first 10
/// minutes ("warming up") — the same honesty as the board, live. The
/// window only shows itself while a stint is live (or unlocked, so it can
/// be placed).
/// </summary>
public partial class MoteTickerWindow : Window
{
    /// <summary>A drop this long ago ends the stint (mirrors MoteFarm).</summary>
    private const double StintGapMin = MoteFarm.StintGapMin;

    /// <summary>Live pace needs a little clock before it means anything.</summary>
    private const double WarmupMin = 10;

    private readonly LootTracker _loot;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    public MoteTickerWindow(LootTracker loot, ConfigService configService, double opacity)
    {
        InitializeComponent();
        _loot = loot;
        Title = "EQL Assistant — Mote stint";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "moteTicker", Anchor.TopRight, 260, 90);

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
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

    /// <summary>The live stint, read fresh off the ledger: the newest mote,
    /// then older same-zone motes chained by ≤15-minute gaps.</summary>
    private (string Zone, DateTime First, int[] ByGrade)? LiveStint(DateTime now)
    {
        string zone = "";
        DateTime first = default, prev = default;
        int[]? byGrade = null;
        foreach (var e in _loot.Entries) // newest first
        {
            int g = MoteFarm.GradeOf(e.Item);
            if (g < 0) continue;
            if (byGrade is null)
            {
                if ((now - e.When).TotalMinutes > StintGapMin) return null; // stint over
                zone = e.Zone;
                byGrade = new int[MoteFarm.Grades.Length];
            }
            else if (!e.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase)
                     || (prev - e.When).TotalMinutes > StintGapMin)
            {
                break;
            }
            byGrade[g] += Math.Max(1, e.Count);
            first = prev = e.When;
        }
        return byGrade is null ? null : (zone, first, byGrade);
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var stint = LiveStint(now);

        if (stint is { } s)
        {
            int total = s.ByGrade.Sum();
            int points = 0;
            for (int i = 0; i < s.ByGrade.Length; i++)
                points += s.ByGrade[i] * MoteFarm.SpellPoints[i];
            double minutes = Math.Max(0.5, (now - s.First).TotalMinutes);

            ZoneText.Text = "MOTE STINT · " + s.Zone.ToUpperInvariant();
            BigText.Text = minutes >= WarmupMin
                ? $"{total} motes · {total * 60.0 / minutes:0}/h · {points * 60.0 / minutes:0} pts/h"
                : $"{total} mote(s) · warming up";
            SubText.Text = MoteFarm.FormatMinutes(minutes) + " · "
                + string.Join(" · ", Enumerable.Range(0, s.ByGrade.Length)
                    .Where(i => s.ByGrade[i] > 0)
                    .Select(i => $"{MoteFarm.Grades[i]} {s.ByGrade[i]}"));
        }

        Card.Visibility = stint is not null ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = !_locked && stint is null
            ? Visibility.Visible : Visibility.Collapsed;

        // Visible only while farming (or unlocked, for placement).
        bool show = !_hidden && (stint is not null || !_locked);
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
