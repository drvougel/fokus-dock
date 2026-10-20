using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FokusDock;

/// <summary>
/// Symbol im Infobereich der Taskleiste: zeigt, ob Fokus-Dock an ist (farbig) oder aus (grau), und bietet alle
/// Funktionen als Menü an, auch wenn ein Tastenkürzel nicht greift. Linksklick öffnet die Prioritätsliste.
/// In dieser Datei sind nur WinForms und System.Drawing eingebunden; WPF-Typen stehen mit vollem Namen
/// (System.Windows.Application, System.Windows.MessageBox), damit nichts mehrdeutig wird.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FokusDock";

    private readonly DockWindow _dock;
    private readonly AppConfig _config;
    private readonly Icon _colorIcon;
    private readonly Icon _grayIcon;
    private readonly IntPtr _grayHandle;   // GetHicon-Handle des grauen Symbols: gehört uns und wird in Dispose freigegeben
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _priorityItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _layoutsItem;
    private readonly ToolStripMenuItem _splitItem;
    private readonly ToolStripMenuItem _monitorItem;
    private readonly NotifyIcon _notify;
    private bool _disposed;

    public TrayIcon(DockWindow dock, AppConfig config)
    {
        _dock = dock;
        _config = config;

        using (Stream stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream)
        {
            _colorIcon = new Icon(stream, SystemInformation.SmallIconSize);
        }
        _grayIcon = MakeGray(_colorIcon, out _grayHandle);

        _menu = new ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            BackColor = DarkColors.Background,
            ForeColor = Color.White,
            ShowItemToolTips = false,
        };

        _enabledItem = new ToolStripMenuItem("Spalten aktiv");
        _enabledItem.Click += (_, _) => Run(_dock.ToggleEnabled);

        _priorityItem = new ToolStripMenuItem("Prioritätsliste…");
        _priorityItem.Click += (_, _) => Run(_dock.OpenPriority);

        _splitItem = new ToolStripMenuItem("Mitte teilen");
        _splitItem.Click += (_, _) => Run(_dock.CycleCenterSplit);

        var layoutItem = new ToolStripMenuItem("Layout…");
        layoutItem.Click += (_, _) => Run(_dock.OpenLayout);

        _layoutsItem = new ToolStripMenuItem("Layouts");
        _monitorItem = new ToolStripMenuItem("Monitor");

        var helpItem = new ToolStripMenuItem("Bedienung…");
        helpItem.Click += (_, _) => Run(_dock.OpenHelp);

        var configItem = new ToolStripMenuItem("config.json öffnen");
        configItem.Click += (_, _) => Run(() => OpenConfig(_config));

        var restartItem = new ToolStripMenuItem("Neu starten");
        restartItem.Click += (_, _) => Run(Restart);

        _autostartItem = new ToolStripMenuItem("Mit Windows starten");
        _autostartItem.Click += (_, _) => Run(ToggleAutostart);

        _exitItem = new ToolStripMenuItem("Beenden");
        _exitItem.Click += (_, _) => Run(() => System.Windows.Application.Current.Shutdown());

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _enabledItem, _priorityItem, layoutItem, _layoutsItem, _splitItem, _monitorItem, helpItem,
            new ToolStripSeparator(),
            configItem, restartItem, _autostartItem,
            new ToolStripSeparator(),
            _exitItem,
        });
        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = Color.White;
        }
        _menu.Opening += (_, _) => RefreshMenu();
        RefreshMenu();

        _notify = new NotifyIcon
        {
            Icon = _colorIcon,
            Text = "Fokus-Dock – an",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Run(_dock.OpenPriority);
            }
        };
    }

    /// <summary>Zustand von Fokus-Dock übernehmen: farbiges oder graues Symbol, Hinweistext, Haken im Menü.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabledItem.Checked = enabled;
        _notify.Icon = enabled ? _colorIcon : _grayIcon;
        _notify.Text = enabled ? "Fokus-Dock – an" : "Fokus-Dock – aus";
    }

    /// <summary>
    /// Aktionen aus Menü und Klick laufen erst, wenn das Menü geschlossen ist. Ein Fehler geht so an den Handler
    /// der App (Protokoll) und nicht an WinForms, das in einem WPF-Programm eigene Fehlerfenster zeigen würde.
    /// </summary>
    private void Run(Action action) => _dock.Dispatcher.BeginInvoke(action);

    /// <summary>Kürzel (in Strg-Schreibweise, bei Fehlschlag mit Hinweis) und Haken beim Öffnen des Menüs frisch lesen.</summary>
    private void RefreshMenu()
    {
        _enabledItem.ShortcutKeyDisplayString = _dock.HotkeyLabel(DockWindow.ToggleHotkeyId);
        _priorityItem.ShortcutKeyDisplayString = _dock.HotkeyLabel(DockWindow.PriorityHotkeyId);
        _exitItem.ShortcutKeyDisplayString = _dock.HotkeyLabel(DockWindow.ExitHotkeyId);

        _autostartItem.Enabled = AutostartCommand() != null;
        _autostartItem.Checked = IsAutostart();
        RebuildLayouts();
        RebuildMonitors();
    }

    /// <summary>Untermenü der gespeicherten Layouts: anwenden, aktuelles speichern, löschen.</summary>
    private void RebuildLayouts()
    {
        ToolStripMenuItem Item(string text, Action action)
        {
            var item = new ToolStripMenuItem(text) { ForeColor = Color.White };
            item.Click += (_, _) => Run(action);
            return item;
        }

        _layoutsItem.DropDown.Renderer = _menu.Renderer;
        _layoutsItem.DropDownItems.Clear();
        List<string> names = _config.Layouts.Select(l => l.Name).ToList();
        foreach (string name in names)
        {
            string captured = name;
            _layoutsItem.DropDownItems.Add(Item(captured, () => _dock.ApplyPreset(captured)));
        }
        if (names.Count > 0)
        {
            _layoutsItem.DropDownItems.Add(new ToolStripSeparator());
        }

        ToolStripMenuItem save = Item("Aktuelles Layout speichern…", _dock.SaveLayoutPreset);
        _layoutsItem.DropDownItems.Add(save);
        foreach (string name in names)
        {
            string captured = name;
            _layoutsItem.DropDownItems.Add(Item("Löschen: " + captured, () => _dock.DeletePreset(captured)));
        }
        _layoutsItem.ShortcutKeyDisplayString = _dock.HotkeyLabel(DockWindow.LayoutHotkeyId);
    }

    /// <summary>Untermenü der Monitore: Primärmonitor oder ein bestimmter Bildschirm.</summary>
    private void RebuildMonitors()
    {
        _monitorItem.DropDown.Renderer = _menu.Renderer;
        _monitorItem.DropDownItems.Clear();
        string[] screens = Monitors.Describe();
        for (int i = 0; i <= screens.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(i == 0 ? "Primärmonitor" : screens[i - 1])
            {
                ForeColor = Color.White,
                Checked = _config.Monitor == i,
            };
            item.Click += (_, _) => Run(() => _dock.SetMonitor(index));
            _monitorItem.DropDownItems.Add(item);
        }
    }

    /// <summary>config.json im zugeordneten Programm öffnen, sonst im Editor. Fehlt die Datei noch, wird sie zuerst geschrieben.</summary>
    internal static void OpenConfig(AppConfig config)
    {
        string path = DockWindow.ConfigPath;
        if (!File.Exists(path))
        {
            config.Save(path);
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // Für .json ist kein Programm hinterlegt
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\""))?.Dispose();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"config.json konnte nicht geöffnet werden: {ex.Message}", "Fokus-Dock",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>Beendet diese Instanz; App.OnExit startet nach der Freigabe des Mutex eine neue.</summary>
    private static void Restart()
    {
        App.RestartRequested = true;
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Startbefehl für den Autostart: der Pfad dieser EXE in Anführungszeichen. Null, wenn Fokus-Dock nicht als eigene EXE läuft.</summary>
    private static string AutostartCommand()
    {
        string path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) || string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return $"\"{path}\"";
    }

    /// <summary>Ob der Run-Wert „FokusDock“ gesetzt ist und genau auf diese EXE zeigt (nach einem Umzug des Ordners ist er es nicht mehr).</summary>
    private static bool IsAutostart()
    {
        string command = AutostartCommand();
        if (command == null)
        {
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value && string.Equals(value, command, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Schlüssel nicht lesbar: dann gilt der Autostart als aus
            return false;
        }
    }

    private static void ToggleAutostart()
    {
        string command = AutostartCommand();
        if (command == null)
        {
            return;
        }

        try
        {
            if (IsAutostart())
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            else
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(RunValueName, command, RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Der Autostart konnte nicht geändert werden: {ex.Message}", "Fokus-Dock",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Graue, halb durchsichtige Fassung des Symbols: Graustufen-Farbmatrix mit Alpha 0,6.
    /// Das Handle gehört dem Aufrufer (DestroyIcon), Icon.FromHandle gibt es nicht frei.
    /// </summary>
    private static Icon MakeGray(Icon source, out IntPtr handle)
    {
        using Bitmap original = source.ToBitmap();
        using var gray = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(gray))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetColorMatrix(new ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0f, 0f },
                new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                new[] { 0f, 0f, 0f, 0.6f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            }));
            g.DrawImage(original, new Rectangle(0, 0, gray.Width, gray.Height), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel, attributes);
        }

        handle = gray.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Zuerst das Symbol entfernen: ein Geistersymbol bliebe sonst, bis die Maus darüberfährt
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        _colorIcon.Dispose();
        _grayIcon.Dispose();
        Native.DestroyIcon(_grayHandle);
    }

    /// <summary>Farben des dunklen Menüs.</summary>
    private sealed class DarkColors : ProfessionalColorTable
    {
        public static readonly Color Background = Color.FromArgb(0x2B, 0x2B, 0x2B);
        public static readonly Color Hover = Color.FromArgb(0x3A, 0x3A, 0x3A);
        public static readonly Color Border = Color.FromArgb(0x7A, 0x7A, 0x7A);
        public static readonly Color Separator = Color.FromArgb(0x5A, 0x5A, 0x5A);

        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Separator;
        public override Color SeparatorLight => Background;   // keine zweite, helle Linie neben dem Trenner
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }

    /// <summary>Zeichnet Text weiß (ausgegraut: grau) und den Haken selbst, weil der Standardhaken schwarz ist.</summary>
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.White : Color.FromArgb(0x8A, 0x8A, 0x8A);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = e.ImageRectangle;
            using (var fill = new SolidBrush(Color.FromArgb(0x4A, 0x4A, 0x4A)))
            using (var border = new Pen(DarkColors.Border))
            {
                g.FillRectangle(fill, r);
                g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);
            }

            SmoothingMode before = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.White, Math.Max(1.5f, r.Width / 10f)))
            {
                g.DrawLines(pen, new[]
                {
                    new PointF(r.X + r.Width * 0.25f, r.Y + r.Height * 0.52f),
                    new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.70f),
                    new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.30f),
                });
            }
            g.SmoothingMode = before;
        }
    }
}
