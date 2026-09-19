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
    public static ApplyResult Apply(List<MonitorLayout> layouts, List<MonitorInfo> monitors, out List<WindowTarget> placed, List<WindowTarget>? current = null, bool keepExistingAcrossMonitors = false)
    {
        DebugLog.Line("=== Apply: расстановка ===");
        var targets = BuildTargets(layouts, monitors, out var missing, current, keepExistingAcrossMonitors);
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
                    if (!MoveTo(t.Handle, t.Rect)) continue;
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
            // Меняется геометрия зон, а не назначенные им окна. Повторное перечисление
            // после SetWindowPos может вернуть другой Z-порядок и поменять окна местами.
            targets = targets.Select(t => t with { Rect = ZoneToPixelRect(t.Zone, t.WorkArea) }).ToList();
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
                if (!Win32.IsWindow(t.Handle))
                {
                    t.Zone.Status = "Окно закрыто";
                    missing.Add(t.Zone.Name);
                    continue;
                }
                if (!MoveTo(t.Handle, t.Rect))
                {
                    // Окно живо, но не двигается — почти наверняка нет прав (UIPI).
                    denied.Add(t.Zone.Name);
                    t.Zone.Status = "Не удалось переместить окно — проверьте права доступа";
                    continue;
                }
                FitToWorkArea(t.Handle, t.WorkArea);
                t.Zone.Status = "Окно размещено";

                // Цель контроля — видимая область зоны (t.Rect), а не внешний прямоугольник окна:
                // MoveTo выравнивает по DWM-рамке, и передача outer rect в watcher давала сдвиг.
                if (TryGetVisibleRect(t.Handle, out var vis2))
                {
                    if (Math.Abs(vis2.Width - t.Rect.Width) > 2 || Math.Abs(vis2.Height - t.Rect.Height) > 2)
                    {
                        oversized.Add(t.Zone.Name);
                        t.Zone.Status = "Размер окна не совпадает с зоной — возможно, достигнут минимальный размер";
                    }
                    else if (!IsAtTargetPosition(t.Handle, t.Rect))
                        t.Zone.Status = "Окно смещено относительно зоны";
                    DebugLog.Line($"  PLACE {t.Zone.Name}: зона=({t.Rect.X:F0},{t.Rect.Y:F0},{t.Rect.Width:F0}x{t.Rect.Height:F0}) видимая=({vis2.X:F0},{vis2.Y:F0},{vis2.Width:F0}x{vis2.Height:F0})");
                }
                else
                    DebugLog.Line($"  PLACE {t.Zone.Name}: зона=({t.Rect.X:F0},{t.Rect.Y:F0},{t.Rect.Width:F0}x{t.Rect.Height:F0})");

                placed.Add(t);
            }
            catch (Exception ex)
            {
                DebugLog.Line($"  FAIL {t.Zone.Name}: {ex.Message}");
                t.Zone.Status = "Ошибка размещения окна";
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
    public static List<WindowTarget> BuildTargets(List<MonitorLayout> layouts, List<MonitorInfo> monitors, out List<string> missing, List<WindowTarget>? current = null, bool keepExistingAcrossMonitors = false)
    {
        var ownProcess = Process.GetCurrentProcess().ProcessName;
        var windows = WindowEnumerator.GetOpenWindows()
            .Where(w => !string.Equals(w.ProcessName, ownProcess, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return BuildTargets(layouts, monitors, windows,
            (hwnd, monitor) => IsOnMonitor(hwnd, monitor, monitors),
            DistanceToMonitorCenter, out missing, current, keepExistingAcrossMonitors);
    }

    // Отдельное сопоставление позволяет проверить несколько мониторов без перемещения окон пользователя.
    internal static List<WindowTarget> BuildTargets(
        List<MonitorLayout> layouts, List<MonitorInfo> monitors, IReadOnlyList<WindowInfo> openWindows,
        Func<IntPtr, MonitorInfo, bool> isOnMonitor, Func<IntPtr, MonitorInfo, double> distanceToMonitor,
        out List<string> missing, List<WindowTarget>? current = null, bool keepExistingAcrossMonitors = false)
    {
        var windows = openWindows.ToList();

        DebugLog.Line("  BuildTargets: мониторы=" + string.Join("; ", monitors.Select(m => $"{m.DeviceName} wa=({(int)m.WorkArea.Left},{(int)m.WorkArea.Top},{(int)m.WorkArea.Right},{(int)m.WorkArea.Bottom})")));
        DebugLog.Line("  BuildTargets: раскладки=" + string.Join("; ", layouts.Select(l => $"{l.DeviceName} {l.Columns}x{l.Rows} зон={l.Zones.Count}")));
        DebugLog.Line("  BuildTargets: открытые окна=" + windows.Count);

        var targets = new List<WindowTarget>();
        missing = new List<string>();

        // Раскладки, у которых есть реальный монитор с непустой рабочей областью.
        var active = new List<(MonitorLayout Layout, MonitorInfo Monitor)>();
        foreach (var layout in layouts)
        {
            foreach (var zone in layout.Zones)
                zone.Status = string.IsNullOrWhiteSpace(zone.ProcessName) ? "Не назначено" : "Ожидает расстановки";
            var monitor = monitors.FirstOrDefault(m => m.DeviceName == layout.DeviceName);
            if (monitor == null)
            {
                DebugLog.Line($"  SKIP раскладка {layout.DeviceName}: такого монитора нет в системе");
                foreach (var zone in layout.Zones) zone.Status = "Монитор отключён";
                continue;
            }

            if (monitor.WorkArea.Width <= 0 || monitor.WorkArea.Height <= 0)
            {
                DebugLog.Line($"  SKIP раскладка {layout.DeviceName}: рабочая область монитора пуста");
                continue;
            }
            active.Add((layout, monitor));
        }

        // Конкретные окна резервируются раньше общих назначений приложений.
        foreach (var (layout, monitor) in active)
        {
            foreach (var zone in layout.Zones.Where(z => z.SelectionMode == WindowSelectionMode.SpecificWindow &&
                         !string.IsNullOrWhiteSpace(z.ProcessName)))
            {
                var win = ResolveSpecificWindow(zone, openWindows, out var reason);
                if (win == null || !windows.Remove(win))
                {
                    zone.Status = win == null ? reason : "Это окно уже назначено другой зоне";
                    missing.Add($"{zone.Name} ({zone.ProcessName})");
                    continue;
                }
                zone.SelectWindow(win);
                BindTarget(targets, win, zone, layout, monitor);
            }
        }

        // Проход 0: закреплённое окно сохраняет свою область, пока открыто, приложение не изменено
        // и окно остаётся на мониторе своей зоны.
        if (current != null)
        {
            foreach (var (layout, monitor) in active)
            {
                foreach (var zone in layout.Zones)
                {
                    if (zone.SelectionMode == WindowSelectionMode.SpecificWindow || string.IsNullOrWhiteSpace(zone.ProcessName)) continue;
                    var existing = current.FirstOrDefault(t => t.Layout == layout && t.Zone == zone);
                    if (existing.Handle == IntPtr.Zero) continue;
                    var candidate = windows.FirstOrDefault(w => w.Handle == existing.Handle);
                    if (candidate == null ||
                        !string.Equals(candidate.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                        (!keepExistingAcrossMonitors && !isOnMonitor(candidate.Handle, monitor)))
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
                // Проход 0 уже закрепил окно: зона не должна забирать второе окно
                // или попадать в список отсутствующих приложений.
                if (targets.Any(t => t.Layout == layout && t.Zone == zone)) continue;
                if (zone.SelectionMode == WindowSelectionMode.SpecificWindow) continue;
                if (string.IsNullOrWhiteSpace(zone.ProcessName))
                {
                    DebugLog.Line($"  SKIP {layout.DeviceName}/{zone.Name}: приложение не назначено");
                    continue;
                }

                var win = windows.FirstOrDefault(w =>
                    string.Equals(w.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                    isOnMonitor(w.Handle, monitor));
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

        // Проход 2: оставшиеся зоны забирают окна нужного приложения.
        // Не отнимаем окно у монитора, где для того же процесса тоже есть зона.
        foreach (var (layout, monitor, zone) in pending)
        {
            var win = PickWindowForZone(windows, zone, monitor, pending, monitors, isOnMonitor, distanceToMonitor);
            if (win == null)
            {
                DebugLog.Line($"  MISS {layout.DeviceName}/{zone.Name}: окно {zone.ProcessName} не найдено");
                missing.Add($"{zone.Name} ({zone.ProcessName})");
                zone.Status = openWindows.Any(w => string.Equals(w.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase))
                    ? "Нет свободного окна приложения" : "Нет открытых окон приложения";
                continue;
            }
            windows.Remove(win);
            BindTarget(targets, win, zone, layout, monitor);
        }
        return targets;
    }

    internal static WindowInfo? ResolveSpecificWindow(Zone zone, IReadOnlyList<WindowInfo> windows, out string reason)
    {
        var candidates = windows.Where(w => string.Equals(w.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (zone.WindowHandle != IntPtr.Zero)
        {
            var exact = candidates.FirstOrDefault(w => w.Handle == zone.WindowHandle && w.ProcessId == zone.WindowProcessId);
            reason = "Выбранное окно закрыто или недоступно — выберите окно заново";
            return exact;
        }
        // После ручной загрузки HWND не восстанавливается: заголовок допустим только при уникальном совпадении.
        var matches = candidates.Where(w => !string.IsNullOrEmpty(zone.WindowTitle) && w.Title == zone.WindowTitle).ToList();
        reason = matches.Count > 1 ? "Несколько окон с таким заголовком — выберите нужное окно"
            : "Сохранённое окно не найдено — выберите окно заново";
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Выбирает окно для зоны: сначала на целевом мониторе, иначе ближайшее, не занятое другим монитором.</summary>
    private static WindowInfo? PickWindowForZone(
        List<WindowInfo> windows, Zone zone, MonitorInfo monitor,
        List<(MonitorLayout Layout, MonitorInfo Monitor, Zone Zone)> pending,
        List<MonitorInfo> monitors, Func<IntPtr, MonitorInfo, bool> isOnMonitor,
        Func<IntPtr, MonitorInfo, double> distanceToMonitor)
    {
        var candidates = windows
            .Where(w => string.Equals(w.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase))
            .Where(w => !IsReservedForOtherMonitor(w.Handle, zone.ProcessName, monitor, pending, monitors, isOnMonitor))
            .ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(w => isOnMonitor(w.Handle, monitor))
            .ThenBy(w => distanceToMonitor(w.Handle, monitor))
            .First();
    }

    /// <summary>Окно стоит на мониторе, где для того же процесса ещё ждёт своя зона — не трогаем.</summary>
    private static bool IsReservedForOtherMonitor(
        IntPtr hwnd, string? processName, MonitorInfo targetMonitor,
        List<(MonitorLayout Layout, MonitorInfo Monitor, Zone Zone)> pending,
        List<MonitorInfo> monitors, Func<IntPtr, MonitorInfo, bool> isOnMonitor)
    {
        var host = monitors.FirstOrDefault(m => isOnMonitor(hwnd, m));
        if (host == null || host.DeviceName == targetMonitor.DeviceName) return false;

        return pending.Any(p =>
            p.Monitor.DeviceName == host.DeviceName &&
            string.Equals(p.Zone.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
    }

    private static double DistanceToMonitorCenter(IntPtr hwnd, MonitorInfo monitor)
    {
        if (!TryGetVisibleRect(hwnd, out var vis)) return double.MaxValue;
        double cx = vis.X + vis.Width / 2, cy = vis.Y + vis.Height / 2;
        double mx = monitor.WorkArea.X + monitor.WorkArea.Width / 2;
        double my = monitor.WorkArea.Y + monitor.WorkArea.Height / 2;
        return (cx - mx) * (cx - mx) + (cy - my) * (cy - my);
    }

    /// <summary>Окно находится на указанном мониторе (Win32 MonitorFromWindow + запасной вариант по центру видимой области).</summary>
    public static bool IsOnMonitor(IntPtr hwnd, MonitorInfo monitor, IReadOnlyList<MonitorInfo> monitors)
    {
        var host = Win32.FindMonitorForWindow(hwnd, monitors);
        if (host != null) return host.DeviceName == monitor.DeviceName;

        if (!TryGetVisibleRect(hwnd, out var vis)) return false;
        double cx = vis.X + vis.Width / 2, cy = vis.Y + vis.Height / 2;
        var wa = monitor.WorkArea;
        return cx >= wa.Left && cx < wa.Right && cy >= wa.Top && cy < wa.Bottom;
    }

    /// <summary>Окно совпадает с целевой видимой областью (с допуском).</summary>
    public static bool IsAtTargetPosition(IntPtr hwnd, Rect targetVisibleRect, int tolerance = 8)
    {
        if (!TryGetVisibleRect(hwnd, out var vis)) return false;
        return Math.Abs(vis.X - targetVisibleRect.X) <= tolerance &&
               Math.Abs(vis.Y - targetVisibleRect.Y) <= tolerance &&
               Math.Abs(vis.Width - targetVisibleRect.Width) <= tolerance &&
               Math.Abs(vis.Height - targetVisibleRect.Height) <= tolerance;
    }

    /// <summary>Добавляет пару «окно → зона» и пишет строку привязки в лог.</summary>
    private static void BindTarget(List<WindowTarget> targets, WindowInfo win, Zone zone, MonitorLayout layout, MonitorInfo monitor)
    {
        zone.Status = "Готово к расстановке";
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

            // Повторная расстановка не должна менять размер уже размещённого окна.
            if (IsAtTargetPosition(hwnd, rect, 1))
                return !topmost || Win32.IsTopmost(hwnd) || Win32.SetWindowPos(hwnd,
                    Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

            int x = (int)Math.Round(rect.X);
            int y = (int)Math.Round(rect.Y);
            // Считаем размер от округлённых краёв, чтобы соседние области стыковались без щелей.
            int w = Math.Max(1, (int)Math.Round(rect.X + rect.Width) - x);
            int h = Math.Max(1, (int)Math.Round(rect.Y + rect.Height) - y);

            IntPtr z = topmost ? Win32.HWND_TOPMOST : Win32.HWND_TOP;
            uint flags = Win32.SWP_NOACTIVATE | (topmost ? 0 : Win32.SWP_NOZORDER);
            // Измеряем невидимую рамку ДО перемещения: обычному окну достаточно
            // одного SetWindowPos, без промежуточного уменьшения и мерцания.
            var frame = GetFrameOffsets(hwnd);
            var dpi = Win32.GetDpiForWindow(hwnd);
            if (!Win32.SetWindowPos(hwnd, z, x + frame.Left, y + frame.Top,
                    Math.Max(1, w - frame.Left + frame.Right),
                    Math.Max(1, h - frame.Top + frame.Bottom), flags))
            {
                // Типичный случай: ошибка 5 (доступ запрещён) — целевое окно запущено
                // от администратора, и Windows (UIPI) не даёт его двигать.
                DebugLog.Line($"  MoveTo {hwnd}: SetWindowPos НЕ СРАБОТАЛ, err={Marshal.GetLastWin32Error()} (5 = окно запущено от администратора)");
                return false;
            }

            // При переходе между мониторами с разным DPI рамка может измениться.
            // Только в этом случае нужна дополнительная коррекция.
            if (dpi != Win32.GetDpiForWindow(hwnd) && !IsAtTargetPosition(hwnd, rect, 1))
            {
                var updatedFrame = GetFrameOffsets(hwnd);
                if (frame == updatedFrame) return true;
                return Win32.SetWindowPos(hwnd, z, x + updatedFrame.Left, y + updatedFrame.Top,
                    Math.Max(1, w - updatedFrame.Left + updatedFrame.Right),
                    Math.Max(1, h - updatedFrame.Top + updatedFrame.Bottom), flags);
            }
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Line($"  MoveTo {hwnd}: {ex.Message}");
            return false;
        }
    }

    private static (int Left, int Top, int Right, int Bottom) GetFrameOffsets(IntPtr hwnd)
    {
        if (Win32.GetWindowRect(hwnd, out var outer) &&
            Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                out Win32.RECT visible, Marshal.SizeOf<Win32.RECT>()) == 0)
            return (outer.Left - visible.Left, outer.Top - visible.Top,
                outer.Right - visible.Right, outer.Bottom - visible.Bottom);
        return default;
    }

    /// <summary>Видимые границы окна (без невидимых рамок тени DWM); при неудаче — весь прямоугольник.</summary>
    public static bool TryGetVisibleRect(IntPtr hwnd, out Rect visible)
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
    internal static void FitToWorkArea(IntPtr hwnd, Rect workArea)
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
