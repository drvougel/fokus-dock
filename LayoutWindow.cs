using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FokusDock;

/// <summary>
/// Layout-Editor als Bildschirm-Vorschau: Apps per Drag & Drop auf einen Platz ziehen,
/// Plätze waagerecht oder senkrecht halbieren. Die Mitte bleibt das Mitte-Fenster und ist nicht belegbar.
/// </summary>
public sealed class LayoutWindow : Window
{
    private const double CanvasWidth = 640;
    private const double CanvasHeight = 360;

    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
    private static readonly Brush CellBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x66));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));

    private readonly Action<List<CellEntry>, bool> _apply; // (Zellen, speichern?)
    private readonly Canvas _canvas = new() { Width = CanvasWidth, Height = CanvasHeight, Background = PanelBrush };
    private readonly ListBox _apps = new() { Width = 200 };
    private readonly RadioButton _horizontal = new()
    {
        Content = "waagerecht halbieren", GroupName = "split", IsChecked = true,
        Foreground = Brushes.White, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly RadioButton _vertical = new()
    {
        Content = "senkrecht halbieren", GroupName = "split",
        Foreground = Brushes.White, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    private List<CellEntry> _cells = new();
    private string _selected;
    private int _centerPercent = 50;
    private int _defaultRows = 3;

    public LayoutWindow(Action<List<CellEntry>, bool> apply)
    {
        _apply = apply;

        Title = "Fokus-Dock Layout";
        Width = 920;
        Height = 640;
        ShowInTaskbar = false;
        Background = PanelBrush;
        Foreground = Brushes.White;

        _apps.Background = PanelBrush;
        _apps.BorderThickness = new Thickness(0);
        _apps.ItemContainerStyle = ItemStyle();
        // Apps aus der Liste ziehen: beim Ziehen über eine Zelle wird der App-Name übergeben
        _apps.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && _apps.SelectedItem is string name)
            {
                DragDrop.DoDragDrop(_apps, name, DragDropEffects.Copy);
            }
        };

        var bottom = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(_horizontal);
        bottom.Children.Add(_vertical);
        bottom.Children.Add(MakeButton("Teilen", (_, _) => Split()));
        bottom.Children.Add(MakeButton("Zusammenführen", (_, _) => Merge()));
        bottom.Children.Add(MakeButton("Standard", (_, _) => ResetToDefault()));
        bottom.Children.Add(MakeButton("Zelle leeren", (_, _) => ClearSelected()));
        bottom.Children.Add(MakeButton("Übernehmen", (_, _) => _apply(Copy(_cells), true)));
        bottom.Children.Add(MakeButton("Schließen", (_, _) => Hide()));

        var canvasHost = new Border { Margin = new Thickness(12, 0, 0, 0), Child = _canvas, VerticalAlignment = VerticalAlignment.Top };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_apps, Dock.Left);
        root.Children.Add(bottom);
        root.Children.Add(_apps);
        root.Children.Add(canvasHost);
        Content = root;

        // Nur ausblenden, damit das Fenster mit dem Prioritäts-Fenster wieder öffnet
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>Zeigt die Zellen (Kopie, der Editor ändert nur seine eigene Liste) und die App-Namen.</summary>
    /// <param name="defaultRows">Zeilen pro Seite für „Standard“ (tilesPerSide).</param>
    public void Open(IEnumerable<CellEntry> cells, IEnumerable<string> apps, int centerPercent, int defaultRows)
    {
        _cells = Copy(cells);
        _centerPercent = centerPercent;
        _defaultRows = defaultRows;
        _selected = null;
        _apps.ItemsSource = apps.ToList();
        Draw();
        Show();
        Activate();
    }

    private static List<CellEntry> Copy(IEnumerable<CellEntry> cells) =>
        cells.Select(c => new CellEntry { Id = c.Id, Side = c.Side, X = c.X, Y = c.Y, W = c.W, H = c.H, App = c.App }).ToList();

    private void Draw()
    {
        _canvas.Children.Clear();
        double sideWidth = CanvasWidth * (100 - _centerPercent) / 200.0;

        // Mitte: zeigt das Mitte-Fenster, nicht belegbar
        AddLabel(sideWidth, 0, CanvasWidth - 2 * sideWidth, CanvasHeight, "Mitte (Fenster)");

        foreach (CellEntry c in _cells)
        {
            double sideLeft = c.Side == "right" ? CanvasWidth - sideWidth : 0;
            double left = sideLeft + c.X * sideWidth;
            double top = c.Y * CanvasHeight;
            double width = c.W * sideWidth;
            double height = c.H * CanvasHeight;

            string id = c.Id;
            bool selected = id == _selected;
            var cell = new Border
            {
                Width = Math.Max(4, width - 6),
                Height = Math.Max(4, height - 6),
                Background = selected ? SelectedBrush : CellBrush,
                BorderBrush = selected ? AccentBrush : MutedBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                AllowDrop = true,
                Child = new TextBlock
                {
                    Text = c.App ?? "frei",
                    Foreground = c.App == null ? MutedBrush : Brushes.White,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6),
                },
            };
            Canvas.SetLeft(cell, left + 3);
            Canvas.SetTop(cell, top + 3);

            cell.MouseLeftButtonUp += (_, _) =>
            {
                _selected = id;
                Draw();
            };
            cell.DragOver += (_, e) =>
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };
            cell.Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.StringFormat) is string name)
                {
                    SetApp(id, name);
                }
                e.Handled = true;
            };

            _canvas.Children.Add(cell);
        }
    }

    private void AddLabel(double x, double y, double width, double height, string text)
    {
        var box = new Border
        {
            Width = Math.Max(4, width - 6),
            Height = Math.Max(4, height - 6),
            BorderBrush = MutedBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = text, Foreground = MutedBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Canvas.SetLeft(box, x + 3);
        Canvas.SetTop(box, y + 3);
        _canvas.Children.Add(box);
    }

    /// <summary>Eine App steht nur auf einem Platz: die alte Zelle der App wird geleert.</summary>
    private void SetApp(string cellId, string app)
    {
        foreach (CellEntry c in _cells.Where(c => c.App == app))
        {
            c.App = null;
        }

        CellEntry target = _cells.FirstOrDefault(c => c.Id == cellId);
        if (target != null)
        {
            target.App = app;
        }
        Draw();
    }

    private void ClearSelected()
    {
        CellEntry cell = _cells.FirstOrDefault(c => c.Id == _selected);
        if (cell != null)
        {
            cell.App = null;
            Draw();
        }
    }

    /// <summary>Gewählte Zelle halbieren: waagerecht = oben/unten, senkrecht = links/rechts.</summary>
    private void Split()
    {
        int index = _cells.FindIndex(c => c.Id == _selected);
        if (index < 0)
        {
            return;
        }

        CellEntry c = _cells[index];
        var first = new CellEntry { Id = c.Id, Side = c.Side, X = c.X, Y = c.Y, W = c.W, H = c.H, App = c.App };
        var second = new CellEntry { Id = NewId(), Side = c.Side, X = c.X, Y = c.Y, W = c.W, H = c.H };

        if (_horizontal.IsChecked == true)
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

    /// <summary>
    /// Teilung aufheben: die gewählte Zelle wird mit ihrer Nachbarzelle gleicher Größe vereint.
    /// Die App bleibt erhalten; ist die gewählte Zelle leer, übernimmt die Nachbarzelle ihre App.
    /// </summary>
    private void Merge()
    {
        CellEntry cell = _cells.FirstOrDefault(c => c.Id == _selected);
        if (cell == null)
        {
            return;
        }

        CellEntry other = _cells.FirstOrDefault(o => o != cell && o.Side == cell.Side && (
            (Same(o.X, cell.X) && Same(o.W, cell.W) && (Same(o.Y + o.H, cell.Y) || Same(cell.Y + cell.H, o.Y))) ||
            (Same(o.Y, cell.Y) && Same(o.H, cell.H) && (Same(o.X + o.W, cell.X) || Same(cell.X + cell.W, o.X)))));
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
        Draw();
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// <summary>Raster wie in tilesPerSide; die App-Zuordnung wird dabei gelöscht.</summary>
    private void ResetToDefault()
    {
        _cells = AppConfig.DefaultCells(_defaultRows);
        _selected = null;
        Draw();
    }

    private static Style ItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, CellBrush));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        return style;
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4) };
        button.Click += click;
        return button;
    }
}
