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
/// The charm card: a HUD window like the incoming panel — locked it is
/// click-through and vanishes while you hold no charm and nothing failed;
/// unlocked it keeps a placeholder so it can be placed. Repaints on every
/// engine change and ticks the clock four times a second.
/// </summary>
public partial class CharmWindow : Window
{
    private static readonly Brush Violet = Freeze("#B39DDB");
    private static readonly Brush VioletFill = Freeze("#9575CD");
    private static readonly Brush VioletChip = Freeze("#2A3350");
    private static readonly Brush Red = Freeze("#FF8A80");
    private static readonly Brush RedFill = Freeze("#E57373");
    private static readonly Brush RedChip = Freeze("#3A1B1E");
    private static readonly Brush Amber = Freeze("#FFB74D");
    private static readonly Brush AmberChip = Freeze("#3A2E14");
    private static readonly Brush AmberBorder = Freeze("#7A5B22");
    private static readonly Brush AmberBg = Freeze("#2A2210");
    private static readonly Brush AmberText = Freeze("#F0D9A8");
    private static readonly Brush RedBorder = Freeze("#7A2E33");
    private static readonly Brush RedBg = Freeze("#2A1416");
    private static readonly Brush RedText = Freeze("#F5C6C2");
    private static readonly Brush Gold = Freeze("#FFD54F");
    private static readonly Brush Grey = Freeze("#9AA3AF");
    private static readonly Brush UnlockedBackdrop = Freeze("#F0141A24");
    private static readonly Brush LockedBackdrop = Freeze("#E0141A24");

    private readonly CrowdControl _cc;
    private readonly CharmBook? _book;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    public CharmWindow(CrowdControl cc, ConfigService configService, double opacity, CharmBook? book = null)
    {
        InitializeComponent();
        _cc = cc;
        _book = book;
        Title = "EQL Assistant — Charm";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "charm", Anchor.TopLeft, 420, 620);

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

    /// <summary>The state last painted: "hold" / "broke" / "failed" / "" — selftest.</summary>
    public string LastState { get; private set; } = "";

    public static string Clock(double s) => $"{(int)(s / 60)}:{(int)(s % 60):00}";

    /// <summary>The card's clock and bar for a holding charm (pure — selftest):
    /// a known ceiling counts DOWN (owner, 21 Sep) and depletes the bar; past
    /// it the clock reads "+m:ss" grey (the wear-off will come); no ceiling
    /// counts up on a faint full bar.</summary>
    public static (string Clock, double Fill, bool Overrun, string Spell) ClockFor(CrowdControl.CharmView c, bool learned)
    {
        if (c.Left is not { } left)
            return (Clock(c.Held) + "↑", 1, false, $"{c.Spell} · counts up, no known ceiling");
        if (left < 0)
            return ("+" + Clock(-left), 1, true, $"{c.Spell} · past the {Clock(c.Ceiling)} ceiling — held until it breaks");
        return (Clock(left), Math.Clamp(left / c.Ceiling, 0, 1), false, $"{c.Spell} · {Clock(c.Ceiling)} ceiling{(learned ? " · learned" : "")}");
    }

    public static string PaceLine(CrowdControl.CharmView c) =>
        c.PetHits == 0 ? "no hits yet" : $"{c.Dps:0} DPS · {c.DamagePerHit:N0}/hit · max {c.MaxHit:N0} · {c.PetHits} hits";

    public static string HistoryLine(CharmBook.MobStats? s) =>
        s is null ? "" : $"before: {s.Charms}× · avg {Clock(s.AvgHeldSec)} · best {Clock(s.LongestSec)} · {s.Dps:0} DPS · {s.Kills} kills";

    public void Refresh()
    {
        var s = _cc.Take(DateTime.Now);
        bool any = s.Charm is not null || s.Attempt is not null;
        Body.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = !any && !_locked ? Visibility.Visible : Visibility.Collapsed;

        if (s.Charm is { } c)
        {
            bool broke = c.Broke;
            LastState = broke ? "broke" : "hold";
            ChipText.Text = broke ? "BROKE" : c.Assumed ? "CHARM?" : "CHARM";
            ChipText.Foreground = broke ? Red : Violet;
            ChipBox.Background = broke ? RedChip : VioletChip;
            PetText.Text = c.Pet;
            var (clock, fill, overrun, spellLine) = ClockFor(c, _cc.LearnedDuration(c.Spell) is not null);
            AgeText.Text = broke ? Clock(c.Held) : clock;
            AgeText.Foreground = broke ? Red : overrun ? Grey : Gold;
            SpellText.Text = broke ? $"{c.Spell} · broke {c.SinceBreak:0} s ago"
                : c.Assumed ? $"{c.Spell} · assumed — landing line not yet seen"
                : spellLine;
            TrackRow.Visibility = Visibility.Visible;
            MetaRow.Visibility = Visibility.Visible;
            double f = broke ? 0.02 : fill;
            FillCol.Width = new GridLength(Math.Max(0.0001, f), GridUnitType.Star);
            RestCol.Width = new GridLength(Math.Max(0.0001, 1 - f), GridUnitType.Star);
            FillBar.Background = broke ? RedFill : overrun ? Grey : VioletFill;
            FillBar.Opacity = c.Ceiling > 0 || broke ? (overrun ? 0.5 : 1) : 0.35;
            HeldText.Text = $"held {Clock(c.Held)}";
            CeilingText.Text = c.Ceiling > 0 ? $"ceiling {Clock(c.Ceiling)}" : "no known ceiling";
            StatsRow.Visibility = broke ? Visibility.Collapsed : Visibility.Visible;
            DealtText.Text = $"pet dealt {c.PetDamage:N0}";
            KillsText.Text = c.PetKills == 1 ? "1 kill" : $"{c.PetKills} kills";
            LastHitText.Text = c.SinceLastHit is { } lh ? $"last hit {lh:0} s ago" : "no hits yet";
            PaceText.Visibility = broke ? Visibility.Collapsed : Visibility.Visible;
            PaceText.Text = PaceLine(c);
            string hist = HistoryLine(_book?.Stats(c.Pet));
            HistoryText.Text = hist;
            HistoryText.Visibility = hist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            VerdictBox.Visibility = broke ? Visibility.Visible : Visibility.Collapsed;
            if (broke)
            {
                VerdictBox.BorderBrush = RedBorder; VerdictBox.Background = RedBg;
                VerdictTitle.Text = $"Recast {c.Spell}";
                VerdictTitle.Foreground = Red;
                VerdictText.Text = $"{c.Pet} is on you — or root it. The bar keeps the time it held.";
                VerdictText.Foreground = RedText;
            }
        }
        else if (s.Attempt is { } a)
        {
            LastState = "failed";
            ChipText.Text = "NO PET";
            ChipText.Foreground = Amber;
            ChipBox.Background = AmberChip;
            PetText.Text = a.Target.Length > 0 ? a.Target : "your target";
            AgeText.Text = "";
            SpellText.Text = $"{a.Spell} · {a.How}";
            TrackRow.Visibility = Visibility.Collapsed;
            MetaRow.Visibility = Visibility.Collapsed;
            StatsRow.Visibility = Visibility.Collapsed;
            PaceText.Visibility = Visibility.Collapsed;
            string hist = HistoryLine(_book?.Stats(a.Target));
            HistoryText.Text = hist;
            HistoryText.Visibility = hist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            VerdictBox.Visibility = Visibility.Visible;
            VerdictBox.BorderBrush = AmberBorder; VerdictBox.Background = AmberBg;
            VerdictTitle.Text = $"{Cap(a.How)} {a.Ago:0} s ago";
            VerdictTitle.Foreground = Amber;
            VerdictText.Text = a.How == "resisted" ? "The mob shrugged it off — try again, or a lower one." : "The cast never finished — try again.";
            VerdictText.Foreground = AmberText;
        }
        else
        {
            LastState = "";
            ChipText.Text = "CHARM";
            ChipText.Foreground = Violet;
            ChipBox.Background = VioletChip;
            PetText.Text = "no charmed pet";
            AgeText.Text = "";
            SpellText.Text = "";
            PaceText.Visibility = Visibility.Collapsed;
            HistoryText.Visibility = Visibility.Collapsed;
        }

        bool show = !_hidden && (any || !_locked);
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) Hide();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

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
