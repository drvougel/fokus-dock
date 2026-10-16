using System;

namespace FokusDock;

/// <summary>Fenster platzieren: Mitte (mit Aktivierung) und Kachelplätze (ohne Aktivierung).</summary>
internal static class WindowPlacer
{
    /// <summary>Mitte-Fenster: in Position setzen, nach vorne holen und aktivieren.</summary>
    public static void Place(IntPtr hwnd, NativeRect rect)
    {
        Restore(hwnd, includeMinimized: true);
        SetPosition(hwnd, Native.HWND_TOP, rect, Native.SWP_SHOWWINDOW);
        Activate(hwnd);
    }

    /// <summary>
    /// Kachelplatz: Fenster in den Platz setzen, ohne Fokus zu stehlen.
    /// front = oberstes Fenster der App; die übrigen werden direkt dahinter gelegt (insertAfter = front).
    /// </summary>
    public static void PlaceInSlot(IntPtr hwnd, NativeRect rect, IntPtr insertAfter, bool front)
    {
        Restore(hwnd, includeMinimized: front);
        SetPosition(hwnd, insertAfter, rect, front ? Native.SWP_SHOWWINDOW : 0);
    }

    /// <summary>Nur die Stapelreihenfolge: das Fenster direkt hinter behindOf legen, Position unverändert.</summary>
    public static void PlaceBehind(IntPtr hwnd, IntPtr behindOf)
    {
        Native.SetWindowPos(hwnd, behindOf, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public static void Activate(IntPtr hwnd)
    {
        Restore(hwnd, includeMinimized: true);

        // Alt-Tastendruck hebt die Sperre für Vordergrundwechsel auf, damit der Fokus wirklich wechselt
        Native.keybd_event(Native.VK_MENU, 0, 0, IntPtr.Zero);
        Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, IntPtr.Zero);
        Native.SetForegroundWindow(hwnd);
    }

    private static void SetPosition(IntPtr hwnd, IntPtr insertAfter, NativeRect rect, uint extraFlags)
    {
        Native.SetWindowPos(hwnd, insertAfter, rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top, Native.SWP_NOACTIVATE | extraFlags);
    }

    private static void Restore(IntPtr hwnd, bool includeMinimized)
    {
        bool minimized = includeMinimized && Native.IsIconic(hwnd);
        if (minimized || Native.IsZoomed(hwnd))
        {
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
        }
    }
}
