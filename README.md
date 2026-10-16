# Fokus-Dock

Drei-Spalten-Layout: Links und rechts stehen echte Fenster übereinander, in der Mitte liegt das aktive Fenster. Alle Fenster sind normal bedienbar. Die Windows-Taskleiste bleibt die Leiste.

```
+-----------+-----------------------+-----------+
| Fenster 1 |                       | Fenster 4 |
+-----------+     Mitte-Fenster     +-----------+
| Fenster 2 |   (echtes Fenster)    | Fenster 5 |
+-----------+                       +-----------+
| Fenster 3 |                       | Fenster 6 |
+-----------+-----------------------+-----------+
```

![Layout: drei Fenster links, eines in der Mitte, drei rechts](docs/mockup-layout.png)

![Vorwärts-Taste: Vorschauen aller Fenster einer App](docs/mockup-liste.png)

*Mockups mit Beispielinhalten, keine echten Bildschirmfotos.*

## Start

Voraussetzung: .NET 8 Desktop Runtime (Windows x64).

```
publish\FokusDock.exe
```

Es läuft nur eine Instanz. Beenden mit `Strg+Alt+Q`.

## Bedienung

| Aktion | Wirkung |
|---|---|
| Klick in ein Fenster einer Spalte | Ganz normale Bedienung, das Fenster bleibt in seinem Platz. |
| **Zurück-Taste der Maus** auf ein Fenster einer Spalte | Dieses Fenster kommt in die Mitte. Das bisherige Mitte-Fenster rückt auf den freigewordenen Platz (Tausch). |
| **Vorwärts-Taste der Maus** auf eine Spalte | Vorschauen aller Fenster dieser App erscheinen über dem Platz, untereinander und scrollbar (Mausrad). Ein Klick auf eine Vorschau setzt dieses Fenster in den Platz. Die Liste verschwindet, wenn die Maus sie verlässt. |
| `Strg+Alt+F` | Spalten ein/aus. Das Mitte-Fenster bleibt, wo es ist. |
| `Strg+Alt+P` | Prioritätsliste: alle bekannten Apps, vorne = wichtiger. Laufende Apps stehen hell, nicht laufende grau. ▲/▼, „immer außen“ und „Entfernen“ werden sofort in `config.json` gespeichert. „Neu sortieren“ verteilt die Plätze neu. |
| `Strg+Alt+Q` | Beendet Fokus-Dock. |

Der **Sammelplatz** ist der unterste Platz rechts: Dort liegen alle Apps, die keinen eigenen Platz bekommen, übereinander, das zuletzt aktive Fenster vorne. Zurück-Taste holt ein Fenster in die Mitte, Vorwärts-Taste zeigt die Vorschauen aller dieser Fenster, sortiert nach Priorität und scrollbar. Abschalten mit `"overflowCorner": false`.

Jede geöffnete App erscheint automatisch: Hat ein Fenster keinen passenden Eintrag in `apps`, legt Fokus-Dock einen an (Name aus der Dateibeschreibung der EXE), hängt ihn hinten an die Liste und speichert ihn.

Jede App hat einen Platz. Pro Platz steht das zuletzt aktive Fenster der App vorne, die anderen Fenster derselben App liegen direkt dahinter. Öffnet eine App ein neues Fenster, wird es in den Platz eingesetzt.

## config.json

Die Datei liegt neben `FokusDock.exe`. Fehlt sie, gelten die Standardwerte. Nach Änderungen Programm neu starten.

| Schlüssel | Bedeutung |
|---|---|
| `tilesPerSide` | Plätze pro Seite: `2` oder `3` (Standard 3). |
| `centerPercent` | Breite des Mitte-Fensters in Prozent der Arbeitsbereichsbreite, `40` bis `60` (Standard 50). Die Seiten bekommen je `(100 − centerPercent) / 2`. |
| `startInCenter` | App, deren Fenster beim Start in der Mitte steht (Standard `"Brave"`), falls sie läuft. |
| `hideDelayMs` | Wartezeit, bis die Fensterliste nach Verlassen ausgeblendet wird (Standard 400). |
| `hotkey` | Tastenkürzel für Ein/Aus, Standard `"Ctrl+Alt+F"`. |
| `exitHotkey` | Tastenkürzel zum Beenden, Standard `"Ctrl+Alt+Q"`. |
| `overflowCorner` | Sammelplatz unten rechts für alle Apps ohne eigenen Platz (Standard `true`). |
| `apps` | Liste der Apps. Die Reihenfolge ist die Startbelegung der Plätze: erst links von oben nach unten, dann rechts. Apps ohne Platz bleiben ohne Platz. Neu erkannte Apps werden automatisch hinten angehängt. |

Tastenkürzel: Modifier `Ctrl`/`Strg`, `Alt`, `Shift`, `Win`, dazu eine Taste, z. B. `"Win+Shift+D"`.

Ein Eintrag in `apps`:

```json
{ "name": "Claude", "processes": [ "Claude.exe" ] }
```

`processes` sind Prozessnamen mit Endung. Sie findest du im Task-Manager (Spalte „Details“). Ein Eintrag mit mehreren Prozessen fasst deren Fenster zu einer App zusammen, z. B. PowerShell mit `powershell.exe`, `pwsh.exe` und `WindowsTerminal.exe`.

### Web-Apps

Web-Apps (Brave-Verknüpfungen) sind eigene Apps:
- Sie laufen in `brave.exe`. Ein Fenster gehört zur Web-App, wenn sein Titel mit `titleStartsWith` beginnt und nicht auf ` - Brave` endet. Normale Brave-Tabs bleiben bei Brave.
- Web-Apps müssen in der Liste vor `Brave` stehen, sonst gehören ihre Fenster zu Brave.

Standardliste: Claude, die Web-App WhatsApp Web, Brave, PowerShell und Explorer. Eine Vorlage mit allen Feldern liegt in `config.example.json`. Kopiere sie nach `config.json` und passe sie an. `config.json` ist persönlich und wird nicht ins Repository übernommen.

## Grenzen

- Kein Administratorrecht nötig. Fenster von Apps mit Admin-Rechten lassen sich nur platzieren, wenn Fokus-Dock selbst als Administrator läuft.
- Die Zurück- und Vorwärts-Taste sind global: Auf einem Fenster in einer Spalte wird der Klick abgefangen. Die Zurück-Funktion im Browser funktioniert dort also nicht.
- Die Spalten reservieren keinen Platz im Arbeitsbereich. Maximierte Fenster werden am linken und rechten Rand überlagert.
- Fenster, die man verschiebt oder in der Größe ändert, werden nicht sofort zurückgesetzt. Die Plätze werden neu eingerichtet, wenn sich die Fensterliste der App ändert.

## Build

```
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

## Dateien

| Datei | Inhalt |
|---|---|
| `DockWindow.xaml(.cs)` | Unsichtbarer Träger für Tastenkürzel, Mausfilter und Timer; Plätze, Mitte, Tausch |
| `MouseHook.cs` | Globaler Mausfilter für Strg+Klick (WH_MOUSE_LL) |
| `WindowListWindow.cs` | Scrollbare Fensterliste (Strg+Rechtsklick) |
| `ThumbnailWindow.cs` | Gemeinsame DWM-Thumbnail-Logik der Liste |
| `DockLayout.cs` | Aufteilung des Arbeitsbereichs in Plätze und Mitte |
| `WindowFinder.cs` | Fenster finden, filtern, Apps zuordnen (inkl. Web-Apps) |
| `WindowPlacer.cs` | Fenster in Plätze und in die Mitte setzen |
| `Native.cs` | P/Invoke (user32, dwmapi, kernel32) |
| `AppConfig.cs`, `Monitors.cs` | Config und Monitorhilfen |

## Lizenz

Apache License 2.0, siehe [LICENSE](LICENSE).
