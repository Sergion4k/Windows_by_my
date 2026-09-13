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
    private bool _started;

    public WindowWatcher() => _callback = OnWinEvent;

    /// <summary>Включает/выключает возврат окон в области.</summary>
    public bool Enabled { get; set; }

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
        SetTargets(LayoutApplier.BuildTargets(layouts, monitors, out _, _targets));
    }

    /// <summary>Расставляет окна с сохранением старых привязок и обновляет карту контроля.</summary>
    public ApplyResult Reapply(List<MonitorLayout> layouts, List<MonitorInfo> monitors)
    {
        _layouts = layouts;
        _monitors = monitors;
        var result = LayoutApplier.Apply(layouts, monitors, out var placed, _targets);
        SetTargets(placed);
        return result;
    }

    /// <summary>Заменяет карту контроля; окна, выпавшие из привязки, снимаются с «поверх всех».</summary>
    private void SetTargets(List<WindowTarget> newTargets)
    {
        var keep = newTargets.Select(t => t.Handle).ToHashSet();
        foreach (var old in _targets)
            if (!keep.Contains(old.Handle))
                ClearTopmost(old.Handle);

        _targets = newTargets;
    }

    /// <summary>Снимает «поверх всех окон» со всех привязанных окон (контроль выключен / закрытие программы).</summary>
    public void ReleaseWindows()
    {
        foreach (var t in _targets)
        {
            try
            {
                ClearTopmost(t.Handle);
            }
            catch (Exception ex)
            {
                DebugLog.Line($"Watcher.ReleaseWindows {t.Handle}: {ex.Message}");
            }
        }
    }

    private static void ClearTopmost(IntPtr hwnd)
    {
        try
        {
            if (!Win32.IsWindow(hwnd)) return;
            Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            DebugLog.Line($"Watcher.ClearTopmost {hwnd}: {ex.Message}");
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (!Enabled) return;

            // Окно могло быть закрыто к моменту события — пропускаем.
            if (!Win32.IsWindow(hwnd)) return;

            var target = _targets.FirstOrDefault(t => t.Handle == hwnd);
            if (target.Handle == IntPtr.Zero) return;

            if (!Win32.GetWindowRect(hwnd, out var r)) return;

            // Окно в своей области (с допуском) — не трогаем.
            if (Math.Abs(r.Left - target.Rect.X) <= PositionTolerance &&
                Math.Abs(r.Top - target.Rect.Y) <= PositionTolerance &&
                Math.Abs((r.Right - r.Left) - target.Rect.Width) <= PositionTolerance &&
                Math.Abs((r.Bottom - r.Top) - target.Rect.Height) <= PositionTolerance)
                return;

            // Окно утащили на другой монитор: возможно, там есть зона этого же приложения.
            // Пересопоставляем привязки вместо того, чтобы тянуть окно обратно на старый экран.
            if (!LayoutApplier.IsOnMonitor(hwnd, target.WorkArea))
            {
                DebugLog.Line($"Watcher: {hwnd} ушёл с монитора зоны {target.Zone.Name} — повторное сопоставление");
                Reapply(_layouts, _monitors);
                return;
            }

            // Пользователь сдвинул/растянул привязанное окно — возвращаем в область (и снова поверх всех).
            LayoutApplier.MoveTo(hwnd, target.Rect, topmost: true);
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
