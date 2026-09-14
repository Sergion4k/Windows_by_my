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
