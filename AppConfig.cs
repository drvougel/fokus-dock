using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Ob Load die Datei wirklich gelesen hat. False bei den Standardwerten, weil config.json fehlt.</summary>
    [JsonIgnore]
    public bool FileExisted { get; set; }

    /// <summary>Speichern ist gesperrt (config.json war defekt): Save schreibt dann nichts, damit die Datei unverändert bleibt.</summary>
    [JsonIgnore]
    public bool SaveDisabled { get; set; }

    /// <summary>Korrekturen, die Load an den Werten vorgenommen hat (je ein Satz). Die Anzeige leert die Liste.</summary>
    [JsonIgnore]
    public List<string> Warnings { get; } = new();

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
        config.FileExisted = true;
        config.Normalize();
        return config;
    }

    /// <summary>
    /// Werte auf das Zulässige bringen. Jede Korrektur kommt als Satz in Warnings,
    /// damit der Nutzer erfährt, was von seiner config.json nicht übernommen wurde.
    /// </summary>
    private void Normalize()
    {
        var apps = new List<AppEntry>();
        foreach (AppEntry app in Apps ?? new List<AppEntry>())
        {
            if (app == null)
            {
                Warnings.Add("Ein leerer Eintrag in apps wird ignoriert.");
            }
            else if (app.Processes == null || app.Processes.Count == 0)
            {
                Warnings.Add($"App ‚{(string.IsNullOrEmpty(app.Name) ? "?" : app.Name)}‘ ohne processes wird ignoriert.");
            }
            else
            {
                apps.Add(app);
            }
        }
        Apps = apps;

        int tiles = Math.Clamp(TilesPerSide, 2, 3);
        if (tiles != TilesPerSide)
        {
            Warnings.Add($"tilesPerSide {TilesPerSide} wurde auf {tiles} gesetzt.");
        }
        TilesPerSide = tiles;

        int center = Math.Clamp(CenterPercent, 40, 60);
        if (center != CenterPercent)
        {
            Warnings.Add($"centerPercent {CenterPercent} wurde auf {center} gesetzt.");
        }
        CenterPercent = center;

        // Ohne Angabe (oder mit leerer Liste) gilt still das Raster aus tilesPerSide
        bool discarded = false;
        if (Cells != null)
        {
            var cells = new List<CellEntry>();
            foreach (CellEntry cell in Cells)
            {
                if (cell == null)
                {
                    Warnings.Add("Ein leerer Eintrag in cells wird ignoriert.");
                    discarded = true;
                    continue;
                }

                string id = string.IsNullOrEmpty(cell.Id) ? "?" : cell.Id;
                if (!IsFraction(cell.X, allowZero: true) || !IsFraction(cell.Y, allowZero: true) ||
                    !IsFraction(cell.W, allowZero: false) || !IsFraction(cell.H, allowZero: false))
                {
                    Warnings.Add($"Zelle ‚{id}‘ hat eine ungültige Größe und wird ignoriert.");
                    discarded = true;
                    continue;
                }

                bool right = string.Equals(cell.Side, "right", StringComparison.OrdinalIgnoreCase);
                if (!right && !string.Equals(cell.Side, "left", StringComparison.OrdinalIgnoreCase))
                {
                    Warnings.Add($"Zelle ‚{id}‘ hat die Seite ‚{cell.Side}‘ und wird links eingesetzt.");
                }
                cell.Side = right ? "right" : "left";
                cells.Add(cell);
            }
            Cells = cells;
        }

        if (Cells == null || Cells.Count == 0)
        {
            if (discarded)
            {
                Warnings.Add("Keine Zelle in cells ist gültig, es gilt das Raster aus tilesPerSide.");
            }
            Cells = DefaultCells(TilesPerSide);
        }
    }

    /// <summary>Anteil des Seitenbereichs: 0..1, bei Breite und Höhe über 0. NaN fällt durch.</summary>
    private static bool IsFraction(double value, bool allowZero) =>
        value <= 1 && (allowZero ? value >= 0 : value > 0);

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

    /// <summary>
    /// Schreibt die Config (inkl. Prioritätsreihenfolge und „immer außen“) zurück. Erst in path.tmp, dann
    /// ersetzen und die alte Datei als path.bak behalten, damit ein Abbruch nie eine halbe Datei hinterlässt.
    /// Liefert false, wenn nichts geschrieben wurde (Speichern gesperrt oder Datei nicht beschreibbar).
    /// </summary>
    public bool Save(string path)
    {
        if (SaveDisabled)
        {
            return false;
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        string json = JsonSerializer.Serialize(this, options);

        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
            {
                File.Replace(tmp, path, path + ".bak");
            }
            else
            {
                File.Move(tmp, path);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Schreibgeschützt, gesperrt oder Laufwerk nicht erreichbar: die Datei bleibt, wie sie ist
            try
            {
                File.Delete(tmp);
            }
            catch (Exception)
            {
                // Auch das Aufräumen scheitert: eine übrige .tmp-Datei schadet nicht
            }
            return false;
        }
    }

    public static AppConfig CreateDefault() => new()
    {
        Cells = DefaultCells(3),
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

    /// <summary>
    /// Nicht verwalten: Fokus-Dock fasst die Fenster dieser App nie an (kein Platz, kein Sammelplatz, kein Einrasten).
    /// Sie bleibt in der Prioritätsliste. Wird nur gespeichert, wenn gesetzt.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Ignored { get; set; }
}
