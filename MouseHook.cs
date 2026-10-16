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

    /// <summary>Zurück-Taste (XBUTTON1) und Vorwärts-Taste (XBUTTON2).</summary>
    public const int XButtonBack = 1;
    public const int XButtonForward = 2;

    private readonly Func<NativePoint, int, bool> _onXButton;
    private readonly HookProc _proc; // Feld halten, damit der Delegat nicht vom GC eingesammelt wird
    private IntPtr _hook;

    /// <param name="onXButton">Punkt und Taste (XButtonBack/XButtonForward); liefert true, um den Klick zu verschlucken.</param>
    public MouseHook(Func<NativePoint, int, bool> onXButton)
    {
        _onXButton = onXButton;
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
        if (nCode >= 0 && wParam.ToInt32() == WM_XBUTTONDOWN)
        {
            MsllHookStruct info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            int button = (int)((info.mouseData >> 16) & 0xFFFF);
            // Auf einem Fenster eines Kachelplatzes wird der Klick verschluckt: die Zurück- oder Vorwärts-Funktion der App bleibt aus
            if ((button == XButtonBack || button == XButtonForward) && _onXButton(info.pt, button))
            {
                return (IntPtr)1;
            }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
