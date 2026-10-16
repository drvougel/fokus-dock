namespace FokusDock;

/// <summary>
/// Einteilung des Arbeitsbereichs des Primärmonitors (Pixel): links, Mitte und rechts, jeweils volle Höhe.
/// Die Seitenbereiche sind in Plätze (Zellen) geteilt, in die echte Fenster gesetzt werden.
/// </summary>
internal sealed class DockLayout
{
    public NativeRect Left { get; init; }
    public NativeRect Center { get; init; }
    public NativeRect Right { get; init; }

    public static DockLayout Compute(AppConfig config)
    {
        NativeRect wa = Monitors.PrimaryWorkArea();
        int sideWidth = (wa.Right - wa.Left) * (100 - config.CenterPercent) / 200;

        return new DockLayout
        {
            Left = new NativeRect { Left = wa.Left, Top = wa.Top, Right = wa.Left + sideWidth, Bottom = wa.Bottom },
            Center = new NativeRect { Left = wa.Left + sideWidth, Top = wa.Top, Right = wa.Right - sideWidth, Bottom = wa.Bottom },
            Right = new NativeRect { Left = wa.Right - sideWidth, Top = wa.Top, Right = wa.Right, Bottom = wa.Bottom },
        };
    }

    /// <summary>Plätze in Pixeln, in der Reihenfolge von config.Cells.</summary>
    public static NativeRect[] SlotRects(AppConfig config)
    {
        DockLayout layout = Compute(config);
        return config.Cells.Select(c =>
        {
            NativeRect side = c.Side == "right" ? layout.Right : layout.Left;
            int width = side.Right - side.Left;
            int height = side.Bottom - side.Top;
            return new NativeRect
            {
                Left = side.Left + (int)Math.Round(c.X * width),
                Right = side.Left + (int)Math.Round((c.X + c.W) * width),
                Top = side.Top + (int)Math.Round(c.Y * height),
                Bottom = side.Top + (int)Math.Round((c.Y + c.H) * height),
            };
        }).ToArray();
    }
}
