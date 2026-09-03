using System.Windows;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Character window: a thin host for <see cref="InventoryPanel"/>'s
/// analysis tabs — Sheet, Focus board, Best in slot. Owns the one thing a
/// panel can't: growing the WINDOW for the sheet's drill drawer.
/// </summary>
public partial class InventoryWindow : Window
{
    public static readonly string[] HostedTabs = { "sheet", "focus", "bis" };

    public InventoryWindow(string eqRoot, string charName, string server,
        SessionStats? session = null)
    {
        InitializeComponent();
        Interop.WindowTheme.ApplyDark(this);
        // "character": a fresh bounds key — the pre-merge Inventory sizes
        // don't fit the four-tab window.
        DialogPlacement.Persist(this, "character");
        Panel.DrawerExtendRequested = ExtendForDrawer;
        Panel.Attach(eqRoot, charName, server, session, HostedTabs);
    }

    /// <summary>Switch to a tab by id (sheet · focus · bis).</summary>
    public void ShowTab(string id) => Panel.ShowTab(id);

    /// <summary>Front the audit board (also the selftest hook).</summary>
    public void ShowFocusTab() => Panel.ShowFocusTab();

    // ---- the drill drawer extends the WINDOW itself ---------------------------

    private bool _drawerExtended;

    /// <summary>Grow (or shrink) the window's width by the sheet's drill
    /// drawer strip, animated — the drawer lives OUTSIDE the fixed canvas.</summary>
    private void ExtendForDrawer(bool extend)
    {
        Log.Info($"[sheet] drawer extend: {extend} (was {_drawerExtended}, width {Width:0})");
        if (extend == _drawerExtended) return;
        _drawerExtended = extend;
        double delta = CharacterSheetView.DrawerGrowth;
        double target = Width + (extend ? delta : -delta);
        var anim = new System.Windows.Media.Animation.DoubleAnimation(
            Width, target, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        // Release the animation's hold afterwards so the user can still resize.
        anim.Completed += (_, _) =>
        {
            BeginAnimation(WidthProperty, null);
            Width = target;
        };
        BeginAnimation(WidthProperty, anim);
    }

    /// <summary>Runs BEFORE the Closing event — the persisted bounds must
    /// never remember the drawer's borrowed width.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_drawerExtended)
        {
            BeginAnimation(WidthProperty, null);
            Width = Math.Max(MinWidth, Width - CharacterSheetView.DrawerGrowth);
            _drawerExtended = false;
        }
        base.OnClosing(e);
    }
}
