using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FokusDock;

/// <summary>
/// Glas für Fenster anderer Programme über das System-Hintergrundmaterial von Windows 11 (Acrylic oder Mica). Der Rahmen wird dazu
/// in die ganze Fläche erweitert: das Material erscheint dann überall dort, wo das Programm seine Fläche durchsichtig lässt
/// (Explorer, Terminal, Einstellungen, WinUI und WPF). Programme, die ihre Fläche selbst deckend zeichnen (Browser, Electron),
/// zeigen es höchstens in der Titelleiste.
/// Fokus-Dock merkt sich die Fenster, die es angefasst hat, und stellt sie beim Beenden oder bei „aus“ wieder her.
/// </summary>
internal static class WindowGlass
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private static readonly Dictionary<IntPtr, int> Applied = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    private static void ExtendFrame(IntPtr hwnd, int amount)
    {
        var m = new Margins { Left = amount, Right = amount, Top = amount, Bottom = amount };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    /// <summary>Modus 3: Hilfsfenster mit Unschärfe hinter dem Fenster (funktioniert bei jedem Programm).</summary>
    private static readonly Dictionary<IntPtr, WallpaperBackdrop> Backdrops = new();
    private static DispatcherTimer _follow;
    private static DispatcherTimer _capture;
    private static int _running;   // laufende Aufnahmen (nur im Oberflächen-Thread gezählt)

    /// <summary>Auflösung der Aufnahme hinter dem Fenster (0,25–1). Kleiner = schneller und weicher.</summary>
    public static double CaptureScale { get; set; } = 0.4;

    /// <summary>Abstand der Aufnahmen in Millisekunden.</summary>
    public static int CaptureIntervalMs { get; set; } = 700;

    /// <summary>Wie deckend das Fenster im Hilfsfenster-Modus bleibt (Prozent) und wie dunkel das Hilfsfenster getönt ist (0–255).</summary>
    public static int WindowPercent { get; set; } = 88;
    public static byte BackdropTint { get; set; } = 60;

    /// <summary>Das Hintergrundbild im Glas zeigen (aus: nur die Fenster dahinter, sonst dunkel).</summary>
    public static bool UseWallpaper { get; set; }

    /// <summary>Radius der Unschärfe in Pixeln (0 = scharf). Weniger = Bild und Text dahinter besser erkennbar.</summary>
    public static int BlurRadius { get; set; } = 10;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>mode: 0 = aus, 1 = Acrylic, 2 = Mica (System-Material), 3 = eigenes Hilfsfenster mit Unschärfe.</summary>
    public static void Apply(IntPtr hwnd, int mode)
    {
        if (!Native.IsWindow(hwnd))
        {
            Applied.Remove(hwnd);
            CloseBackdrop(hwnd);
            return;
        }
        if (mode <= 0)
        {
            Restore(hwnd);
            return;
        }
        if (Applied.TryGetValue(hwnd, out int current) && current == mode)
        {
            return;
        }
        if (mode == 3)
        {
            ApplyBackdrop(hwnd);
            return;
        }
        CloseBackdrop(hwnd);

        int dark = 1;   // DWMWA_USE_IMMERSIVE_DARK_MODE: dunkel getöntes Material statt hellem
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int type = mode == 1 ? 3 : 2;   // 3 = Acrylic (TransientWindow), 2 = Mica (MainWindow)
        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0)
        {
            // Der Rahmen wird bewusst NICHT in die Fläche erweitert: bei Programmen, die ihren Text per GDI zeichnen (zum Beispiel dem
            // Explorer), würde der Text dann durchsichtig und verschwände. Die Materialien wirken deshalb nur in der Titelleiste;
            // flächiges Glas gibt es über das Hilfsfenster (Modus 3).
            Applied[hwnd] = mode;
        }
    }

    private static void ApplyBackdrop(IntPtr hwnd)
    {
        if (!Backdrops.ContainsKey(hwnd))
        {
            var backdrop = new WallpaperBackdrop(BlurRadius, BackdropTint, UseWallpaper);
            backdrop.Show();
            Backdrops[hwnd] = backdrop;
        }
        WindowAlpha.Apply(hwnd, WindowPercent);
        Applied[hwnd] = 3;
        if (_follow == null)
        {
            _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _follow.Tick += (_, _) => Follow();
            _follow.Start();
        }
        if (_capture == null)
        {
            _capture = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(200, CaptureIntervalMs)) };
            _capture.Tick += (_, _) => CaptureAll();
            _capture.Start();
        }
        Follow();
    }

    private static void CloseBackdrop(IntPtr hwnd)
    {
        if (Backdrops.Remove(hwnd, out WallpaperBackdrop backdrop))
        {
            backdrop.Close();
            WindowAlpha.Restore(hwnd);
        }
    }

    /// <summary>
    /// Nimmt für jedes Hilfsfenster das Bild dahinter auf (Hintergrund und Fenster hinter dem Glasfenster) und zeigt es weichgezeichnet an.
    /// Die Aufnahme läuft in einem Hintergrund-Thread, damit ein hängendes Fenster Fokus-Dock nicht aufhält.
    /// </summary>
    private static void CaptureAll()
    {
        foreach (KeyValuePair<IntPtr, WallpaperBackdrop> pair in Backdrops.ToList())
        {
            IntPtr target = pair.Key;
            WallpaperBackdrop backdrop = pair.Value;
            if (backdrop.Capturing || !backdrop.IsVisible || !Native.IsWindow(target) || Native.IsIconic(target))
            {
                continue;
            }
            if (_running >= 2)
            {
                break;   // höchstens zwei Aufnahmen gleichzeitig, der Rest kommt im nächsten Takt dran
            }
            if (!Native.GetWindowRect(target, out NativeRect rect))
            {
                continue;
            }

            backdrop.Capturing = true;
            _running++;
            double scale = Math.Clamp(CaptureScale, 0.2, 1.0);
            bool useWall = UseWallpaper;
            Task.Run(() =>
            {
                System.Windows.Media.Imaging.BitmapSource frame = null;
                int w = 0, h = 0;
                try
                {
                    frame = BackdropCompose.Compose(target, rect, scale, useWall, out w, out h);
                }
                catch (Exception)
                {
                    // Aufnahme fehlgeschlagen: das letzte Bild bleibt stehen
                }
                backdrop.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _running--;
                    backdrop.Capturing = false;
                    if (frame != null && Backdrops.ContainsKey(target))
                    {
                        backdrop.LastWidth = rect.Right - rect.Left;
                        backdrop.LastHeight = rect.Bottom - rect.Top;
                        backdrop.SetFrame(frame);
                    }
                }));
            });
        }
    }

    /// <summary>Hilfsfenster an Lage, Größe und Stapelreihenfolge des Fensters anpassen (direkt dahinter).</summary>
    private static void Follow()
    {
        foreach (KeyValuePair<IntPtr, WallpaperBackdrop> pair in Backdrops.ToList())
        {
            IntPtr target = pair.Key;
            WallpaperBackdrop backdrop = pair.Value;
            if (!Native.IsWindow(target))
            {
                backdrop.Close();
                Backdrops.Remove(target);
                Applied.Remove(target);
                continue;
            }
            if (backdrop.Handle == IntPtr.Zero)
            {
                continue;
            }
            if (!Native.IsWindowVisible(target) || Native.IsIconic(target))
            {
                backdrop.Hide();
                continue;
            }
            if (!backdrop.IsVisible)
            {
                backdrop.Show();
            }

            Native.GetWindowRect(target, out NativeRect rect);
            Native.SetWindowPos(backdrop.Handle, target, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            backdrop.Place(rect.Left, rect.Top);
        }
    }

    public static void Restore(IntPtr hwnd)
    {
        if (Backdrops.ContainsKey(hwnd))
        {
            Applied.Remove(hwnd);
            CloseBackdrop(hwnd);
            return;
        }
        if (!Applied.Remove(hwnd) || !Native.IsWindow(hwnd))
        {
            return;
        }
        int auto = 0;   // DWMSBT_AUTO: Windows entscheidet wie vorher
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref auto, sizeof(int));
        ExtendFrame(hwnd, 0);   // aufräumen, falls eine frühere Version den Rahmen erweitert hat
    }

    public static void RestoreAll()
    {
        foreach (IntPtr hwnd in Applied.Keys.Concat(Backdrops.Keys).Distinct().ToList())
        {
            Restore(hwnd);
        }
    }
}
