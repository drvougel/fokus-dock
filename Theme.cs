using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FokusDock;

/// <summary>
/// Gemeinsame Farben und Fenstergrundlagen des dunklen Designs. Die Pinsel stehen nur in App.xaml
/// (Application.Resources) und werden von dort gelesen, damit es für jede Farbe eine einzige Quelle gibt.
/// Die Fenster rufen <see cref="Apply"/> im Konstruktor auf; Knöpfe, Listen und Scrollbalken stylt App.xaml implizit.
/// </summary>
internal static class Theme
{
    /// <summary>Fensterhintergrund.</summary>
    public static SolidColorBrush Surface => Get("Surface");

    /// <summary>Karten, Zellen und Knöpfe auf dem Fensterhintergrund.</summary>
    public static SolidColorBrush Card => Get("Card");

    /// <summary>Maus über einem Knopf, einer Zeile oder einem Menüeintrag.</summary>
    public static SolidColorBrush Hover => Get("Hover");

    /// <summary>Gewählte Zeile oder Zelle.</summary>
    public static SolidColorBrush Selected => Get("Selected");

    /// <summary>Hervorhebung: Tastaturfokus, gewählter Rahmen, Hauptknopf.</summary>
    public static SolidColorBrush Accent => Get("Accent");

    /// <summary>Haupttext.</summary>
    public static SolidColorBrush Text => Get("Text");

    /// <summary>Nebentext, z. B. „läuft nicht“ oder „frei“ (mindestens 5:1 auf Fläche und Karte).</summary>
    public static SolidColorBrush Muted => Get("Muted");

    /// <summary>Rahmen und Linien.</summary>
    public static SolidColorBrush Line => Get("Line");

    /// <summary>Erfolg und Bestätigung.</summary>
    public static SolidColorBrush Ok => Get("Ok");

    /// <summary>Fehlt ein Pinsel in App.xaml, bleibt die Eigenschaft leer (Standardfarbe) statt das Fenster abzubrechen.</summary>
    private static SolidColorBrush Get(string key) => Application.Current?.TryFindResource(key) as SolidColorBrush;

    /// <summary>
    /// Dunkler Hintergrund, weiße Schrift, Segoe UI und eine dunkle Titelleiste. Gehört in den Konstruktor des Fensters,
    /// Titel, Größe und Inhalt setzt das Fenster selbst.
    /// </summary>
    internal static void Apply(Window window)
    {
        window.Background = Surface;
        window.Foreground = Text;
        window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        window.FontSize = 13;
        window.UseLayoutRounding = true;

        // Das Handle gibt es erst nach SourceInitialized; ist es schon da, gilt das Attribut sofort
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            UseDarkTitleBar(handle);
        }
        else
        {
            window.SourceInitialized += (_, _) => UseDarkTitleBar(new WindowInteropHelper(window).Handle);
        }
    }

    private static void UseDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Rückgabewert bewusst ignoriert: ältere Windows-Versionen kennen das Attribut nicht, die Titelleiste bleibt dann hell
        int on = 1;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}
