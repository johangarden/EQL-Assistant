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
/// The incoming-damage panel: a HUD window like the enemy DoTs — locked it
/// is click-through and vanishes while nothing hits you; unlocked it keeps
/// a placeholder so it can be placed. Refreshes four times a second from
/// the <see cref="IncomingWatch"/>.
/// </summary>
public partial class IncomingWindow : Window
{
    private static readonly Brush MeleeFill = Freeze("#E57373");
    private static readonly Brush SpellFill = Freeze("#9575CD");
    // The stance pill IS the verdict: green = your stance halves the bigger
    // half, red = the other one would (and the pill names it), grey = mixed
    // or nothing taken (owner, 15 Sep — the verdict text below was bloat).
    private static readonly Brush ChipDim = Freeze("#C9D4E3");
    private static readonly Brush ChipDimBg = Freeze("#232B3D");
    private static readonly Brush ChipDimBorder = Freeze("#3A4560");
    private static readonly Brush ChipOk = Freeze("#7CE07C");
    private static readonly Brush ChipOkBg = Freeze("#1A2A1E");
    private static readonly Brush ChipOkBorder = Freeze("#2E6B48");
    private static readonly Brush ChipSwitch = Freeze("#FF8A80");
    private static readonly Brush ChipSwitchBg = Freeze("#2A1416");
    private static readonly Brush ChipSwitchBorder = Freeze("#7A2E33");
    private static readonly Brush UnlockedBackdrop = Freeze("#F0141A24");
    private static readonly Brush LockedBackdrop = Freeze("#E0141A24");

    private readonly IncomingWatch _watch;
    private readonly Func<string> _stance;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    public IncomingWindow(IncomingWatch watch, Func<string> stance, ConfigService configService, double opacity, int windowSec)
    {
        InitializeComponent();
        _watch = watch;
        _stance = stance;
        _watch.WindowSec = windowSec;
        Title = "EQL Assistant — Incoming damage";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "incoming", Anchor.TopLeft, 420, 300);

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { _placement.Attach(); ApplyLockVisual(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        Closed += (_, _) => _tick.Stop(); // panel law: timers die with the window
        Refresh(); // a never-shown window (selftest) still renders honestly
    }

    public void ApplySettings(int windowSec, double opacity)
    {
        _watch.WindowSec = windowSec;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        Refresh();
    }

    public void SetHidden(bool hidden) { _hidden = hidden; Refresh(); }
    public void SetLocked(bool locked) { _locked = locked; ApplyClickThrough(); ApplyLockVisual(); Refresh(); }
    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();

    /// <summary>The verdict kind last painted ("switch" / "ok" / "mixed" / "") — selftest.</summary>
    public string LastKind { get; private set; } = "";
    /// <summary>The stance pill's text last painted ("DEFENSIVE ▸ MAGE HUNTER") — selftest.</summary>
    public string LastChip { get; private set; } = "";

    public void Refresh()
    {
        var s = _watch.Take(DateTime.Now, _stance());
        TitleText.Text = $"INCOMING · LAST {s.WindowSec}S";
        AxisLeft.Text = $"−{s.WindowSec}s";
        Placeholder.Text = $"No damage taken in the last {s.WindowSec} s.";

        string st = s.Stance.Trim();
        string stanceLabel = st.Length > 0 ? st.ToUpperInvariant() : "STANCE ?";
        string target = s.VerdictKind == "switch" ? (s.SpellShare >= 0.6 ? "MAGE HUNTER" : "DEFENSIVE") : "";
        StanceText.Text = target.Length > 0 ? $"{stanceLabel} ▸ {target}" : stanceLabel;
        (StanceText.Foreground, StanceChip.Background, StanceChip.BorderBrush) = s.VerdictKind switch
        {
            "ok" => (ChipOk, ChipOkBg, ChipOkBorder),
            "switch" => (ChipSwitch, ChipSwitchBg, ChipSwitchBorder),
            _ => (ChipDim, ChipDimBg, ChipDimBorder),
        };
        LastChip = StanceText.Text;

        bool any = s.Any;
        Body.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = !any && !_locked ? Visibility.Visible : Visibility.Collapsed;
        LastKind = s.VerdictKind;

        if (any)
        {
            MeleeText.Text = $"−{s.Melee:N0} melee {s.MeleeShare * 100:0}%";
            SpellText.Text = $"{s.SpellShare * 100:0}% spells −{s.Spell:N0}";
            MeleeCol.Width = new GridLength(Math.Max(0.0001, s.Melee), GridUnitType.Star);
            SpellCol.Width = new GridLength(Math.Max(0.0001, s.Spell), GridUnitType.Star);
            MeleeBar.Visibility = s.Melee > 0 ? Visibility.Visible : Visibility.Collapsed;
            SpellBar.Visibility = s.Spell > 0 ? Visibility.Visible : Visibility.Collapsed;

            double max = 1;
            for (int i = 0; i < s.WindowSec; i++) max = Math.Max(max, s.MeleeCols[i] + s.SpellCols[i]);
            Columns.Columns = s.WindowSec;
            Columns.Children.Clear();
            for (int i = 0; i < s.WindowSec; i++)
            {
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0) };
                double sh = s.SpellCols[i] / max * 36, mh = s.MeleeCols[i] / max * 36;
                if (sh > 0.5) col.Children.Add(new Border { Height = sh, Background = SpellFill, Opacity = 0.9 });
                if (mh > 0.5) col.Children.Add(new Border { Height = mh, Background = MeleeFill, Opacity = 0.9 });
                Columns.Children.Add(col);
            }

        }

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
