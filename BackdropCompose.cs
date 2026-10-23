using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FokusDock;

/// <summary>
/// Setzt das Bild hinter einem Glasfenster zusammen: Hintergrundbild, darüber alle sichtbaren Fenster, die in der Stapelreihenfolge hinter
/// dem Glasfenster liegen (jeweils mit PrintWindow aufgenommen), in verkleinerter Auflösung. Das Ergebnis wird im Hilfsfenster weichgezeichnet.
/// Läuft in einem Hintergrund-Thread; PrintWindow kann bei Fenstern, die nicht antworten, hängen.
/// </summary>
internal static class BackdropCompose
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint PW_RENDERFULLCONTENT = 0x2;
    private const int DWMWA_CLOAKED = 14;

    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "CEF-OSC-WIDGET", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
    };

    private static readonly object WallLock = new();
    private static Bitmap _wall;          // Hintergrundbild, auf die Größe des ganzen Desktops (verkleinert) gebracht
    private static double _wallScale;

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Fenster, die in der Stapelreihenfolge hinter target liegen (von vorn nach hinten), sichtbar und mit dem Bereich überlappend.</summary>
    private static List<IntPtr> BehindWindows(IntPtr target, NativeRect area)
    {
        var list = new List<IntPtr>();
        IntPtr h = Native.GetWindow(target, 2);   // GW_HWNDNEXT
        int guard = 0;
        while (h != IntPtr.Zero && guard++ < 400)
        {
            try
            {
                if (Native.IsWindowVisible(h) && !Native.IsIconic(h) && Native.GetWindowRect(h, out NativeRect r)
                    && r.Left < area.Right && r.Right > area.Left && r.Top < area.Bottom && r.Bottom > area.Top
                    && r.Right - r.Left > 8 && r.Bottom - r.Top > 8)
                {
                    Native.GetWindowThreadProcessId(h, out uint pid);
                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                    Native.DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int));
                    var cls = new StringBuilder(80);
                    GetClassName(h, cls, 80);
                    bool overlay = (ex & WS_EX_TRANSPARENT) != 0 || ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_NOACTIVATE) != 0);
                    if (pid != OwnPid && cloaked == 0 && !overlay && !SkipClasses.Contains(cls.ToString()))
                    {
                        list.Add(h);
                    }
                }
            }
            catch (Exception)
            {
                // ein Fenster, das verschwindet, während wir es lesen: überspringen
            }
            h = Native.GetWindow(h, 2);
        }
        return list;
    }

    private static Bitmap Wallpaper(double scale)
    {
        lock (WallLock)
        {
            if (_wall != null && Math.Abs(_wallScale - scale) < 1e-6)
            {
                return _wall;
            }
            _wall?.Dispose();
            _wall = null;
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            try
            {
                if (File.Exists(path))
                {
                    using var src = new Bitmap(path);
                    int w = Math.Max(1, (int)(SystemInformationVirtualWidth() * scale));
                    int h = Math.Max(1, (int)(SystemInformationVirtualHeight() * scale));
                    var scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    _wall = scaled;
                    _wallScale = scale;
                }
            }
            catch (Exception)
            {
                _wall = null;
            }
            return _wall;
        }
    }

    private static int SystemInformationVirtualWidth() => (int)System.Windows.Forms.SystemInformation.VirtualScreen.Width;
    private static int SystemInformationVirtualHeight() => (int)System.Windows.Forms.SystemInformation.VirtualScreen.Height;

    /// <summary>
    /// Bild hinter dem Bereich area (Bildschirmpixel) in der Auflösung scale (zum Beispiel 0,5), von unten nach oben zusammengesetzt.
    /// Gibt ein eingefrorenes WPF-Bild zurück, das in jedem Thread benutzt werden darf, oder null.
    /// </summary>
    public static BitmapSource Compose(IntPtr target, NativeRect area, double scale, bool useWallpaper, out int widthPx, out int heightPx)
    {
        widthPx = Math.Max(1, (int)((area.Right - area.Left) * scale));
        heightPx = Math.Max(1, (int)((area.Bottom - area.Top) * scale));
        using var bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.Clear(Color.FromArgb(22, 24, 29));

            Bitmap wall = useWallpaper ? Wallpaper(scale) : null;
            if (wall != null)
            {
                var virt = System.Windows.Forms.SystemInformation.VirtualScreen;
                int sx = (int)((area.Left - virt.Left) * scale);
                int sy = (int)((area.Top - virt.Top) * scale);
                g.DrawImage(wall, new Rectangle(0, 0, widthPx, heightPx), new Rectangle(sx, sy, widthPx, heightPx), GraphicsUnit.Pixel);
            }

            List<IntPtr> behind = BehindWindows(target, area);
            for (int i = behind.Count - 1; i >= 0; i--)   // hinterstes zuerst
            {
                IntPtr hw = behind[i];
                if (!Native.GetWindowRect(hw, out NativeRect wr))
                {
                    continue;
                }
                int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
                if (ww <= 0 || wh <= 0 || (long)ww * wh > 40_000_000)
                {
                    continue;
                }
                try
                {
                    using var wb = new Bitmap(ww, wh, PixelFormat.Format24bppRgb);
                    using (Graphics wg = Graphics.FromImage(wb))
                    {
                        IntPtr hdc = wg.GetHdc();
                        try
                        {
                            PrintWindow(hw, hdc, PW_RENDERFULLCONTENT);
                        }
                        finally
                        {
                            wg.ReleaseHdc(hdc);
                        }
                    }
                    g.DrawImage(wb, new Rectangle((int)((wr.Left - area.Left) * scale), (int)((wr.Top - area.Top) * scale), (int)(ww * scale), (int)(wh * scale)));
                }
                catch (Exception)
                {
                    // Fenster nicht aufnehmbar: bleibt weg
                }
            }
        }

        IntPtr hbitmap = bmp.GetHbitmap();
        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hbitmap);
        }
    }
}
