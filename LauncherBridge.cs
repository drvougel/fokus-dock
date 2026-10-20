using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FokusDock;

/// <summary>
/// Schnittstelle für den Launcher (eigenes Programm): ein benannter Pipe-Server, nur für den angemeldeten Benutzer.
/// Der Launcher schickt Textzeilen, jede wird im UI-Thread behandelt und mit einer Zeile beantwortet. So kann er ein Programm
/// aus seiner Leiste direkt auf einen Platz ziehen: Vorschau der Zielzone beim Ziehen (hover), Einordnen beim Loslassen (place).
/// Zeilenformat: Befehl|Wert|Wert|...
/// </summary>
internal sealed class LauncherBridge : IDisposable
{
    public const string PipeName = "FokusDock.Launcher";

    private readonly Dispatcher _dispatcher;
    private readonly Func<string, string> _handle;
    private readonly CancellationTokenSource _cts = new();

    /// <param name="handle">Behandelt eine Zeile und liefert die Antwort. Läuft im UI-Thread.</param>
    public LauncherBridge(Dispatcher dispatcher, Func<string, string> handle)
    {
        _dispatcher = dispatcher;
        _handle = handle;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                _ = Task.Run(() => Serve(server));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Pipe konnte nicht angelegt werden (z. B. Name belegt): ins Protokoll, nach kurzer Pause erneut
                App.WriteLog("Launcher-Pipe: " + ex.Message);
                try
                {
                    await Task.Delay(2000, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task Serve(NamedPipeServerStream server)
    {
        try
        {
            using (server)
            using (var reader = new StreamReader(server, Encoding.UTF8))
            using (var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true })
            {
                string line;
                while (!_cts.IsCancellationRequested && (line = await reader.ReadLineAsync(_cts.Token)) != null)
                {
                    string reply;
                    try
                    {
                        reply = await _dispatcher.InvokeAsync(() => _handle(line)).Task;
                    }
                    catch (Exception ex)
                    {
                        App.WriteLog("Launcher-Befehl: " + ex.Message);
                        reply = "error";
                    }
                    await writer.WriteLineAsync(reply ?? "ok");
                }
            }
        }
        catch (Exception)
        {
            // Der Launcher hat aufgelegt oder wird beendet: nichts zu tun
        }
    }

    public void Dispose() => _cts.Cancel();
}
