using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Media;

namespace FokusDock;

/// <summary>
/// Grundlage für Einblendungen über allen Fenstern: rahmenlos, durchsichtig, immer oben, ohne Taskleistenknopf
/// und ohne Fokus. Die Größe kommt aus dem Inhalt, die Lage setzt <see cref="PlacePixels"/> in echten Pixeln.
/// </summary>
internal class OverlayWindow : Window
{
    private bool _clickThrough;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        UseLayoutRounding = true;

        SourceInitialized += (_, _) => ApplyExStyle();
    }

    /// <summary>Klicks gehen durch das Fenster hindurch an das Fenster darunter (WS_EX_TRANSPARENT).</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            ApplyExStyle();
        }
    }

    /// <summary>Setzt das Fenster auf ein Pixelrechteck, ganz oben und ohne Aktivierung, und zeigt es.</summary>
    public void PlacePixels(NativeRect r)
    {
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible)
        {
            Show();
        }
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Erweiterte Stile: nie aktivierbar, kein Alt+Tab-Eintrag, optional klickdurchlässig. Ohne Handle passiert nichts, SourceInitialized holt es nach.</summary>
    private void ApplyExStyle()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int style = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        style = _clickThrough ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, style);
    }
}

/// <summary>
/// Vorschau beim Ziehen: Rahmen und Füllung über der Zone, die beim Loslassen getroffen wird, mit einem Etikett in der Mitte.
/// Klickdurchlässig und ohne Fokus, damit sie das Ablegen nicht behindert. Das Fenster ist nur so groß wie die Zone.
/// </summary>
internal sealed class ZoneHighlight : OverlayWindow
{
    private const int Inset = 6;   // Pixel, um die der Rahmen von der Zonenkante nach innen rückt

    private readonly Border _solid = new();
    private readonly Rectangle _dashed = new();
    private readonly Border _label = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _detail = new();

    public ZoneHighlight()
    {
        ClickThrough = true;
        // Vor dem ersten Platzieren winzig und außerhalb des Bildschirms, damit nichts aufblitzt
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;

        _solid.BorderBrush = Theme.Accent ?? Brushes.DeepSkyBlue;
        _solid.BorderThickness = new Thickness(3);
        _solid.CornerRadius = new CornerRadius(8);
        _solid.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xC2, 0xFF));

        _dashed.Stroke = Theme.Line ?? Brushes.Gray;
        _dashed.StrokeThickness = 3;
        _dashed.StrokeDashArray = new DoubleCollection { 4, 3 };
        _dashed.RadiusX = 8;
        _dashed.RadiusY = 8;
        _dashed.Margin = new Thickness(1.5);   // halbe Strichstärke, damit der Strich nicht abgeschnitten wird
        _dashed.Visibility = Visibility.Collapsed;

        _title.FontSize = 16;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Theme.Text ?? Brushes.White;
        _title.TextAlignment = TextAlignment.Center;
        _title.TextWrapping = TextWrapping.Wrap;
        _detail.FontSize = 13;
        _detail.Foreground = Theme.Muted ?? Brushes.Gray;
        _detail.TextAlignment = TextAlignment.Center;
        _detail.TextWrapping = TextWrapping.Wrap;

        var texts = new StackPanel();
        texts.Children.Add(_title);
        texts.Children.Add(_detail);
        _label.Child = texts;
        _label.Padding = new Thickness(12, 8, 12, 8);
        _label.CornerRadius = new CornerRadius(6);
        _label.Background = new SolidColorBrush(Theme.Card?.Color ?? Color.FromRgb(0x2B, 0x2B, 0x2B)) { Opacity = 0.9 };
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.MaxWidth = 420;

        var grid = new Grid();
        grid.Children.Add(_solid);
        grid.Children.Add(_dashed);
        grid.Children.Add(_label);
        Content = grid;
    }

    /// <summary>Zeigt die Vorschau über der Zone. <paramref name="free"/> = gestrichelt ohne Füllung (Shift: nichts wird gespeichert).</summary>
    public void ShowZone(NativeRect zone, string title, string detail, bool free)
    {
        _solid.Visibility = free ? Visibility.Collapsed : Visibility.Visible;
        _dashed.Visibility = free ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = title;
        _detail.Text = detail;
        _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        PlacePixels(new NativeRect
        {
            Left = zone.Left + Inset,
            Top = zone.Top + Inset,
            Right = zone.Right - Inset,
            Bottom = zone.Bottom - Inset
        });
    }

    /// <summary>Blendet die Vorschau aus. Das Fenster bleibt für das nächste Mal bestehen.</summary>
    public void HideZone()
    {
        if (IsVisible)
        {
            Hide();
        }
    }
}
