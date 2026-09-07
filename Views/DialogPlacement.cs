using System.Windows;
using System.Windows.Controls;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Remembers a dialog window's size and position across sessions: call once
/// in the constructor (after InitializeComponent, before Show). Restores the
/// last dragged bounds when they still land on a screen, and saves them on
/// close — the RESTORE bounds, so a maximized close remembers both the
/// maximized state and the size to come back to.
/// </summary>
public static class DialogPlacement
{
    private static readonly Lazy<ConfigService> SharedConfig = new(() => new ConfigService());

    /// <param name="positionOnly">For fixed-size windows: restore and save
    /// only WHERE it sits, never how big it is.</param>
    public static void Persist(Window window, string key, bool positionOnly = false)
    {
        var config = SharedConfig.Value;
        var saved = config.LoadDialogBounds(key);
        if (saved is { Pinned: true }) window.Topmost = true; // the pin outlives a lost monitor
        if (saved is { } b && OnScreen(b))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = b.Left;
            window.Top = b.Top;
            if (!positionOnly)
            {
                window.Width = b.Width;
                window.Height = b.Height;
                if (b.Maximized) window.WindowState = WindowState.Maximized;
            }
        }

        window.Closing += (_, _) => Save(window, key);
    }

    /// <summary>Write the window's current restore bounds + pin state now.</summary>
    public static void Save(Window window, string key)
    {
        var r = window.RestoreBounds;
        if (r.Width < 100 || r.Height < 80)
        {
            // Never persist a degenerate size — but a pin flipped before the
            // window has laid out still deserves remembering.
            if (SharedConfig.Value.LoadDialogBounds(key) is { } old)
                SharedConfig.Value.SaveDialogBounds(key, old with { Pinned = window.Topmost });
            return;
        }
        SharedConfig.Value.SaveDialogBounds(key, new ConfigService.DialogBounds(
            r.Left, r.Top, r.Width, r.Height, window.WindowState == WindowState.Maximized, window.Topmost));
    }

    /// <summary>The title-row pin for a page window (owner ruling, 7 Sep —
    /// "each time I went to my bags the window disappeared behind the
    /// game"): click toggles Topmost, the state rides in the same bounds
    /// file as the window's position, so a page pinned once stays pinned.
    /// Dock it Right as the title row's FIRST child so it sits outermost.</summary>
    public static PagePin Pin(Window window, string key)
    {
        var pin = new PagePin(window, () => Save(window, key));
        DockPanel.SetDock(pin, Dock.Right);
        return pin;
    }

    /// <summary>A monitor may have left since last time — restore only when a
    /// meaningful slice of the window still lands on the virtual desktop.</summary>
    private static bool OnScreen(ConfigService.DialogBounds b)
    {
        if (b.Width < 100 || b.Height < 80) return false;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth;
        double vb = vt + SystemParameters.VirtualScreenHeight;
        const double keep = 60;
        return b.Left + b.Width > vl + keep && b.Left < vr - keep
            && b.Top >= vt - 10 && b.Top < vb - keep;
    }
}
