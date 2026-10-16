using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FokusDock;

public sealed class AppConfig
{
    /// <summary>Kacheln pro Seite (links und rechts): 2 oder 3.</summary>
    public int TilesPerSide { get; set; } = 3;

    /// <summary>Breite des Mitte-Fensters in Prozent der Arbeitsbereichsbreite (40–60).</summary>
    public int CenterPercent { get; set; } = 50;

    /// <summary>App, deren Fenster beim Start in der Mitte steht (Name aus apps).</summary>
    public string StartInCenter { get; set; } = "Brave";

    /// <summary>Wartezeit, bis die Fensterliste nach Verlassen ausgeblendet wird.</summary>
    public int HideDelayMs { get; set; } = 400;

    /// <summary>Ein/Aus der Spalten.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+F";

    /// <summary>Beendet das Programm.</summary>
    public string ExitHotkey { get; set; } = "Ctrl+Alt+Q";

    /// <summary>Öffnet das Prioritäts-Fenster.</summary>
    public string PriorityHotkey { get; set; } = "Ctrl+Alt+P";

    /// <summary>Sammelplatz unten rechts: alle Apps ohne eigenen Platz liegen dort übereinander.</summary>
    public bool OverflowCorner { get; set; } = true;

    /// <summary>
    /// Prioritätsliste: vorne = wichtiger. Die Reihenfolge bestimmt, welche Apps einen Platz außen bekommen.
    /// Startbelegung: erst links von oben nach unten, dann rechts.
    /// </summary>
    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>
    /// Plätze außen: Zellen je Seite (Anteile des Seitenbereichs 0..1). Reihenfolge = Platzreihenfolge.
    /// Ohne Angabe wird ein Raster aus tilesPerSide Zeilen pro Seite erzeugt.
    /// </summary>
    public List<CellEntry> Cells { get; set; }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return CreateDefault();
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options) ?? CreateDefault();
        config.Apps ??= new List<AppEntry>();
        config.Apps.RemoveAll(a => a.Processes == null || a.Processes.Count == 0);
        config.TilesPerSide = Math.Clamp(config.TilesPerSide, 2, 3);
        config.CenterPercent = Math.Clamp(config.CenterPercent, 40, 60);
        if (config.Cells == null || config.Cells.Count == 0)
        {
            config.Cells = DefaultCells(config.TilesPerSide);
        }
        return config;
    }

    /// <summary>Raster: n Zeilen links, dann n Zeilen rechts, jeweils gleich hoch.</summary>
    public static List<CellEntry> DefaultCells(int rowsPerSide) =>
        new[] { "left", "right" }
            .SelectMany(side => Enumerable.Range(0, rowsPerSide).Select(k => new CellEntry
            {
                Id = (side == "left" ? "L" : "R") + (k + 1),
                Side = side,
                X = 0,
                Y = (double)k / rowsPerSide,
                W = 1,
                H = 1.0 / rowsPerSide,
            }))
            .ToList();

    /// <summary>Schreibt die Config (inkl. Prioritätsreihenfolge und „immer außen“) zurück.</summary>
    public void Save(string path)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    public static AppConfig CreateDefault() => new()
    {
        Apps = new List<AppEntry>
        {
            WithPriority(App("Claude", "Claude.exe")),
            WebApp("WhatsApp Web", "WhatsApp Web"),
            WithPriority(App("Brave", "brave.exe")),
            App("PowerShell", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe"),
            App("Explorer", "explorer.exe"),
        },
    };

    private static AppEntry App(string name, params string[] processes) =>
        new() { Name = name, Processes = processes.ToList() };

    private static AppEntry WithPriority(AppEntry entry)
    {
        entry.AlwaysOutside = true;
        return entry;
    }

    /// <summary>Web-App aus der Taskleiste: läuft in brave.exe, Fenstertitel beginnt mit dem Namen.</summary>
    private static AppEntry WebApp(string name, string titleStartsWith) => WithPriority(new AppEntry
    {
        Name = name,
        Processes = new List<string> { "brave.exe" },
        TitleStartsWith = titleStartsWith,
    });
}

/// <summary>Ein Platz außen. Anteile beziehen sich auf den Seitenbereich (links oder rechts).</summary>
public sealed class CellEntry
{
    public string Id { get; set; } = "";
    public string Side { get; set; } = "left";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    public double H { get; set; } = 1;

    /// <summary>Feste App dieses Platzes (Name). Null = frei, dann füllt die Priorität.</summary>
    public string App { get; set; }
}

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public List<string> Processes { get; set; } = new();

    /// <summary>Nur Fenster, deren Titel damit beginnt (Web-Apps). Null = alle Fenster der Prozesse.</summary>
    public string TitleStartsWith { get; set; }

    /// <summary>Immer außen, sobald nicht in der Mitte: bekommt notfalls einen Platz einer normalen App.</summary>
    public bool AlwaysOutside { get; set; }

    /// <summary>Per Ziehen in den Sammelplatz gelegt: bekommt keinen eigenen Platz, bis sie wieder auf einen Platz gezogen wird.</summary>
    public bool InCorner { get; set; }
}
