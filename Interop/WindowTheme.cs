using System.Windows;
using System.Windows.Interop;

namespace EQLOverlay.Interop;

/// <summary>
/// Applies the dark Win32 chrome to standard (title-barred) dialog windows,
/// so the system title bar matches the in-window dark theme.
/// </summary>
public static class WindowTheme
{
    public static void ApplyDark(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle != 0)
            NativeMethods.UseDarkTitleBar(handle);
        else
            window.SourceInitialized += (_, _) =>
                NativeMethods.UseDarkTitleBar(new WindowInteropHelper(window).Handle);
    }
}
