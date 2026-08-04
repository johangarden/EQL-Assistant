using System.Runtime.InteropServices;

namespace EQLOverlay.Interop;

/// <summary>
/// Thin P/Invoke layer for the Win32 calls we need to make a proper
/// game overlay: extended window styles (click-through, no alt-tab) and
/// system-wide hotkeys.
/// </summary>
internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;

    // Extended window styles.
    public const int WS_EX_TRANSPARENT = 0x00000020; // mouse events pass through
    public const int WS_EX_LAYERED     = 0x00080000; // required alongside transparent
    public const int WS_EX_TOOLWINDOW  = 0x00000080; // keep out of Alt-Tab list
    public const int WS_EX_NOACTIVATE  = 0x08000000; // never steal focus from the game

    // RegisterHotKey modifiers.
    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_CONTROL  = 0x0002;
    public const uint MOD_SHIFT    = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    // 32/64-bit safe wrappers.
    public static nint GetWindowLongAuto(nint hWnd, int nIndex) =>
        nint.Size == 8 ? GetWindowLongPtr(hWnd, nIndex) : GetWindowLong(hWnd, nIndex);

    public static nint SetWindowLongAuto(nint hWnd, int nIndex, nint dwNewLong) =>
        nint.Size == 8
            ? SetWindowLongPtr(hWnd, nIndex, dwNewLong)
            : SetWindowLong(hWnd, nIndex, (int)dwNewLong);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>
    /// Force a window to the foreground even when spawned from a background /
    /// no-activate app (uses the AttachThreadInput trick to bypass the foreground lock).
    /// </summary>
    public static void ForceForeground(nint hWnd)
    {
        try
        {
            nint fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint cur = GetCurrentThreadId();
            bool attached = fgThread != cur && AttachThreadInput(cur, fgThread, true);
            SetForegroundWindow(hWnd);
            if (attached) AttachThreadInput(cur, fgThread, false);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Turn mouse click-through on or off for the given window.</summary>
    public static void SetClickThrough(nint hWnd, bool enabled)
    {
        nint ex = GetWindowLongAuto(hWnd, GWL_EXSTYLE);
        long style = ex.ToInt64();

        style |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (enabled)
            style |= WS_EX_TRANSPARENT;
        else
            style &= ~(long)WS_EX_TRANSPARENT;

        SetWindowLongAuto(hWnd, GWL_EXSTYLE, new nint(style));
    }
}
