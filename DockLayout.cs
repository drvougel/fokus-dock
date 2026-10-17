using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// Kurzname eines Platzes: „L“ oder „R“ plus der Rang der Zelle innerhalb ihrer Seite (oben nach unten, bei gleicher Höhe links nach rechts), ab 1.
    /// </summary>
    public static string SlotLabel(AppConfig config, int index) => SlotLabel(config.Cells, index);

    /// <summary>Wie SlotLabel(AppConfig, int), für eine beliebige Zellenliste (z. B. im Layout-Editor).</summary>
    public static string SlotLabel(IReadOnlyList<CellEntry> cells, int index)
    {
        CellEntry cell = cells[index];
        bool right = cell.Side == "right";
        int rank = 1 + cells.Count(o => (o.Side == "right") == right
            && (o.Y < cell.Y - 1e-6 || (Math.Abs(o.Y - cell.Y) <= 1e-6 && o.X < cell.X - 1e-6)));
        return (right ? "R" : "L") + rank;
    }

    /// <summary>
    /// Sammelplatz: die unterste Zelle der rechten Seite (ohne rechte Zellen die letzte). -1, wenn abgeschaltet.
    /// Er bekommt nie eine eigene App, sondern nimmt alle Apps ohne Platz auf.
    /// </summary>
    public static int OverflowIndex(IReadOnlyList<CellEntry> cells, bool enabled)
    {
        if (!enabled || cells.Count < 2)
        {
            return -1;
        }

        int best = -1;
        for (int c = 0; c < cells.Count; c++)
        {
            if (cells[c].Side != "right")
            {
                continue;
            }
            if (best < 0 || cells[c].Y + cells[c].H > cells[best].Y + cells[best].H
                || (cells[c].Y + cells[c].H == cells[best].Y + cells[best].H && cells[c].X > cells[best].X))
            {
                best = c;
            }
        }
        return best >= 0 ? best : cells.Count - 1;
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
