using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FokusDock;

/// <summary>
/// Unsichtbarer Träger für Tastenkürzel, Mausfilter und Timer. Setzt die echten Fenster in die
/// Kachelplätze der Seitenspalten und das Mitte-Fenster in die Mitte.
/// Die Zurück-Taste der Maus auf ein Spaltenfenster holt es in die Mitte, die Vorwärts-Taste zeigt die Fenster der App.
/// </summary>
public partial class DockWindow : Window
{
    internal const int ToggleHotkeyId = 1;
    internal const int ExitHotkeyId = 2;
    internal const int PriorityHotkeyId = 3;
    private const int ListEscHotkeyId = 4;   // Esc, solange die Fensterliste offen ist
    internal static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private readonly AppConfig _config;
    private readonly WindowListWindow _list = new();
    private readonly MouseHook _mouse;
    private readonly DragSnap _drag;
    private readonly DispatcherTimer _dragTimer;   // läuft nur während eines Ziehens: aktualisiert die Zielvorschau
    private ZoneHighlight _highlight;              // Zielvorschau; entsteht beim ersten Ziehen
    private IntPtr _dragHwnd;                      // gezogenes Fenster (Root), IntPtr.Zero = kein Ziehen mit Vorschau
    private int _dragApp = -1;
    private int _dragWidth;
    private int _dragHeight;
    private (DropKind Kind, int Slot, bool Free) _dragShown = (DropKind.None, -1, false);   // zuletzt gezeichneter Stand
    private PriorityWindow _priority;
    private LayoutWindow _layout;
    private HelpWindow _help;                // Bedienungshilfe; entsteht beim ersten Aufruf
    private TrayIcon _tray;                  // Infobereichssymbol mit Menü; null, wenn es nicht angelegt werden konnte
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _displayTimer; // einmalig: wartet nach einer Bildschirm- oder Arbeitsbereichsänderung ab

    // App-Index je Kachelplatz (links von oben nach unten, dann rechts), -1 = frei.
    // Die Mitte-App steht nie in einem Kachelplatz.
    private int[] _slots;
    private string[] _slotSignature; // Fensterreihenfolge, zu der der Platz zuletzt eingerichtet wurde

    private List<WindowInfo>[] _groups;      // letzter Fensterstand je App
    private IntPtr _centerHwnd;              // echtes Fenster in der Mitte
    private int _centerApp = -1;             // App des Mitte-Fensters, -1 = keines
    private int _listSlot = -1;              // Kachelplatz, dessen Fensterliste offen ist
    private NativeRect _listRect;            // Pixelrechteck der offenen Fensterliste (Klick-daneben-Test)
    private bool _escRegistered;             // Esc-Kürzel für die Liste ist belegt
    private bool _initialized;
    private bool _enabled = true;            // Spalten ein/aus (Hotkey)
    private bool _fullscreenPause;           // Vollbild läuft (Spiel, Video, Präsentation): Fokus-Dock fasst keine Fenster an
    private DateTime? _leaveSince;
    private DateTime? _startCenterUntil;     // Start-App lief beim Start noch nicht: bis dahin kommt sie beim Auftauchen in die Mitte
    private readonly List<string> _hotkeyErrors = new();   // Zeilen für die eine Meldung nach dem Start
    private readonly HashSet<int> _failedHotkeys = new();  // Kürzel, die nicht registriert werden konnten
    private readonly HashSet<IntPtr> _blocked = new();     // Fenster, die sich nicht verschieben lassen (Administrator): Fokus-Dock lässt sie in Ruhe
    private DateTime _lastSaveToast = DateTime.MinValue;   // Speicherfehler erscheinen höchstens einmal pro Minute

    public DockWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        int slotCount = config.Cells.Count;
        _slots = Enumerable.Repeat(-1, slotCount).ToArray();
        _slotSignature = new string[slotCount];
        _groups = Enumerable.Range(0, config.Apps.Count).Select(_ => new List<WindowInfo>()).ToArray();

        _mouse = new MouseHook(OnSlotButton, OnMouseClick);
        _drag = new DragSnap(OnDragStart, OnDragEnd, OnWindowMoved);
        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _dragTimer.Tick += (_, _) => DragTick();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += (_, _) => Poll();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();

        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _displayTimer.Tick += (_, _) => OnDisplayChanged();

        HelpWindow.Opener = OpenHelp;   // F1 in den anderen Fenstern
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Die Timer starten auch, wenn der erste Refresh scheitert: der Fehler geht an den Handler der App
        // (Protokoll), das Programm soll danach nicht ohne Timer und damit halbtot weiterlaufen.
        try
        {
            Refresh();
        }
        finally
        {
            _pollTimer.Start();
            _refreshTimer.Start();
            // Hinweise zur config.json erst zeigen, wenn die Fenster angeordnet sind
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowConfigWarnings));
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowHotkeyErrors));
            if (!_config.FileExisted && !_config.SaveDisabled)
            {
                // Erster Start ohne config.json: die Hilfe einmal zeigen; die Datei entsteht jetzt, damit sie beim nächsten Start nicht mehr fehlt
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowFirstRunHelp));
            }
        }
    }

    /// <summary>Hilfe beim ersten Start. config.json wird vorher geschrieben, damit sie nur einmal erscheint.</summary>
    private void ShowFirstRunHelp()
    {
        SaveConfig(out _);
        OpenHelp();
    }

    /// <summary>Einmalig die Korrekturen melden, die AppConfig.Load an der config.json vorgenommen hat.</summary>
    private void ShowConfigWarnings()
    {
        if (_config.Warnings.Count == 0)
        {
            return;
        }

        const int maxLines = 12;
        List<string> lines = _config.Warnings.Take(maxLines).Select(w => "• " + w).ToList();
        if (_config.Warnings.Count > maxLines)
        {
            lines.Add($"• … und {_config.Warnings.Count - maxLines} weitere.");
        }
        _config.Warnings.Clear();

        MessageBox.Show("config.json wurde beim Laden angepasst:\n" + string.Join("\n", lines),
            "Fokus-Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);

        // Kein Eintrag in Alt+Tab: das Fenster ist nur Träger, ein Taskleistenknopf fehlt schon (ShowInTaskbar=False)
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);

        // Zuerst die Filter: ein belegtes Kürzel darf Maus und Ziehen nicht aufhalten, auch nicht während der Meldung dazu
        _mouse.Install();
        _drag.Install();

        AddHotkey(hwnd, ToggleHotkeyId, "Ein/Aus", _config.Hotkey);
        AddHotkey(hwnd, PriorityHotkeyId, "Prioritätsliste", _config.PriorityHotkey);
        AddHotkey(hwnd, ExitHotkeyId, "Beenden", _config.ExitHotkey);

        try
        {
            _tray = new TrayIcon(this, _config);
            _tray.SetEnabled(_enabled);
        }
        catch (Exception ex)
        {
            // Ohne Symbol bleibt Fokus-Dock über die Kürzel bedienbar
            App.WriteLog(ex.ToString());
        }
    }

    /// <summary>Kürzel registrieren; bei einem Fehler wird der Grund für die Meldung nach dem Start gemerkt.</summary>
    private void AddHotkey(IntPtr hwnd, int id, string purpose, string text)
    {
        string reason = RegisterHotkey(hwnd, id, text);
        if (reason == null)
        {
            return;
        }

        _failedHotkeys.Add(id);
        string shown = HotkeyText(text);
        _hotkeyErrors.Add($"{purpose}: {(string.IsNullOrWhiteSpace(shown) ? "(leer)" : shown)} ({reason})");
    }

    /// <summary>Registriert das Kürzel. Liefert null bei Erfolg, sonst den Grund.</summary>
    private static string RegisterHotkey(IntPtr hwnd, int id, string text)
    {
        if (!TryParseHotkey(text, out uint modifiers, out uint key))
        {
            return "ungültig";
        }

        if (Native.RegisterHotKey(hwnd, id, modifiers | Native.MOD_NOREPEAT, key))
        {
            return null;
        }

        int error = Marshal.GetLastWin32Error();
        return error == Native.ERROR_HOTKEY_ALREADY_REGISTERED ? "schon von einem anderen Programm belegt" : $"Fehler {error}";
    }

    /// <summary>Genau eine Meldung für alle Kürzel, die nicht verfügbar sind. Das Symbol im Infobereich bleibt der Weg zu den Funktionen.</summary>
    private void ShowHotkeyErrors()
    {
        if (_hotkeyErrors.Count == 0)
        {
            return;
        }

        string lines = string.Join("\n", _hotkeyErrors.Select(h => "• " + h));
        _hotkeyErrors.Clear();

        MessageBox.Show("Diese Tastenkürzel sind nicht verfügbar:\n" + lines +
            "\n\nDie Funktionen erreichst du über das Fokus-Dock-Symbol im Infobereich. " +
            "Ändern kannst du die Kürzel in config.json („hotkey“, „priorityHotkey“, „exitHotkey“).",
            "Fokus-Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Kürzel aus der config.json in der Schreibweise der Oberfläche: „Ctrl“ heißt dort „Strg“, egal wie es geschrieben ist.</summary>
    internal static string HotkeyText(string raw) =>
        string.Join("+", (raw ?? "").Split('+', StringSplitOptions.TrimEntries)
            .Select(part => part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ? "Strg" : part));

    /// <summary>Kürzel für das Menü; ist es nicht verfügbar, steht „(nicht verfügbar)“ dahinter.</summary>
    internal string HotkeyLabel(int id)
    {
        string raw = id switch
        {
            ToggleHotkeyId => _config.Hotkey,
            ExitHotkeyId => _config.ExitHotkey,
            _ => _config.PriorityHotkey,
        };
        string text = HotkeyText(raw);
        if (!_failedHotkeys.Contains(id))
        {
            return text;
        }
        return text.Length == 0 ? "(nicht verfügbar)" : text + " (nicht verfügbar)";
    }

    private void OnClosed(object sender, EventArgs e)
    {
        try
        {
            _tray?.Dispose();   // zuerst: das Symbol verschwindet sofort aus dem Infobereich
            _pollTimer.Stop();
            _refreshTimer.Stop();
            _displayTimer.Stop();
            _mouse.Dispose();
            _drag.Dispose();
            _dragTimer.Stop();
            _highlight?.Close();
            HideList();
            _list.Close();
            _priority?.Close();
            _layout?.Shutdown();
            _help?.Close();
        }
        finally
        {
            // Das Prioritäts- und das Layoutfenster blenden sich beim Schließen nur aus und halten sonst den Prozess am Leben.
            // Wird dieses unsichtbare Fenster geschlossen (z. B. über die Taskleiste), endet das ganze Programm,
            // auch wenn das Aufräumen oben scheitert.
            Application.Current.Shutdown();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == ToggleHotkeyId)
            {
                ToggleEnabled();
                handled = true;
            }
            else if (id == ExitHotkeyId)
            {
                Application.Current.Shutdown();
                handled = true;
            }
            else if (id == PriorityHotkeyId)
            {
                OpenPriority();
                handled = true;
            }
            else if (id == ListEscHotkeyId)
            {
                HideList();
                handled = true;
            }
        }
        else if (msg == Native.WM_DISPLAYCHANGE
                 || (msg == Native.WM_SETTINGCHANGE && wParam.ToInt64() == Native.SPI_SETWORKAREA))
        {
            // Auflösung oder Arbeitsbereich (z. B. Taskleiste) geändert. Windows meldet das oft mehrfach kurz nacheinander,
            // daher beginnt die Wartezeit mit jeder Meldung neu. Nicht als behandelt markieren: WPF braucht die Meldung selbst.
            _displayTimer.Stop();
            _displayTimer.Start();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Nach einer Änderung von Auflösung oder Arbeitsbereich: Mitte-Fenster und Plätze auf die neue Fläche bringen.
    /// Die Signaturen werden verworfen, damit der Abgleich alle Plätze neu setzt.
    /// </summary>
    private void OnDisplayChanged()
    {
        _displayTimer.Stop();
        HideList();
        Array.Fill(_slotSignature, null);

        // Das Mitte-Fenster nur anfassen, wenn Fokus-Dock an ist und kein Vollbild läuft. Ein Spiel, das die Auflösung
        // wechselt, löst diese Meldung ebenfalls aus; der Zustand wird deshalb jetzt frisch gelesen.
        _fullscreenPause = QueryFullscreenPause();
        Toast.Paused = _fullscreenPause;
        if (_enabled && !_fullscreenPause && _centerHwnd != IntPtr.Zero && Native.IsWindow(_centerHwnd)
            && !WindowFinder.IsFullscreen(_centerHwnd))
        {
            WindowPlacer.Move(_centerHwnd, DockLayout.Compute(_config).Center);
        }
        Refresh();
    }

    /// <summary>
    /// Ob ein Vollbild läuft: Windows meldet dann "beschäftigt" (2), Direct3D-Vollbild (3) oder Präsentationsmodus (4).
    /// Nur Refresh und die Anzeigeänderung fragen die API; der Mausfilter liest das Feld.
    /// </summary>
    private static bool QueryFullscreenPause() =>
        Native.SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;

    internal void ToggleEnabled()
    {
        _enabled = !_enabled;
        if (_enabled)
        {
            Refresh();
        }
        else
        {
            HideList();
        }
        _tray?.SetEnabled(_enabled);
        Toast.Show(_enabled ? "Fokus-Dock an" : "Fokus-Dock aus · Fenster bleiben, wo sie sind");
    }

    internal void OpenPriority()
    {
        Refresh();
        _priority ??= new PriorityWindow(ApplyPriority, Resort, OpenLayout, app => _config.Cells.Any(c => c.App == app.Name));
        _priority.Open(_config.Apps, RunningFlags(), PlaceTexts());
    }

    /// <summary>Pro App, ob Fenster offen sind (Reihenfolge wie _config.Apps).</summary>
    private List<bool> RunningFlags() => _config.Apps.Select((_, i) => _groups[i].Count > 0).ToList();

    /// <summary>
    /// Platztext je App für die Prioritätsliste: „Mitte“, „L2“ bzw. „L2 fest“ (feste Zelle), „Sammelplatz“, „ohne Platz“ oder „–“.
    /// </summary>
    private List<string> PlaceTexts()
    {
        var texts = new List<string>();
        for (int i = 0; i < _config.Apps.Count; i++)
        {
            AppEntry app = _config.Apps[i];
            int slot = Array.IndexOf(_slots, i);
            if (i == _centerApp)
            {
                texts.Add("Mitte");
            }
            else if (slot >= 0 && slot < _config.Cells.Count && slot != OverflowSlot)
            {
                texts.Add(DockLayout.SlotLabel(_config, slot) + (_config.Cells[slot].App == app.Name ? " fest" : ""));
            }
            else if (app.Ignored)
            {
                texts.Add("–");
            }
            else if (i < _groups.Length && _groups[i].Count > 0)
            {
                texts.Add(OverflowSlot >= 0 ? "Sammelplatz" : "ohne Platz");
            }
            else
            {
                texts.Add("–");
            }
        }
        return texts;
    }

    /// <summary>
    /// Jede geöffnete App gehört in die Liste: Fenster ohne passenden Eintrag werden als neue App
    /// hinten angehängt und sofort gespeichert. Hinten anhängen hält die Indizes von Plätzen und Mitte gültig.
    /// </summary>
    private void AddDiscoveredApps()
    {
        List<WindowInfo> unmatched = WindowFinder.FindUnmatched(_config.Apps);
        if (unmatched.Count == 0)
        {
            return;
        }

        var added = new List<string>();
        bool merged = false;
        foreach (WindowInfo info in unmatched)
        {
            if (info.WebAppName != null)
            {
                // Dieselbe Web-App läuft schon als Eintrag (z. B. aus Brave): den Browser-Prozess dort ergänzen
                AppEntry same = _config.Apps.FirstOrDefault(a =>
                    a.TitleStartsWith != null && a.TitleStartsWith.Equals(info.WebAppName, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    if (!same.Processes.Contains(info.ProcessName, StringComparer.OrdinalIgnoreCase))
                    {
                        same.Processes.Add(info.ProcessName);
                        merged = true;
                    }
                    continue;
                }
            }

            string name = info.WebAppName ?? WindowFinder.DisplayName(info);
            if (IndexOfApp(name) >= 0)
            {
                name = $"{name} ({Path.GetFileNameWithoutExtension(info.ProcessName)})";
            }
            _config.Apps.Add(new AppEntry
            {
                Name = name,
                Processes = new List<string> { info.ProcessName },
                TitleStartsWith = info.WebAppName,
            });
            added.Add(name);
        }

        if (added.Count == 0 && !merged)
        {
            return;
        }

        // Ein Toast für alle neuen Apps dieses Durchgangs; ein Speicherfehler geht vor
        SaveConfig(out bool notified);
        if (notified || added.Count == 0)
        {
            return;
        }
        if (added.Count == 1)
        {
            Toast.Show($"Neue App aufgenommen: {added[0]}");
        }
        else
        {
            string names = string.Join(", ", added.Take(3)) + (added.Count > 3 ? " …" : "");
            Toast.Show($"{added.Count} neue Apps aufgenommen: {names}");
        }
    }

    /// <summary>
    /// config.json speichern. Scheitert das, erscheint ein Toast (höchstens einmal pro Minute); notified sagt,
    /// ob er gerade gezeigt wurde, damit ein folgender Toast ihn nicht gleich wieder ersetzt.
    /// </summary>
    private bool SaveConfig(out bool notified)
    {
        notified = false;
        if (_config.Save(ConfigPath))
        {
            return true;
        }

        if (DateTime.Now - _lastSaveToast >= TimeSpan.FromMinutes(1))
        {
            _lastSaveToast = DateTime.Now;
            notified = true;
            Toast.Show(_config.SaveDisabled
                ? "Änderungen werden nicht gespeichert, weil config.json fehlerhaft ist"
                : "config.json kann nicht gespeichert werden");
        }
        return false;
    }

    /// <summary>Titel für die Meldungen: höchstens 40 Zeichen, sonst mit Auslassungszeichen gekürzt.</summary>
    private string TitleOf(IntPtr hwnd)
    {
        string title = _groups.SelectMany(g => g).FirstOrDefault(w => w.Hwnd == hwnd)?.Title ?? "";
        return title.Length <= 40 ? title : title[..39] + "…";
    }

    /// <summary>
    /// Merkt ein Fenster als nicht verschiebbar vor, wenn das Platzieren an „Zugriff verweigert“ scheiterte
    /// (Fenster mit Administratorrechten). Pro Fenster erscheint der Hinweis einmal. Liefert true, wenn es dieser Fall war.
    /// </summary>
    private bool NoteIfBlocked(IntPtr hwnd)
    {
        if (!WindowPlacer.LastFailureWasAccessDenied)
        {
            return false;
        }

        if (_blocked.Add(hwnd))
        {
            Toast.Show($"„{TitleOf(hwnd)}“ läuft als Administrator und lässt sich nicht verschieben. Fokus-Dock lässt es in Ruhe.");
        }
        return true;
    }

    /// <summary>
    /// Neue Prioritätsreihenfolge übernehmen. Plätze und Mitte werden über den App-Namen umgehängt,
    /// weil sich die Indizes der Apps ändern.
    /// </summary>
    private void ApplyPriority(List<AppEntry> ordered, bool save)
    {
        string[] slotNames = _slots.Select(i => i < 0 ? null : _config.Apps[i].Name).ToArray();
        string centerName = _centerApp < 0 ? null : _config.Apps[_centerApp].Name;

        _config.Apps = ordered.ToList();
        for (int s = 0; s < _slots.Length; s++)
        {
            _slots[s] = IndexOfApp(slotNames[s]);
        }
        _centerApp = IndexOfApp(centerName);

        if (save)
        {
            SaveConfig(out _);
        }
        Refresh();
    }

    /// <summary>„Neu sortieren“: alle Plätze nach Priorität neu belegen (nur Apps mit Fenstern).</summary>
    private void Resort()
    {
        Refresh();
        AssignSlots(onlyRunning: true);
        Refresh();
        Toast.Show("Plätze neu verteilt");
    }

    private int IndexOfApp(string name) =>
        name == null ? -1 : _config.Apps.FindIndex(a => a.Name == name);

    /// <summary>Fensterstand lesen und die Kachelplätze und das Mitte-Fenster nachziehen.</summary>
    private void Refresh()
    {
        // Solange ein Vollbild läuft, bleiben alle Fenster unberührt; nur der Fensterstand wird weiter gelesen
        _fullscreenPause = QueryFullscreenPause();
        Toast.Paused = _fullscreenPause;

        // Geschlossene Fenster verlassen die Liste der nicht verschiebbaren; ein neues Fenster bekommt wieder einen Versuch
        _blocked.RemoveWhere(h => !Native.IsWindow(h));

        AddDiscoveredApps();
        List<WindowInfo>[] groups = WindowFinder.Collect(_config.Apps);
        _groups = groups;

        if (!_initialized && !_fullscreenPause)
        {
            _initialized = true;
            InitializeState(groups);
        }

        PlaceLateStartApp(groups);

        if (_centerHwnd != IntPtr.Zero)
        {
            _centerApp = AppOfWindow(groups, _centerHwnd);
            if (_centerApp < 0)
            {
                _centerHwnd = IntPtr.Zero;
            }
        }

        if (_enabled && !_fullscreenPause)
        {
            EnforceHomes();
            EnsurePriorityApps();
            FillGaps(groups);
            ApplySlots(groups);
        }

        if (_listSlot >= 0 && WindowsOfSlot(_listSlot).Count == 0)
        {
            HideList();
        }

        // Offene Prioritätsliste: Status und Plätze nachziehen (ändert nur Texte, außer die App-Folge weicht ab)
        if (_priority != null && _priority.IsVisible)
        {
            _priority.UpdateState(_config.Apps, RunningFlags(), PlaceTexts());
        }
    }

    /// <summary>
    /// Start: Brave (falls laufend) in die Mitte, die übrigen Apps in der Reihenfolge der Config auf die Plätze.
    /// Läuft die Start-App noch nicht (z. B. Autostart bei der Anmeldung), kommt sie in den nächsten 60 Sekunden
    /// beim Auftauchen in die Mitte (siehe Refresh).
    /// </summary>
    private void InitializeState(List<WindowInfo>[] groups)
    {
        int start = StartApp();
        if (!PlaceStartAppInCenter(groups) && start >= 0 && !IsIgnored(start))
        {
            _startCenterUntil = DateTime.UtcNow.AddSeconds(60);
        }

        AssignSlots(onlyRunning: false);
    }

    /// <summary>Index der App aus startInCenter, -1 wenn es keine solche App gibt.</summary>
    private int StartApp() =>
        _config.Apps.FindIndex(a => a.Name.Equals(_config.StartInCenter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Das oberste Fenster der Start-App in die Mitte setzen, falls sie läuft und verwaltet wird.
    /// Vollbildfenster bleiben unberührt. Liefert true, wenn ein Fenster in der Mitte steht.
    /// </summary>
    private bool PlaceStartAppInCenter(List<WindowInfo>[] groups)
    {
        int start = StartApp();
        if (start < 0 || IsIgnored(start))
        {
            return false;
        }

        WindowInfo window = groups[start].FirstOrDefault(w => !w.Fullscreen && !_blocked.Contains(w.Hwnd));
        if (window == null)
        {
            return false;
        }

        // Erst platzieren: lässt sich das Fenster nicht verschieben, ändert sich am Zustand nichts
        if (!WindowPlacer.Place(window.Hwnd, DockLayout.Compute(_config).Center))
        {
            NoteIfBlocked(window.Hwnd);
            return false;
        }

        _centerApp = start;
        _centerHwnd = window.Hwnd;
        return true;
    }

    /// <summary>
    /// Späte Start-App: solange die Frist läuft und die Mitte leer ist, wandert die Start-App beim Auftauchen
    /// in die Mitte. Ihr bisher reservierter Platz außen wird frei und von der Lückenfüllung belegt.
    /// </summary>
    private void PlaceLateStartApp(List<WindowInfo>[] groups)
    {
        if (_startCenterUntil == null)
        {
            return;
        }

        if (DateTime.UtcNow >= _startCenterUntil)
        {
            _startCenterUntil = null;
            return;
        }

        if (!_enabled || _fullscreenPause || _centerHwnd != IntPtr.Zero || !PlaceStartAppInCenter(groups))
        {
            return;
        }

        _startCenterUntil = null;
        int old = Array.IndexOf(_slots, _centerApp);
        if (old >= 0 && SlotWindows(_centerApp).Count == 0)
        {
            _slots[old] = -1;
        }
        Array.Fill(_slotSignature, null);
    }

    /// <summary>Ob eine App einen Platz außen haben kann: immer, außer sie steht in der Mitte ohne weitere Fenster.</summary>
    private bool CanHaveSlot(int app) => app != _centerApp || SlotWindows(app).Count > 0;

    /// <summary>
    /// Plätze belegen: zuerst die festen Apps ihrer Zelle, danach die übrigen Apps in Prioritätsreihenfolge
    /// in die freien Plätze. onlyRunning: nur Apps mit Fenstern.
    /// </summary>
    private void AssignSlots(bool onlyRunning)
    {
        Array.Fill(_slots, -1);
        int overflow = OverflowSlot;
        for (int c = 0; c < _config.Cells.Count; c++)
        {
            int app = IndexOfApp(_config.Cells[c].App);
            if (c != overflow && app >= 0 && !IsIgnored(app) && CanHaveSlot(app) && (!onlyRunning || SlotWindows(app).Count > 0) && !_slots.Contains(app))
            {
                _slots[c] = app;
            }
        }

        int cell = 0;
        for (int i = 0; i < _config.Apps.Count; i++)
        {
            if (IsIgnored(i) || !CanHaveSlot(i) || _slots.Contains(i) || _config.Apps[i].InCorner || (onlyRunning && SlotWindows(i).Count == 0))
            {
                continue;
            }

            while (cell < _slots.Length && (_slots[cell] >= 0 || cell == overflow))
            {
                cell++;
            }
            if (cell >= _slots.Length)
            {
                break;
            }
            _slots[cell] = i;
        }

        Array.Fill(_slotSignature, null);
    }

    /// <summary>Eine App mit fester Zelle wandert zurück in ihre Zelle, sobald sie läuft. Dort verdrängt sie die App.</summary>
    private void EnforceHomes()
    {
        int overflow = OverflowSlot;
        for (int c = 0; c < _config.Cells.Count; c++)
        {
            int app = IndexOfApp(_config.Cells[c].App);
            if (c == overflow || app < 0 || _slots[c] == app || !CanHaveSlot(app) || SlotWindows(app).Count == 0)
            {
                continue;
            }

            int old = Array.IndexOf(_slots, app);
            if (old >= 0)
            {
                _slots[old] = -1;
            }
            _slots[c] = app;
            _slotSignature[c] = null;
        }
    }

    /// <summary>Neues Layout übernehmen: Zellen und Standardapps ersetzen, Plätze neu belegen.</summary>
    private void ApplyLayout(List<CellEntry> cells, int centerPercent, bool save)
    {
        _config.Cells = cells;
        _config.CenterPercent = Math.Clamp(centerPercent, 40, 60);
        _slots = new int[cells.Count];
        _slotSignature = new string[cells.Count];
        HideList();
        AssignSlots(onlyRunning: false);

        // Breitere oder schmalere Mitte: das Mitte-Fenster sofort auf die neue Größe setzen
        if (_enabled && !_fullscreenPause && _centerHwnd != IntPtr.Zero && Native.IsWindow(_centerHwnd)
            && !WindowFinder.IsFullscreen(_centerHwnd))
        {
            WindowPlacer.Move(_centerHwnd, DockLayout.Compute(_config).Center);
        }

        bool notified = false;
        if (save)
        {
            SaveConfig(out notified);
        }
        Refresh();
        if (save && !notified)
        {
            Toast.Show("Layout übernommen");
        }
    }

    /// <summary>Bedienungshilfe öffnen (Infobereich, F1, erster Start).</summary>
    internal void OpenHelp()
    {
        _help ??= new HelpWindow(_config, () => TrayIcon.OpenConfig(_config));
        _help.Open();
    }

    internal void OpenLayout()
    {
        _layout ??= new LayoutWindow(ApplyLayout);
        if (_layout.IsVisible)
        {
            // Offener Editor: ungespeicherte Änderungen nicht verwerfen, nur nach vorn holen
            _layout.BringToFront();
            return;
        }
        _layout.Open(_config.Cells, _config.Apps.Select(a => a.Name), _config.CenterPercent, _config.TilesPerSide, _config.OverflowCorner);
    }

    /// <summary>
    /// Immer-außen-Apps (alwaysOutside): laufen sie und stehen nicht in der Mitte, bekommen sie einen Platz.
    /// Dafür wird eine Lücke genutzt, sonst der Platz einer normalen App verdrängt (die dann ohne Platz ist).
    /// </summary>
    private void EnsurePriorityApps()
    {
        for (int i = 0; i < _config.Apps.Count; i++)
        {
            if (!_config.Apps[i].AlwaysOutside || _config.Apps[i].InCorner || i == _centerApp || _slots.Contains(i) || SlotWindows(i).Count == 0)
            {
                continue;
            }

            int slot = FindSlot(app => app < 0 || SlotWindows(app).Count == 0);
            if (slot < 0)
            {
                slot = FindSlot(app => app >= 0 && !_config.Apps[app].AlwaysOutside);
            }
            if (slot < 0)
            {
                continue;
            }

            _slots[slot] = i;
            _slotSignature[slot] = null;
        }
    }

    /// <summary>
    /// Lücken füllen: Ein Platz ist leer, wenn er frei ist oder seine App keine Fenster hat.
    /// Dann rückt die erste laufende App ohne Platz (Reihenfolge der Config) nach. Gibt es keine,
    /// bleibt der Platz für seine App reserviert, bis sie läuft.
    /// </summary>
    private void FillGaps(List<WindowInfo>[] groups)
    {
        int overflow = OverflowSlot;
        for (int s = 0; s < _slots.Length; s++)
        {
            if (s == overflow || (_slots[s] >= 0 && SlotWindows(_slots[s]).Count > 0))
            {
                continue;
            }

            int next = FirstRunningWithoutSlot(groups);
            if (next >= 0)
            {
                _slots[s] = next;
                _slotSignature[s] = null;
            }
        }
    }

    /// <summary>Erste laufende App, die weder einen Platz hat noch in der Mitte steht.</summary>
    private int FirstRunningWithoutSlot(List<WindowInfo>[] groups)
    {
        for (int i = 0; i < _config.Apps.Count; i++)
        {
            if (!_slots.Contains(i) && !_config.Apps[i].InCorner && SlotWindows(i).Count > 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Je Platz das oberste Fenster der App einsetzen, die übrigen Fenster der App direkt dahinter.
    /// Nur wenn sich die Fensterreihenfolge der App geändert hat, wird neu platziert.
    /// Vollbildfenster zählen für die Reihenfolge mit, werden aber nie platziert.
    /// </summary>
    private void ApplySlots(List<WindowInfo>[] groups)
    {
        NativeRect[] rects = DockLayout.SlotRects(_config);
        int overflow = OverflowSlot;
        for (int s = 0; s < _slots.Length; s++)
        {
            if (s != overflow && _slots[s] < 0)
            {
                _slotSignature[s] = null;
                continue;
            }

            List<WindowInfo> windows = WindowsOfSlot(s);
            string signature = string.Join(",", windows.Select(w => w.Hwnd + (w.Fullscreen ? "F" : "")));
            if (signature == _slotSignature[s])
            {
                continue;
            }
            _slotSignature[s] = signature;

            if (windows.Count == 0)
            {
                continue;
            }

            List<WindowInfo> placeable = windows.Where(w => !w.Fullscreen).ToList();
            if (placeable.Count == 0)
            {
                continue;
            }

            IntPtr front = placeable[0].Hwnd;
            bool blocked = false;
            for (int k = placeable.Count - 1; k >= 1; k--)
            {
                if (!WindowPlacer.PlaceInSlot(placeable[k].Hwnd, rects[s], front, front: false))
                {
                    blocked |= NoteIfBlocked(placeable[k].Hwnd);
                }
            }
            if (!WindowPlacer.PlaceInSlot(front, rects[s], Native.HWND_TOP, front: true))
            {
                blocked |= NoteIfBlocked(front);
            }

            if (blocked)
            {
                // Das Fenster fehlt ab jetzt in der Platzliste (SlotWindows): der nächste Abgleich vergibt den Platz neu
                _slotSignature[s] = null;
            }
        }
    }

    /// <summary>
    /// Mausfilter: Zusatztaste auf ein Fenster eines Kachelplatzes. Liefert true, wenn der Klick verschluckt wird.
    /// Zurück = Fenster in die Mitte, Vorwärts = Fensterliste der App.
    /// </summary>
    private bool OnSlotButton(NativePoint pt, int button)
    {
        if (!_enabled || _fullscreenPause)
        {
            return false;
        }

        NativeRect[] rects = DockLayout.SlotRects(_config);
        int slot = -1;
        for (int s = 0; s < rects.Length; s++)
        {
            if (Contains(rects[s], pt))
            {
                slot = s;
                break;
            }
        }
        if (slot < 0 || (_slots[slot] < 0 && slot != OverflowSlot))
        {
            return false;
        }

        IntPtr hwnd = Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT);
        WindowInfo hit = WindowsOfSlot(slot).FirstOrDefault(w => w.Hwnd == hwnd);
        if (hit == null || hit.Fullscreen)
        {
            // Kein verwaltetes Fenster oder ein Vollbildfenster: die Taste gehört der App
            return false;
        }

        // Der Mausfilter läuft im Nachrichtenkontext; die Arbeit erst danach im UI-Thread ausführen
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (button == MouseHook.XButtonForward)
            {
                ShowList(slot);
            }
            else
            {
                SwapToCenter(hwnd, slot);
            }
        }));
        return true;
    }

    /// <summary>Fenster einer App, die außen stehen können: alle außer dem Mitte-Fenster. Nicht verwaltete Apps haben keine.</summary>
    private List<WindowInfo> SlotWindows(int app) =>
        IsIgnored(app) ? new List<WindowInfo>() : _groups[app].Where(w => w.Hwnd != _centerHwnd && !_blocked.Contains(w.Hwnd)).ToList();

    /// <summary>Ob die App auf „nicht verwalten“ steht (ignored): Fokus-Dock fasst ihre Fenster nie an.</summary>
    private bool IsIgnored(int app) =>
        app >= 0 && app < _config.Apps.Count && _config.Apps[app].Ignored;

    /// <summary>
    /// Sammelplatz: die unterste Zelle der rechten Seite (ohne rechte Zellen die letzte). -1, wenn abgeschaltet.
    /// Er bekommt nie eine eigene App, sondern nimmt alle Apps ohne Platz auf.
    /// </summary>
    private int OverflowSlot => DockLayout.OverflowIndex(_config.Cells, _config.OverflowCorner);

    /// <summary>Fenster eines Platzes: beim Sammelplatz alle Apps ohne Platz, sonst die Fenster seiner App.</summary>
    private List<WindowInfo> WindowsOfSlot(int slot)
    {
        if (slot == OverflowSlot)
        {
            return OverflowWindows();
        }
        return _slots[slot] < 0 ? new List<WindowInfo>() : SlotWindows(_slots[slot]);
    }

    /// <summary>Fenster aller Apps ohne Platz in Z-Reihenfolge: das zuletzt aktive liegt vorne.</summary>
    private List<WindowInfo> OverflowWindows() =>
        Enumerable.Range(0, _groups.Length)
            .Where(i => !_slots.Contains(i))
            .SelectMany(SlotWindows)
            .OrderBy(w => w.Z)
            .ToList();

    /// <summary>Dieselben Fenster für die Liste: nach Priorität der App, innerhalb der App das zuletzt aktive zuerst.</summary>
    private List<WindowInfo> OverflowListWindows() =>
        Enumerable.Range(0, _groups.Length)
            .Where(i => !_slots.Contains(i))
            .SelectMany(SlotWindows)
            .ToList();

    /// <summary>Erster normaler Platz (nicht der Sammelplatz), dessen App die Bedingung erfüllt.</summary>
    private int FindSlot(Func<int, bool> match)
    {
        int overflow = OverflowSlot;
        for (int s = 0; s < _slots.Length; s++)
        {
            if (s != overflow && match(_slots[s]))
            {
                return s;
            }
        }
        return -1;
    }

    /// <summary>
    /// Fenster aus dem Sammelplatz in die Mitte. Das bisherige Mitte-Fenster muss nicht umgesetzt werden:
    /// der nächste Abgleich legt es auf den Platz seiner App oder, ohne Platz, in den Sammelplatz.
    /// </summary>
    private bool MoveToCenter(IntPtr hwnd)
    {
        int app = AppOfWindow(_groups, hwnd);
        if (app < 0 || hwnd == _centerHwnd || IsIgnored(app) || _blocked.Contains(hwnd))
        {
            return false;
        }

        // Erst platzieren: lässt sich das Fenster nicht verschieben, bleibt alles, wie es war
        if (!WindowPlacer.Place(hwnd, DockLayout.Compute(_config).Center))
        {
            NoteIfBlocked(hwnd);
            Refresh();
            return false;
        }

        _centerHwnd = hwnd;
        _centerApp = app;
        Array.Fill(_slotSignature, null);
        Refresh();
        return true;
    }

    /// <summary>
    /// Zustand vor dem Einrasten, damit „Rückgängig“ Plätze, feste Zellen und Mitte wiederherstellen kann.
    /// Die Namen der Apps sichern die Zuordnung der Indizes: eine geänderte Reihenfolge macht die Sicherung ungültig.
    /// </summary>
    private sealed class SnapUndo
    {
        public string[] AppNames;
        public bool[] InCorner;
        public string[] CellApps;       // Länge = Zellenzahl zur Zeit der Sicherung
        public int[] Slots;
        public IntPtr CenterHwnd;
        public int CenterApp;
    }

    private SnapUndo CaptureUndo() => new()
    {
        AppNames = _config.Apps.Select(a => a.Name).ToArray(),
        InCorner = _config.Apps.Select(a => a.InCorner).ToArray(),
        CellApps = _config.Cells.Select(c => c.App).ToArray(),
        Slots = (int[])_slots.Clone(),
        CenterHwnd = _centerHwnd,
        CenterApp = _centerApp,
    };

    /// <summary>
    /// Rückgängig-Knopf des Toasts nach dem Einrasten. Gilt nur, solange Zellenzahl und App-Reihenfolge gleich sind;
    /// neu angehängte Apps bleiben bestehen.
    /// </summary>
    private void UndoSnap(SnapUndo undo)
    {
        bool valid = undo.CellApps.Length == _config.Cells.Count
            && undo.Slots.Length == _slots.Length
            && _config.Apps.Count >= undo.AppNames.Length;
        for (int i = 0; valid && i < undo.AppNames.Length; i++)
        {
            valid = _config.Apps[i].Name == undo.AppNames[i];
        }
        if (!valid)
        {
            Toast.Show("Rückgängig nicht mehr möglich");
            return;
        }

        for (int c = 0; c < undo.CellApps.Length; c++)
        {
            _config.Cells[c].App = undo.CellApps[c];
        }
        for (int i = 0; i < undo.InCorner.Length; i++)
        {
            _config.Apps[i].InCorner = undo.InCorner[i];
        }
        Array.Copy(undo.Slots, _slots, _slots.Length);
        Array.Fill(_slotSignature, null);
        HideList();

        if (undo.CenterHwnd != _centerHwnd)
        {
            if (undo.CenterHwnd != IntPtr.Zero && Native.IsWindow(undo.CenterHwnd))
            {
                // Das frühere Mitte-Fenster kommt zurück in die Mitte; das eingerastete Fenster räumt der nächste Abgleich weg
                _centerHwnd = undo.CenterHwnd;
                _centerApp = undo.CenterApp;
                WindowPlacer.Place(_centerHwnd, DockLayout.Compute(_config).Center);
            }
            else
            {
                _centerHwnd = IntPtr.Zero;
                _centerApp = -1;
            }
        }

        SaveConfig(out bool notified);
        Refresh();
        if (!notified)
        {
            Toast.Show("Rückgängig gemacht");
        }
    }

    /// <summary>Was beim Ablegen an dieser Mausposition geschieht: Mitte, ein Kachelplatz, der Sammelplatz oder nichts.</summary>
    private enum DropKind { None, Center, Slot, Overflow }

    /// <summary>
    /// Zone unter dem Mauszeiger. Die Mitte geht vor den Plätzen. Wird beim Ablegen (SnapWindow)
    /// und für die Zielvorschau beim Ziehen benutzt, damit beide immer dasselbe zeigen.
    /// </summary>
    private (DropKind Kind, int Slot) ZoneAt(NativePoint pt)
    {
        if (Contains(DockLayout.Compute(_config).Center, pt))
        {
            return (DropKind.Center, -1);
        }

        int slot = Array.FindIndex(DockLayout.SlotRects(_config), r => Contains(r, pt));
        if (slot < 0)
        {
            return (DropKind.None, -1);
        }
        return (slot == OverflowSlot ? DropKind.Overflow : DropKind.Slot, slot);
    }

    /// <summary>Ein Fenster wird gezogen: bei einer verwalteten App läuft die Zielvorschau, sonst nichts.</summary>
    private void OnDragStart(IntPtr hwnd)
    {
        EndDrag();
        if (!_enabled || _fullscreenPause)
        {
            return;
        }

        hwnd = Native.GetAncestor(hwnd, Native.GA_ROOT);
        int app = AppOfWindow(_groups, hwnd);
        if (app < 0 || IsIgnored(app) || _blocked.Contains(hwnd) || WindowFinder.IsFullscreen(hwnd)
            || !Native.GetWindowRect(hwnd, out NativeRect rect))
        {
            return;
        }

        _dragHwnd = hwnd;
        _dragApp = app;
        _dragWidth = rect.Right - rect.Left;
        _dragHeight = rect.Bottom - rect.Top;
        _dragShown = (DropKind.None, -1, false);
        _dragTimer.Start();
    }

    /// <summary>Das Ziehen ist zu Ende (losgelassen oder mit Esc abgebrochen): Vorschau weg, bevor eingerastet wird.</summary>
    private void OnDragEnd() => EndDrag();

    private void EndDrag()
    {
        _dragTimer.Stop();
        _dragHwnd = IntPtr.Zero;
        _dragApp = -1;
        _dragShown = (DropKind.None, -1, false);
        _highlight?.HideZone();
    }

    private void DragTick()
    {
        if (_dragHwnd == IntPtr.Zero || !_enabled || _fullscreenPause || !Native.GetWindowRect(_dragHwnd, out NativeRect rect))
        {
            EndDrag();
            return;
        }

        // Ändert sich die Größe, wird das Fenster in der Größe verändert und nicht verschoben: keine Vorschau
        if (Math.Abs(rect.Right - rect.Left - _dragWidth) > 2 || Math.Abs(rect.Bottom - rect.Top - _dragHeight) > 2)
        {
            EndDrag();
            return;
        }

        Native.GetCursorPos(out NativePoint pt);
        (DropKind kind, int slot) = ZoneAt(pt);
        bool free = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
        if ((kind, slot, free) == _dragShown)
        {
            return;
        }

        _dragShown = (kind, slot, free);
        if (kind == DropKind.None)
        {
            _highlight?.HideZone();
            return;
        }

        NativeRect zone = kind == DropKind.Center ? DockLayout.Compute(_config).Center : DockLayout.SlotRects(_config)[slot];
        _highlight ??= new ZoneHighlight();
        (string title, string detail) = DropTexts(kind, slot, free);
        _highlight.ShowZone(zone, title, detail, free);
    }

    /// <summary>Beschriftung der Zielvorschau. Sie nennt dieselben Folgen wie die Meldung nach dem Ablegen (SnapWindow).</summary>
    private (string Title, string Detail) DropTexts(DropKind kind, int slot, bool free)
    {
        if (free)
        {
            return ("Frei ablegen", "Nichts wird gespeichert");
        }

        string app = AppName(_dragApp);
        switch (kind)
        {
            case DropKind.Center:
                return ("Mitte", $"{app} wird das Mitte-Fenster");
            case DropKind.Overflow:
                return ("Sammelplatz", $"{app} bekommt keinen festen Platz");
        }

        string detail = $"{app} bekommt diesen Platz fest";
        int other = _slots[slot];
        if (other >= 0 && other != _dragApp)
        {
            // Wie MoveAppToSlot: die verdrängte App tauscht auf den alten Platz der gezogenen App, hatte diese keinen, verliert sie ihren
            int old = Array.IndexOf(_slots, _dragApp);
            detail += old >= 0
                ? $"\n{AppName(other)} tauscht auf {DockLayout.SlotLabel(_config, old)}"
                : $"\n{AppName(other)} verliert den Platz";
        }
        return ($"Platz {DockLayout.SlotLabel(_config, slot)}", detail);
    }

    /// <summary>
    /// Fenster per Ziehen abgelegt (Mauszeiger beim Loslassen): über der Mitte wird es das Mitte-Fenster,
    /// über einem Platz bekommt seine App diesen Platz fest, über dem Sammelplatz wandert die App dorthin.
    /// Woanders abgelegt bleibt es, wo es ist. Mit gedrückter Shift-Taste beim Loslassen rastet nichts ein:
    /// das Fenster bleibt liegen und die config.json unverändert.
    /// </summary>
    private void OnWindowMoved(IntPtr hwnd)
    {
        if (!_enabled || _fullscreenPause || (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0)
        {
            return;
        }

        hwnd = Native.GetAncestor(hwnd, Native.GA_ROOT);
        Native.GetCursorPos(out NativePoint pt);
        Dispatcher.BeginInvoke(new Action(() => SnapWindow(hwnd, pt)));
    }

    private void SnapWindow(IntPtr hwnd, NativePoint pt)
    {
        // Nur den Fensterstand lesen, noch nichts platzieren: sonst springt das Fenster erst auf den alten Platz
        AddDiscoveredApps();
        _groups = WindowFinder.Collect(_config.Apps);
        int app = AppOfWindow(_groups, hwnd);
        if (app < 0 || IsIgnored(app) || _blocked.Contains(hwnd))
        {
            return;
        }

        // Dieselbe Zonenlogik wie die Zielvorschau beim Ziehen
        (DropKind kind, int slot) = ZoneAt(pt);
        if (kind == DropKind.None)
        {
            return;
        }

        if (kind == DropKind.Center)
        {
            if (hwnd == _centerHwnd)
            {
                WindowPlacer.Place(hwnd, DockLayout.Compute(_config).Center);
                return;
            }

            SnapUndo toCenter = CaptureUndo();
            if (MoveToCenter(hwnd))
            {
                Toast.Show($"{AppName(app)} → Mitte", "Rückgängig", () => UndoSnap(toCenter));
            }
            return;
        }

        NativeRect[] rects = DockLayout.SlotRects(_config);

        // Erst platzieren: lässt sich das Fenster nicht verschieben (Administrator), bleibt die Belegung unverändert
        if (!WindowPlacer.PlaceInSlot(hwnd, rects[slot], Native.HWND_TOP, front: true))
        {
            NoteIfBlocked(hwnd);
            Refresh();
            return;
        }

        SnapUndo undo = CaptureUndo();
        bool overflow = kind == DropKind.Overflow;
        int displaced = overflow ? -1 : _slots[slot];
        if (hwnd == _centerHwnd)
        {
            _centerHwnd = IntPtr.Zero;
            _centerApp = -1;
        }
        MoveAppToSlot(app, slot);
        Array.Fill(_slotSignature, null);
        bool saved = SaveConfig(out bool notified);
        Refresh();
        if (notified)
        {
            return;
        }

        string text = overflow
            ? $"{AppName(app)} → Sammelplatz"
            : $"{AppName(app)} → Platz {DockLayout.SlotLabel(_config, slot)}{(saved ? ", fest gespeichert" : "")}";
        if (displaced >= 0 && displaced != app)
        {
            int moved = Array.IndexOf(_slots, displaced);
            text += moved >= 0
                ? $" · {AppName(displaced)} → {DockLayout.SlotLabel(_config, moved)}"
                : $" · {AppName(displaced)} ohne Platz";
        }
        Toast.Show(text, "Rückgängig", () => UndoSnap(undo));
    }

    /// <summary>
    /// App auf einen Platz setzen und die Zelle als ihren festen Platz merken. Die verdrängte App tauscht
    /// auf den alten Platz. Der Sammelplatz ist kein fester Platz: dort wird die App nur als InCorner markiert.
    /// </summary>
    private void MoveAppToSlot(int app, int slot)
    {
        AppEntry entry = _config.Apps[app];
        int old = Array.IndexOf(_slots, app);
        foreach (CellEntry cell in _config.Cells.Where(c => c.App == entry.Name))
        {
            cell.App = null;
        }

        if (slot == OverflowSlot)
        {
            if (old >= 0)
            {
                _slots[old] = -1;
            }
            entry.InCorner = true;
            return;
        }

        entry.InCorner = false;
        int displaced = _slots[slot];
        string slotHome = _config.Cells[slot].App;
        _slots[slot] = app;
        _config.Cells[slot].App = entry.Name;
        if (old >= 0 && old != slot)
        {
            _slots[old] = displaced;
            // Hatte die verdrängte App hier ihren festen Platz, zieht er mit auf den alten Platz
            if (displaced >= 0 && slotHome == _config.Apps[displaced].Name)
            {
                _config.Cells[old].App = slotHome;
            }
        }
    }

    /// <summary>Fenster aus dem Kachelplatz in die Mitte. Das bisherige Mitte-Fenster übernimmt den Platz.</summary>
    private void SwapToCenter(IntPtr hwnd, int slot)
    {
        if (slot == OverflowSlot)
        {
            MoveToCenter(hwnd);
            return;
        }

        int app = _slots[slot];
        if (app < 0 || hwnd == _centerHwnd || _blocked.Contains(hwnd))
        {
            return;
        }

        // Zuerst das neue Mitte-Fenster setzen: lässt es sich nicht verschieben (Administrator),
        // bleiben Plätze und bisheriges Mitte-Fenster unverändert.
        if (!WindowPlacer.Place(hwnd, DockLayout.Compute(_config).Center))
        {
            NoteIfBlocked(hwnd);
            Refresh();
            return;
        }

        NativeRect slotRect = DockLayout.SlotRects(_config)[slot];
        IntPtr previousWindow = _centerHwnd;
        int previousApp = _centerApp;

        // Hat die App noch weitere (verschiebbare) Fenster, behält sie ihren Platz außen.
        bool keepsSlot = _groups[app].Any(w => w.Hwnd != hwnd && !_blocked.Contains(w.Hwnd));
        _slotSignature[slot] = null;
        if (keepsSlot)
        {
            // Das bisherige Mitte-Fenster liegt hinter dem neuen; seine App wartet auf eine Lücke
            if (previousWindow != IntPtr.Zero)
            {
                WindowPlacer.PlaceBehind(previousWindow, hwnd);
            }
        }
        else
        {
            // Tausch: die bisherige Mitte-App rückt auf den freigewordenen Platz (sofern sie nicht schon einen hat)
            _slots[slot] = Array.IndexOf(_slots, previousApp) >= 0 ? -1 : previousApp;
            if (previousWindow != IntPtr.Zero)
            {
                WindowPlacer.PlaceInSlot(previousWindow, slotRect, Native.HWND_TOP, front: true);
            }
        }

        _centerHwnd = hwnd;
        _centerApp = app;
        Refresh();
    }

    /// <summary>Fensterliste eines Kachelplatzes, über dem Platz. Beim Sammelplatz mit Gruppenzeilen je App.</summary>
    private void ShowList(int slot)
    {
        bool overflow = slot == OverflowSlot;
        List<WindowInfo> windows = overflow ? OverflowListWindows() : WindowsOfSlot(slot);
        if (windows.Count == 0)
        {
            return;
        }
        if (_listSlot == slot && _list.IsVisible)
        {
            // Zweiter Druck auf die Vorwärts-Taste: Liste wieder zu
            HideList();
            return;
        }

        DockLayout layout = DockLayout.Compute(_config);
        bool right = _config.Cells[slot].Side == "right";
        NativeRect slotRect = DockLayout.SlotRects(_config)[slot];

        var items = windows.Select(w => (w, AppName(AppOfWindow(_groups, w.Hwnd)))).ToList();
        // Vorne: das oberste Fenster, das ein Platz zeigen würde (Z-Reihenfolge; Vollbildfenster werden nie platziert)
        IEnumerable<WindowInfo> byZ = overflow ? windows.OrderBy(w => w.Z) : windows;
        IntPtr front = byZ.FirstOrDefault(w => !w.Fullscreen)?.Hwnd ?? IntPtr.Zero;
        string header = overflow
            ? $"Sammelplatz · {WindowCount(windows.Count)}"
            : $"{DockLayout.SlotLabel(_config, slot)} · {AppName(_slots[slot])} · {WindowCount(windows.Count)}";

        _listSlot = slot;
        _list.ShowFor(items, slotRect, right ? layout.Right : layout.Left, front, header, OnListPick, OnListCenter, HideList);
        _listRect = _list.PixelRect;

        // Esc schließt die Liste; das Kürzel ist nur belegt, solange sie offen ist. Schlägt es fehl, wird das ignoriert.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (_escRegistered)
        {
            Native.UnregisterHotKey(hwnd, ListEscHotkeyId);
        }
        _escRegistered = Native.RegisterHotKey(hwnd, ListEscHotkeyId, Native.MOD_NOREPEAT, Native.VK_ESCAPE);
    }

    private string AppName(int app) =>
        app >= 0 && app < _config.Apps.Count ? _config.Apps[app].Name : "";

    private static string WindowCount(int n) => n == 1 ? "1 Fenster" : $"{n} Fenster";

    /// <summary>Aus der Fensterliste gewählt: das Fenster wird das oberste im Kachelplatz.</summary>
    private void OnListPick(IntPtr hwnd)
    {
        int slot = _listSlot;
        HideList();
        if (slot < 0)
        {
            return;
        }
        if (!Native.IsWindow(hwnd))
        {
            // Das Fenster wurde geschlossen, während die Liste offen war
            Refresh();
            return;
        }

        if (!WindowPlacer.PlaceInSlot(hwnd, DockLayout.SlotRects(_config)[slot], Native.HWND_TOP, front: true))
        {
            // Nicht verschiebbar (Administrator): nichts aktivieren, das Fenster fällt aus der Platzliste
            NoteIfBlocked(hwnd);
            Refresh();
            return;
        }

        WindowPlacer.Activate(hwnd);
        _slotSignature[slot] = null;
        Refresh();
    }

    /// <summary>Zurück-Taste auf einer Kachel der Fensterliste: das Fenster kommt in die Mitte.</summary>
    private void OnListCenter(IntPtr hwnd)
    {
        HideList();
        if (!Native.IsWindow(hwnd))
        {
            Refresh();
            return;
        }
        MoveToCenter(hwnd);
    }

    private void HideList()
    {
        _list.HideList();
        _listSlot = -1;
        _leaveSince = null;
        if (_escRegistered)
        {
            Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, ListEscHotkeyId);
            _escRegistered = false;
        }
    }

    /// <summary>Mausfilter: Linksklick oder Rechtsklick irgendwo. Bei offener Liste schließt ein Klick außerhalb sie sofort.</summary>
    private void OnMouseClick(NativePoint pt)
    {
        if (_listSlot >= 0 && !Contains(_listRect, pt))
        {
            Dispatcher.BeginInvoke(new Action(HideList));
        }
    }

    /// <summary>Fensterliste ausblenden, sobald die Maus sie eine Weile verlassen hat.</summary>
    private void Poll()
    {
        if (_listSlot < 0)
        {
            return;
        }

        if (IsCursorOver(_list))
        {
            _leaveSince = null;
            return;
        }

        _leaveSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _leaveSince.Value >= TimeSpan.FromMilliseconds(_config.HideDelayMs))
        {
            HideList();
        }
    }

    private static bool Contains(NativeRect rect, NativePoint p) =>
        p.X >= rect.Left && p.X < rect.Right && p.Y >= rect.Top && p.Y < rect.Bottom;

    private static int AppOfWindow(List<WindowInfo>[] groups, IntPtr hwnd)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            if (groups[i].Any(w => w.Hwnd == hwnd))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool IsCursorOver(Window window)
    {
        if (!window.IsVisible)
        {
            return false;
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetCursorPos(out NativePoint p) || !Native.GetWindowRect(hwnd, out NativeRect r))
        {
            return false;
        }
        return Contains(r, p);
    }

    /// <summary>
    /// Liest „Ctrl+Alt+F“ o. ä. Verlangt mindestens einen Modifier: eine einzelne Taste würde global belegt.
    /// Eine einzelne Ziffer ist die Ziffer selbst (Key.D0 bis D9), nicht der Zahlenwert der Key-Aufzählung.
    /// </summary>
    private static bool TryParseHotkey(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        string[] parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "strg":
                    modifiers |= Native.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= Native.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= Native.MOD_SHIFT;
                    break;
                case "win":
                    modifiers |= Native.MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        Key wpfKey;
        if (int.TryParse(parts[^1], out int number))
        {
            // Zahlen vor Enum.TryParse abfangen: „1“ wäre sonst Key 1 (Cancel) und „65“ ein beliebiger Key-Wert
            if (parts[^1].Length != 1 || number < 0 || number > 9)
            {
                return false;
            }
            wpfKey = (Key)((int)Key.D0 + number);
        }
        else if (!Enum.TryParse(parts[^1], true, out wpfKey) || !Enum.IsDefined(wpfKey))
        {
            return false;
        }

        key = (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
        return modifiers != 0 && key != 0;
    }
}
