using System;
using System.Runtime.InteropServices;

namespace FokusDock;

/// <summary>Fenster platzieren: Mitte (mit Aktivierung) und Kachelplätze (ohne Aktivierung).</summary>
internal static class WindowPlacer
{
    /// <summary>Windows-Fehlercode des letzten Platzierens (5 = Zugriff verweigert, z. B. Fenster mit Administratorrechten), 0 nach Erfolg.</summary>
    public static int LastError { get; private set; }

    /// <summary>Ob das letzte Platzieren an „Zugriff verweigert“ scheiterte: das Fenster läuft erhöht und lässt sich nicht verschieben.</summary>
    public static bool LastFailureWasAccessDenied => LastError == 5;

    /// <summary>
    /// Mitte-Fenster: in Position setzen, nach vorne holen und aktivieren.
    /// Liefert false, wenn das Fenster sich nicht verschieben ließ; dann wird es auch nicht aktiviert.
    /// </summary>
    public static bool Place(IntPtr hwnd, NativeRect rect)
    {
        Restore(hwnd, includeMinimized: true);
        ClearTopmost(hwnd);
        if (!SetPosition(hwnd, Native.HWND_TOP, rect, Native.SWP_SHOWWINDOW))
        {
            return false;
        }
        Activate(hwnd);
        return true;
    }

    /// <summary>
    /// Mitte-Fenster an eine neue Fläche anpassen (z. B. nach einer Änderung des Arbeitsbereichs): nur Position und Größe,
    /// ohne Aktivierung und ohne die Stapelreihenfolge zu ändern. Minimierte und maximierte Fenster bleiben unberührt.
    /// </summary>
    public static void Move(IntPtr hwnd, NativeRect rect)
    {
        if (Native.IsIconic(hwnd) || Native.IsZoomed(hwnd))
        {
            return;
        }
        _ = SetPosition(hwnd, Native.HWND_TOP, rect, Native.SWP_NOZORDER);
    }

    /// <summary>
    /// Kachelplatz: Fenster in den Platz setzen, ohne Fokus zu stehlen.
    /// front = oberstes Fenster der App; die übrigen werden direkt dahinter gelegt (insertAfter = front).
    /// Liefert false, wenn das Fenster sich nicht verschieben ließ.
    /// </summary>
    public static bool PlaceInSlot(IntPtr hwnd, NativeRect rect, IntPtr insertAfter, bool front)
    {
        Restore(hwnd, includeMinimized: front);
        ClearTopmost(hwnd);
        return SetPosition(hwnd, insertAfter, rect, front ? Native.SWP_SHOWWINDOW : 0);
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

    /// <summary>
    /// „Immer im Vordergrund“ wegnehmen. Ein Fenster, das hinter einem solchen eingeordnet wird, erbt den Zustand, und so
    /// stehen bald alle Fenster eines Platzes über den anderen: eine App, die in der Taskleiste angeklickt wird, bliebe verdeckt.
    /// </summary>
    private static void ClearTopmost(IntPtr hwnd)
    {
        if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0)
        {
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private static bool SetPosition(IntPtr hwnd, IntPtr insertAfter, NativeRect rect, uint extraFlags)
    {
        bool ok = Native.SetWindowPos(hwnd, insertAfter, rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top, Native.SWP_NOACTIVATE | extraFlags);

        // Den Fehlercode sofort sichern: spätere Aufrufe (Aktivieren) würden ihn überschreiben
        LastError = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
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
