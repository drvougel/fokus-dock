using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FokusDock;

/// <summary>
/// Vorwärts-Taste auf einem Kachelplatz: Vorschauen aller Fenster des Platzes, untereinander.
/// Die Liste ist so hoch, dass alle Kacheln ohne Scrollen passen (höchstens so hoch wie die Seite).
/// Ein Klick auf eine Vorschau setzt dieses Fenster in den Platz, die Zurück-Taste holt es in die Mitte.
/// </summary>
public sealed class WindowListWindow : ThumbnailWindow
{
    // Maße in DIP; die Kachelhöhe ergibt sich als 40 + Vorschauhöhe + 10 (siehe TileHeight)
    private const double HeaderHeight = 44;
    private const double GroupRowHeight = 24;
    private const double OuterMargin = 8;      // Rand des Scrollbereichs
    private const double BorderWidth = 2;      // Kachelrahmen
    private const double PreviewInset = 3;     // Abstand der Vorschau zum Rahmen: das DWM-Thumbnail soll ihn nicht überdecken
    private const double TitleRowHeight = 36;
    private const double TileGap = 4;
    private const double ScrollBarWidth = 10;  // wie ScrollBar in App.xaml

    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly StackPanel _stack;

    public WindowListWindow()
    {
        Title = "Fokus-Dock – Fensterliste";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Theme.Apply(this);   // Farben und Schrift; der dunkle Scrollbalken kommt aus App.xaml

        _stack = new StackPanel();
        Content = new Border();   // wird in ShowFor gefüllt
    }

    /// <summary>Pixelrechteck der Liste auf dem Bildschirm (nach ShowFor), für den Klick-daneben-Test.</summary>
    internal NativeRect PixelRect { get; private set; }

    /// <param name="items">Fenster mit dem Namen ihrer App. Mehrere Apps hintereinander ergeben Gruppenzeilen und den App-Namen in der Kachel (Sammelplatz).</param>
    /// <param name="slot">Der Platz in Pixeln: Breite und Oberkante der Liste.</param>
    /// <param name="side">Die Seite, zu der der Platz gehört (Höhengrenze).</param>
    /// <param name="front">Das vordere Fenster des Platzes (Akzentrahmen und „vorne“).</param>
    /// <param name="header">Erste Kopfzeile, z. B. „L2 · Brave · 3 Fenster“.</param>
    /// <param name="onPick">Klick auf eine Kachel.</param>
    /// <param name="onCenter">Zurück-Taste auf einer Kachel.</param>
    /// <param name="onClose">Vorwärts-Taste auf der Liste.</param>
    internal void ShowFor(IReadOnlyList<(WindowInfo Window, string App)> items, NativeRect slot, NativeRect side, IntPtr front,
        string header, Action<IntPtr> onPick, Action<IntPtr> onCenter, Action onClose, bool showApps = false)
    {
        ReleaseThumbnails();
        _stack.Children.Clear();

        int groups = 0;
        string last = null;
        foreach ((WindowInfo _, string app) in items)
        {
            if (last == null || !string.Equals(app, last, StringComparison.Ordinal))
            {
                groups++;
                last = app;
            }
        }
        // App-Name und Gruppenzeilen nur im Sammelplatz, der mehrere Apps mischt (erkennbar an der Kopfzeile)
        bool showApp = showApps || header.StartsWith("Sammelplatz", StringComparison.Ordinal);
        if (!showApp)
        {
            groups = 0;
        }

        // Höhe berechnen (Pixel = DIP * Skalierung des Monitors)
        double scale = DpiScale();
        double widthDip = (slot.Right - slot.Left) / scale;
        int sideHeight = side.Bottom - side.Top;
        int slotHeight = slot.Bottom - slot.Top;

        double ContentHeight(bool scrollBar)
        {
            double tileWidth = widthDip - 2 - 2 * OuterMargin - (scrollBar ? ScrollBarWidth : 0);
            return 2 + HeaderHeight + items.Count * TileHeight(tileWidth) + groups * GroupRowHeight + 2 * OuterMargin;
        }

        bool needScroll = ContentHeight(false) * scale > sideHeight;
        double contentDip = ContentHeight(needScroll);
        int height = Math.Clamp((int)Math.Ceiling(contentDip * scale), slotHeight, sideHeight);
        int top = slot.Top;
        if (top + height > side.Bottom)
        {
            top = side.Bottom - height;
        }

        double tileW = widthDip - 2 - 2 * OuterMargin - (needScroll ? ScrollBarWidth : 0);
        double previewH = PreviewHeight(tileW);

        // Inhalt aufbauen
        var headerPanel = new StackPanel { Margin = new Thickness(14, 7, 14, 0), Height = HeaderHeight - 7 };
        headerPanel.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = "Klick: in den Platz · Zurück-Taste: in die Mitte · Esc: schließen",
            FontSize = 11,
            Foreground = Theme.Muted,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // Der ScrollViewer des vorigen Aufrufs hängt noch an _stack: lösen, sonst wirft WPF (zwei logische Eltern)
        if (_stack.Parent is ScrollViewer oldScroll)
        {
            oldScroll.Content = null;
        }
        var scroll = new ScrollViewer
        {
            Margin = new Thickness(OuterMargin),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _stack,
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroll, 1);
        grid.Children.Add(headerPanel);
        grid.Children.Add(scroll);

        var outer = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Line,
            Background = Theme.Surface,
            Child = grid,
        };
        Content = outer;
        ClipElement = scroll;

        // Ein Mausklick der Vorwärts-Taste irgendwo auf der Liste schließt sie
        PreviewMouseDown -= OnPreviewMouseDown;
        PreviewMouseDown += OnPreviewMouseDown;
        _onClose = onClose;

        string lastApp = null;
        var binds = new List<(FrameworkElement Area, WindowInfo Window, ImageSource Icon, StackPanel Placeholder)>();
        foreach ((WindowInfo window, string app) in items)
        {
            if (showApp && !string.Equals(app, lastApp, StringComparison.Ordinal))
            {
                int count = 0;
                foreach ((WindowInfo _, string other) in items)
                {
                    count += string.Equals(other, app, StringComparison.Ordinal) ? 1 : 0;
                }
                _stack.Children.Add(new Border
                {
                    Height = GroupRowHeight,
                    Child = new TextBlock
                    {
                        Text = $"{app} · {Count(count)}",
                        FontSize = 12,
                        Foreground = Theme.Muted,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 0, 0),
                    },
                });
            }
            lastApp = app;

            _stack.Children.Add(BuildTile(window, app, showApp, window.Hwnd == front, tileW, previewH, onPick, onCenter, binds));
        }

        PlaceWindow(new NativeRect { Left = slot.Left, Top = top, Right = slot.Right, Bottom = top + height });
        PixelRect = new NativeRect { Left = slot.Left, Top = top, Right = slot.Right, Bottom = top + height };
        int round = Native.DWMWCP_ROUND;   // runde Ecken (Windows 11)
        Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Show();

        // Erst nach dem Einblenden binden: DWM braucht das sichtbare Fenster
        foreach (var bind in binds)
        {
            bool minimized = Native.IsIconic(bind.Window.Hwnd);
            bool bound = !minimized && Bind(bind.Area, bind.Window.Hwnd);
            if (!bound)
            {
                bind.Placeholder.Children.Add(new Image { Source = bind.Icon, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, Visibility = bind.Icon == null ? Visibility.Collapsed : Visibility.Visible });
                bind.Placeholder.Children.Add(new TextBlock
                {
                    Text = minimized ? "Minimiert" : "Keine Vorschau",
                    Foreground = Theme.Muted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
        }
    }

    private Action _onClose;

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;
            _onClose?.Invoke();
        }
    }

    internal void HideList()
    {
        ReleaseThumbnails();
        Hide();
    }

    private static double PreviewHeight(double tileWidth) => Math.Max(0, tileWidth - 2 * BorderWidth - 2 * PreviewInset) * 9 / 16;

    /// <summary>Kachelhöhe: 40 (Titelzeile) + Vorschau + 10, aufgeteilt in Rahmen, Titelzeile, Innenabstand und Lücke.</summary>
    private static double TileHeight(double tileWidth) =>
        TileGap + 2 * BorderWidth + TitleRowHeight + 2 * PreviewInset + PreviewHeight(tileWidth);

    private static string Count(int n) => n == 1 ? "1 Fenster" : $"{n} Fenster";

    private Border BuildTile(WindowInfo window, string app, bool showApp, bool isFront, double tileWidth, double previewHeight,
        Action<IntPtr> onPick, Action<IntPtr> onCenter,
        List<(FrameworkElement Area, WindowInfo Window, ImageSource Icon, StackPanel Placeholder)> binds)
    {
        IntPtr hwnd = window.Hwnd;
        ImageSource icon = IconOf(window.ExePath);
        string title = Caption(window);

        var titleRow = new Grid { Height = TitleRowHeight, Margin = new Thickness(8, 0, 8, 0) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (icon != null)
        {
            var image = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            titleRow.Children.Add(image);
        }
        if (showApp)
        {
            var name = new TextBlock
            {
                Text = app,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(name, 1);
            titleRow.Children.Add(name);
        }
        var titleText = new TextBlock
        {
            Text = title,
            Foreground = Theme.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title,
        };
        Grid.SetColumn(titleText, 2);
        titleRow.Children.Add(titleText);
        if (isFront)
        {
            var badge = new Border
            {
                Background = Theme.Accent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "vorne", FontSize = 11, Foreground = Brushes.Black },
            };
            Grid.SetColumn(badge, 3);
            titleRow.Children.Add(badge);
        }

        var placeholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var preview = new Border
        {
            Background = Brushes.Black,
            Margin = new Thickness(PreviewInset),
            Height = previewHeight,
            Child = placeholder,
        };
        Grid.SetRow(preview, 1);
        Grid.SetRow(titleRow, 0);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(titleRow);
        content.Children.Add(preview);

        var tile = new Border
        {
            Margin = new Thickness(0, 0, 0, TileGap),
            Background = Theme.Card,
            BorderThickness = new Thickness(BorderWidth),
            BorderBrush = isFront ? Theme.Accent : Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Child = content,
        };
        tile.MouseEnter += (_, _) => tile.Background = Theme.Hover;
        tile.MouseLeave += (_, _) => tile.Background = Theme.Card;
        tile.MouseLeftButtonUp += (_, _) => onPick?.Invoke(hwnd);
        tile.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1)
            {
                e.Handled = true;
                onCenter?.Invoke(hwnd);
            }
        };

        binds.Add((preview, window, icon, placeholder));
        return tile;
    }

    /// <summary>Icon der EXE, nach Pfad zwischengespeichert. Null, wenn es sich nicht lesen lässt.</summary>
    private static ImageSource IconOf(string exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return null;
        }
        if (IconCache.TryGetValue(exePath, out ImageSource cached))
        {
            return cached;
        }

        ImageSource source = null;
        try
        {
            using System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
            }
        }
        catch (Exception)
        {
            // Kein Icon lesbar (Datei weg, kein Zugriff): die Kachel kommt ohne aus
        }

        IconCache[exePath] = source;
        return source;
    }

    private static string Caption(WindowInfo window) =>
        string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
}
