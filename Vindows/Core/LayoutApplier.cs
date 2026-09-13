using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Vindows.Core;

/// <summary>Расчёт областей и размещение окон по ним.</summary>
public static class LayoutApplier
{
    /// <summary>
    /// Расставляет окна всех раскладок: для каждой области находит окно выбранного приложения.
    /// При current != null старые привязки сохраняются: закреплённое окно не отдаёт свою область
    /// новым окнам того же приложения, пока живо и остаётся на мониторе своей зоны.
    /// Если окно не помещается в зону (минимальный размер окна), зона расширяется за счёт соседней.
    /// placed — фактические прямоугольники окон после расстановки (могут отличаться от зоны,
    /// если окно нельзя сжать до её размера).
    /// </summary>
    public static ApplyResult Apply(List<MonitorLayout> layouts, List<MonitorInfo> monitors, out List<WindowTarget> placed, List<WindowTarget>? current = null)
    {
        DebugLog.Line("=== Apply: расстановка ===");
        var targets = BuildTargets(layouts, monitors, out var missing, current);
        bool adjusted = false;

        // Несколько проходов: расширение одной зоны может сделать соседнюю слишком узкой,
        // поэтому измеряем и подстраиваем, пока все окна не встанут или сетка не упрётся в минимум.
        for (int pass = 0; pass < 4; pass++)
        {
            bool resized = false;
            foreach (var t in targets)
            {
                try
                {
                    // Окно могло быть закрыто между перечислением и расстановкой.
                    if (!Win32.IsWindow(t.Handle)) continue;
                    MoveTo(t.Handle, t.Rect, topmost: true);
                    if (!TryGetVisibleRect(t.Handle, out var vis)) continue;
                    double overflowW = vis.Width - t.Rect.Width;
                    double overflowH = vis.Height - t.Rect.Height;
                    if (overflowW <= 2 && overflowH <= 2) continue;
                    DebugLog.Line($"  FIT pass={pass} зона={t.Zone.Name} не влезает на {overflowW:F0}x{overflowH:F0}px — расширяю зону");
                    ExpandZoneToFit(t, overflowW, overflowH);
                    resized = true;
                }
                catch (Exception ex)
                {
                    DebugLog.Line($"  FAIL (подгонка) {t.Zone.Name}: {ex.Message}");
                }
            }
            if (!resized) break;
            adjusted = true;
            targets = BuildTargets(layouts, monitors, out missing, current);
        }

        // Финальная расстановка с учётом итоговых зон.
        placed = new List<WindowTarget>(targets.Count);
        var oversized = new List<string>();
        var denied = new List<string>();
        foreach (var t in targets)
        {
            try
            {
                // Окно могло быть закрыто между перечислением и расстановкой.
                if (!Win32.IsWindow(t.Handle)) continue;
                if (!MoveTo(t.Handle, t.Rect, topmost: true))
                {
                    // Окно живо, но не двигается — почти наверняка нет прав (UIPI).
                    denied.Add(t.Zone.Name);
                    continue;
                }
                FitToWorkArea(t.Handle, t.WorkArea);

                // Фактический прямоугольник после расстановки — он и становится целью контроля привязки.
                var final = t;
                if (Win32.GetWindowRect(t.Handle, out var r2))
                    final = t with { Rect = new Rect(r2.Left, r2.Top, r2.Right - r2.Left, r2.Bottom - r2.Top) };

                if (TryGetVisibleRect(t.Handle, out var vis2) &&
                    (Math.Abs(vis2.Width - t.Rect.Width) > 2 || Math.Abs(vis2.Height - t.Rect.Height) > 2))
                    oversized.Add(t.Zone.Name);

                DebugLog.Line($"  PLACE {t.Zone.Name}: зона=({t.Rect.X:F0},{t.Rect.Y:F0},{t.Rect.Width:F0}x{t.Rect.Height:F0}) факт=({final.Rect.X:F0},{final.Rect.Y:F0},{final.Rect.Width:F0}x{final.Rect.Height:F0})");
                placed.Add(final);
            }
            catch (Exception ex)
            {
                DebugLog.Line($"  FAIL {t.Zone.Name}: {ex.Message}");
            }
        }

        DebugLog.Line($"  ИТОГ: размещено={placed.Count}, не найдено=[{string.Join(", ", missing)}], oversized=[{string.Join(", ", oversized)}], нет прав=[{string.Join(", ", denied)}]");
        return new ApplyResult { Placed = placed.Count, Missing = missing, Oversized = oversized, Denied = denied, GridAdjusted = adjusted };
    }

    /// <summary>
    /// Расширяет зону окна за счёт соседней колонки/строки, если окно не помещается.
    /// overflowW/overflowH — на сколько пикселей окно больше зоны.
    /// </summary>
    private static void ExpandZoneToFit(WindowTarget t, double overflowW, double overflowH)
    {
        var zones = t.Layout.Zones;
        int columns = t.Layout.Columns;
        int idx = zones.IndexOf(t.Zone);
        if (idx < 0 || columns < 1 || zones.Count % columns != 0) return;
        // Вырожденная рабочая область дала бы NaN/бесконечность в процентах — не расширяем.
        if (t.WorkArea.Width <= 0 || t.WorkArea.Height <= 0) return;
        int rows = zones.Count / columns;
        int col = idx % columns, row = idx / columns;

        if (overflowW > 2 && columns > 1)
        {
            double need = overflowW / t.WorkArea.Width * 100.0;
            // Растём в сторону соседа: вправо, если справа есть колонка, иначе влево.
            if (col < columns - 1)
                MoveVerticalSplitter(zones, columns, col, need);
            else
                MoveVerticalSplitter(zones, columns, col - 1, -need);
        }
        if (overflowH > 2 && rows > 1)
        {
            double need = overflowH / t.WorkArea.Height * 100.0;
            if (row < rows - 1)
                MoveHorizontalSplitter(zones, columns, row, need);
            else
                MoveHorizontalSplitter(zones, columns, row - 1, -need);
        }
    }

    /// <summary>Сопоставляет назначенные приложения с открытыми окнами: пары «окно → область в пикселях».
    /// Если приложение назначено зонам на разных мониторах, окно закрепляется за зоной того монитора,
    /// на котором оно сейчас находится, а не за первой по порядку зоной на другом экране.</summary>
    public static List<WindowTarget> BuildTargets(List<MonitorLayout> layouts, List<MonitorInfo> monitors, out List<string> missing, List<WindowTarget>? current = null)
    {
        var ownProcess = Process.GetCurrentProcess().ProcessName;
        var windows = WindowEnumerator.GetOpenWindows()
            .Where(w => !string.Equals(w.ProcessName, ownProcess, StringComparison.OrdinalIgnoreCase))
            .ToList();

        DebugLog.Line("  BuildTargets: мониторы=" + string.Join("; ", monitors.Select(m => $"{m.DeviceName} wa=({(int)m.WorkArea.Left},{(int)m.WorkArea.Top},{(int)m.WorkArea.Right},{(int)m.WorkArea.Bottom})")));
        DebugLog.Line("  BuildTargets: раскладки=" + string.Join("; ", layouts.Select(l => $"{l.DeviceName} {l.Columns}x{l.Rows} зон={l.Zones.Count}")));
        DebugLog.Line("  BuildTargets: открытые окна=" + windows.Count);

        var targets = new List<WindowTarget>();
        missing = new List<string>();

        // Раскладки, у которых есть реальный монитор с непустой рабочей областью.
        var active = new List<(MonitorLayout Layout, MonitorInfo Monitor)>();
        foreach (var layout in layouts)
        {
            var monitor = monitors.FirstOrDefault(m => m.DeviceName == layout.DeviceName);
            if (monitor == null)
            {
                DebugLog.Line($"  SKIP раскладка {layout.DeviceName}: такого монитора нет в системе");
                continue;
            }

            if (monitor.WorkArea.Width <= 0 || monitor.WorkArea.Height <= 0)
            {
                DebugLog.Line($"  SKIP раскладка {layout.DeviceName}: рабочая область монитора пуста");
                continue;
            }
            active.Add((layout, monitor));
        }

        // Проход 0: закреплённое окно сохраняет свою область, пока открыто, приложение не изменено
        // и окно остаётся на мониторе своей зоны.
        if (current != null)
        {
            foreach (var (layout, monitor) in active)
            {
                foreach (var zone in layout.Zones)
                {
                    if (string.IsNullOrWhiteSpace(zone.ProcessName)) continue;
                    var existing = current.FirstOrDefault(t => t.Layout == layout && t.Zone == zone);
                    if (existing.Handle == IntPtr.Zero) continue;
                    var candidate = windows.FirstOrDefault(w => w.Handle == existing.Handle);
                    if (candidate == null ||
                        !string.Equals(candidate.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                        !IsOnMonitor(candidate.Handle, monitor.WorkArea))
                        continue; // окно ушло на другой монитор — отпускаем, его подберёт зона нового экрана
                    windows.Remove(candidate);
                    BindTarget(targets, candidate, zone, layout, monitor);
                }
            }
        }

        // Проход 1: окна, уже стоящие на мониторе зоны, закрепляются за ней,
        // чтобы не достаться первой по порядку зоне на другом экране.
        var pending = new List<(MonitorLayout Layout, MonitorInfo Monitor, Zone Zone)>();
        foreach (var (layout, monitor) in active)
        {
            foreach (var zone in layout.Zones)
            {
                if (string.IsNullOrWhiteSpace(zone.ProcessName))
                {
                    DebugLog.Line($"  SKIP {layout.DeviceName}/{zone.Name}: приложение не назначено");
                    continue;
                }

                var win = windows.FirstOrDefault(w =>
                    string.Equals(w.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                    IsOnMonitor(w.Handle, monitor.WorkArea));
                if (win == null)
                {
                    // Своих окон на мониторе нет — откладываем на второй проход.
                    pending.Add((layout, monitor, zone));
                    continue;
                }
                windows.Remove(win);
                BindTarget(targets, win, zone, layout, monitor);
            }
        }

        // Проход 2: оставшиеся зоны забирают любые окна нужного приложения.
        foreach (var (layout, monitor, zone) in pending)
        {
            var win = windows.FirstOrDefault(w =>
                string.Equals(w.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (win == null)
            {
                DebugLog.Line($"  MISS {layout.DeviceName}/{zone.Name}: окно {zone.ProcessName} не найдено");
                missing.Add($"{zone.Name} ({zone.ProcessName})");
                continue;
            }
            windows.Remove(win);
            BindTarget(targets, win, zone, layout, monitor);
        }
        return targets;
    }

    /// <summary>Центр окна лежит в рабочей области монитора.</summary>
    public static bool IsOnMonitor(IntPtr hwnd, Rect workArea) =>
        Win32.GetWindowRect(hwnd, out var r) &&
        (r.Left + r.Right) / 2.0 >= workArea.Left && (r.Left + r.Right) / 2.0 < workArea.Right &&
        (r.Top + r.Bottom) / 2.0 >= workArea.Top && (r.Top + r.Bottom) / 2.0 < workArea.Bottom;

    /// <summary>Добавляет пару «окно → зона» и пишет строку привязки в лог.</summary>
    private static void BindTarget(List<WindowTarget> targets, WindowInfo win, Zone zone, MonitorLayout layout, MonitorInfo monitor)
    {
        var rect = ZoneToPixelRect(zone, monitor.WorkArea);
        DebugLog.Line($"  BIND {layout.DeviceName}/{zone.Name}: '{win.Title}' [{win.ProcessName}] hwnd={win.Handle} -> ({rect.X:F0},{rect.Y:F0},{rect.Width:F0}x{rect.Height:F0})");
        targets.Add(new WindowTarget(win.Handle, rect, layout, zone, monitor.WorkArea));
    }

    /// <summary>Переводит проценты области в физические пиксели рабочей области монитора.</summary>
    public static Rect ZoneToPixelRect(Zone zone, Rect workArea) =>
        new(
            workArea.X + workArea.Width * zone.X / 100.0,
            workArea.Y + workArea.Height * zone.Y / 100.0,
            workArea.Width * zone.Width / 100.0,
            workArea.Height * zone.Height / 100.0);

    /// <summary>Перемещает и меняет размер окна (свёрнутое/развёрнутое предварительно восстанавливается).
    /// Окно подгоняется по видимой части: невидимые рамки тени DWM не учитываются,
    /// иначе зазоры между окнами получаются неровными.
    /// Возвращает false, если окно не удалось сдвинуть (закрыто или нет прав —
    /// например, целевое окно запущено от администратора, а Vindows нет).</summary>
    public static bool MoveTo(IntPtr hwnd, Rect rect, bool topmost = false)
    {
        // Окно могло быть закрыто между перечислением и расстановкой.
        if (!Win32.IsWindow(hwnd)) return false;

        try
        {
            if (Win32.IsIconic(hwnd) || Win32.IsZoomed(hwnd))
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

            int x = (int)Math.Round(rect.X);
            int y = (int)Math.Round(rect.Y);
            // Считаем размер от округлённых краёв, чтобы соседние области стыковались без щелей.
            int w = Math.Max(1, (int)Math.Round(rect.X + rect.Width) - x);
            int h = Math.Max(1, (int)Math.Round(rect.Y + rect.Height) - y);

            IntPtr z = topmost ? Win32.HWND_TOPMOST : Win32.HWND_TOP;
            // Без SWP_NOZORDER, иначе HWND_TOPMOST/HWND_TOP не применится к z-порядку.
            if (!Win32.SetWindowPos(hwnd, z, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW))
            {
                // Типичный случай: ошибка 5 (доступ запрещён) — целевое окно запущено
                // от администратора, и Windows (UIPI) не даёт его двигать.
                DebugLog.Line($"  MoveTo {hwnd}: SetWindowPos НЕ СРАБОТАЛ, err={Marshal.GetLastWin32Error()} (5 = окно запущено от администратора)");
                return false;
            }

            // Невидимые рамки (тень DWM) у каждого окна свои: сдвигаем окно так,
            // чтобы его видимая часть совпала с заданным прямоугольником.
            if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT visible, Marshal.SizeOf<Win32.RECT>()) == 0 &&
                Win32.GetWindowRect(hwnd, out var outer))
            {
                // dl/dt/dr/db — ширина невидимых рамок: dl < 0 означает рамку слева.
                // Видимая часть = outer + смещения, поэтому внешний размер должен быть
                // БОЛЬШЕ целевого на сумму рамок: w - dl + dr (а не w - dl - dr,
                // иначе окно получается на 2×рамку уже и появляются лишние зазоры).
                int dl = outer.Left - visible.Left, dt = outer.Top - visible.Top;
                int dr = outer.Right - visible.Right, db = outer.Bottom - visible.Bottom;
                if (dl != 0 || dt != 0 || dr != 0 || db != 0)
                    Win32.SetWindowPos(hwnd, z, x + dl, y + dt,
                        Math.Max(1, w - dl + dr), Math.Max(1, h - dt + db),
                        Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            }
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Line($"  MoveTo {hwnd}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Видимые границы окна (без невидимых рамок тени DWM); при неудаче — весь прямоугольник.</summary>
    private static bool TryGetVisibleRect(IntPtr hwnd, out Rect visible)
    {
        visible = default;
        if (!Win32.IsWindow(hwnd)) return false;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT r, Marshal.SizeOf<Win32.RECT>()) == 0)
        {
            visible = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return true;
        }
        if (Win32.GetWindowRect(hwnd, out r))
        {
            visible = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Если окно нельзя сжать до зоны (минимальный размер окна), его края могут выйти за рабочую
    /// область. Прижимаем окно обратно внутрь, чтобы оно не вылезало за экран.
    /// </summary>
    private static void FitToWorkArea(IntPtr hwnd, Rect workArea)
    {
        // Окно могло быть закрыто между перечислением и расстановкой.
        if (!Win32.IsWindow(hwnd)) return;

        try
        {
            if (!Win32.GetWindowRect(hwnd, out var outer)) return;
            var vis = outer;
            if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT v, Marshal.SizeOf<Win32.RECT>()) == 0)
                vis = v;

            int waLeft = (int)Math.Round(workArea.Left);
            int waTop = (int)Math.Round(workArea.Top);
            int waRight = (int)Math.Round(workArea.Right);
            int waBottom = (int)Math.Round(workArea.Bottom);

            int dx = 0, dy = 0;
            if (vis.Right > waRight) dx = waRight - vis.Right;     // вылезло справа — прижимаем правый край
            if (vis.Bottom > waBottom) dy = waBottom - vis.Bottom; // вылезло снизу
            if (vis.Left + dx < waLeft) dx = waLeft - vis.Left;    // не толкаем за левый край
            if (vis.Top + dy < waTop) dy = waTop - vis.Top;

            if (dx == 0 && dy == 0) return;
            Win32.SetWindowPos(hwnd, Win32.HWND_TOP, outer.Left + dx, outer.Top + dy, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            DebugLog.Line($"  FitToWorkArea {hwnd}: {ex.Message}");
        }
    }

    /// <summary>Строит равномерную сетку областей: колонки × строки с зазором. Проценты — от рабочей области.</summary>
    public static List<Zone> BuildGridZones(int columns, int rows, int gap, Rect workArea)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        gap = Math.Max(0, gap);

        // Вырожденная рабочая область дала бы NaN/бесконечность в процентах зон.
        if (workArea.Width <= 0 || workArea.Height <= 0)
            return new List<Zone>();

        double cellW = (workArea.Width - gap * (columns - 1)) / columns;
        double cellH = (workArea.Height - gap * (rows - 1)) / rows;

        var zones = new List<Zone>();
        int n = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                double x = c * (cellW + gap);
                double y = r * (cellH + gap);
                n++;
                zones.Add(new Zone
                {
                    Name = $"Зона {n}",
                    X = x / workArea.Width * 100,
                    Y = y / workArea.Height * 100,
                    Width = cellW / workArea.Width * 100,
                    Height = cellH / workArea.Height * 100,
                });
            }
        }
        return zones;
    }

    /// <summary>Минимальный размер области в процентах от рабочей области — меньше разделитель не сожмёт.</summary>
    public const double MinZonePercent = 4.0;

    /// <summary>
    /// Сдвигает вертикальный разделитель: зоны слева меняют ширину на delta, справа — на −delta.
    /// splitter — индекс колонки слева от разделителя (0..columns−2).
    /// Возвращает фактически применённый сдвиг в процентах (с учётом минимальной ширины).
    /// </summary>
    public static double MoveVerticalSplitter(List<Zone> zones, int columns, int splitter, double deltaPercent)
    {
        if (columns < 1 || zones.Count == 0 || zones.Count % columns != 0) return 0;
        if (splitter < 0 || splitter >= columns - 1) return 0;
        // NaN/бесконечность отравили бы проценты всех зон — игнорируем такой сдвиг.
        if (!double.IsFinite(deltaPercent)) return 0;
        int rows = zones.Count / columns;

        double minDelta = double.NegativeInfinity, maxDelta = double.PositiveInfinity;
        for (int r = 0; r < rows; r++)
        {
            var left = zones[r * columns + splitter];
            var right = zones[r * columns + splitter + 1];
            minDelta = Math.Max(minDelta, MinZonePercent - left.Width);
            maxDelta = Math.Min(maxDelta, right.Width - MinZonePercent);
        }

        double delta = Math.Clamp(deltaPercent, minDelta, maxDelta);
        if (!double.IsFinite(delta) || Math.Abs(delta) < 1e-9) return 0;

        for (int r = 0; r < rows; r++)
        {
            var left = zones[r * columns + splitter];
            var right = zones[r * columns + splitter + 1];
            left.Width += delta;
            right.X += delta;
            right.Width -= delta;
        }
        return delta;
    }

    /// <summary>
    /// Сдвигает горизонтальный разделитель: зоны сверху меняют высоту на delta, снизу — на −delta.
    /// splitter — индекс строки сверху от разделителя (0..rows−2).
    /// Возвращает фактически применённый сдвиг в процентах (с учётом минимальной высоты).
    /// </summary>
    public static double MoveHorizontalSplitter(List<Zone> zones, int columns, int splitter, double deltaPercent)
    {
        if (columns < 1 || zones.Count == 0 || zones.Count % columns != 0) return 0;
        int rows = zones.Count / columns;
        if (splitter < 0 || splitter >= rows - 1) return 0;
        // NaN/бесконечность отравили бы проценты всех зон — игнорируем такой сдвиг.
        if (!double.IsFinite(deltaPercent)) return 0;

        double minDelta = double.NegativeInfinity, maxDelta = double.PositiveInfinity;
        for (int c = 0; c < columns; c++)
        {
            var top = zones[splitter * columns + c];
            var bottom = zones[(splitter + 1) * columns + c];
            minDelta = Math.Max(minDelta, MinZonePercent - top.Height);
            maxDelta = Math.Min(maxDelta, bottom.Height - MinZonePercent);
        }

        double delta = Math.Clamp(deltaPercent, minDelta, maxDelta);
        if (!double.IsFinite(delta) || Math.Abs(delta) < 1e-9) return 0;

        for (int c = 0; c < columns; c++)
        {
            var top = zones[splitter * columns + c];
            var bottom = zones[(splitter + 1) * columns + c];
            top.Height += delta;
            bottom.Y += delta;
            bottom.Height -= delta;
        }
        return delta;
    }
}

/// <summary>
/// Временная диагностика расстановки: дописывает строки в %APPDATA%\Vindows\debug.log.
/// Логирование не должно влиять на работу приложения — все ошибки проглатываются.
/// </summary>
internal static class DebugLog
{
    private static readonly object Gate = new();
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vindows", "debug.log");

    public static void Clear()
    {
        try
        {
            lock (Gate) File.Delete(FilePath);
        }
        catch
        {
        }
    }

    public static void Line(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
