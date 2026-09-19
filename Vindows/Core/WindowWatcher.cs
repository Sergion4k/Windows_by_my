namespace Vindows.Core;

/// <summary>
/// Контроль привязки: следит за окончанием перетаскивания/растягивания привязанных окон
/// (EVENT_SYSTEM_MOVESIZEEND) и возвращает их в назначенные области.
/// Пока программа запущена и <see cref="Enabled"/> — окна держатся в областях;
/// при закрытии программы хук снимается системой, и окна становятся свободными.
/// </summary>
public sealed class WindowWatcher : IDisposable
{
    // EVENT_SYSTEM_MOVESIZEEND = 0x000B (не 0x000A — это MOVESIZESTART).
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>Допуск в пикселях: меньшее отклонение считается «окно в области».</summary>
    private const int PositionTolerance = 8;

    private readonly Win32.WinEventDelegate _callback;
    private IntPtr _hook;
    private List<WindowTarget> _targets = new();
    private List<MonitorLayout> _layouts = new();
    private List<MonitorInfo> _monitors = new();
    private readonly HashSet<IntPtr> _moving = new();
    private bool _started;
    private readonly Dictionary<IntPtr, uint> _raisedWindows = new();

    public WindowWatcher() => _callback = OnWinEvent;

    /// <summary>Включает/выключает возврат окон в области.</summary>
    public bool Enabled { get; set; }
    public bool KeepOnTop { get; private set; }
    public MonitorMoveBehavior MonitorMoveBehavior { get; set; }
    public event Action? StateChanged;

    public void SetKeepOnTop(bool value)
    {
        KeepOnTop = value;
        ApplyTopmost();
    }

    public void ForgetZone(Zone zone) => SetTargets(_targets.Where(t => t.Zone != zone).ToList());

    public void RefreshControlStatus()
    {
        foreach (var t in _targets)
            if (t.Zone.Status is "Окно размещено" or "Окно закреплено")
                t.Zone.Status = Enabled ? "Окно закреплено" : "Окно размещено";
        StateChanged?.Invoke();
    }

    /// <summary>Устанавливает системный хук. Вызывать из потока UI (WPF) — у него есть очередь сообщений.
    /// Возвращает false, если хук не установился (контроль работать не будет).</summary>
    public bool Start()
    {
        if (_started) return true;
        try
        {
            _hook = Win32.SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
            _started = _hook != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            DebugLog.Line("Watcher.Start: " + ex.Message);
            _started = false;
        }
        return _started;
    }

    public void Stop()
    {
        if (!_started) return;
        try
        {
            Win32.UnhookWinEvent(_hook);
        }
        catch (Exception ex)
        {
            DebugLog.Line("Watcher.Stop: " + ex.Message);
        }
        _hook = IntPtr.Zero;
        _started = false;
    }

    /// <summary>Обновляет карту «окно → целевая область» после расстановки или смены сетки.</summary>
    public void UpdateTargets(List<MonitorLayout> layouts, List<MonitorInfo> monitors)
    {
        _layouts = layouts;
        _monitors = monitors;
        SetTargets(LayoutApplier.BuildTargets(layouts, monitors, out _, _targets,
            MonitorMoveBehavior == MonitorMoveBehavior.ReturnToZone));
    }

    /// <summary>Расставляет окна с сохранением старых привязок и обновляет карту контроля.</summary>
    public ApplyResult Reapply(List<MonitorLayout> layouts, List<MonitorInfo> monitors, IReadOnlySet<Zone>? zonesToPlace = null)
    {
        _layouts = layouts;
        _monitors = monitors;
        // Собственная расстановка тоже генерирует MOVESIZEEND — не перехватываем её в OnWinEvent.
        var wasEnabled = Enabled;
        Enabled = false;
        try
        {
            var result = LayoutApplier.Apply(layouts, monitors, out var placed, _targets,
                MonitorMoveBehavior == MonitorMoveBehavior.ReturnToZone, zonesToPlace);
            SetTargets(placed);
            ApplyTopmost();
            return result;
        }
        finally
        {
            Enabled = wasEnabled;
            RefreshControlStatus();
        }
    }

    /// <summary>Заменяет карту контроля; окна, выпавшие из привязки, снимаются с «поверх всех».</summary>
    private void SetTargets(List<WindowTarget> newTargets)
    {
        var keep = newTargets.Select(t => t.Handle).ToHashSet();
        foreach (var old in _targets)
            if (!keep.Contains(old.Handle))
                RestoreTopmost(old.Handle);

        _targets = newTargets;
    }

    /// <summary>Снимает только тот режим «поверх всех», который включила Vindows.</summary>
    public void ReleaseWindows()
    {
        foreach (var hwnd in _raisedWindows.Keys.ToList()) RestoreTopmost(hwnd);
    }

    private void RestoreTopmost(IntPtr hwnd)
    {
        if (!_raisedWindows.Remove(hwnd, out var originalPid)) return;
        try
        {
            if (!Win32.IsWindow(hwnd)) return;
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != originalPid) return;
            Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            DebugLog.Line($"Watcher.ClearTopmost {hwnd}: {ex.Message}");
        }
    }

    private void ApplyTopmost()
    {
        if (!KeepOnTop) { ReleaseWindows(); return; }
        foreach (var t in _targets)
        {
            if (!Win32.IsWindow(t.Handle) || Win32.IsTopmost(t.Handle)) continue;
            Win32.GetWindowThreadProcessId(t.Handle, out var pid);
            if (Win32.SetWindowPos(t.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE))
                _raisedWindows[t.Handle] = pid;
            else
                t.Zone.Status = "Не удалось включить «Поверх всех» — проверьте права доступа";
        }
        StateChanged?.Invoke();
    }

    internal static bool ShouldReleaseAfterMove(MonitorMoveBehavior behavior, bool onAssignedMonitor) =>
        behavior == MonitorMoveBehavior.ReleaseWindow && !onAssignedMonitor;

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (!Enabled) return;

            // Окно могло быть закрыто к моменту события — пропускаем.
            if (!Win32.IsWindow(hwnd)) return;

            var target = _targets.FirstOrDefault(t => t.Handle == hwnd);
            if (target.Handle == IntPtr.Zero) return;

            // Собственный MoveTo тоже шлёт MOVESIZEEND — не реагируем на него.
            if (_moving.Contains(hwnd)) return;

            // Окно в своей видимой области (с допуском) — не трогаем.
            if (LayoutApplier.IsAtTargetPosition(hwnd, target.Rect, PositionTolerance))
                return;

            var hostMonitor = _monitors.FirstOrDefault(m => m.DeviceName == target.Layout.DeviceName);

            // Отпущенное окно остаётся свободным до явной команды «Расставить окна».
            if (hostMonitor != null && ShouldReleaseAfterMove(MonitorMoveBehavior,
                    LayoutApplier.IsOnMonitor(hwnd, hostMonitor, _monitors)))
            {
                SetTargets(_targets.Where(t => t.Handle != hwnd).ToList());
                target.Zone.Status = "Окно отпущено на другом мониторе — повторная привязка кнопкой «Расставить окна»";
                StateChanged?.Invoke();
                return;
            }

            // Пользователь сдвинул/растянул привязанное окно — возвращаем в область (и снова поверх всех).
            _moving.Add(hwnd);
            try
            {
                if (LayoutApplier.MoveTo(hwnd, target.Rect))
                {
                    LayoutApplier.FitToWorkArea(hwnd, target.WorkArea);
                    target.Zone.Status = LayoutApplier.IsAtTargetPosition(hwnd, target.Rect)
                        ? "Окно закреплено" : "Окно не помещается в зону";
                    ApplyTopmost();
                }
                else target.Zone.Status = "Не удалось вернуть окно в зону — проверьте права доступа";
                StateChanged?.Invoke();
            }
            finally
            {
                _moving.Remove(hwnd);
            }
        }
        catch (Exception ex)
        {
            // Исключение в колбэке хука роняет приложение целиком — глушим и логируем.
            DebugLog.Line("Watcher.OnWinEvent: " + ex.Message);
        }
    }

    public void Dispose()
    {
        Stop();
        ReleaseWindows();
    }
}
