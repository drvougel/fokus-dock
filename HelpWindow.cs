using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FokusDock;

/// <summary>
/// Bedienungshilfe: Maus, Tastatur und Begriffe auf einer Seite. Aufruf über Infobereich › Bedienung…, F1 in den
/// Fokus-Dock-Fenstern und einmal automatisch beim ersten Start ohne config.json. Das Fenster wird nur ausgeblendet.
/// </summary>
internal sealed class HelpWindow : Window
{
    /// <summary>Öffnet die Hilfe; DockWindow setzt das, damit F1 in anderen Fenstern sie ohne Verweis aufs DockWindow erreicht.</summary>
    internal static Action Opener { get; set; }

    /// <summary>Hilfe öffnen (tut nichts, solange Fokus-Dock nicht läuft).</summary>
    internal static void Request() => Opener?.Invoke();

    public HelpWindow(AppConfig config, Action openConfig)
    {
        Title = "Fokus-Dock – Bedienung";
        Width = 560;
        Height = 640;
        MinWidth = 420;
        MinHeight = 360;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Theme.Apply(this);

        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        AddSection(body, "Maus",
            ("Zurück-Taste auf ein Fenster einer Spalte",
                "Das Fenster kommt in die Mitte, das Mitte-Fenster tauscht auf den Platz."),
            ("Vorwärts-Taste auf eine Spalte",
                "Vorschauen aller Fenster des Platzes. Klick setzt ein Fenster in den Platz, Zurück-Taste holt es in die Mitte, Esc schließt."),
            ("Fenster ziehen und über einem Platz loslassen",
                "Das Fenster rastet ein, seine App bekommt den Platz fest. Mit gedrückter Shift-Taste frei ablegen."));

        AddSection(body, "Tastatur",
            (DockWindow.HotkeyText(config.Hotkey), "Spalten ein/aus (Ein/Aus). Das Mitte-Fenster bleibt, wo es ist."),
            (DockWindow.HotkeyText(config.PriorityHotkey), "Prioritätsliste."),
            (DockWindow.HotkeyText(config.ExitHotkey), "Fokus-Dock beenden."),
            ("F1 in Fokus-Dock-Fenstern", "Diese Hilfe."),
            ("Symbol im Infobereich", "Linksklick öffnet die Prioritätsliste, Rechtsklick das Menü."));

        AddSection(body, "Begriffe",
            ("Platz", "Ein Feld in der linken oder rechten Spalte. Jede App hat höchstens einen Platz, dort steht ihr zuletzt aktives Fenster vorne."),
            ("Mitte", "Das große Fenster zwischen den Spalten, in dem du gerade arbeitest."),
            ("Sammelplatz", "Der unterste Platz rechts. Dort liegen alle Apps ohne eigenen Platz übereinander."),
            ("Platz garantiert", "Läuft die App und steht nicht in der Mitte, bekommt sie immer einen Platz, notfalls auf Kosten einer weniger wichtigen App."),
            ("Nicht verwalten", "Fokus-Dock fasst Fenster dieser App nie an: kein Platz, kein Sammelplatz, kein Einrasten."));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };

        // Fuß: Pfad der Einstellungen und zwei Knöpfe
        var pathText = new TextBlock
        {
            Text = "Einstellungen: " + DockWindow.ConfigPath,
            Foreground = Theme.Muted,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var open = new Button { Content = "config.json öffnen", Margin = new Thickness(0, 0, 6, 0) };
        open.Click += (_, _) => openConfig();
        var close = new Button { Content = "Schließen" };
        close.Click += (_, _) => Hide();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(open);
        buttons.Children.Add(close);

        var footer = new Grid { Margin = new Thickness(16, 8, 16, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(pathText);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(scroll);
        Content = root;

        // Nur ausblenden, damit die Hilfe wieder öffnet
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.F1)
            {
                e.Handled = true;   // ist schon offen
            }
        };
    }

    /// <summary>Zeigt die Hilfe und holt sie nach vorn.</summary>
    public void Open()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
    }

    /// <summary>Überschrift (SemiBold 15) und zweispaltiges Grid Aktion | Wirkung.</summary>
    private static void AddSection(StackPanel parent, string heading, params (string Action, string Effect)[] rows)
    {
        parent.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, parent.Children.Count == 0 ? 0 : 16, 0, 6),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var action = new TextBlock
            {
                Text = rows[i].Action,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 12, 8),
            };
            var effect = new TextBlock
            {
                Text = rows[i].Effect,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(action, i);
            Grid.SetRow(effect, i);
            Grid.SetColumn(effect, 1);
            grid.Children.Add(action);
            grid.Children.Add(effect);
        }
        parent.Children.Add(grid);
    }
}
