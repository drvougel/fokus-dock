using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FokusDock;

/// <summary>
/// Vierecke teilen und zusammenlegen. Ein Viereck ist ein Platz (CellEntry) oder das Benachrichtigungsfeld
/// (NotificationAreaEntry). Teilen halbiert es, die frei gewordene Hälfte wird ein neuer, leerer Platz. Zusammenlegen macht
/// es wieder doppelt so groß und nimmt dafür den passenden Nachbarplatz auf. Die Gesamtgröße der Spalten bleibt dabei gleich.
/// Reine Logik ohne Fenster.
/// </summary>
internal static class TileOps
{
    /// <summary>Kleinste Breite und Höhe, auf die geteilt wird (Anteile des Seitenbereichs).</summary>
    private const double MinWidth = 0.2;
    private const double MinHeight = 0.1;
    private const double Eps = 1e-3;

    private static (string Side, double X, double Y, double W, double H) Geo(object e) => e switch
    {
        CellEntry c => (c.Side, c.X, c.Y, c.W, c.H),
        NotificationAreaEntry n => (n.Side, n.X, n.Y, n.W, n.H),
        _ => default,
    };

    private static void Set(object e, double x, double y, double w, double h)
    {
        switch (e)
        {
            case CellEntry c:
                (c.X, c.Y, c.W, c.H) = (Round(x), Round(y), Round(w), Round(h));
                break;
            case NotificationAreaEntry n:
                (n.X, n.Y, n.W, n.H) = (Round(x), Round(y), Round(w), Round(h));
                break;
        }
    }

    private static double Round(double v) => Math.Round(v, 4);

    /// <summary>Ob das Viereck noch zur Konfiguration gehört (ein Klick kann ein Viereck treffen, das gerade zusammengelegt wurde).</summary>
    private static bool Valid(AppConfig config, object e) =>
        e is CellEntry c ? config.Cells.Contains(c) : e is NotificationAreaEntry n && ReferenceEquals(config.Notifications, n);

    private static string NewId(AppConfig config)
    {
        for (int i = 1; ; i++)
        {
            string id = "S" + i;
            if (!config.Cells.Any(c => c.Id == id))
            {
                return id;
            }
        }
    }

    /// <summary>
    /// Viereck halbieren. sideBySide: senkrechte Teilung in links und rechts (Breite halbiert), sonst waagerechte Teilung in
    /// oben und unten (Höhe halbiert). False, wenn die Hälfte zu klein würde.
    /// </summary>
    public static bool Split(AppConfig config, object entry, bool sideBySide)
    {
        if (!Valid(config, entry))
        {
            return false;
        }

        var g = Geo(entry);
        double x = g.X, y = g.Y, w = g.W, h = g.H;
        double newX, newY, newW, newH;
        if (sideBySide)
        {
            if (w / 2 < MinWidth - Eps)
            {
                return false;
            }
            double half = Round(w / 2);
            (newX, newY, newW, newH) = (x + half, y, Round(w - half), h);
            w = half;
        }
        else
        {
            if (h / 2 < MinHeight - Eps)
            {
                return false;
            }
            double half = Round(h / 2);
            (newX, newY, newW, newH) = (x, y + half, w, Round(h - half));
            h = half;
        }

        Set(entry, x, y, w, h);
        var added = new CellEntry { Id = NewId(config), Side = g.Side, Corner = entry is CellEntry sourceCell && sourceCell.Corner };
        Set(added, newX, newY, newW, newH);
        int at = entry is CellEntry c ? config.Cells.IndexOf(c) + 1 : config.Cells.Count;
        config.Cells.Insert(at, added);
        return true;
    }

    /// <summary>
    /// Viereck verdoppeln: den Nachbarplatz mit gleicher Kante aufnehmen. Hat das Viereck schon die volle Breite, wird nach
    /// oben oder unten zusammengelegt, sonst zuerst zur Seite. Ein Platz nimmt das Benachrichtigungsfeld nie auf.
    /// </summary>
    public static bool Merge(AppConfig config, object entry)
    {
        if (!Valid(config, entry))
        {
            return false;
        }

        var g = Geo(entry);
        List<CellEntry> others = config.Cells.Where(c => !ReferenceEquals(c, entry) && c.Side == g.Side).ToList();

        CellEntry beside = others.FirstOrDefault(o => Math.Abs(o.Y - g.Y) < Eps && Math.Abs(o.H - g.H) < Eps
            && (Math.Abs(o.X - (g.X + g.W)) < Eps || Math.Abs(o.X + o.W - g.X) < Eps));
        CellEntry stacked = others.FirstOrDefault(o => Math.Abs(o.X - g.X) < Eps && Math.Abs(o.W - g.W) < Eps
            && (Math.Abs(o.Y - (g.Y + g.H)) < Eps || Math.Abs(o.Y + o.H - g.Y) < Eps));

        bool narrow = g.W < 1 - Eps;
        CellEntry take = narrow ? beside ?? stacked : stacked ?? beside;
        if (take == null)
        {
            return false;
        }

        if (ReferenceEquals(take, beside))
        {
            Set(entry, Math.Min(g.X, take.X), g.Y, g.W + take.W, g.H);
        }
        else
        {
            Set(entry, g.X, Math.Min(g.Y, take.Y), g.W, g.H + take.H);
        }
        // Nimmt ein Platz einen Sammelplatz auf, ist das zusammengelegte Viereck ebenfalls einer
        if (entry is CellEntry merged && take.Corner)
        {
            merged.Corner = true;
        }
        config.Cells.Remove(take);
        return true;
    }
}

/// <summary>Was ein Knopf bei einem Viereck auslöst.</summary>
internal enum SizeAction
{
    /// <summary>Senkrechte Linie: in links und rechts teilen, die Breite halbieren.</summary>
    SplitSideBySide,

    /// <summary>Waagerechte Linie: in oben und unten teilen, die Höhe halbieren.</summary>
    SplitStacked,

    /// <summary>Plus: mit dem Nachbarn zusammenlegen, doppelt so groß.</summary>
    Merge,
}

/// <summary>
/// Knöpfe oben in der Mitte eines Vierecks: teilen mit senkrechter Linie, teilen mit waagerechter Linie, zusammenlegen.
/// Dezent, bis die Maus darüber steht. Liegt über den Fenstern, nimmt aber keinen Fokus.
/// </summary>
internal sealed class SizeButtons : OverlayWindow
{
    private const double PartDip = 28;
    private const double HeightDip = 26;
    private const double TopOffsetDip = 4;
    private const double WidthDip = PartDip * 3 + 2;

    /// <summary>Wird mit der gewählten Aktion aufgerufen. DockWindow setzt sie bei jedem Abgleich neu.</summary>
    public Action<SizeAction> Act { get; set; }

    public SizeButtons()
    {
        ClickThrough = false;
        Width = WidthDip;
        Height = HeightDip;
        Left = -32000;
        Top = -32000;
        Opacity = 0.55;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        // Reihenfolge: Teilung in links/rechts, Teilung in oben/unten, zusammenlegen
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Part(Square(vertical: true), "Teilen: links und rechts (Breite halbieren)", SizeAction.SplitSideBySide, new CornerRadius(6, 0, 0, 6)));
        row.Children.Add(Divider());
        row.Children.Add(Part(Square(vertical: false), "Teilen: oben und unten (Höhe halbieren)", SizeAction.SplitStacked, new CornerRadius(0)));
        row.Children.Add(Divider());
        row.Children.Add(Part(Plus(), "Zusammenlegen: doppelt so groß", SizeAction.Merge, new CornerRadius(0, 6, 6, 0)));

        Content = new Border
        {
            Background = Theme.Surface ?? Brushes.Black,
            BorderBrush = Theme.Line ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = row,
        };

        MouseEnter += (_, _) => Opacity = 1;
        MouseLeave += (_, _) => Opacity = 0.55;
    }

    private static Border Divider() => new() { Width = 1, Background = Theme.Line ?? Brushes.Gray };

    private static UIElement Plus() => new TextBlock
    {
        Text = "+",
        Foreground = Theme.Text ?? Brushes.White,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// Ein Viereck mit einer Linie: senkrecht (in links und rechts geteilt) oder waagerecht (in oben und unten geteilt).
    /// Gezeichnet statt als Schriftzeichen, damit es auf jedem Rechner gleich aussieht.
    /// </summary>
    private static UIElement Square(bool vertical) => new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse(vertical ? "M 1,1 L 13,1 L 13,13 L 1,13 Z M 7,1 L 7,13" : "M 1,1 L 13,1 L 13,13 L 1,13 Z M 1,7 L 13,7"),
        Stroke = Theme.Text ?? Brushes.White,
        StrokeThickness = 1.5,
        Width = 14,
        Height = 14,
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border Part(UIElement symbol, string tip, SizeAction action, CornerRadius corners)
    {
        var part = new Border
        {
            Width = PartDip,
            Background = Theme.Card ?? Brushes.DimGray,
            CornerRadius = corners,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = symbol,
        };
        part.MouseEnter += (_, _) => part.Background = Theme.Hover ?? Brushes.Gray;
        part.MouseLeave += (_, _) => part.Background = Theme.Card ?? Brushes.DimGray;
        part.MouseLeftButtonUp += (_, _) => Act?.Invoke(action);
        return part;
    }

    /// <summary>Oben in der Mitte des Vierecks platzieren. Zu schmale Vierecke bekommen keine Knöpfe.</summary>
    public void PlaceAtTop(NativeRect rect)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = (int)Math.Round(WidthDip * scale);
        int height = (int)Math.Round(HeightDip * scale);
        if (rect.Right - rect.Left < width * 2)
        {
            Hide();
            return;
        }

        int left = rect.Left + (rect.Right - rect.Left - width) / 2;
        int top = rect.Top + (int)Math.Round(TopOffsetDip * scale);
        PlacePixels(new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height });
    }
}
