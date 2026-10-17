using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FokusDock;

public sealed class WindowInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; }
    /// <summary>Prozessname mit Endung, z. B. "Code.exe". Gruppierung erfolgt darüber.</summary>
    public string ProcessName { get; init; }
    /// <summary>Pfad zur EXE für das Icon. Null, wenn der Prozess nicht lesbar ist (z. B. Admin).</summary>
    public string ExePath { get; init; }
    /// <summary>Position in der Z-Reihenfolge beim letzten Einsammeln, 0 = oberstes Fenster.</summary>
    public int Z { get; set; }
    /// <summary>Das Fenster deckt seinen Monitor ganz ab und hat keine Titelleiste (Spiel, F11, Vollbildvideo). Solche Fenster werden nicht platziert.</summary>
    public bool Fullscreen { get; init; }
}

internal static class WindowFinder
{
    private sealed record ProcInfo(string Name, string Path);

    /// <summary>Suffix, den Brave an normale Tab-Fenster hängt. Web-App-Fenster haben ihn nicht.</summary>
    private const string BrowserSuffix = " - Brave";

    /// <summary>Fensterklassen der Shell: Desktop, Taskleisten. Werden nie als App-Fenster eingeordnet.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>
    /// Sammelt sichtbare Fenster und ordnet sie den Apps aus der Config zu (gleicher Index).
    /// Ein Fenster gehört zum ersten passenden Eintrag: Web-Apps stehen daher vor Brave.
    /// Jede Liste ist nach Z-Reihenfolge sortiert: das oberste (zuletzt aktive) Fenster steht vorne.
    /// </summary>
    public static List<WindowInfo>[] Collect(IReadOnlyList<AppEntry> apps)
    {
        var groups = new List<WindowInfo>[apps.Count];
        for (int i = 0; i < groups.Length; i++)
        {
            groups[i] = new List<WindowInfo>();
        }

        uint self = (uint)Environment.ProcessId;
        int z = 0;

        Native.EnumWindows((hwnd, _) =>
        {
            WindowInfo info = Describe(hwnd, self);
            if (info != null)
            {
                info.Z = z++;
                for (int i = 0; i < apps.Count; i++)
                {
                    if (Matches(apps[i], info))
                    {
                        groups[i].Add(info);
                        break;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        return groups;
    }

    /// <summary>
    /// Fenster von Prozessen, die noch keinem Config-Eintrag zugeordnet sind: ein Eintrag pro Prozessname,
    /// in Z-Reihenfolge (zuletzt aktiv zuerst). Daraus werden neue Apps angelegt.
    /// </summary>
    public static List<WindowInfo> FindUnmatched(IReadOnlyList<AppEntry> apps)
    {
        var result = new List<WindowInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Environment.ProcessId;

        Native.EnumWindows((hwnd, _) =>
        {
            WindowInfo info = Describe(hwnd, self);
            // Vollbildfenster (Spiele, F11, Videos) werden nie als neue App aufgenommen
            if (info != null && !info.Fullscreen && !apps.Any(app => Matches(app, info)) && seen.Add(info.ProcessName))
            {
                result.Add(info);
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Lesbarer App-Name: Dateibeschreibung der EXE, sonst Prozessname ohne Endung.</summary>
    public static string DisplayName(WindowInfo info)
    {
        try
        {
            if (info.ExePath != null)
            {
                string description = FileVersionInfo.GetVersionInfo(info.ExePath).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description))
                {
                    return description;
                }
            }
        }
        catch (Exception)
        {
            // Versionsinfo nicht lesbar: Prozessname genügt
        }
        return Path.GetFileNameWithoutExtension(info.ProcessName);
    }

    private static bool Matches(AppEntry app, WindowInfo info)
    {
        if (!app.Processes.Any(p => p.Equals(info.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (app.TitleStartsWith == null)
        {
            return true;
        }

        return info.Title.StartsWith(app.TitleStartsWith, StringComparison.OrdinalIgnoreCase)
               && !info.Title.EndsWith(BrowserSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static WindowInfo Describe(IntPtr hwnd, uint self)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.GetWindowTextLength(hwnd) == 0)
        {
            return null;
        }

        // Oberflächen der Shell (Desktop, Taskleiste) sind keine App-Fenster, auch wenn sie einen Titel haben
        var className = new StringBuilder(256);
        Native.GetClassName(hwnd, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString()))
        {
            return null;
        }

        // Hilfs- und Tool-Fenster ausblenden
        int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0)
        {
            return null;
        }

        // Wie die Taskleiste: besessene Fenster (Dialoge wie "Speichern unter") und Fenster, die sich nicht
        // aktivieren lassen, sind keine Hauptfenster. WS_EX_APPWINDOW erzwingt einen Taskleistenknopf und hebt das auf.
        if ((exStyle & Native.WS_EX_APPWINDOW) == 0
            && (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero || (exStyle & Native.WS_EX_NOACTIVATE) != 0))
        {
            return null;
        }

        // Auf anderen virtuellen Desktops oder unsichtbar "gecloakte" Fenster ignorieren
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null;
        }

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == self)
        {
            return null;
        }

        ProcInfo proc = ReadProcess(pid);
        if (proc == null)
        {
            return null;
        }

        // Store-/UWP-Apps laufen im Host ApplicationFrameHost; die eigentliche App ist ein Kindfenster.
        if (proc.Name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            ProcInfo child = FindUwpChild(hwnd, pid);
            if (child != null)
            {
                proc = child;
            }
        }

        int length = Native.GetWindowTextLength(hwnd);
        var title = new StringBuilder(length + 1);
        Native.GetWindowText(hwnd, title, title.Capacity);

        return new WindowInfo
        {
            Hwnd = hwnd,
            Title = title.ToString(),
            ProcessName = proc.Name,
            ExePath = proc.Path,
            Fullscreen = IsFullscreen(hwnd),
        };
    }

    /// <summary>
    /// Vollbild: das Fenster deckt seinen Monitor vollständig ab und hat keine Titelleiste.
    /// Ein maximiertes Fenster mit Titelleiste zählt nicht, auch wenn es bei ausgeblendeter Taskleiste den Monitor füllt.
    /// </summary>
    internal static bool IsFullscreen(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out NativeRect rect))
        {
            return false;
        }

        IntPtr monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        NativeRect screen = info.rcMonitor;
        bool coversMonitor = rect.Left <= screen.Left && rect.Top <= screen.Top
                             && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
        return coversMonitor && (Native.GetWindowLong(hwnd, Native.GWL_STYLE) & Native.WS_CAPTION) != Native.WS_CAPTION;
    }

    private static ProcInfo FindUwpChild(IntPtr frame, uint framePid)
    {
        ProcInfo result = null;
        Native.EnumChildWindows(frame, (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out uint childPid);
            if (childPid != 0 && childPid != framePid)
            {
                result = ReadProcess(childPid);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static ProcInfo ReadProcess(uint pid)
    {
        IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var path = new StringBuilder(1024);
                uint size = (uint)path.Capacity;
                if (Native.QueryFullProcessImageName(handle, 0, path, ref size))
                {
                    string full = path.ToString(0, (int)size);
                    return new ProcInfo(Path.GetFileName(full), full);
                }
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }

        // Fallback, z. B. wenn der Prozess nicht abfragbar ist: Name ohne Pfad
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new ProcInfo(process.ProcessName + ".exe", null);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
