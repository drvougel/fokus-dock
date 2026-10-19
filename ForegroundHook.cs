using System;
using System.Runtime.InteropServices;

namespace FokusDock;

/// <summary>
/// Meldet, wenn ein anderes Fenster in den Vordergrund kommt (EVENT_SYSTEM_FOREGROUND), z. B. nach einem Klick auf eine App in der
/// Windows-Taskleiste oder mit Alt+Tab. Fokus-Dock gleicht dann sofort ab, statt bis zum nächsten Sekundentakt zu warten.
/// </summary>
internal sealed class ForegroundHook : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private readonly Action<IntPtr> _onForeground;
    private readonly WinEventProc _proc; // Feld halten, damit der Delegat nicht vom GC eingesammelt wird
    private IntPtr _hook;

    /// <param name="onForeground">Fenster, das in den Vordergrund gekommen ist.</param>
    public ForegroundHook(Action<IntPtr> onForeground)
    {
        _onForeground = onForeground;
        _proc = (_, _, hwnd, _, _, _, _) =>
        {
            if (hwnd != IntPtr.Zero)
            {
                _onForeground(hwnd);
            }
        };
    }

    public void Install()
    {
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
