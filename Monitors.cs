using System;
using System.Runtime.InteropServices;

namespace FokusDock;

/// <summary>Monitor-Hilfen. Alle Koordinaten in Pixeln des Primärmonitors.</summary>
internal static class Monitors
{
    public static NativeRect PrimaryWorkArea()
    {
        IntPtr monitor = Native.MonitorFromPoint(new NativePoint(), Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        Native.GetMonitorInfo(monitor, ref info);
        return info.rcWork;
    }

    /// <summary>Skalierungsfaktor für WPF-Koordinaten (DIP) des Primärmonitors.</summary>
    public static double Scale => Native.GetDpiForSystem() / 96.0;
}
