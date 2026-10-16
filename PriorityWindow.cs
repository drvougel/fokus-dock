using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FokusDock;

/// <summary>
/// Prioritätsfenster: alle bekannten Apps, vorne in der Liste = wichtiger. Laufende Apps stehen hell,
/// nicht laufende grau. ▲/▼ und „immer außen“ werden sofort gespeichert. „Neu sortieren“ verteilt die Plätze neu.
/// </summary>
public sealed class PriorityWindow : Window
{
    private readonly Action<List<AppEntry>, bool> _apply; // (Reihenfolge, speichern?)
    private readonly Action _resort;
    private readonly Action _openLayout;
    private readonly ListBox _list = new();
    private List<AppEntry> _order = new();
    private List<bool> _running = new();

    public PriorityWindow(Action<List<AppEntry>, bool> apply, Action resort, Action openLayout)
    {
        _apply = apply;
        _resort = resort;
        _openLayout = openLayout;

        Title = "Fokus-Dock Priorität";
        Width = 420;
        Height = 520;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
        Foreground = Brushes.White;

        // Dunkles Design: die Standardzeilen der Liste sind sonst weiß mit weißer Schrift
        _list.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
        _list.BorderThickness = new Thickness(0);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B))));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        _list.ItemContainerStyle = itemStyle;

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(MakeButton("▲", (_, _) => Move(-1)));
        buttons.Children.Add(MakeButton("▼", (_, _) => Move(1)));
        buttons.Children.Add(MakeButton("Entfernen", (_, _) => Remove()));
        buttons.Children.Add(MakeButton("Neu sortieren", (_, _) =>
        {
            _apply(_order, true);
            _resort();
        }));
        buttons.Children.Add(MakeButton("Layout…", (_, _) => _openLayout()));
        buttons.Children.Add(MakeButton("Schließen", (_, _) => Hide()));

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;

        // Fenster nur ausblenden, damit es mit dem Hotkey wieder öffnet
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>Zeigt die Liste in der übergebenen Reihenfolge (vorne = wichtigste). running: pro App, ob Fenster offen sind.</summary>
    public void Open(IEnumerable<AppEntry> apps, IReadOnlyList<bool> running)
    {
        _order = apps.ToList();
        _running = running.ToList();
        Rebuild(_list.SelectedIndex < 0 ? 0 : Math.Min(_list.SelectedIndex, _order.Count - 1));
        Show();
        Activate();
    }

    private void Rebuild(int select)
    {
        _list.Items.Clear();
        for (int i = 0; i < _order.Count; i++)
        {
            AppEntry app = _order[i];
            bool running = i < _running.Count && _running[i];
            var row = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };

            var check = new CheckBox
            {
                Content = "immer außen",
                IsChecked = app.AlwaysOutside,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            check.Checked += (_, _) => { app.AlwaysOutside = true; _apply(_order, true); };
            check.Unchecked += (_, _) => { app.AlwaysOutside = false; _apply(_order, true); };
            DockPanel.SetDock(check, Dock.Right);

            row.Children.Add(check);
            row.Children.Add(new TextBlock
            {
                Text = app.Name,
                Foreground = running ? Brushes.White : Brushes.Gray,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _list.Items.Add(row);
        }

        if (_order.Count > 0)
        {
            _list.SelectedIndex = select;
        }
    }

    private void Move(int step)
    {
        int index = _list.SelectedIndex;
        int target = index + step;
        if (index < 0 || target < 0 || target >= _order.Count)
        {
            return;
        }

        (_order[index], _order[target]) = (_order[target], _order[index]);
        (_running[index], _running[target]) = (_running[target], _running[index]);
        _apply(_order, true);
        Rebuild(target);
    }

    /// <summary>Eintrag löschen, z. B. für alte Apps. Läuft die App noch, wird sie beim nächsten Abgleich wieder angehängt.</summary>
    private void Remove()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _order.Count)
        {
            return;
        }

        _order.RemoveAt(index);
        _running.RemoveAt(index);
        _apply(_order, true);
        Rebuild(Math.Min(index, _order.Count - 1));
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4) };
        button.Click += click;
        return button;
    }
}
