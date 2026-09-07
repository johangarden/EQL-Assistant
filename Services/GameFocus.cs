namespace EQLOverlay.Services;

/// <summary>
/// "Hide the overlay while the game isn't in front" (owner idea, 7 Sep):
/// the HUD panels and the pinned pages are Topmost, so without this they
/// sit over the browser too. The game is recognised by WHERE its exe lives
/// — the followed log sits in "&lt;root&gt;\Logs", the game runs from
/// "&lt;root&gt;" — so no exe name is hard-coded. Every doubt keeps the
/// overlay visible: a log outside the standard layout, a foreground
/// process we may not read, our own windows.
/// </summary>
public static class GameFocus
{
    /// <summary>True when the overlay may stay visible for the current
    /// foreground process.</summary>
    public static bool Keep(string? foregroundExePath, int foregroundPid, int ownPid, string eqRoot)
    {
        if (foregroundPid == ownPid) return true;              // our own windows
        if (string.IsNullOrEmpty(eqRoot)) return true;         // can't tell the game apart
        if (string.IsNullOrEmpty(foregroundExePath)) return true; // protected process — don't guess
        string rootDir = eqRoot.TrimEnd('\\', '/') + '\\';
        return foregroundExePath.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Hysteresis for the watch: the alt-tab switcher and the taskbar
/// flash through the foreground for a moment — the overlay only hides once
/// the game has been away for a grace period, and returns at once.</summary>
public sealed class GameAwayTracker
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(1.2);
    private DateTime? _awaySince;

    public bool Away { get; private set; }

    public bool Update(bool gameInFront, DateTime now)
    {
        if (gameInFront)
        {
            _awaySince = null;
            Away = false;
        }
        else
        {
            _awaySince ??= now;
            if (now - _awaySince.Value >= Grace) Away = true;
        }
        return Away;
    }

    public void Reset()
    {
        _awaySince = null;
        Away = false;
    }
}
