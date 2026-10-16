using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FokusDock;

/// <summary>
/// Strg+Rechtsklick auf eine Spalte: Vorschauen aller Fenster der App, untereinander und scrollbar.
/// Ein Klick auf eine Vorschau setzt dieses Fenster in den Kachelplatz.
/// </summary>
public sealed class WindowListWindow : ThumbnailWindow
{
    private readonly StackPanel _stack;

    public WindowListWindow()
    {
        Title = "Fokus-Fenster";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16));

        _stack = new StackPanel();
        var scroll = new ScrollViewer
        {
            Margin = new Thickness(8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _stack,
        };
        Content = scroll;
        ClipElement = scroll;
    }

    internal void ShowFor(IReadOnlyList<WindowInfo> windows, NativeRect area, Action<IntPtr> onPick)
    {
        ReleaseThumbnails();
        _stack.Children.Clear();
        PlaceWindow(area);
        Show();

        // Breite der Kachel: Spaltenbreite ohne Rand und Scrollbalken
        double width = (area.Right - area.Left) / Monitors.Scale - 16 - 20;
        double thumbHeight = Math.Max(0, width) * 9 / 16;

        foreach (WindowInfo window in windows)
        {
            IntPtr hwnd = window.Hwnd;
            var thumb = new Border { Background = Brushes.Black, Height = thumbHeight, Cursor = Cursors.Hand };
            var caption = new TextBlock
            {
                Text = Caption(window),
                Foreground = Brushes.White,
                Margin = new Thickness(8, 6, 8, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var content = new StackPanel();
            content.Children.Add(caption);
            content.Children.Add(thumb);

            var tile = new Border
            {
                Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                Child = content,
            };
            tile.MouseLeftButtonUp += (_, _) => onPick?.Invoke(hwnd);

            _stack.Children.Add(tile);
            Bind(thumb, hwnd);
        }
    }

    internal void HideList()
    {
        ReleaseThumbnails();
        Hide();
    }

    private static string Caption(WindowInfo window) =>
        string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
}
