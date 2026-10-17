using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FokusDock;

/// <summary>
/// Prioritätsfenster: alle bekannten Apps, vorne in der Liste = wichtiger. Jede Zeile zeigt Rang, Status (läuft / läuft nicht),
/// Name, Merkmale und den aktuellen Platz. Einstellungen der gewählten App stehen im Detailbereich darunter.
/// Alles wird sofort gespeichert; „Plätze neu verteilen“ belegt die Plätze nach der Reihenfolge neu.
/// </summary>
public sealed class PriorityWindow : Window
{
    /// <summary>Eine Zeile der Liste mit den Elementen, die sich beim Aktualisieren ändern.</summary>
    private sealed class Row
    {
        public Grid Grid;
        public Ellipse Dot;
        public TextBlock Rank;
        public TextBlock Name;
        public TextBlock Features;
        public TextBlock Place;
        public bool Running;
    }

    private readonly Action<List<AppEntry>, bool> _apply; // (Reihenfolge, speichern?)
    private readonly Action _resort;
    private readonly Action _openLayout;
    private readonly Func<AppEntry, bool> _hasFixedCell;   // hat die App eine feste Zelle?
    private readonly ListBox _list = new();
    private readonly List<Row> _rows = new();
    private readonly TextBlock _heading = new();
    private readonly CheckBox _guaranteed = new();
    private readonly CheckBox _ignored = new();
    private readonly Button _remove = new();
    private readonly Border _detail = new();
    private List<AppEntry> _order = new();
    private List<bool> _running = new();
    private List<string> _places = new();
    private bool _rebuilding;   // Aufbau der Liste läuft: Auswahländerungen ignorieren
    private bool _syncing;      // Kästchen werden per Code gesetzt: nicht als Eingabe werten

    public PriorityWindow(Action<List<AppEntry>, bool> apply, Action resort, Action openLayout, Func<AppEntry, bool> hasFixedCell)
    {
        _apply = apply;
        _resort = resort;
        _openLayout = openLayout;
        _hasFixedCell = hasFixedCell;

        Title = "Fokus-Dock – Prioritätsliste";
        Width = 560;
        Height = 600;
        MinWidth = 520;
        MinHeight = 380;
        // Mit Taskleistenknopf: sonst verschwindet das Fenster hinter den Spaltenfenstern und ist nicht mehr zu finden
        ShowInTaskbar = true;
        Theme.Apply(this);   // Farben, Schrift, dunkle Titelleiste; Liste, Knöpfe und Kästchen stylt App.xaml

        var header = new TextBlock
        {
            Text = "Oben = wichtiger. Laufende Apps bekommen in dieser Reihenfolge einen Platz, erst links von oben, dann rechts. " +
                   "▲/▼ wirkt beim nächsten freien Platz, „Plätze neu verteilen“ sofort.",
            Foreground = Theme.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        // Detailbereich der gewählten App
        _heading.FontSize = 15;
        _heading.FontWeight = FontWeights.SemiBold;
        _heading.Margin = new Thickness(0, 0, 0, 6);

        _guaranteed.Content = "Platz garantiert";
        _guaranteed.ToolTip = "Bekommt immer einen Seitenplatz, wenn sie läuft und nicht in der Mitte steht. Verdrängt dafür eine normale App.";
        _guaranteed.Margin = new Thickness(0, 0, 0, 4);
        _guaranteed.Click += (_, _) => OnGuaranteedChanged();

        _ignored.Content = "Nicht verwalten";
        _ignored.ToolTip = "Fokus-Dock lässt die Fenster dieser App in Ruhe.";
        _ignored.Margin = new Thickness(0, 0, 0, 8);
        _ignored.Click += (_, _) => OnIgnoredChanged();

        _remove.Content = "Entfernen";
        _remove.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTipService.SetShowOnDisabled(_remove, true);
        _remove.Click += (_, _) => Remove();

        var detailStack = new StackPanel();
        detailStack.Children.Add(_heading);
        detailStack.Children.Add(_guaranteed);
        detailStack.Children.Add(_ignored);
        detailStack.Children.Add(_remove);
        _detail.Child = detailStack;
        _detail.Background = Theme.Card;
        _detail.CornerRadius = new CornerRadius(4);
        _detail.Padding = new Thickness(12, 10, 12, 10);
        _detail.Margin = new Thickness(0, 10, 0, 0);

        // Fußleiste: Reihenfolge ändern, Plätze neu verteilen, Layout, Schließen
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        Button close = MakeButton("Schließen", (_, _) => Hide());
        close.Margin = new Thickness(0);
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);

        Button up = MakeIconButton("", "Höher (Alt+↑)", (_, _) => Move(-1));
        Button down = MakeIconButton("", "Tiefer (Alt+↓)", (_, _) => Move(1));
        Button resortButton = MakeButton("Plätze neu verteilen", (_, _) => _resort());
        resortButton.ToolTip = "Alle Plätze jetzt nach dieser Reihenfolge neu belegen";
        Button layout = MakeButton("Layout…", (_, _) => _openLayout());
        foreach (Button b in new[] { up, down, resortButton, layout })
        {
            DockPanel.SetDock(b, Dock.Left);
            footer.Children.Add(b);
        }

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_detail, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(_detail);
        root.Children.Add(_list);
        Content = root;

        _list.SelectionChanged += (_, e) => OnSelectionChanged(e);

        // Fenster nur ausblenden, damit es mit dem Hotkey wieder öffnet
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Die gewählte App (Auswahl hängt an der Referenz, nicht am Zeilenindex), sonst null.</summary>
    private AppEntry Selected =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _order.Count ? _order[_list.SelectedIndex] : null;

    /// <summary>Alt+↑/↓ verschiebt, Strg+Pos1/Ende ganz nach oben/unten, Entf entfernt, Esc schließt.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys mods = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if ((mods & ModifierKeys.Alt) != 0 && (key == Key.Up || key == Key.Down))
        {
            Move(key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if ((mods & ModifierKeys.Control) != 0 && key == Key.Home)
        {
            MoveTo(0);
            e.Handled = true;
        }
        else if ((mods & ModifierKeys.Control) != 0 && key == Key.End)
        {
            MoveTo(_order.Count - 1);
            e.Handled = true;
        }
        else if (key == Key.Delete && mods == ModifierKeys.None)
        {
            Remove();
            e.Handled = true;
        }
        else if (key == Key.F1)
        {
            HelpWindow.Request();
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Zeigt die Liste in der übergebenen Reihenfolge (vorne = wichtigste). running: pro App, ob Fenster offen sind.
    /// places: pro App der Platztext.
    /// </summary>
    public void Open(IEnumerable<AppEntry> apps, IReadOnlyList<bool> running, IReadOnlyList<string> places)
    {
        AppEntry keep = Selected;
        _order = apps.ToList();
        _running = running.ToList();
        _places = places.ToList();
        Rebuild(keep);
        Show();
        // Das Fenster hat einen Taskleistenknopf und lässt sich minimieren: Activate() allein holt es nicht zurück
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    /// <summary>
    /// Stand nachziehen, solange das Fenster offen ist (ruft DockWindow nach jedem Abgleich). Bei gleicher App-Folge ändert das
    /// nur Texte und Punkte der bestehenden Zeilen. Weicht die Folge ab (neue App erkannt), wird die Liste neu aufgebaut, die Auswahl bleibt.
    /// </summary>
    public void UpdateState(IReadOnlyList<AppEntry> apps, IReadOnlyList<bool> running, IReadOnlyList<string> places)
    {
        if (!IsVisible)
        {
            return;
        }

        _running = running.ToList();
        _places = places.ToList();

        bool same = apps.Count == _order.Count;
        for (int i = 0; same && i < apps.Count; i++)
        {
            same = ReferenceEquals(apps[i], _order[i]);
        }

        if (!same)
        {
            AppEntry keep = Selected;
            _order = apps.ToList();
            Rebuild(keep);
            return;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            UpdateRow(i);
        }
        UpdateDetail();
    }

    /// <summary>Baut alle Zeilen neu auf und wählt select (wenn noch vorhanden, sonst die erste Zeile).</summary>
    private void Rebuild(AppEntry select)
    {
        _rebuilding = true;
        try
        {
            _list.Items.Clear();
            _rows.Clear();
            for (int i = 0; i < _order.Count; i++)
            {
                var row = new Row();
                row.Grid = new Grid { Margin = new Thickness(2, 2, 2, 2) };
                row.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

                row.Rank = new TextBlock
                {
                    Foreground = Theme.Muted,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Margin = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Name = new TextBlock
                {
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                row.Features = new TextBlock
                {
                    Foreground = Theme.Muted,
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Place = new TextBlock
                {
                    Foreground = Theme.Muted,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                Grid.SetColumn(row.Rank, 0);
                Grid.SetColumn(row.Dot, 1);
                Grid.SetColumn(row.Name, 2);
                Grid.SetColumn(row.Features, 3);
                Grid.SetColumn(row.Place, 4);
                row.Grid.Children.Add(row.Rank);
                row.Grid.Children.Add(row.Dot);
                row.Grid.Children.Add(row.Name);
                row.Grid.Children.Add(row.Features);
                row.Grid.Children.Add(row.Place);

                _rows.Add(row);
                _list.Items.Add(row.Grid);
                row.Running = !(i < _running.Count && _running[i]);   // erzwingt die erste Darstellung in UpdateRow
                UpdateRow(i, force: true);
            }

            int index = select == null ? -1 : _order.FindIndex(a => ReferenceEquals(a, select));
            if (index < 0 && _order.Count > 0)
            {
                index = 0;
            }
            _list.SelectedIndex = index;
        }
        finally
        {
            _rebuilding = false;
        }

        UpdateDetail();
        if (_list.SelectedItem != null)
        {
            _list.ScrollIntoView(_list.SelectedItem);
        }
    }

    /// <summary>Setzt Rang, Punkt, Namen, Merkmale und Platz einer Zeile; fasst nur an, was sich geändert hat.</summary>
    private void UpdateRow(int i, bool force = false)
    {
        if (i >= _rows.Count || i >= _order.Count)
        {
            return;
        }

        Row row = _rows[i];
        AppEntry app = _order[i];
        bool running = i < _running.Count && _running[i];
        string place = i < _places.Count ? _places[i] : "–";

        string rank = (i + 1) + ".";
        if (row.Rank.Text != rank)
        {
            row.Rank.Text = rank;
        }
        if (row.Name.Text != app.Name)
        {
            row.Name.Text = app.Name;
        }

        if (force || row.Running != running)
        {
            row.Running = running;
            if (running)
            {
                row.Dot.Fill = Theme.Ok;
                row.Dot.Stroke = null;
                row.Dot.StrokeThickness = 0;
            }
            else
            {
                row.Dot.Fill = null;
                row.Dot.Stroke = Theme.Line;
                row.Dot.StrokeThickness = 1.5;
            }
            row.Dot.ToolTip = running ? "läuft" : "läuft nicht";
        }

        var features = new List<string>();
        if (app.AlwaysOutside)
        {
            features.Add("garantiert");
        }
        if (app.Ignored)
        {
            features.Add("nicht verwaltet");
        }
        string featureText = string.Join(" · ", features);
        if (row.Features.Text != featureText)
        {
            row.Features.Text = featureText;
        }

        if (row.Place.Text != place)
        {
            row.Place.Text = place;
        }

        ApplyNameBrush(i);
    }

    /// <summary>Laufende und gewählte Apps in Textfarbe, die übrigen gedämpft (auf der gewählten Zeile reicht der Kontrast sonst nicht).</summary>
    private void ApplyNameBrush(int i)
    {
        if (i < 0 || i >= _rows.Count)
        {
            return;
        }

        bool running = i < _running.Count && _running[i];
        Brush brush = running || i == _list.SelectedIndex ? Theme.Text : Theme.Muted;
        if (!ReferenceEquals(_rows[i].Name.Foreground, brush))
        {
            _rows[i].Name.Foreground = brush;
        }
    }

    private void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        if (_rebuilding)
        {
            return;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            ApplyNameBrush(i);
        }
        UpdateDetail();
        if (_list.SelectedItem != null)
        {
            _list.ScrollIntoView(_list.SelectedItem);
        }
    }

    /// <summary>Detailbereich für die gewählte App: Überschrift, Kästchen und Zustand des Knopfs „Entfernen“.</summary>
    private void UpdateDetail()
    {
        AppEntry app = Selected;
        _detail.Visibility = app == null ? Visibility.Collapsed : Visibility.Visible;
        if (app == null)
        {
            return;
        }

        _syncing = true;
        try
        {
            _heading.Text = app.Name;
            _guaranteed.IsChecked = app.AlwaysOutside;
            _ignored.IsChecked = app.Ignored;
        }
        finally
        {
            _syncing = false;
        }

        bool running = IsRunning(_list.SelectedIndex);
        _remove.IsEnabled = !running;
        _remove.ToolTip = running
            ? "Läuft gerade und würde sofort wieder aufgenommen. Stattdessen „Nicht verwalten“ wählen."
            : null;
    }

    private bool IsRunning(int index) => index >= 0 && index < _running.Count && _running[index];

    private void OnGuaranteedChanged()
    {
        AppEntry app = Selected;
        if (_syncing || app == null)
        {
            return;
        }

        app.AlwaysOutside = _guaranteed.IsChecked == true;
        UpdateRow(_list.SelectedIndex);
        _apply(_order, true);
    }

    private void OnIgnoredChanged()
    {
        AppEntry app = Selected;
        if (_syncing || app == null)
        {
            return;
        }

        app.Ignored = _ignored.IsChecked == true;
        UpdateRow(_list.SelectedIndex);
        _apply(_order, true);
    }

    private void Move(int step)
    {
        int index = _list.SelectedIndex;
        if (index < 0)
        {
            return;
        }
        MoveTo(index + step);
    }

    /// <summary>Schiebt die gewählte App an die Stelle target (0 = ganz oben).</summary>
    private void MoveTo(int target)
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _order.Count || target < 0 || target >= _order.Count || target == index)
        {
            return;
        }

        AppEntry app = _order[index];
        bool running = index < _running.Count && _running[index];
        string place = index < _places.Count ? _places[index] : "–";

        _order.RemoveAt(index);
        _order.Insert(target, app);
        if (index < _running.Count)
        {
            _running.RemoveAt(index);
        }
        if (index < _places.Count)
        {
            _places.RemoveAt(index);
        }
        _running.Insert(Math.Min(target, _running.Count), running);
        _places.Insert(Math.Min(target, _places.Count), place);

        _apply(_order, true);
        Rebuild(app);
    }

    /// <summary>
    /// Eintrag löschen, z. B. für alte Apps. Läuft die App, geht das nicht (sie käme sofort wieder).
    /// Hat der Eintrag eigene Einstellungen (Titelanfang, mehrere Prozesse, feste Zelle), fragt das Fenster vorher nach.
    /// </summary>
    private void Remove()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _order.Count || IsRunning(index))
        {
            return;
        }

        AppEntry app = _order[index];
        bool custom = !string.IsNullOrEmpty(app.TitleStartsWith) || app.Processes.Count > 1 || _hasFixedCell(app);
        if (custom && MessageBox.Show(this, $"„{app.Name}“ hat eigene Einstellungen. Trotzdem entfernen?", "Fokus-Dock",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _order.RemoveAt(index);
        if (index < _running.Count)
        {
            _running.RemoveAt(index);
        }
        if (index < _places.Count)
        {
            _places.RemoveAt(index);
        }
        _apply(_order, true);
        Rebuild(_order.Count == 0 ? null : _order[Math.Min(index, _order.Count - 1)]);
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        button.Click += click;
        return button;
    }

    /// <summary>Quadratischer Symbolknopf (32×32) mit einem Zeichen der Symbolschrift.</summary>
    private static Button MakeIconButton(string glyph, string tip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = tip,
        };
        button.Click += click;
        return button;
    }
}
