using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FokusDock;

/// <summary>
/// Kurze Einblendung unten in der Mitte des Arbeitsbereichs, z. B. „Fokus-Dock an“. Es gibt nur eine Instanz:
/// ein neuer Toast ersetzt den alten. Ohne Knopf ist sie klickdurchlässig und nimmt nie den Fokus.
/// </summary>
internal sealed class Toast : OverlayWindow
{
    private const double ShowSeconds = 2.5;       // Anzeigedauer ohne Aktion
    private const double ActionSeconds = 6;       // Anzeigedauer mit Aktion (Rückgängig)
    private const double BottomGapDip = 24;       // Abstand zur Unterkante des Arbeitsbereichs
    private const double MaxTextWidth = 520;

    private static Toast _instance;

    /// <summary>Solange ein Vollbild läuft (P2), erscheint nichts. DockWindow setzt das Feld bei jedem Abgleich.</summary>
    internal static bool Paused { get; set; }

    private readonly DispatcherTimer _timer = new();
    private int _generation;      // erkennt veraltete Abschlüsse der Ausblendung, wenn inzwischen ein neuer Toast läuft
    private Action _action;

    private Toast()
    {
        SizeToContent = SizeToContent.WidthAndHeight;

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            FadeOut();
        };

        // Mit Knopf hält die Anzeige an, solange die Maus darüber steht
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) =>
        {
            if (_action != null && IsVisible)
            {
                _timer.Start();
            }
        };
    }

    /// <summary>Zeigt den Text (und optional einen Knopf mit Aktion). Darf aus jedem Thread gerufen werden.</summary>
    public static void Show(string text, string actionText = null, Action action = null)
    {
        Application app = Application.Current;
        if (app == null || Paused || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => Show(text, actionText, action)));
            return;
        }

        _instance ??= new Toast();
        _instance.Display(text, actionText, action);
    }

    private void Display(string text, string actionText, Action action)
    {
        _timer.Stop();
        _generation++;
        _action = actionText != null && action != null ? action : null;

        Content = BuildContent(text, _action != null ? actionText : null);
        ClickThrough = _action == null;
        _timer.Interval = TimeSpan.FromSeconds(_action != null ? ActionSeconds : ShowSeconds);

        // Größe aus dem Inhalt, dann in echten Pixeln unten mittig im Arbeitsbereich des Primärmonitors
        new WindowInteropHelper(this).EnsureHandle();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int width = (int)Math.Ceiling(content.DesiredSize.Width * scale);
        int height = (int)Math.Ceiling(content.DesiredSize.Height * scale);

        NativeRect work = Monitors.PrimaryWorkArea();
        int left = work.Left + (work.Right - work.Left - width) / 2;
        int bottom = work.Bottom - (int)Math.Round(BottomGapDip * scale);
        UpdateLayout();

        if (!IsVisible)
        {
            Opacity = 0;
        }
        PlacePixels(new NativeRect { Left = left, Top = bottom - height, Right = left + width, Bottom = bottom });

        BeginAnimation(OpacityProperty, new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(120) });
        _timer.Start();
    }

    /// <summary>Text mit Akzentstreifen links und optional einem Knopf rechts.</summary>
    private FrameworkElement BuildContent(string text, string actionText)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = Theme.Text,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaxTextWidth,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (actionText != null)
        {
            var button = new Button
            {
                Content = actionText,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
            button.SetResourceReference(StyleProperty, "AccentButton");
            button.Click += (_, _) => RunAction();
            row.Children.Add(button);
        }

        var strip = new Border { Width = 3, Background = Theme.Accent, CornerRadius = new CornerRadius(7, 0, 0, 7) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(strip, 0);
        var padded = new Border { Padding = new Thickness(14, 10, 14, 10), Child = row };
        Grid.SetColumn(padded, 1);
        grid.Children.Add(strip);
        grid.Children.Add(padded);

        return new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid,
        };
    }

    /// <summary>Knopf gedrückt: Toast sofort weg, dann die Aktion (sie darf selbst einen neuen Toast zeigen).</summary>
    private void RunAction()
    {
        Action action = _action;
        _action = null;
        Dismiss();
        action?.Invoke();
    }

    /// <summary>Sofort ausblenden, ohne Animation.</summary>
    private void Dismiss()
    {
        _timer.Stop();
        _generation++;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Hide();
    }

    private void FadeOut()
    {
        int generation = ++_generation;
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(200) };
        fade.Completed += (_, _) =>
        {
            // Ein neuer Toast während der Ausblendung zählt die Nummer hoch: dann bleibt das Fenster stehen
            if (generation == _generation)
            {
                _action = null;
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, fade);
    }
}
