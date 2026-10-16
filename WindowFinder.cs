using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
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

        Native.EnumWindows((hwnd, _) =>
        {
            WindowInfo info = Describe(hwnd, self);
            if (info != null)
            {
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
        if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0)
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
        };
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
