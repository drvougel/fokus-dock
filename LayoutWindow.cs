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
/// Layout-Editor als Bildschirm-Vorschau: Apps per Drag & Drop auf einen Platz ziehen,
/// Plätze teilen und zusammenführen, Mitte-Breite und Zeilen je Seite einstellen.
/// Die Mitte bleibt das Mitte-Fenster, der Sammelplatz nimmt nie eine feste App auf.
/// </summary>
public sealed class LayoutWindow : Window
{
    private const double CanvasWidth = 640;

    private readonly Action<List<CellEntry>, int, bool> _apply; // (Zellen, centerPercent, speichern?)
    private readonly Canvas _canvas = new() { Width = CanvasWidth, Height = 360, Background = Theme.Surface };
    private readonly ListBox _apps = new() { Width = 200 };
    private readonly Slider _slider = new()
    {
        Minimum = 40, Maximum = 60, TickFrequency = 1, IsSnapToTickEnabled = true,
        Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 24, 0),
    };
    private readonly TextBlock _sliderLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 110 };
    private readonly Dictionary<string, List<RadioButton>> _rowButtons = new();
    private readonly TextBlock _status = new() { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _splitH = MakeButton("Oben/unten teilen");
    private readonly Button _splitV = MakeButton("Links/rechts teilen");
    private readonly Button _merge = MakeButton("Zusammenführen");
    private readonly Button _clear = MakeButton("Platz leeren");

    private List<CellEntry> _cells = new();
    private string _selected;
    private int _centerPercent = 50;
    private int _defaultRows = 3;
    private bool _overflowEnabled = true;
    private double _canvasHeight = 360;
    private bool _dirty;
    private bool _syncing;            // Steuerelemente werden per Code gesetzt, keine Änderung des Nutzers
    private bool _forceClose;         // beim Beenden des Programms wirklich schließen
    private string _dragName;         // App, auf der die Maustaste gedrückt wurde (Start eines möglichen Ziehens)
    private Point _dragOrigin;

    public LayoutWindow(Action<List<CellEntry>, int, bool> apply)
    {
        _apply = apply;

        Width = 920;
        Height = 660;
        MinWidth = 760;
        MinHeight = 480;
        // Mit Taskleistenknopf: sonst verschwindet das Fenster hinter den Spaltenfenstern und ist nicht mehr zu finden
        ShowInTaskbar = true;
        Theme.Apply(this);   // Farben, Schrift, dunkle Titelleiste; Liste, Knöpfe und Auswahlknöpfe stylt App.xaml
        UpdateTitle();

        // Apps aus der Liste ziehen: erst ab der Mindestdistanz und nur, wenn die Taste auf einer Zeile gedrückt wurde
        // (nicht am Scrollbalken). Übergeben wird der App-Name.
        _apps.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragName = AppNameAt(e.OriginalSource);
            _dragOrigin = e.GetPosition(_apps);
        };
        _apps.PreviewMouseLeftButtonUp += (_, _) => _dragName = null;
        _apps.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragName == null)
            {
                return;
            }

            Point p = e.GetPosition(_apps);
            if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            string name = _dragName;
            _dragName = null;
            DragDrop.DoDragDrop(_apps, name, DragDropEffects.Copy);
        };
        // Doppelklick: App in den gewählten Platz setzen
        _apps.PreviewMouseDoubleClick += (_, e) =>
        {
            string name = AppNameAt(e.OriginalSource);
            if (name == null)
            {
                return;
            }

            e.Handled = true;
            CellEntry cell = _cells.FirstOrDefault(c => c.Id == _selected);
            if (cell == null)
            {
                _status.Text = "Erst einen Platz anklicken.";
            }
            else if (IsOverflow(cell))
            {
                _status.Text = "Der Sammelplatz nimmt keine feste App auf.";
            }
            else
            {
                SetApp(cell.Id, name);
            }
        };

        // Hinweiszeile
        var hint = new TextBlock
        {
            Text = "App auf einen Platz ziehen oder doppelklicken. Platz anklicken, um ihn zu teilen, zusammenzuführen oder zu leeren.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };

        // Einstellungsleiste: Mitte-Breite und Zeilen je Seite
        _slider.ValueChanged += (_, _) =>
        {
            int value = (int)Math.Round(_slider.Value);
            _sliderLabel.Text = $"Mitte-Breite: {value} %";
            if (_syncing || value == _centerPercent)
            {
                return;
            }
            _centerPercent = value;
            MarkDirty();
            Draw();
        };
        var settings = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        settings.Children.Add(_sliderLabel);
        settings.Children.Add(_slider);
        AddRowChoice(settings, "left", "Zeilen links:");
        AddRowChoice(settings, "right", "Zeilen rechts:");

        // Knöpfe zum Bearbeiten des gewählten Platzes
        _splitH.Click += (_, _) => Split(horizontal: true);
        _splitV.Click += (_, _) => Split(horizontal: false);
        _merge.Click += (_, _) => Merge();
        _clear.Click += (_, _) => ClearSelected();
        var reset = MakeButton("Raster zurücksetzen");
        reset.Click += (_, _) => ResetToDefault();
        var edit = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        edit.Children.Add(_splitH);
        edit.Children.Add(_splitV);
        edit.Children.Add(_merge);
        edit.Children.Add(_clear);
        edit.Children.Add(reset);

        // Fußleiste: Status links, Übernehmen und Schließen rechts
        var accept = MakeButton("Übernehmen", accent: true);
        accept.Click += (_, _) => Accept();
        var close = MakeButton("Schließen");
        close.Margin = new Thickness(0);
        close.Click += (_, _) => TryClose();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(accept);
        buttons.Children.Add(close);
        var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_status);

        // Vorschau mit dem Seitenverhältnis des Bildschirms; die Viewbox skaliert sie mit dem Fenster
        var preview = new Viewbox
        {
            Child = _canvas, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 0, 0, 0),
        };

        var top = new StackPanel();
        top.Children.Add(hint);
        top.Children.Add(settings);
        var bottom = new StackPanel();
        bottom.Children.Add(edit);
        bottom.Children.Add(footer);
        var heading = new TextBlock { Text = "Apps", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(heading, Dock.Top);
        var left = new DockPanel { Width = 200 };
        left.Children.Add(heading);
        left.Children.Add(_apps);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(left);
        root.Children.Add(preview);
        Content = root;

        // Nur ausblenden, damit das Fenster mit dem Prioritäts-Fenster wieder öffnet. Bei ungespeicherten Änderungen fragen.
        Closing += (_, e) =>
        {
            if (_forceClose)
            {
                return;
            }
            e.Cancel = true;
            TryClose();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                TryClose();
                e.Handled = true;
            }
            else if (e.Key == Key.F1)
            {
                HelpWindow.Request();
                e.Handled = true;
            }
        };
    }

    /// <summary>Zeigt die Zellen (Kopie, der Editor ändert nur seine eigene Liste) und die App-Namen.</summary>
    /// <param name="defaultRows">Zeilen pro Seite für „Raster zurücksetzen“ (tilesPerSide).</param>
    /// <param name="overflowEnabled">Sammelplatz an (overflowCorner): bestimmt, welche Zelle der Sammelplatz ist.</param>
    public void Open(IEnumerable<CellEntry> cells, IEnumerable<string> apps, int centerPercent, int defaultRows, bool overflowEnabled)
    {
        _cells = Copy(cells);
        _centerPercent = Math.Clamp(centerPercent, 40, 60);
        _defaultRows = defaultRows;
        _overflowEnabled = overflowEnabled;
        _selected = null;
        _dirty = false;
        _status.Text = "";
        UpdateTitle();

        // Vorschau im Seitenverhältnis des Arbeitsbereichs
        NativeRect wa = Monitors.PrimaryWorkArea();
        double width = Math.Max(1, wa.Right - wa.Left);
        double height = Math.Max(1, wa.Bottom - wa.Top);
        _canvasHeight = CanvasWidth * height / width;
        _canvas.Height = _canvasHeight;

        _syncing = true;
        _slider.Value = _centerPercent;
        _sliderLabel.Text = $"Mitte-Breite: {_centerPercent} %";
        _syncing = false;

        _appNames = apps.ToList();
        Draw();
        Show();
        // Das Fenster hat einen Taskleistenknopf und lässt sich minimieren: Activate() allein holt es nicht zurück
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    /// <summary>Holt das offene Fenster nach vorn (auch aus Minimiert), ohne etwas zurückzusetzen.</summary>
    internal void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    /// <summary>Schließt das Fenster wirklich (beim Beenden des Programms), ohne Rückfrage.</summary>
    internal void Shutdown()
    {
        _forceClose = true;
        Close();
    }

    private List<string> _appNames = new();

    private static List<CellEntry> Copy(IEnumerable<CellEntry> cells) =>
        cells.Select(c => new CellEntry { Id = c.Id, Side = c.Side, X = c.X, Y = c.Y, W = c.W, H = c.H, App = c.App }).ToList();

    private bool IsOverflow(CellEntry cell) => _cells.IndexOf(cell) == DockLayout.OverflowIndex(_cells, _overflowEnabled);

    // ---- Ungespeichert, Schließen -------------------------------------------------------------

    private void MarkDirty()
    {
        _dirty = true;
        _status.Text = "Ungespeicherte Änderungen";
        UpdateTitle();
    }

    private void UpdateTitle() => Title = _dirty ? "Fokus-Dock – Layout *" : "Fokus-Dock – Layout";

    private void Accept()
    {
        _apply(Copy(_cells), _centerPercent, true);
        _dirty = false;
        _status.Text = $"Übernommen {DateTime.Now:HH:mm}";
        UpdateTitle();
    }

    /// <summary>Schließen, X und Esc: bei ungespeicherten Änderungen erst fragen.</summary>
    private void TryClose()
    {
        if (_dirty)
        {
            MessageBoxResult answer = MessageBox.Show(this, "Änderungen übernehmen?", "Fokus-Dock – Layout",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }
            if (answer == MessageBoxResult.Yes)
            {
                Accept();
            }
            else
            {
                _dirty = false;   // Nein: verwerfen, Open() holt beim nächsten Mal den gespeicherten Stand
                UpdateTitle();
            }
        }
        Hide();
    }

    // ---- App-Liste ----------------------------------------------------------------------------

    /// <summary>Name der App, wenn das Ereignis auf einer Zeile der Liste liegt (nicht am Scrollbalken oder im Leerraum).</summary>
    private string AppNameAt(object source) =>
        source is DependencyObject d && ItemsControl.ContainerFromElement(_apps, d) is ListBoxItem item
            ? item.Tag as string
            : null;

    /// <summary>Baut die Liste neu auf: Name, dahinter bei Apps mit Platz „→ L2“.</summary>
    private void RefreshApps()
    {
        string keep = (_apps.SelectedItem as ListBoxItem)?.Tag as string;
        _apps.Items.Clear();
        foreach (string name in _appNames)
        {
            var text = new TextBlock();
            text.Inlines.Add(name);
            int index = _cells.FindIndex(c => c.App == name);
            if (index >= 0)
            {
                text.Inlines.Add(new System.Windows.Documents.Run("  → " + DockLayout.SlotLabel(_cells, index)) { Foreground = Theme.Muted });
            }
            var item = new ListBoxItem { Content = text, Tag = name };
            _apps.Items.Add(item);
            if (name == keep)
            {
                item.IsSelected = true;
            }
        }
    }

    // ---- Zeilen je Seite ----------------------------------------------------------------------

    private void AddRowChoice(Panel parent, string side, string caption)
    {
        parent.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var list = new List<RadioButton>();
        for (int rows = 1; rows <= 4; rows++)
        {
            int n = rows;
            var button = new RadioButton
            {
                Content = rows.ToString(), GroupName = "rows-" + side,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            button.Checked += (_, _) =>
            {
                if (!_syncing)
                {
                    RebuildSide(side, n);
                }
            };
            list.Add(button);
            parent.Children.Add(button);
        }
        _rowButtons[side] = list;
        parent.Children.Add(new Border { Width = 14 });
    }

    /// <summary>Zeilenzahl einer Seite, wenn sie ein einfaches Raster aus n gleich hohen, vollen Zellen ist, sonst 0.</summary>
    private int SimpleRows(string side)
    {
        List<CellEntry> cells = _cells.Where(c => c.Side == side).OrderBy(c => c.Y).ToList();
        int n = cells.Count;
        if (n == 0)
        {
            return 0;
        }
        for (int k = 0; k < n; k++)
        {
            CellEntry c = cells[k];
            if (!Same(c.X, 0) || !Same(c.W, 1) || !Same(c.Y, (double)k / n) || !Same(c.H, 1.0 / n))
            {
                return 0;
            }
        }
        return n;
    }

    /// <summary>
    /// Ersetzt die Zellen einer Seite durch rows gleich hohe, volle Zellen (Ids L1…/R1…). Die Apps der alten Zellen
    /// werden in der Reihenfolge Y, dann X übernommen, überzählige verworfen.
    /// </summary>
    private void RebuildSide(string side, int rows)
    {
        List<string> apps = _cells.Where(c => c.Side == side && c.App != null)
            .OrderBy(c => c.Y).ThenBy(c => c.X).Select(c => c.App).ToList();
        List<CellEntry> fresh = AppConfig.DefaultCells(rows).Where(c => c.Side == side).ToList();
        // Der Sammelplatz darf keine feste App haben: diese Zelle auslassen
        List<CellEntry> all = fresh.Concat(_cells.Where(c => c.Side != side)).ToList();
        int overflow = DockLayout.OverflowIndex(all, _overflowEnabled);
        CellEntry overflowCell = overflow >= 0 ? all[overflow] : null;
        int next = 0;
        foreach (CellEntry cell in fresh)
        {
            if (cell != overflowCell && next < apps.Count)
            {
                cell.App = apps[next++];
            }
        }

        List<CellEntry> other = _cells.Where(c => c.Side != side).ToList();
        _cells = side == "left" ? fresh.Concat(other).ToList() : other.Concat(fresh).ToList();
        if (_selected != null && _cells.All(c => c.Id != _selected))
        {
            _selected = null;
        }
        MarkDirty();
        Draw();
    }

    // ---- Vorschau -----------------------------------------------------------------------------

    private void Draw()
    {
        _canvas.Children.Clear();
        double sideWidth = CanvasWidth * (100 - _centerPercent) / 200.0;
        int overflow = DockLayout.OverflowIndex(_cells, _overflowEnabled);

        // Mitte: zeigt das Mitte-Fenster, nicht belegbar
        AddLabel(sideWidth, 0, CanvasWidth - 2 * sideWidth, _canvasHeight, "Mitte (Fenster)");

        for (int i = 0; i < _cells.Count; i++)
        {
            CellEntry c = _cells[i];
            double sideLeft = c.Side == "right" ? CanvasWidth - sideWidth : 0;
            double left = sideLeft + c.X * sideWidth;
            double top = c.Y * _canvasHeight;
            double width = c.W * sideWidth;
            double height = c.H * _canvasHeight;

            string id = c.Id;
            bool selected = id == _selected;
            bool isOverflow = i == overflow;
            Brush normalBackground = selected ? Theme.Selected : Theme.Card;
            Brush normalBorder = selected ? Theme.Accent : Theme.Line;

            var cell = new Border
            {
                Width = Math.Max(4, width - 6),
                Height = Math.Max(4, height - 6),
                Background = normalBackground,
                BorderBrush = isOverflow ? Brushes.Transparent : normalBorder,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                AllowDrop = true,
            };

            if (isOverflow)
            {
                // Sammelplatz: gestrichelter Rahmen, Text „alle übrigen Apps“; Ablegen ist nicht möglich
                var grid = new Grid();
                grid.Children.Add(new Rectangle
                {
                    Stroke = normalBorder, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 },
                    RadiusX = 4, RadiusY = 4, IsHitTestVisible = false,
                });
                var texts = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6) };
                texts.Children.Add(new TextBlock { Text = "Sammelplatz", Foreground = Theme.Text, FontSize = 14, TextAlignment = TextAlignment.Center });
                texts.Children.Add(new TextBlock { Text = "alle übrigen Apps", Foreground = Theme.Muted, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
                grid.Children.Add(texts);
                cell.Child = grid;

                cell.DragOver += (_, e) =>
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                };
                cell.Drop += (_, e) => e.Handled = true;
            }
            else
            {
                cell.Child = new TextBlock
                {
                    Text = c.App ?? "frei",
                    Foreground = c.App == null ? Theme.Muted : Theme.Text,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6),
                };

                // Ablege-Rückmeldung: die Zelle unter dem Zeiger wird hervorgehoben
                cell.DragEnter += (_, _) =>
                {
                    cell.Background = Theme.Selected;
                    cell.BorderBrush = Theme.Accent;
                };
                cell.DragLeave += (_, _) =>
                {
                    cell.Background = normalBackground;
                    cell.BorderBrush = normalBorder;
                };
                cell.DragOver += (_, e) =>
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                };
                cell.Drop += (_, e) =>
                {
                    cell.Background = normalBackground;
                    cell.BorderBrush = normalBorder;
                    if (e.Data.GetData(DataFormats.StringFormat) is string name)
                    {
                        SetApp(id, name);
                    }
                    e.Handled = true;
                };
            }

            Canvas.SetLeft(cell, left + 3);
            Canvas.SetTop(cell, top + 3);
            cell.MouseLeftButtonUp += (_, _) =>
            {
                _selected = id;
                Draw();
            };

            _canvas.Children.Add(cell);
        }

        RefreshApps();
        SyncRowChoices();
        UpdateButtons();
    }

    private void SyncRowChoices()
    {
        _syncing = true;
        foreach ((string side, List<RadioButton> buttons) in _rowButtons)
        {
            int rows = SimpleRows(side);
            for (int k = 0; k < buttons.Count; k++)
            {
                buttons[k].IsChecked = rows == k + 1;
            }
        }
        _syncing = false;
    }

    /// <summary>Knöpfe nur bei passender Auswahl; der Tooltip eines deaktivierten Knopfs nennt den Grund.</summary>
    private void UpdateButtons()
    {
        CellEntry cell = _cells.FirstOrDefault(c => c.Id == _selected);
        SetState(_splitH, cell != null, "Erst einen Platz anklicken.");
        SetState(_splitV, cell != null, "Erst einen Platz anklicken.");
        SetState(_merge, cell != null && FindMergePartner(cell) != null,
            cell == null ? "Erst einen Platz anklicken." : "Dieser Platz hat keinen Nachbarn gleicher Größe zum Zusammenführen.");
        SetState(_clear, cell != null && cell.App != null,
            cell == null ? "Erst einen Platz anklicken." : "Dieser Platz hat keine feste App.");
    }

    private static void SetState(Button button, bool enabled, string reason)
    {
        button.IsEnabled = enabled;
        button.ToolTip = enabled ? null : reason;
        ToolTipService.SetShowOnDisabled(button, true);
    }

    private void AddLabel(double x, double y, double width, double height, string text)
    {
        var box = new Border
        {
            Width = Math.Max(4, width - 6),
            Height = Math.Max(4, height - 6),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = text, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Canvas.SetLeft(box, x + 3);
        Canvas.SetTop(box, y + 3);
        _canvas.Children.Add(box);
    }

    // ---- Bearbeiten ---------------------------------------------------------------------------

    /// <summary>Eine App steht nur auf einem Platz: die alte Zelle der App wird geleert. Der Sammelplatz nimmt nichts auf.</summary>
    private void SetApp(string cellId, string app)
    {
        CellEntry target = _cells.FirstOrDefault(c => c.Id == cellId);
        if (target == null || IsOverflow(target))
        {
            return;
        }

        foreach (CellEntry c in _cells.Where(c => c.App == app))
        {
            c.App = null;
        }
        target.App = app;
        MarkDirty();
        Draw();
    }

    private void ClearSelected()
    {
        CellEntry cell = _cells.FirstOrDefault(c => c.Id == _selected);
        if (cell != null && cell.App != null)
        {
            cell.App = null;
            MarkDirty();
            Draw();
        }
    }

    /// <summary>Gewählte Zelle halbieren: horizontal = oben/unten, sonst links/rechts.</summary>
    private void Split(bool horizontal)
    {
        int index = _cells.FindIndex(c => c.Id == _selected);
        if (index < 0)
        {
            return;
        }

        CellEntry c = _cells[index];
        var first = new CellEntry { Id = c.Id, Side = c.Side, X = c.X, Y = c.Y, W = c.W, H = c.H, App = c.App };
        var second = new CellEntry { Id = NewId(), Side = c.Side, X = c.X, Y = c.Y, W = c.W, H = c.H };

        if (horizontal)
        {
            first.H = c.H / 2;
            second.Y = c.Y + c.H / 2;
            second.H = c.H / 2;
        }
        else
        {
            first.W = c.W / 2;
            second.X = c.X + c.W / 2;
            second.W = c.W / 2;
        }

        _cells[index] = first;
        _cells.Insert(index + 1, second);
        _selected = first.Id;
        MarkDirty();
        Draw();
    }

    private string NewId()
    {
        int n = _cells.Count + 1;
        while (_cells.Any(c => c.Id == "S" + n))
        {
            n++;
        }
        return "S" + n;
    }

    /// <summary>Nachbarzelle gleicher Größe, mit der sich die Zelle vereinen lässt; null, wenn es keine gibt.</summary>
    private CellEntry FindMergePartner(CellEntry cell) =>
        _cells.FirstOrDefault(o => o != cell && o.Side == cell.Side && (
            (Same(o.X, cell.X) && Same(o.W, cell.W) && (Same(o.Y + o.H, cell.Y) || Same(cell.Y + cell.H, o.Y))) ||
            (Same(o.Y, cell.Y) && Same(o.H, cell.H) && (Same(o.X + o.W, cell.X) || Same(cell.X + cell.W, o.X)))));

    /// <summary>
    /// Teilung aufheben: die gewählte Zelle wird mit ihrer Nachbarzelle gleicher Größe vereint.
    /// Die App bleibt erhalten; ist die gewählte Zelle leer, übernimmt die Nachbarzelle ihre App.
    /// </summary>
    private void Merge()
    {
        CellEntry cell = _cells.FirstOrDefault(c => c.Id == _selected);
        CellEntry other = cell == null ? null : FindMergePartner(cell);
        if (other == null)
        {
            return;
        }

        double x0 = Math.Min(cell.X, other.X);
        double y0 = Math.Min(cell.Y, other.Y);
        double x1 = Math.Max(cell.X + cell.W, other.X + other.W);
        double y1 = Math.Max(cell.Y + cell.H, other.Y + other.H);
        cell.X = x0;
        cell.Y = y0;
        cell.W = x1 - x0;
        cell.H = y1 - y0;
        cell.App ??= other.App;

        _cells.Remove(other);
        MarkDirty();
        Draw();
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// <summary>Raster wie in tilesPerSide; die App-Zuordnung wird dabei gelöscht (bei belegten Plätzen erst nach Rückfrage).</summary>
    private void ResetToDefault()
    {
        if (_cells.Any(c => c.App != null))
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"Alle Plätze werden auf {_defaultRows} gleich hohe Zeilen je Seite gesetzt, feste Apps werden gelöscht. Fortfahren?",
                "Fokus-Dock – Layout", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _cells = AppConfig.DefaultCells(_defaultRows);
        _selected = null;
        MarkDirty();
        Draw();
    }

    /// <param name="accent">Hauptknopf: Stil AccentButton aus App.xaml (Akzentfläche, schwarze Schrift).</param>
    private static Button MakeButton(string text, bool accent = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
        if (accent)
        {
            button.SetResourceReference(StyleProperty, "AccentButton");
        }
        return button;
    }
}
