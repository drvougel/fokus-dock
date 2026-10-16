using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FokusDock;

/// <summary>
/// Unsichtbarer Träger für Tastenkürzel, Mausfilter und Timer. Setzt die echten Fenster in die
/// Kachelplätze der Seitenspalten und das Mitte-Fenster in die Mitte.
/// Strg+Linksklick auf ein Spaltenfenster holt es in die Mitte, Strg+Rechtsklick zeigt die Fenster der App.
/// </summary>
public partial class DockWindow : Window
{
    private const int ToggleHotkeyId = 1;
    private const int ExitHotkeyId = 2;
    private const int PriorityHotkeyId = 3;
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private readonly AppConfig _config;
    private readonly WindowListWindow _list = new();
    private readonly MouseHook _mouse;
    private readonly DragSnap _drag;
    private PriorityWindow _priority;
    private LayoutWindow _layout;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _refreshTimer;

    // App-Index je Kachelplatz (links von oben nach unten, dann rechts), -1 = frei.
    // Die Mitte-App steht nie in einem Kachelplatz.
    private int[] _slots;
    private string[] _slotSignature; // Fensterreihenfolge, zu der der Platz zuletzt eingerichtet wurde

    private List<WindowInfo>[] _groups;      // letzter Fensterstand je App
    private IntPtr _centerHwnd;              // echtes Fenster in der Mitte
    private int _centerApp = -1;             // App des Mitte-Fensters, -1 = keines
    private int _listSlot = -1;              // Kachelplatz, dessen Fensterliste offen ist
    private bool _initialized;
    private bool _enabled = true;            // Spalten ein/aus (Hotkey)
    private DateTime? _leaveSince;

    public DockWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        int slotCount = config.Cells.Count;
        _slots = Enumerable.Repeat(-1, slotCount).ToArray();
        _slotSignature = new string[slotCount];
        _groups = Enumerable.Range(0, config.Apps.Count).Select(_ => new List<WindowInfo>()).ToArray();

        _mouse = new MouseHook(OnSlotButton);
        _drag = new DragSnap(OnWindowMoved);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += (_, _) => Poll();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        _pollTimer.Start();
        _refreshTimer.Start();
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);

        RegisterHotkey(hwnd, ToggleHotkeyId, _config.Hotkey, "Tastenkürzel zum Ein/Aus");
        RegisterHotkey(hwnd, ExitHotkeyId, _config.ExitHotkey, "Beenden-Tastenkürzel");
        RegisterHotkey(hwnd, PriorityHotkeyId, _config.PriorityHotkey, "Prioritäts-Tastenkürzel");

        _mouse.Install();
        _drag.Install();
    }

    private static void RegisterHotkey(IntPtr hwnd, int id, string text, string purpose)
    {
        if (!TryParseHotkey(text, out uint modifiers, out uint key) ||
            !Native.RegisterHotKey(hwnd, id, modifiers | Native.MOD_NOREPEAT, key))
        {
            MessageBox.Show($"{purpose} \"{text}\" konnte nicht registriert werden (ungültig oder schon belegt).",
                "Fokus-Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _pollTimer.Stop();
        _refreshTimer.Stop();
        _mouse.Dispose();
        _drag.Dispose();
        HideList();
        _list.Close();
        _priority?.Close();
        _layout?.Close();
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
        }
        return IntPtr.Zero;
    }

    private void ToggleEnabled()
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
    }

    private void OpenPriority()
    {
        Refresh();
        _priority ??= new PriorityWindow(ApplyPriority, Resort, OpenLayout);
        _priority.Open(_config.Apps, _config.Apps.Select((_, i) => _groups[i].Count > 0).ToList());
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

        foreach (WindowInfo info in unmatched)
        {
            string name = WindowFinder.DisplayName(info);
            if (IndexOfApp(name) >= 0)
            {
                name = $"{name} ({Path.GetFileNameWithoutExtension(info.ProcessName)})";
            }
            _config.Apps.Add(new AppEntry { Name = name, Processes = new List<string> { info.ProcessName } });
        }

        _config.Save(ConfigPath);
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
            _slotSignature[s] = null;
        }
        _centerApp = IndexOfApp(centerName);

        if (save)
        {
            _config.Save(ConfigPath);
        }
        Refresh();
    }

    /// <summary>„Neu sortieren“: alle Plätze nach Priorität neu belegen (nur Apps mit Fenstern).</summary>
    private void Resort()
    {
        Refresh();
        AssignSlots(onlyRunning: true);
        Refresh();
    }

    private int IndexOfApp(string name) =>
        name == null ? -1 : _config.Apps.FindIndex(a => a.Name == name);

    /// <summary>Fensterstand lesen und die Kachelplätze und das Mitte-Fenster nachziehen.</summary>
    private void Refresh()
    {
        AddDiscoveredApps();
        List<WindowInfo>[] groups = WindowFinder.Collect(_config.Apps);
        _groups = groups;

        if (!_initialized)
        {
            _initialized = true;
            InitializeState(groups);
        }

        if (_centerHwnd != IntPtr.Zero)
        {
            _centerApp = AppOfWindow(groups, _centerHwnd);
            if (_centerApp < 0)
            {
                _centerHwnd = IntPtr.Zero;
            }
        }

        if (_enabled)
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
    }

    /// <summary>Start: Brave (falls laufend) in die Mitte, die übrigen Apps in der Reihenfolge der Config auf die Plätze.</summary>
    private void InitializeState(List<WindowInfo>[] groups)
    {
        int start = _config.Apps.FindIndex(a => a.Name.Equals(_config.StartInCenter, StringComparison.OrdinalIgnoreCase));
        if (start >= 0 && groups[start].Count > 0)
        {
            _centerApp = start;
            _centerHwnd = groups[start][0].Hwnd;
            WindowPlacer.Place(_centerHwnd, DockLayout.Compute(_config).Center);
        }

        AssignSlots(onlyRunning: false);
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
            if (c != overflow && app >= 0 && CanHaveSlot(app) && (!onlyRunning || SlotWindows(app).Count > 0) && !_slots.Contains(app))
            {
                _slots[c] = app;
            }
        }

        int cell = 0;
        for (int i = 0; i < _config.Apps.Count; i++)
        {
            if (!CanHaveSlot(i) || _slots.Contains(i) || _config.Apps[i].InCorner || (onlyRunning && SlotWindows(i).Count == 0))
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
    private void ApplyLayout(List<CellEntry> cells, bool save)
    {
        _config.Cells = cells;
        _slots = new int[cells.Count];
        _slotSignature = new string[cells.Count];
        HideList();
        AssignSlots(onlyRunning: false);

        if (save)
        {
            _config.Save(ConfigPath);
        }
        Refresh();
    }

    private void OpenLayout()
    {
        _layout ??= new LayoutWindow(ApplyLayout);
        _layout.Open(_config.Cells, _config.Apps.Select(a => a.Name), _config.CenterPercent, _config.TilesPerSide);
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
            string signature = string.Join(",", windows.Select(w => w.Hwnd.ToString()));
            if (signature == _slotSignature[s])
            {
                continue;
            }
            _slotSignature[s] = signature;

            if (windows.Count == 0)
            {
                continue;
            }

            IntPtr front = windows[0].Hwnd;
            for (int k = windows.Count - 1; k >= 1; k--)
            {
                WindowPlacer.PlaceInSlot(windows[k].Hwnd, rects[s], front, front: false);
            }
            WindowPlacer.PlaceInSlot(front, rects[s], Native.HWND_TOP, front: true);
        }
    }

    /// <summary>
    /// Mausfilter: Zusatztaste auf ein Fenster eines Kachelplatzes. Liefert true, wenn der Klick verschluckt wird.
    /// Zurück = Fenster in die Mitte, Vorwärts = Fensterliste der App.
    /// </summary>
    private bool OnSlotButton(NativePoint pt, int button)
    {
        if (!_enabled)
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
        if (!WindowsOfSlot(slot).Any(w => w.Hwnd == hwnd))
        {
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

    /// <summary>Fenster einer App, die außen stehen können: alle außer dem Mitte-Fenster.</summary>
    private List<WindowInfo> SlotWindows(int app) =>
        _groups[app].Where(w => w.Hwnd != _centerHwnd).ToList();

    /// <summary>
    /// Sammelplatz: die unterste Zelle der rechten Seite (ohne rechte Zellen die letzte). -1, wenn abgeschaltet.
    /// Er bekommt nie eine eigene App, sondern nimmt alle Apps ohne Platz auf.
    /// </summary>
    private int OverflowSlot
    {
        get
        {
            List<CellEntry> cells = _config.Cells;
            if (!_config.OverflowCorner || cells.Count < 2)
            {
                return -1;
            }

            int best = -1;
            for (int c = 0; c < cells.Count; c++)
            {
                if (cells[c].Side != "right")
                {
                    continue;
                }
                if (best < 0 || cells[c].Y + cells[c].H > cells[best].Y + cells[best].H
                    || (cells[c].Y + cells[c].H == cells[best].Y + cells[best].H && cells[c].X > cells[best].X))
                {
                    best = c;
                }
            }
            return best >= 0 ? best : cells.Count - 1;
        }
    }

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
    private void MoveToCenter(IntPtr hwnd)
    {
        int app = AppOfWindow(_groups, hwnd);
        if (app < 0 || hwnd == _centerHwnd)
        {
            return;
        }

        WindowPlacer.Place(hwnd, DockLayout.Compute(_config).Center);
        _centerHwnd = hwnd;
        _centerApp = app;
        Array.Fill(_slotSignature, null);
        Refresh();
    }

    /// <summary>
    /// Fenster per Ziehen abgelegt (Mauszeiger beim Loslassen): über der Mitte wird es das Mitte-Fenster,
    /// über einem Platz bekommt seine App diesen Platz fest, über dem Sammelplatz wandert die App dorthin.
    /// Woanders abgelegt bleibt es, wo es ist.
    /// </summary>
    private void OnWindowMoved(IntPtr hwnd)
    {
        if (!_enabled)
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
        if (app < 0)
        {
            return;
        }

        NativeRect center = DockLayout.Compute(_config).Center;
        if (Contains(center, pt))
        {
            if (hwnd == _centerHwnd)
            {
                WindowPlacer.Place(hwnd, center);
            }
            else
            {
                MoveToCenter(hwnd);
            }
            return;
        }

        NativeRect[] rects = DockLayout.SlotRects(_config);
        int slot = Array.FindIndex(rects, r => Contains(r, pt));
        if (slot < 0)
        {
            return;
        }

        if (hwnd == _centerHwnd)
        {
            _centerHwnd = IntPtr.Zero;
            _centerApp = -1;
        }
        MoveAppToSlot(app, slot);
        WindowPlacer.PlaceInSlot(hwnd, rects[slot], Native.HWND_TOP, front: true);
        Array.Fill(_slotSignature, null);
        _config.Save(ConfigPath);
        Refresh();
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
        if (app < 0 || hwnd == _centerHwnd)
        {
            return;
        }

        NativeRect slotRect = DockLayout.SlotRects(_config)[slot];
        IntPtr previousWindow = _centerHwnd;
        int previousApp = _centerApp;

        // Hat die App noch weitere Fenster, behält sie ihren Platz außen.
        bool keepsSlot = _groups[app].Any(w => w.Hwnd != hwnd);
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

        WindowPlacer.Place(hwnd, DockLayout.Compute(_config).Center);
        _centerHwnd = hwnd;
        _centerApp = app;
        Refresh();
    }

    /// <summary>Fensterliste der App eines Kachelplatzes, scrollbar, über dem Platz.</summary>
    private void ShowList(int slot)
    {
        List<WindowInfo> windows = slot == OverflowSlot ? OverflowListWindows() : WindowsOfSlot(slot);
        if (windows.Count == 0)
        {
            return;
        }

        _listSlot = slot;
        _list.ShowFor(windows, DockLayout.SlotRects(_config)[slot], OnListPick);
    }

    /// <summary>Aus der Fensterliste gewählt: das Fenster wird das oberste im Kachelplatz.</summary>
    private void OnListPick(IntPtr hwnd)
    {
        int slot = _listSlot;
        HideList();
        if (slot < 0)
        {
            return;
        }

        WindowPlacer.PlaceInSlot(hwnd, DockLayout.SlotRects(_config)[slot], Native.HWND_TOP, front: true);
        WindowPlacer.Activate(hwnd);
        _slotSignature[slot] = null;
        Refresh();
    }

    private void HideList()
    {
        _list.HideList();
        _listSlot = -1;
        _leaveSince = null;
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

    private static bool TryParseHotkey(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        string[] parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
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

        if (!Enum.TryParse(parts[^1], true, out Key wpfKey))
        {
            return false;
        }

        key = (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
        return key != 0;
    }
}
