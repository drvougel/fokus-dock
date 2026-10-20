using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FokusDock;

/// <summary>
/// Hinweisfenster, das den Programmablauf nicht anhält. Eine MessageBox ist modal: Solange sie offen ist, laufen die Timer von
/// Fokus-Dock nicht mehr (Abgleich, Platzvergabe, Einordnen aus dem Launcher). Dieses Fenster ist nur ein normales, nicht-modales
/// Fenster, bleibt vorn und schließt mit OK oder Esc.
/// </summary>
internal sealed class NoticeWindow : Window
{
    private NoticeWindow(string title, string text)
    {
        Theme.Apply(this);
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;

        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        var ok = new Button
        {
            Content = "OK",
            Padding = new Thickness(22, 4, 22, 4),
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        ok.Click += (_, _) => Close();

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(message);
        root.Children.Add(ok);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>Zeigt den Hinweis und kehrt sofort zurück.</summary>
    public static void Show(string title, string text) => new NoticeWindow(title, text).Show();
}
