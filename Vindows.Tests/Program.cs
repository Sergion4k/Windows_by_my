using System.Windows;
using Vindows.Core;

// Запуск: dotnet run --project Vindows.Tests -c Release
// Проверки используют снимки окон: реальные окна рабочего стола не перемещаются.
var primary = Monitor("primary", new Rect(0, 0, 1920, 1040));
var secondary = Monitor("secondary", new Rect(-2560, -300, 2560, 1400));
var monitors = new List<MonitorInfo> { primary, secondary };
var first = Layout(primary);
var second = Layout(secondary);
var layouts = new List<MonitorLayout> { first, second };
var windows = new List<WindowInfo> { Window(1), Window(2), Window(3) };
var hosts = new Dictionary<IntPtr, string>
{
    [new(1)] = primary.DeviceName,
    [new(2)] = primary.DeviceName,
    [new(3)] = secondary.DeviceName,
};
int passed = 0;

List<WindowTarget> Match(out List<string> missing, List<WindowTarget>? current = null) =>
    LayoutApplier.BuildTargets(layouts, monitors, windows,
        (hwnd, monitor) => hosts.GetValueOrDefault(hwnd) == monitor.DeviceName,
        (_, _) => 0, out missing, current);

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

var initial = Match(out var missing);
Check(initial.Count == 2 && missing.Count == 0 &&
    initial.Single(t => t.Layout == second).Handle == new IntPtr(3),
    "Each monitor keeps its local window");

// Раньше проход 1 повторно занимал первую зону окном 2, а вторую помечал отсутствующей.
var repeated = Match(out missing, initial);
Check(repeated.Count == 2 && missing.Count == 0 &&
    repeated.Select(t => t.Zone).Distinct().Count() == 2 &&
    repeated.Single(t => t.Layout == first).Handle == new IntPtr(1),
    "Reapply binds each zone once and does not steal an extra window");

windows.Reverse();
layouts.Reverse();
repeated = Match(out missing, repeated);
Check(repeated.Count == 2 && missing.Count == 0 &&
    repeated.Single(t => t.Layout == first).Handle == new IntPtr(1) &&
    repeated.Single(t => t.Layout == second).Handle == new IntPtr(3),
    "Existing bindings survive changes in window and monitor order");

windows.RemoveAll(w => w.Handle == new IntPtr(1));
repeated = Match(out missing, repeated);
Check(repeated.Count == 2 && missing.Count == 0 &&
    repeated.Single(t => t.Layout == first).Handle == new IntPtr(2),
    "A closed window is replaced without disturbing the other monitor");

hosts[new(2)] = secondary.DeviceName;
hosts[new(3)] = primary.DeviceName;
repeated = Match(out missing, repeated);
Check(repeated.Count == 2 && missing.Count == 0 &&
    repeated.Single(t => t.Layout == first).Handle == new IntPtr(3) &&
    repeated.Single(t => t.Layout == second).Handle == new IntPtr(2),
    "Windows dragged between monitors bind to their new monitors");

hosts[new(2)] = primary.DeviceName;
repeated = Match(out missing, repeated);
Check(repeated.Count == 2 && missing.Count == 0 &&
    repeated.Single(t => t.Layout == second).Handle == new IntPtr(2),
    "An empty secondary zone receives the unbound window from the primary monitor");

windows.RemoveAll(w => w.Handle == new IntPtr(2));
repeated = Match(out missing, repeated);
Check(repeated.Count == 1 && missing.Count == 1 && repeated[0].Layout == first,
    "A missing window does not produce false missing reports for occupied zones");

monitors.Remove(secondary);
repeated = Match(out missing, repeated);
Check(repeated.Count == 1 && missing.Count == 0,
    "Disconnected monitor layouts are skipped");

var grid = LayoutApplier.BuildGridZones(2, 1, 8, secondary.WorkArea);
var left = LayoutApplier.ZoneToPixelRect(grid[0], secondary.WorkArea);
var right = LayoutApplier.ZoneToPixelRect(grid[1], secondary.WorkArea);
Check(Math.Abs(left.Left + 2560) < 0.001 && Math.Abs(left.Top + 300) < 0.001 &&
    Math.Abs(right.Right) < 0.001 && Math.Abs(right.Left - left.Right - 8) < 0.001 &&
    Math.Abs(right.Bottom - secondary.WorkArea.Bottom) < 0.001,
    "Secondary monitor geometry preserves negative origins, work area and pixel gaps");

// Конкретные окна имеют приоритет над назначениями всего приложения.
monitors.Add(secondary);
layouts = [first, second];
windows = [Window(1), Window(2), Window(3)];
hosts[new(1)] = primary.DeviceName;
hosts[new(2)] = primary.DeviceName;
hosts[new(3)] = secondary.DeviceName;
second.Zones[0].SelectionMode = WindowSelectionMode.SpecificWindow;
second.Zones[0].SelectWindow(windows[0]);
var specific = Match(out missing);
Check(specific.Count == 2 && missing.Count == 0 &&
    specific.Single(t => t.Layout == second).Handle == new IntPtr(1) &&
    specific.Single(t => t.Layout == first).Handle == new IntPtr(2),
    "Specific window is reserved before app matching, even on another monitor");

windows[0] = new WindowInfo { Handle = new(1), Title = "Changed title", ProcessName = "editor" };
specific = Match(out missing, specific);
Check(specific.Single(t => t.Layout == second).Handle == new IntPtr(1) &&
    second.Zones[0].WindowTitle == "Changed title", "Changing the title does not replace the selected window");

windows.RemoveAt(0);
specific = Match(out missing, specific);
Check(specific.Count == 1 && missing.Count == 1 && second.Zones[0].Status.Contains("закрыто"),
    "Closed specific window is not replaced by another window of the same app");

windows.Insert(0, new WindowInfo { Handle = new(1), ProcessId = 99, Title = "Changed title", ProcessName = "editor" });
Check(LayoutApplier.ResolveSpecificWindow(second.Zones[0], windows, out _) == null,
    "A reused handle owned by another process does not match");

var saved = new LayoutFile { Monitors = layouts, KeepOnTop = true, ControlEnabled = false,
    MonitorMoveBehavior = MonitorMoveBehavior.ReleaseWindow };
var json = System.Text.Json.JsonSerializer.Serialize(saved);
var restored = System.Text.Json.JsonSerializer.Deserialize<LayoutFile>(json)!;
var restoredZone = restored.Monitors[1].Zones[0];
Check(restored.KeepOnTop && !restored.ControlEnabled &&
    restored.MonitorMoveBehavior == MonitorMoveBehavior.ReleaseWindow &&
    restoredZone.WindowHandle == IntPtr.Zero && !json.Contains("WindowProcessId") && !json.Contains("Status"),
    "Saved layouts preserve independent options without session handles or statuses");
Check(LayoutApplier.ResolveSpecificWindow(restoredZone, windows, out _)?.Handle == new IntPtr(1),
    "Saved specific selection resolves a unique title");
windows.Add(new WindowInfo { Handle = new(4), Title = "Changed title", ProcessName = "editor" });
Check(LayoutApplier.ResolveSpecificWindow(restoredZone, windows, out var reason) == null && reason.Contains("Несколько"),
    "Ambiguous saved titles require a new selection");

first.Zones[0].SelectionMode = WindowSelectionMode.SpecificWindow;
first.Zones[0].SelectWindow(windows[0]);
second.Zones[0].SelectWindow(windows[0]);
specific = Match(out missing);
Check(specific.Count == 1 && missing.Count == 1 && second.Zones[0].Status.Contains("другой зоне"),
    "The same specific window cannot occupy two zones");

var legacy = System.Text.Json.JsonSerializer.Deserialize<LayoutFile>("{\"Monitors\":[{\"DeviceName\":\"primary\",\"Zones\":[{\"Name\":\"Zone 1\",\"ProcessName\":\"editor\"}]}],\"ControlEnabled\":true}")!;
Check(legacy.Monitors[0].Zones[0].SelectionMode == WindowSelectionMode.AnyWindow && !legacy.KeepOnTop,
    "Old layouts load as app assignments with topmost disabled");

Check(WindowWatcher.ShouldReleaseAfterMove(MonitorMoveBehavior.ReleaseWindow, false) &&
    !WindowWatcher.ShouldReleaseAfterMove(MonitorMoveBehavior.ReleaseWindow, true) &&
    !WindowWatcher.ShouldReleaseAfterMove(MonitorMoveBehavior.ReturnToZone, false),
    "Cross-monitor behavior releases only when explicitly selected");

first.Zones[0].SelectionMode = WindowSelectionMode.AnyWindow;
second.Zones[0].SelectionMode = WindowSelectionMode.AnyWindow;
windows = [Window(1), Window(3)];
var beforeDrag = Match(out missing);
hosts[new(1)] = secondary.DeviceName;
hosts[new(3)] = primary.DeviceName;
var returned = LayoutApplier.BuildTargets(layouts, monitors, windows,
    (hwnd, monitor) => hosts[hwnd] == monitor.DeviceName, (_, _) => 0, out missing, beforeDrag, true);
Check(returned.Single(t => t.Layout == first).Handle == new IntPtr(1) &&
    returned.Single(t => t.Layout == second).Handle == new IntPtr(3),
    "Return-to-zone policy keeps original assignments after crossing monitors");

var edited = new Zone { Name = "Zone", ProcessName = "missing" };
int changes = 0;
var vm = new Vindows.ZoneVM(edited, "слева", windows, () => changes++);
Check(edited.ProcessName == "missing" && vm.SelectedOption!.Label.Contains("нет открытых"),
    "Refreshing options retains assignments for absent apps");
vm.SelectedOption = null;
Check(edited.ProcessName == "missing" && changes == 0, "Transient UI selection resets do not erase assignments");
vm.ModeIndex = 1;
Check(edited.ProcessName == null && edited.WindowHandle == IntPtr.Zero,
    "Switching to specific mode requires an explicit window selection");
vm.SelectedOption = vm.Options.Single(o => o.Window?.Handle == new IntPtr(3));
Check(edited.WindowHandle == new IntPtr(3) && edited.SelectionMode == WindowSelectionMode.SpecificWindow,
    "Specific selection stores the chosen handle");
vm.SelectedOption = vm.Options[0];
Check(edited.ProcessName == null && edited.WindowHandle == IntPtr.Zero,
    "Explicit unassignment clears the selected window");

var copy = new Zone { Name = "New zone" };
copy.CopyAssignmentFrom(second.Zones[0]);
Check(copy.ProcessName == second.Zones[0].ProcessName && copy.WindowHandle == second.Zones[0].WindowHandle,
    "Rebuilding the grid retains complete assignments");
Check(Vindows.MainWindow.ZonePosition(1, 2, 1) == "справа" && Vindows.MainWindow.ZonePosition(1, 1, 2) == "снизу",
    "Zone names describe their positions");
Check(Win32.MonitorDisplayName("DISPLAY2", new Win32.RECT { Left = -2560, Top = -300, Right = 0, Bottom = 1100 }, false, 1)
    == "Монитор 2 · 2560×1400 · слева, выше", "Monitor labels use device numbers and relative positions");

Check(Vindows.MainWindow.HaveSameMonitors(monitors, monitors.AsEnumerable().Reverse().ToList()),
    "Unchanged monitors do not trigger background placement, regardless of enumeration order");
Check(!Vindows.MainWindow.HaveSameMonitors(monitors,
    [Monitor("primary", new Rect(0, 0, 1920, 1000)), secondary]),
    "A changed work area triggers a monitor refresh");
Check(!Vindows.MainWindow.HaveSameMonitors(monitors, [primary]),
    "Disconnecting a monitor triggers a refresh");

var dragGrid = LayoutApplier.BuildGridZones(3, 3, 8, primary.WorkArea);
double horizontalGap = dragGrid[1].X - dragGrid[0].X - dragGrid[0].Width;
double verticalGap = dragGrid[3].Y - dragGrid[0].Y - dragGrid[0].Height;
var unaffected = dragGrid[2].Width;
Check(LayoutApplier.MoveVerticalSplitter(dragGrid, 3, 0, 7) == 7 &&
    Enumerable.Range(0, 3).All(row => Math.Abs(dragGrid[row * 3].Width - dragGrid[0].Width) < 1e-9) &&
    Math.Abs(dragGrid[1].X - dragGrid[0].X - dragGrid[0].Width - horizontalGap) < 1e-9 &&
    dragGrid[2].Width == unaffected,
    "A vertical grip resizes adjacent columns across all rows, preserving gaps and other columns");
Check(LayoutApplier.MoveHorizontalSplitter(dragGrid, 3, 1, -6) == -6 &&
    Enumerable.Range(0, 3).All(col => Math.Abs(dragGrid[3 + col].Height - dragGrid[3].Height) < 1e-9) &&
    Math.Abs(dragGrid[6].Y - dragGrid[3].Y - dragGrid[3].Height - verticalGap) < 1e-9,
    "A horizontal grip resizes adjacent rows across all columns and preserves gaps");
LayoutApplier.MoveVerticalSplitter(dragGrid, 3, 0, 1000);
LayoutApplier.MoveHorizontalSplitter(dragGrid, 3, 0, -1000);
Check(dragGrid.All(zone => zone.Width >= LayoutApplier.MinZonePercent - 1e-9 &&
    zone.Height >= LayoutApplier.MinZonePercent - 1e-9 && zone.X >= 0 && zone.Y >= 0 &&
    zone.X + zone.Width <= 100.000001 && zone.Y + zone.Height <= 100.000001),
    "Dragging beyond the preview keeps zones inside the monitor and above minimum size");
Check(LayoutApplier.MoveVerticalSplitter(dragGrid, 3, 0, double.NaN) == 0 &&
    LayoutApplier.MoveHorizontalSplitter(dragGrid, 3, 0, double.PositiveInfinity) == 0,
    "Invalid drag coordinates leave the grid unchanged");

var pairGrid = LayoutApplier.BuildGridZones(2, 2, 4, primary.WorkArea);
Rect Geometry(Zone zone) => new(zone.X, zone.Y, zone.Width, zone.Height);
var pairBefore = pairGrid.Select(Geometry).ToArray();
Check(LayoutApplier.MoveCellSplitter(pairGrid, 0, 1, true, 10) == 10 &&
    pairGrid[0].Width == pairBefore[0].Width + 10 &&
    pairGrid[1].Width == pairBefore[1].Width - 10 &&
    Geometry(pairGrid[2]) == pairBefore[2] && Geometry(pairGrid[3]) == pairBefore[3],
    "Dragging the upper vertical grip changes only cells 1 and 2");
pairBefore = pairGrid.Select(Geometry).ToArray();
Check(LayoutApplier.MoveCellSplitter(pairGrid, 2, 3, true, -5) == -5 &&
    Geometry(pairGrid[0]) == pairBefore[0] && Geometry(pairGrid[1]) == pairBefore[1],
    "The lower vertical grip moves independently of the upper grip");
pairGrid = LayoutApplier.BuildGridZones(2, 2, 4, primary.WorkArea);
pairBefore = pairGrid.Select(Geometry).ToArray();
Check(LayoutApplier.MoveCellSplitter(pairGrid, 0, 2, false, 10) == 10 &&
    pairGrid[0].Height == pairBefore[0].Height + 10 &&
    pairGrid[2].Height == pairBefore[2].Height - 10 &&
    Geometry(pairGrid[1]) == pairBefore[1] && Geometry(pairGrid[3]) == pairBefore[3],
    "Dragging the left horizontal grip changes only cells 1 and 3");
pairBefore = pairGrid.Select(Geometry).ToArray();
Check(LayoutApplier.MoveCellSplitter(pairGrid, 0, 1, true, 10) == 0 &&
    pairGrid.Select(Geometry).SequenceEqual(pairBefore),
    "A staggered border stops before overlapping a third cell");
Check(LayoutApplier.MoveCellSplitter(pairGrid, 0, 1, true, -5) == -5,
    "A staggered border can still move in the unobstructed direction");
Check(LayoutApplier.MoveCellSplitter(pairGrid, -1, 1, true, 2) == 0 &&
    LayoutApplier.MoveCellSplitter(pairGrid, 0, 99, true, 2) == 0 &&
    LayoutApplier.MoveCellSplitter(pairGrid, 0, 0, true, 2) == 0 &&
    LayoutApplier.MoveCellSplitter(pairGrid, 0, 1, true, double.NaN) == 0,
    "Invalid cell pairs and drag coordinates do not change zones");

var sharedRows = new MonitorLayout
{
    DeviceName = primary.DeviceName, Columns = 3, Rows = 3,
    Zones = LayoutApplier.BuildGridZones(3, 3, 4, primary.WorkArea),
};
LayoutApplier.MoveCellSplitter(sharedRows.Zones, 0, 1, true, 7);
LayoutApplier.MoveCellSplitter(sharedRows.Zones, 3, 4, true, -5);
var sharedBefore = sharedRows.Zones.Select(Geometry).ToArray();
Check(LayoutApplier.MoveHorizontalSplitter(sharedRows.Zones, 3, 0, 8) == 8 &&
    Enumerable.Range(0, 3).All(c =>
        sharedRows.Zones[c].Height == sharedBefore[c].Height + 8 &&
        sharedRows.Zones[3 + c].Height == sharedBefore[3 + c].Height - 8) &&
    Enumerable.Range(0, 9).All(i => sharedRows.Zones[i].Width == sharedBefore[i].Width &&
        sharedRows.Zones[i].X == sharedBefore[i].X) &&
    Enumerable.Range(6, 3).All(i => Geometry(sharedRows.Zones[i]) == sharedBefore[i]),
    "Up-down movement changes both full rows and preserves their independent column widths");
Check(Vindows.MainWindow.GetSplitterZones(sharedRows, 1, 4, false)
    .SetEquals(new[] { sharedRows.Zones[1], sharedRows.Zones[4] }),
    "An up-down drag reapplies only the selected cell pair");
Check(Vindows.MainWindow.GetSplitterZones(sharedRows, 3, 4, true)
    .SetEquals(sharedRows.Zones.Where((_, index) => index % sharedRows.Columns is 0 or 1)),
    "A left-right drag reapplies both adjacent columns across all rows");

var random = new Random(42);
foreach (int gap in new[] { 0, 8 })
{
    var independentGrid = LayoutApplier.BuildGridZones(4, 3, gap, primary.WorkArea);
    for (int step = 0; step < 500; step++)
    {
        bool vertical = random.Next(2) == 0;
        int row = random.Next(vertical ? 3 : 2);
        int col = random.Next(vertical ? 3 : 4);
        int a = row * 4 + col, b = a + (vertical ? 1 : 4);
        var before = independentGrid.Select(Geometry).ToArray();
        int delta = random.Next(-100, 101);
        if (vertical) LayoutApplier.MoveCellSplitter(independentGrid, a, b, true, delta);
        else LayoutApplier.MoveHorizontalSplitter(independentGrid, 4, row, delta);
        var after = independentGrid.Select(Geometry).ToArray();
        bool valid = after.Select((rect, index) => (rect, index)).All(item =>
            ((vertical ? item.index == a || item.index == b : item.index / 4 == row || item.index / 4 == row + 1)
                || item.rect == before[item.index]) &&
            item.rect.Width >= LayoutApplier.MinZonePercent - 1e-8 &&
            item.rect.Height >= LayoutApplier.MinZonePercent - 1e-8 &&
            item.rect.Left >= -1e-8 && item.rect.Top >= -1e-8 &&
            item.rect.Right <= 100 + 1e-8 && item.rect.Bottom <= 100 + 1e-8);
        for (int i = 0; i < after.Length; i++)
            for (int j = i + 1; j < after.Length; j++)
            {
                var overlap = Rect.Intersect(after[i], after[j]);
                valid &= overlap.IsEmpty || overlap.Width <= 1e-8 || overlap.Height <= 1e-8;
            }
        if (!valid) throw new InvalidOperationException($"Independent resize failed: gap={gap}, step={step}");
    }
    Check(true, $"500 pair-width and shared-row drags preserve other cells, bounds and non-overlap (gap {gap})");
}

// Только собственное скрытое окно теста: окна пользователя не затрагиваются.
Exception? nativeFailure = null;
var nativeThread = new Thread(() =>
{
    try
    {
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Vindows placement test")
        {
            WindowStyle = unchecked((int)0x80000000), Width = 400, Height = 300,
            PositionX = 100, PositionY = 100,
        });
        int positionChanges = 0;
        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0047) positionChanges++; // WM_WINDOWPOSCHANGED
            return IntPtr.Zero;
        });
        var target = new Rect(120, 130, 500, 350);
        Check(LayoutApplier.MoveTo(source.Handle, target) &&
            LayoutApplier.IsAtTargetPosition(source.Handle, target, 1) && positionChanges == 1,
            "Placement reaches the target with a single native position change");
        bool repeatedPlacementSucceeded = true;
        for (int i = 0; i < 10; i++)
            repeatedPlacementSucceeded &= LayoutApplier.MoveTo(source.Handle, target);
        Check(repeatedPlacementSucceeded && positionChanges == 1,
            "Ten repeated placements succeed without redundant native position changes");
        Check(!Win32.IsWindowVisible(source.Handle), "Placement does not force hidden windows to appear");
    }
    catch (Exception ex) { nativeFailure = ex; }
});
nativeThread.SetApartmentState(ApartmentState.STA);
nativeThread.Start();
nativeThread.Join();
if (nativeFailure != null) throw new InvalidOperationException("Native placement regression", nativeFailure);

Console.WriteLine($"{passed} checks passed.");

static MonitorInfo Monitor(string name, Rect area) => new()
{
    DeviceName = name, DisplayName = name, Bounds = area, WorkArea = area,
};

static MonitorLayout Layout(MonitorInfo monitor) => new()
{
    DeviceName = monitor.DeviceName,
    Columns = 1,
    Zones = new() { new Zone { Name = monitor.DeviceName, Width = 100, Height = 100, ProcessName = "editor" } },
};

static WindowInfo Window(int handle) => new()
{
    Handle = new(handle), Title = $"Editor {handle}", ProcessName = "editor",
};
