using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace FokusDock;

/// <summary>
/// Hilfsfenster hinter einem fremden Fenster, das den Desktop-Hintergrund mit einstellbarer Unschärfe zeigt. Das Fenster darüber ist leicht
/// durchsichtig und lässt dadurch das weichgezeichnete Bild durchscheinen. Die Unschärfe ist ein WPF-Filter (BlurEffect) mit frei wählbarem
/// Radius, anders als das Acrylic von Windows, dessen Stärke fest ist. Es zeigt nur das Hintergrundbild, nicht andere Fenster dahinter.
/// Ohne auffindbares Hintergrundbild fällt es auf Acrylic zurück. Das Hilfsfenster ist für die Maus durchlässig, nimmt keinen Fokus
/// und taucht nicht in der Taskleiste auf.
/// </summary>
internal sealed class WallpaperBackdrop : Window
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WinCompAttrData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder value, uint winIni);

    private readonly byte _tintAlpha;
    private readonly Image _image;
    private readonly bool _hasWallpaper;
    private readonly bool _imageBased;   // zeigt ein Bild (Hintergrund und/oder Aufnahme) statt des Acrylic-Ersatzes
    private bool _frames;    // ab dem ersten zusammengesetzten Bild bestimmt dieses die Anzeige, nicht mehr das feste Hintergrundbild
    public IntPtr Handle { get; private set; }

    /// <summary>Läuft gerade eine Aufnahme dieses Hilfsfensters (verhindert, dass sich Aufnahmen stapeln).</summary>
    public bool Capturing { get; set; }
    public int LastWidth { get; set; }
    public int LastHeight { get; set; }
    public DateTime LastCapture { get; set; } = DateTime.MinValue;

    /// <summary>Zusammengesetztes Bild hinter dem Fenster anzeigen (füllt das ganze Hilfsfenster).</summary>
    public void SetFrame(System.Windows.Media.Imaging.BitmapSource frame)
    {
        if (_image == null || frame == null)
        {
            return;
        }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _frames = true;
        _image.Source = frame;
        _image.Stretch = Stretch.Fill;
        _image.Width = LastWidth / scale;
        _image.Height = LastHeight / scale;
        Canvas.SetLeft(_image, 0);
        Canvas.SetTop(_image, 0);
    }

    /// <param name="blurRadius">Radius der Unschärfe in Pixeln (0 = scharf). Weniger = Text und Bild dahinter besser erkennbar.</param>
    /// <param name="tintAlpha">Stärke der dunklen Tönung (0–255); mehr = ruhiger, weniger = klarer.</param>
    public WallpaperBackdrop(int blurRadius, byte tintAlpha, bool useWallpaper)
    {
        _tintAlpha = tintAlpha;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowsTransparency = false;
        Background = Brushes.Black;
        Left = -32000;
        Top = -32000;
        Width = 100;
        Height = 100;

        BitmapSource wallpaper = useWallpaper ? LoadWallpaper() : null;
        _hasWallpaper = wallpaper != null;
        _imageBased = _hasWallpaper || !useWallpaper;   // ohne Hintergrundbild-Wunsch: nur die Aufnahme der Fenster dahinter, sonst dunkel
        if (_imageBased)
        {
            _image = new Image
            {
                Source = wallpaper,
                Stretch = Stretch.Fill,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                Effect = new BlurEffect { Radius = Math.Max(0, blurRadius), KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            var canvas = new Canvas { ClipToBounds = true };
            canvas.Children.Add(_image);
            var tint = new Border { Background = new SolidColorBrush(Color.FromArgb(tintAlpha, 22, 24, 29)), IsHitTestVisible = false };
            var grid = new Grid();
            grid.Children.Add(canvas);
            grid.Children.Add(tint);
            Content = grid;
        }
        SourceInitialized += (_, _) => Init();
    }

    /// <summary>Das aktuelle Hintergrundbild: bevorzugt die von Windows vorbereitete Datei (passend zu Größe und Anpassung), sonst der gesetzte Pfad.</summary>
    private static BitmapSource LoadWallpaper()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper"),
            CurrentWallpaperPath(),
        };
        foreach (string path in candidates)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception)
            {
                // nächste Quelle versuchen
            }
        }
        return null;
    }

    private static string CurrentWallpaperPath()
    {
        var sb = new System.Text.StringBuilder(520);
        return SystemParametersInfo(0x0073, (uint)sb.Capacity, sb, 0) ? sb.ToString() : null;   // SPI_GETDESKWALLPAPER
    }

    /// <summary>Das Hilfsfenster liegt an der Bildschirmstelle (links, oben in Pixeln): das Bild so verschieben, dass es zum Desktop passt.</summary>
    public void Place(int leftPx, int topPx)
    {
        if (!_hasWallpaper || _frames)
        {
            return;
        }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Canvas.SetLeft(_image, SystemParameters.VirtualScreenLeft - leftPx / scale);
        Canvas.SetTop(_image, SystemParameters.VirtualScreenTop - topPx / scale);
    }

    private void Init()
    {
        Handle = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        int dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));    // gerundete Ecken wie die Fenster darüber
        if (_imageBased)
        {
            return;
        }

        // Ersatz ohne auffindbares Hintergrundbild: Acrylic von Windows
        HwndSource.FromHwnd(Handle).CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(Handle, ref m);
        int none = 1;
        DwmSetWindowAttribute(Handle, 38, ref none, sizeof(int));
        var policy = new AccentPolicy { AccentState = 4, AccentFlags = 2, GradientColor = ((uint)_tintAlpha << 24) | (29u << 16) | (24u << 8) | 22u };
        int size = Marshal.SizeOf(policy);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WinCompAttrData { Attribute = 19, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(Handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
