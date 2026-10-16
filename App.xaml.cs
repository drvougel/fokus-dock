using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace FokusDock;

public partial class App : Application
{
    private const string MutexName = @"Local\FokusDock.SingleInstance";
    private Mutex _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Muss vor dem ersten Fenster gesetzt werden, damit alle Koordinaten in echten Pixeln stimmen.
        Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessPerMonitorV2);

        _mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            MessageBox.Show("Fokus-Dock läuft bereits.", "Fokus-Dock", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        AppConfig config;
        try
        {
            config = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"config.json konnte nicht gelesen werden:\n{ex.Message}\n\nEs werden die Standardwerte verwendet.",
                "Fokus-Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
            config = AppConfig.CreateDefault();
        }

        new DockWindow(config).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
