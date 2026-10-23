using System;
using System.Collections.Generic;
using System.Linq;

namespace FokusDock;

/// <summary>
/// Durchsichtigkeit einzelner Fenster über den Alpha-Wert des Fensters (WS_EX_LAYERED). Der Hintergrund scheint durch, wird aber nicht
/// unscharf. Wirkt bei fast allen Win32-Fenstern, auch bei Browsern; Store-Apps (UWP) ignorieren es meist.
/// Fokus-Dock merkt sich, welche Fenster es angefasst hat, und stellt sie beim Beenden oder bei 100 % wieder her.
/// </summary>
internal static class WindowAlpha
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x2;

    /// <summary>Fenster → zuletzt gesetzter Alpha-Wert (0–254).</summary>
    private static readonly Dictionary<IntPtr, int> Applied = new();

    /// <summary>Fenster, bei denen erst Fokus-Dock das Layered-Flag gesetzt hat (nur dann wird es wieder entfernt).</summary>
    private static readonly HashSet<IntPtr> AddedLayered = new();

    /// <summary>Prozent (10–99) auf das Fenster anwenden; 100 oder mehr stellt es wieder her.</summary>
    public static void Apply(IntPtr hwnd, int percent)
    {
        if (!Native.IsWindow(hwnd))
        {
            Applied.Remove(hwnd);
            AddedLayered.Remove(hwnd);
            return;
        }
        if (percent >= 100)
        {
            Restore(hwnd);
            return;
        }

        int alpha = Math.Clamp(percent, 10, 99) * 255 / 100;
        if (Applied.TryGetValue(hwnd, out int current) && current == alpha)
        {
            return;
        }

        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0)
        {
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | WS_EX_LAYERED);
            AddedLayered.Add(hwnd);
        }
        if (Native.SetLayeredWindowAttributes(hwnd, 0, (byte)alpha, LWA_ALPHA))
        {
            Applied[hwnd] = alpha;
        }
    }

    public static void Restore(IntPtr hwnd)
    {
        if (!Applied.Remove(hwnd))
        {
            return;
        }
        if (!Native.IsWindow(hwnd))
        {
            AddedLayered.Remove(hwnd);
            return;
        }

        Native.SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        if (AddedLayered.Remove(hwnd))
        {
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
        }
    }

    public static void RestoreAll()
    {
        foreach (IntPtr hwnd in Applied.Keys.ToList())
        {
            Restore(hwnd);
        }
    }
}
