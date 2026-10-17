using System;
using System.Runtime.InteropServices;

namespace FokusDock;

/// <summary>
/// Globaler Mausfilter (WH_MOUSE_LL) für die Zusatztasten der Maus. Er sieht Klicks in echten Fenstern,
/// die WPF nicht erreichen. Liefert der Callback true, wird der Klick verschluckt.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;

    /// <summary>Zurück-Taste (XBUTTON1) und Vorwärts-Taste (XBUTTON2).</summary>
    public const int XButtonBack = 1;
    public const int XButtonForward = 2;

    private readonly Func<NativePoint, int, bool> _onXButton;
    private readonly Action<NativePoint> _onClick;
    private readonly HookProc _proc; // Feld halten, damit der Delegat nicht vom GC eingesammelt wird
    private IntPtr _hook;
    private int _swallowed; // Taste, deren Drücken verschluckt wurde; ihr Loslassen wird ebenfalls verschluckt (0 = keine)

    /// <param name="onXButton">Punkt und Taste (XButtonBack/XButtonForward); liefert true, um den Klick zu verschlucken.</param>
    /// <param name="onClick">Optional: meldet jedes Drücken der linken und rechten Taste (Punkt); der Klick wird nie verschluckt.</param>
    public MouseHook(Func<NativePoint, int, bool> onXButton, Action<NativePoint> onClick = null)
    {
        _onXButton = onXButton;
        _onClick = onClick;
        _proc = Callback;
    }

    public void Install()
    {
        _hook = Native.SetWindowsHookEx(WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int message = nCode >= 0 ? wParam.ToInt32() : 0;
        if (_onClick != null && (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN))
        {
            _onClick(Marshal.PtrToStructure<MsllHookStruct>(lParam).pt);
        }
        if (message == WM_XBUTTONDOWN || message == WM_XBUTTONUP)
        {
            MsllHookStruct info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            int button = (int)((info.mouseData >> 16) & 0xFFFF);
            if (message == WM_XBUTTONDOWN)
            {
                _swallowed = 0;
                // Auf einem Fenster eines Kachelplatzes wird der Klick verschluckt: die Zurück- oder Vorwärts-Funktion der App bleibt aus
                if ((button == XButtonBack || button == XButtonForward) && _onXButton(info.pt, button))
                {
                    _swallowed = button;
                    return (IntPtr)1;
                }
            }
            else if (_swallowed != 0 && button == _swallowed)
            {
                // Auch das Loslassen verschlucken: sonst geht es an das Fenster, das nach dem Tausch unter dem Zeiger liegt,
                // und ein Browser dort navigiert zurück
                _swallowed = 0;
                return (IntPtr)1;
            }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
