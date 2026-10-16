using System;
using System.Runtime.InteropServices;

namespace FokusDock;

/// <summary>
/// Meldet, wenn der Nutzer ein Fenster fertig verschoben hat (EVENT_SYSTEM_MOVESIZEEND).
/// Größenänderungen werden ignoriert: nur wenn die Fenstergröße gleich blieb, war es ein Verschieben.
/// Programmatisches Setzen per SetWindowPos löst das Ereignis nicht aus.
/// </summary>
internal sealed class DragSnap : IDisposable
{
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private readonly Action<IntPtr> _onMoved;
    private readonly WinEventProc _proc; // Feld halten, damit der Delegat nicht vom GC eingesammelt wird
    private IntPtr _hook;
    private IntPtr _startHwnd;
    private int _startWidth;
    private int _startHeight;

    /// <param name="onMoved">Fenster, das gerade verschoben (nicht in der Größe geändert) wurde.</param>
    public DragSnap(Action<IntPtr> onMoved)
    {
        _onMoved = onMoved;
        _proc = Callback;
    }

    public void Install()
    {
        _hook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private void Callback(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out NativeRect rect))
        {
            return;
        }

        if (eventType == EVENT_SYSTEM_MOVESIZESTART)
        {
            _startHwnd = hwnd;
            _startWidth = rect.Right - rect.Left;
            _startHeight = rect.Bottom - rect.Top;
            return;
        }

        bool sameSize = hwnd == _startHwnd
                        && Math.Abs(rect.Right - rect.Left - _startWidth) <= 2
                        && Math.Abs(rect.Bottom - rect.Top - _startHeight) <= 2;
        _startHwnd = IntPtr.Zero;
        if (sameSize)
        {
            _onMoved(hwnd);
        }
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
