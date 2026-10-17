using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FokusDock;

public partial class App : Application
{
    private const string MutexName = @"Local\FokusDock.SingleInstance";
    private const string ShowEventName = @"Local\FokusDock.Show";   // zweite Starts melden sich hier bei der laufenden Instanz
    private const string RestartArg = "--neustart";
    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "fokusdock.log");

    /// <summary>Gesetzt vom Menü „Neu starten“: OnExit startet nach der Freigabe des Mutex eine neue Instanz.</summary>
    internal static bool RestartRequested { get; set; }

    private Mutex _mutex;
    private bool _ownsMutex;        // nur die erste Instanz besitzt den Mutex und darf ihn freigeben
    private EventWaitHandle _showEvent;
    private RegisteredWaitHandle _showWait;
    private DockWindow _dock;
    private DateTime _lastErrorToastAt = DateTime.MinValue; // der Fehlerhinweis erscheint höchstens einmal pro Minute
    private string _lastErrorText;  // derselbe Fehler im Takt der Timer wird nur einmal pro Minute protokolliert
    private DateTime _lastErrorAt;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Muss vor dem ersten Fenster gesetzt werden, damit alle Koordinaten in echten Pixeln stimmen.
        Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessPerMonitorV2);

        _mutex = new Mutex(true, MutexName, out bool created);
        if (!created && e.Args.Contains(RestartArg))
        {
            // Neustart: die alte Instanz gibt den Mutex beim Beenden frei, darauf kurz warten
            try
            {
                created = _mutex.WaitOne(5000);
            }
            catch (AbandonedMutexException)
            {
                // Die alte Instanz ist ohne Freigabe verschwunden: der Mutex gehört damit uns
                created = true;
            }
        }
        _ownsMutex = created;
        if (!created)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        ListenForSecondStart();
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        AppConfig config;
        try
        {
            config = AppConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            if (!TryUseDefaultsForBadConfig(ex, configPath, out config))
            {
                Shutdown();
                return;
            }
        }

        // Ohne Shutdown() bliebe bei einem Fehler hier ein Prozess ohne Fenster zurück (OnExplicitShutdown)
        try
        {
            _dock = new DockWindow(config);
            _dock.Show();
        }
        catch (Exception ex)
        {
            WriteLog(ex.ToString());
            MessageBox.Show($"Fokus-Dock konnte nicht starten: {ex.Message}", "Fokus-Dock", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Zweiter Start: die laufende Instanz öffnet die Prioritätsliste, hier gibt es keine Meldung.
    /// Fehlt ihr Signal (alter Stand oder hängt), bleibt nur der Hinweis auf den Task-Manager.
    /// </summary>
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle running))
            {
                using (running)
                {
                    // Der Start kam vom Nutzer: dieser Prozess darf das Vordergrundrecht weitergeben,
                    // sonst bliebe die Liste hinter anderen Fenstern (es gibt keinen Taskleistenknopf, der blinken könnte)
                    Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                    running.Set();
                }
                return;
            }
        }
        catch (Exception ex)
        {
            WriteLog(ex.ToString());
        }

        MessageBox.Show("Fokus-Dock läuft bereits, reagiert aber nicht. Beende FokusDock.exe im Task-Manager und starte es neu.",
            "Fokus-Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Erste Instanz: auf das Signal eines zweiten Starts warten und dann die Prioritätsliste öffnen.</summary>
    private void ListenForSecondStart()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(() => _dock?.OpenPriority())),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            // Ohne das Signal läuft Fokus-Dock normal weiter; ein zweiter Start zeigt dann den Hinweis
            WriteLog(ex.ToString());
        }
    }

    /// <summary>
    /// config.json ist defekt oder nicht lesbar: Kopie sichern, mit Standardwerten weitermachen und das Speichern
    /// sperren, damit die Datei unangetastet bleibt. Liefert false, wenn der Nutzer Fokus-Dock beenden will.
    /// </summary>
    private static bool TryUseDefaultsForBadConfig(Exception ex, string configPath, out AppConfig config)
    {
        config = AppConfig.CreateDefault();
        config.SaveDisabled = true;
        config.FileExisted = true;   // die Datei ist da, nur defekt: keine Erste-Start-Hilfe

        string backupName = $"config.defekt-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        try
        {
            File.Copy(configPath, Path.Combine(AppContext.BaseDirectory, backupName), overwrite: true);
        }
        catch (Exception)
        {
            backupName = null;
        }

        string head;
        if (ex is JsonException { LineNumber: not null, BytePositionInLine: not null } json)
        {
            head = $"config.json enthält einen Fehler in Zeile {json.LineNumber.Value + 1}, Spalte {json.BytePositionInLine.Value + 1}.";
        }
        else if (ex is JsonException)
        {
            head = "config.json enthält einen Fehler.";
        }
        else
        {
            head = "config.json konnte nicht gelesen werden.";
        }

        // Die Position steht schon oben (ab 1 gezählt): die nullbasierte Angabe aus der Meldung würde nur verwirren
        string reason = Regex.Replace(ex.Message, @"\s*(\|\s*)?LineNumber: \d+\s*\|\s*BytePositionInLine: \d+\.?", "").Trim();
        string copy = backupName != null ? $"Eine Kopie liegt unter {backupName}." : "Eine Kopie konnte nicht angelegt werden.";

        MessageBoxResult answer = MessageBox.Show(
            $"{head}\n\n{reason}\n\nFokus-Dock startet mit Standardwerten und ändert die Datei nicht. {copy}\n\n" +
            "Ja: Datei jetzt öffnen\nNein: Mit Standardwerten weiter\nAbbrechen: Fokus-Dock beenden",
            "Fokus-Dock", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{configPath}\""))?.Dispose();
            }
            catch (Exception)
            {
                // Notepad nicht startbar: Fokus-Dock läuft trotzdem mit den Standardwerten weiter
            }
        }
        return true;
    }

    /// <summary>Fehler im Dispatcher (Timer, Fensternachrichten): protokollieren und weiterlaufen, statt still zu beenden.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        string text = e.Exception.ToString();
        if (text != _lastErrorText || DateTime.Now - _lastErrorAt > TimeSpan.FromMinutes(1))
        {
            _lastErrorText = text;
            _lastErrorAt = DateTime.Now;
            WriteLog(text);
        }

        // Höchstens einmal pro Minute und ohne Dialog: ein Fehler im Takt der Timer würde sonst jede Sekunde stören
        if (DateTime.Now - _lastErrorToastAt >= TimeSpan.FromMinutes(1))
        {
            _lastErrorToastAt = DateTime.Now;
            try
            {
                Toast.Show("Ein Fehler ist aufgetreten, Details in fokusdock.log");
            }
            catch (Exception ex)
            {
                // Die Einblendung selbst scheitert: nur ins Protokoll, kein weiterer Fehler aus dem Fehlerhandler
                WriteLog(ex.ToString());
            }
        }
    }

    /// <summary>Hängt Zeitstempel und Text an fokusdock.log neben der EXE an. Ein Fehler beim Schreiben wird verschluckt.</summary>
    internal static void WriteLog(string text)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Log nicht beschreibbar: die Meldung am Bildschirm bleibt
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();

        // Eine zweite Instanz besitzt den Mutex nicht: ReleaseMutex würde dort eine Ausnahme werfen
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // gehört einem anderen Thread: beim Beenden unbedeutend
            }
        }
        _mutex?.Dispose();

        // Erst nach der Freigabe des Mutex: die neue Instanz wartet mit --neustart kurz darauf
        if (RestartRequested)
        {
            StartNewInstance();
        }
        base.OnExit(e);
    }

    private static void StartNewInstance()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, RestartArg))?.Dispose();
        }
        catch (Exception ex)
        {
            WriteLog(ex.ToString());
            MessageBox.Show($"Fokus-Dock konnte nicht neu gestartet werden: {ex.Message}", "Fokus-Dock", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
