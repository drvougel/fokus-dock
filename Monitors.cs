using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace FokusDock;

/// <summary>Monitor-Hilfen. Alle Koordinaten in Pixeln des Primärmonitors.</summary>
internal static class Monitors
{
    /// <summary>Gewählter Monitor: 0 = Primärmonitor, 1 und höher = Nummer in der Reihenfolge der Windows-Anzeige. Ein unbekannter Wert gilt als Primärmonitor.</summary>
    public static int Selected { get; set; }

    /// <summary>Beschreibungen der angeschlossenen Monitore für das Menü, in der Reihenfolge von <see cref="Selected"/>.</summary>
    public static string[] Describe() =>
        System.Windows.Forms.Screen.AllScreens
            .Select((s, i) => $"{i + 1} · {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (Primär)" : "")}")
            .ToArray();

    /// <summary>Arbeitsbereich des gewählten Monitors (Standard: Primärmonitor).</summary>
    public static NativeRect PrimaryWorkArea()
    {
        System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
        if (Selected > 0 && Selected <= screens.Length)
        {
            System.Drawing.Rectangle work = screens[Selected - 1].WorkingArea;
            return new NativeRect { Left = work.Left, Top = work.Top, Right = work.Right, Bottom = work.Bottom };
        }

        IntPtr monitor = Native.MonitorFromPoint(new NativePoint(), Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        Native.GetMonitorInfo(monitor, ref info);
        return info.rcWork;
    }

    /// <summary>Skalierungsfaktor für WPF-Koordinaten (DIP) des Primärmonitors.</summary>
    public static double Scale => Native.GetDpiForSystem() / 96.0;
}
