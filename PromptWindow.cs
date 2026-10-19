using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FokusDock;

/// <summary>
/// Kleiner Dialog mit einer Texteingabe, zum Beispiel für den Namen eines Layouts. Enter bestätigt, Esc bricht ab,
/// ein leerer Name wird nicht angenommen.
/// </summary>
internal sealed class PromptWindow : Window
{
    private readonly TextBox _box = new();
    private readonly TextBlock _error = new();

    private PromptWindow(string title, string label, string initial)
    {
        Theme.Apply(this);
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;

        _box.Text = initial;
        _box.Margin = new Thickness(0, 6, 0, 0);
        _box.Padding = new Thickness(6, 4, 6, 4);
        _error.Foreground = System.Windows.Media.Brushes.IndianRed;
        _error.FontSize = 12;
        _error.Margin = new Thickness(0, 4, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        _box.TextChanged += (_, _) => _error.Visibility = Visibility.Collapsed;

        var ok = new Button { Content = "Speichern", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Abbrechen", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = label });
        root.Children.Add(_box);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
    }

    public string Result { get; private set; }

    private void Accept()
    {
        string text = _box.Text.Trim();
        if (text.Length == 0)
        {
            _error.Text = "Gib einen Namen ein.";
            _error.Visibility = Visibility.Visible;
            return;
        }

        Result = text;
        DialogResult = true;
    }

    /// <summary>Fragt nach einem Text. Liefert null, wenn der Dialog abgebrochen wurde.</summary>
    public static string Ask(string title, string label, string initial = "")
    {
        var window = new PromptWindow(title, label, initial);
        return window.ShowDialog() == true ? window.Result : null;
    }
}
