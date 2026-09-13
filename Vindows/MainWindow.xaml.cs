using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Vindows.Core;

namespace Vindows;

public partial class MainWindow : Window
{
    private readonly List<MonitorInfo> _monitors = new();
    private readonly List<MonitorLayout> _layouts = new();
    private readonly ObservableCollection<WindowInfo> _windows = new();
    private readonly WindowWatcher _watcher = new();
    private readonly Dictionary<FrameworkElement, GripData> _grips = new();
    private MonitorInfo? _currentMonitor;
    private MonitorLayout? _currentLayout;
    private GripData? _dragGrip;
    private Point _dragLast;
    private bool _initialized;

    /// <summary>Разделитель сетки: вертикальный (между колонками) или горизонтальный (между строками).</summary>
    private sealed record GripData(bool IsVertical, int Index);

    /// <summary>Список открытых окон для ComboBox в каждой зоне.</summary>
    public ObservableCollection<WindowInfo> Windows => _windows;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
        Closed += (_, _) => _watcher.Dispose();
        // Когда пользователь выбирает наше окно — поднимаем его поверх расставленных окон.
        Activated += (_, _) => { if (ControlCheck.IsChecked == true) RaiseToTopmost(); };
    }

    /// <summary>Поднимает окно Vindows наверх слоя «поверх всех», чтобы оно не пряталось
    /// за привязанными окнами после расстановки.</summary>
    private void RaiseToTopmost()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            DebugLog.Line("RaiseToTopmost: " + ex.Message);
        }
    }

    /// <summary>Общий путь повторной расстановки: расставить окна, поднять своё окно, перерисовать UI.</summary>
    private ApplyResult ReapplyAll()
    {
        var result = _watcher.Reapply(_layouts, _monitors);
        if (ControlCheck.IsChecked == true) RaiseToTopmost();
        RebuildZoneList();
        RedrawPreview();
        return result;
    }

    private void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            InitializeCore();
        }
        catch (Exception ex)
        {
            DebugLog.Line("Initialize: " + ex);
            StatusText.Text = "Ошибка инициализации: " + ex.Message;
        }
    }

    private void InitializeCore()
    {
        DebugLog.Clear();
        DebugLog.Line("=== Запуск Vindows ===");
        _monitors.AddRange(Win32.GetMonitors());
        DebugLog.Line($"Система сообщила о {_monitors.Count} мониторах: " +
            string.Join("; ", _monitors.Select(m => $"{m.DeviceName} [{m.DisplayName}]")));
        var file = LayoutStore.Load();
        DebugLog.Line($"Загружено раскладок из layout.json: {file.Monitors.Count} (Контроль окон: {file.ControlEnabled})");
        _layouts.AddRange(file.Monitors);
        foreach (var m in _monitors)
            GetLayoutFor(m);

        MonitorsList.ItemsSource = _monitors;
        MonitorsList.SelectedIndex = _monitors.Count > 0 ? 0 : -1;
        RefreshWindows();

        // Галочка управляет контролем окон; подписываемся после установки значения,
        // чтобы не сработать во время инициализации.
        ControlCheck.IsChecked = file.ControlEnabled;
        Topmost = file.ControlEnabled;
        ControlCheck.Checked += ControlCheck_Changed;
        ControlCheck.Unchecked += ControlCheck_Changed;

        _watcher.Enabled = file.ControlEnabled;
        var hookOk = _watcher.Start();

        string status;
        if (file.ControlEnabled)
        {
            // Восстанавливаем привязку: пока программы не было, окна могли сдвинуть.
            var result = ReapplyAll();
            status = $"Мониторов: {_monitors.Count}. Привязка восстановлена — размещено окон: {result.Placed}." + ApplyNotes(result);
        }
        else
        {
            // Снимаем «поверх всех», если прошлый запуск завершился некорректно.
            _watcher.ReleaseWindows();
            _watcher.UpdateTargets(_layouts, _monitors);
            status = $"Мониторов: {_monitors.Count}. Контроль окон выключен — окна свободны.";
        }
        if (!hookOk) status += "  ВНИМАНИЕ: хук не установился, контроль перемещения не работает.";
        StatusText.Text = status;
    }

    /// <summary>Возвращает раскладку монитора; если её нет — создаёт сетку по умолчанию (2×1).</summary>
    private MonitorLayout GetLayoutFor(MonitorInfo monitor)
    {
        var layout = _layouts.FirstOrDefault(l => l.DeviceName == monitor.DeviceName);
        if (layout != null)
        {
            DebugLog.Line($"GetLayoutFor {monitor.DeviceName}: найдена раскладка, зон={layout.Zones.Count}");
            return layout;
        }

        DebugLog.Line($"GetLayoutFor {monitor.DeviceName}: раскладки нет — создаю сетку по умолчанию");
        layout = new MonitorLayout { DeviceName = monitor.DeviceName };
        layout.Zones = LayoutApplier.BuildGridZones(layout.Columns, layout.Rows, layout.Gap, monitor.WorkArea);
        _layouts.Add(layout);
        return layout;
    }

    private void SetupCurrentMonitorUI()
    {
        if (_currentMonitor == null) return;
        _currentLayout = GetLayoutFor(_currentMonitor);

        ColsBox.Text = _currentLayout.Columns.ToString();
        RowsBox.Text = _currentLayout.Rows.ToString();
        GapBox.Text = _currentLayout.Gap.ToString();

        RebuildZoneList();
        RedrawPreview();
    }

    private void RebuildZoneList()
    {
        if (_currentLayout == null) return;
        ZonesList.ItemsSource = _currentLayout.Zones.Select(z => new ZoneVM(z)).ToList();
    }

    private void RefreshWindows()
    {
        // Снимок назначений: очистка списка сбрасывает выбор в ComboBox, и TwoWay-привязка
        // записывает null в зоны. Восстанавливаем назначения после перезаполнения списка.
        var saved = _layouts.ToDictionary(l => l, l => l.Zones.Select(z => z.ProcessName).ToList());

        _windows.Clear();
        _windows.Add(new WindowInfo { Handle = IntPtr.Zero, Title = "— не выбрано —", ProcessName = "" });
        foreach (var w in WindowEnumerator.GetOpenWindows())
            _windows.Add(w);

        var names = _windows.Select(w => w.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var layout in _layouts)
        {
            var before = saved[layout];
            for (int i = 0; i < layout.Zones.Count; i++)
            {
                var desired = i < before.Count ? before[i] : null;
                layout.Zones[i].ProcessName =
                    !string.IsNullOrEmpty(desired) && names.Contains(desired) ? desired : null;
            }
        }
        RebuildZoneList();
    }

    private void RedrawPreview()
    {
        PreviewCanvas.Children.Clear();
        _grips.Clear();
        if (_currentMonitor == null || _currentLayout == null) return;

        var wa = _currentMonitor.WorkArea;
        if (wa.Width <= 0 || wa.Height <= 0) return;
        double aw = PreviewCanvas.ActualWidth;
        double ah = PreviewCanvas.ActualHeight;
        if (aw <= 0 || ah <= 0) return;

        var (scale, ox, oy) = PreviewTransform();

        var border = new Rectangle
        {
            Width = wa.Width * scale,
            Height = wa.Height * scale,
            Stroke = Brushes.Gray,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(border, ox);
        Canvas.SetTop(border, oy);
        PreviewCanvas.Children.Add(border);

        int i = 0;
        foreach (var zone in _currentLayout.Zones)
        {
            i++;
            var r = LayoutApplier.ZoneToPixelRect(zone, wa);
            var rect = new Rectangle
            {
                Width = r.Width * scale,
                Height = r.Height * scale,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2B, 0x7C, 0xD3)),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0x2B, 0x7C, 0xD3)),
            };
            Canvas.SetLeft(rect, ox + r.X * scale);
            Canvas.SetTop(rect, oy + r.Y * scale);
            PreviewCanvas.Children.Add(rect);

            var label = new TextBlock { Text = i.ToString(), Foreground = Brushes.LightGray, FontSize = 11 };
            Canvas.SetLeft(label, ox + r.X * scale + 4);
            Canvas.SetTop(label, oy + r.Y * scale + 2);
            PreviewCanvas.Children.Add(label);
        }

        DrawSplitterGrips(scale, ox, oy);
    }

    /// <summary>Масштаб и отступы для отображения рабочей области в предпросмотре.</summary>
    private (double Scale, double OffsetX, double OffsetY) PreviewTransform()
    {
        var wa = _currentMonitor!.WorkArea;
        double aw = PreviewCanvas.ActualWidth;
        double ah = PreviewCanvas.ActualHeight;
        // Вырожденная рабочая область/канвас дали бы NaN/бесконечный масштаб.
        if (wa.Width <= 0 || wa.Height <= 0 || aw <= 0 || ah <= 0) return (1, 0, 0);
        double scale = Math.Min(aw / wa.Width, ah / wa.Height);
        return (scale, (aw - wa.Width * scale) / 2, (ah - wa.Height * scale) / 2);
    }

    /// <summary>Рисует линии и перетаскиваемые ручки на разделителях сетки.</summary>
    private void DrawSplitterGrips(double scale, double ox, double oy)
    {
        var layout = _currentLayout!;
        var zones = layout.Zones;
        int cols = layout.Columns, rows = layout.Rows;
        if (zones.Count != cols * rows) return;

        var wa = _currentMonitor!.WorkArea;
        double waW = wa.Width * scale, waH = wa.Height * scale;
        var lineBrush = new SolidColorBrush(Color.FromArgb(90, 0x2B, 0x7C, 0xD3));

        // Горизонтальные разделители (между строками).
        for (int r = 0; r < rows - 1; r++)
        {
            var zone = zones[r * cols];
            double sepY = oy + (zone.Y + zone.Height) / 100.0 * waH;
            var line = new Rectangle { Width = waW, Height = 1, Fill = lineBrush };
            Canvas.SetLeft(line, ox);
            Canvas.SetTop(line, sepY);
            PreviewCanvas.Children.Add(line);
            PreviewCanvas.Children.Add(CreateGrip(new GripData(false, r), ox + waW / 2, sepY));
        }

        // Вертикальные разделители (между колонками) — поверх горизонтальных.
        for (int c = 0; c < cols - 1; c++)
        {
            var zone = zones[c];
            double sepX = ox + (zone.X + zone.Width) / 100.0 * waW;
            var line = new Rectangle { Width = 1, Height = waH, Fill = lineBrush };
            Canvas.SetLeft(line, sepX);
            Canvas.SetTop(line, oy);
            PreviewCanvas.Children.Add(line);
            PreviewCanvas.Children.Add(CreateGrip(new GripData(true, c), sepX, oy + waH / 2));
        }
    }

    /// <summary>Создаёт ручку разделителя: прозрачная зона захвата с видимым «гроуфером».</summary>
    private FrameworkElement CreateGrip(GripData data, double x, double y)
    {
        bool vertical = data.IsVertical;
        var hitArea = new Border
        {
            Width = vertical ? 22 : 56,
            Height = vertical ? 56 : 22,
            Background = Brushes.Transparent,
            Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS,
            Child = new Border
            {
                Width = vertical ? 8 : 30,
                Height = vertical ? 30 : 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x7C, 0xD3)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(hitArea, x - hitArea.Width / 2);
        Canvas.SetTop(hitArea, y - hitArea.Height / 2);
        _grips[hitArea] = data;
        return hitArea;
    }

    /// <summary>Находит ручку разделителя под курсором.</summary>
    private GripData? HitTestGrip(Point pos)
    {
        foreach (var el in _grips.Keys)
        {
            double left = Canvas.GetLeft(el), top = Canvas.GetTop(el);
            if (pos.X >= left && pos.X < left + el.Width && pos.Y >= top && pos.Y < top + el.Height)
                return _grips[el];
        }
        return null;
    }

    /// <summary>Пропорция областей по сторонам разделителя, например «42% / 58%».</summary>
    private string SplitStatus(GripData grip)
    {
        var layout = _currentLayout;
        if (layout == null) return "";
        var zones = layout.Zones;
        int cols = layout.Columns, rows = layout.Rows;
        if (zones.Count != cols * rows) return "";

        double left = 0, right = 0;
        if (grip.IsVertical)
        {
            for (int r = 0; r < rows; r++)
            {
                left += zones[r * cols + grip.Index].Width;
                right += zones[r * cols + grip.Index + 1].Width;
            }
        }
        else
        {
            for (int c = 0; c < cols; c++)
            {
                left += zones[grip.Index * cols + c].Height;
                right += zones[(grip.Index + 1) * cols + c].Height;
            }
        }

        double total = left + right;
        return total <= 0 ? "" : $"{left / total * 100:0}% / {right / total * 100:0}%";
    }

    // ---------- Обработчики ----------

    private void MonitorsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentMonitor = MonitorsList.SelectedItem as MonitorInfo;
        SetupCurrentMonitorUI();
    }

    private void ApplyGridBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLayout == null || _currentMonitor == null) return;
        if (!int.TryParse(ColsBox.Text, out var cols) || cols < 1) { StatusText.Text = "Колонки: введите число ≥ 1"; return; }
        if (!int.TryParse(RowsBox.Text, out var rows) || rows < 1) { StatusText.Text = "Строки: введите число ≥ 1"; return; }
        if (!int.TryParse(GapBox.Text, out var gap) || gap < 0 || gap > 100) { StatusText.Text = "Зазор: введите число 0..100"; return; }

        var newZones = LayoutApplier.BuildGridZones(cols, rows, gap, _currentMonitor.WorkArea);

        // Переносим назначенные приложения по индексу зоны.
        for (int i = 0; i < Math.Min(newZones.Count, _currentLayout.Zones.Count); i++)
            newZones[i].ProcessName = _currentLayout.Zones[i].ProcessName;

        _currentLayout.Columns = cols;
        _currentLayout.Rows = rows;
        _currentLayout.Gap = gap;
        _currentLayout.Zones = newZones;

        RebuildZoneList();
        RedrawPreview();
        if (ControlCheck.IsChecked == true)
            _watcher.UpdateTargets(_layouts, _monitors);
        StatusText.Text = $"Сетка {cols}×{rows} применена: {newZones.Count} областей";
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        StatusText.Text = $"Открытых окон: {_windows.Count - 1}";
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        // Сначала расставляем (Apply сам получает актуальный список окон), затем обновляем UI.
        var result = ReapplyAll();
        SaveLayouts();
        RefreshWindows();

        var text = $"Размещено окон: {result.Placed}.";
        if (result.Missing.Count > 0)
            text += $"  Не найдены: {string.Join(", ", result.Missing)}";
        else if (result.Placed == 0)
            text += "  Приложения для областей не назначены.";
        text += ApplyNotes(result);
        StatusText.Text = text;
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = SaveLayouts()
            ? $"Раскладка сохранена: {LayoutStore.FilePath}"
            : "Не удалось сохранить раскладку (нет доступа к папке %APPDATA%\\Vindows).";
    }

    /// <summary>Сохраняет раскладку вместе с состоянием контроля окон. Возвращает успех записи.</summary>
    private bool SaveLayouts() =>
        LayoutStore.Save(new LayoutFile
        {
            Monitors = _layouts,
            ControlEnabled = ControlCheck.IsChecked == true,
        });

    private void ControlCheck_Changed(object sender, RoutedEventArgs e)
    {
        _watcher.Enabled = ControlCheck.IsChecked == true;
        // Пока расставленные окна «поверх всех», держим и своё окно в этом слое,
        // иначе его не будет видно из-за привязанных окон.
        Topmost = ControlCheck.IsChecked == true;

        if (ControlCheck.IsChecked == true)
        {
            // Контроль включили — возвращаем все привязанные окна в их области.
            var result = ReapplyAll();
            StatusText.Text = $"Контроль включён. Размещено окон: {result.Placed}." + ApplyNotes(result);
        }
        else
        {
            _watcher.ReleaseWindows();
            StatusText.Text = "Контроль выключен — окна можно свободно перемещать.";
        }

        SaveLayouts();
    }

    private void LoadBtn_Click(object sender, RoutedEventArgs e)
    {
        var file = LayoutStore.Load();
        _layouts.Clear();
        _layouts.AddRange(file.Monitors);
        foreach (var m in _monitors)
            GetLayoutFor(m);

        SetupCurrentMonitorUI();
        RefreshWindows();

        ControlCheck.IsChecked = file.ControlEnabled;
        Topmost = file.ControlEnabled;
        _watcher.Enabled = file.ControlEnabled;
        if (file.ControlEnabled)
        {
            var result = ReapplyAll();
            StatusText.Text = $"Раскладка загружена. Размещено окон: {result.Placed}." + ApplyNotes(result);
        }
        else
        {
            _watcher.ReleaseWindows();
            _watcher.UpdateTargets(_layouts, _monitors);
            StatusText.Text = "Раскладка загружена (контроль окон выключен)";
        }
    }

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawPreview();

    private void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentLayout == null || _currentMonitor == null) return;

        var grip = HitTestGrip(e.GetPosition(PreviewCanvas));
        if (grip == null) return;

        _dragGrip = grip;
        _dragLast = e.GetPosition(PreviewCanvas);
        Mouse.OverrideCursor = grip.IsVertical ? Cursors.SizeWE : Cursors.SizeNS;
        PreviewCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragGrip == null || _currentLayout == null || _currentMonitor == null) return;

        var (scale, _, _) = PreviewTransform();
        if (scale <= 0) return;

        var pos = e.GetPosition(PreviewCanvas);
        double delta = _dragGrip.IsVertical
            ? (pos.X - _dragLast.X) / (_currentMonitor.WorkArea.Width * scale) * 100.0
            : (pos.Y - _dragLast.Y) / (_currentMonitor.WorkArea.Height * scale) * 100.0;

        double applied = _dragGrip.IsVertical
            ? LayoutApplier.MoveVerticalSplitter(_currentLayout.Zones, _currentLayout.Columns, _dragGrip.Index, delta)
            : LayoutApplier.MoveHorizontalSplitter(_currentLayout.Zones, _currentLayout.Columns, _dragGrip.Index, delta);
        if (Math.Abs(applied) < 1e-9) return;

        // Сдвигаем точку отсчёта ровно на применённую величину, чтобы ручка не отставала от курсора
        // при упоре в минимальный размер области.
        if (_dragGrip.IsVertical)
            _dragLast = new Point(_dragLast.X + applied / 100.0 * _currentMonitor.WorkArea.Width * scale, pos.Y);
        else
            _dragLast = new Point(pos.X, _dragLast.Y + applied / 100.0 * _currentMonitor.WorkArea.Height * scale);

        RedrawPreview();
        StatusText.Text = $"Разделитель: {SplitStatus(_dragGrip)}";
    }

    private void PreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragGrip == null) return;

        var grip = _dragGrip;
        _dragGrip = null;
        PreviewCanvas.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        e.Handled = true;

        RebuildZoneList();
        if (ControlCheck.IsChecked == true)
        {
            var result = ReapplyAll();
            StatusText.Text = $"Разделитель зафиксирован: {SplitStatus(grip)}. Размещено окон: {result.Placed}." + ApplyNotes(result);
        }
        else
        {
            _watcher.UpdateTargets(_layouts, _monitors);
            StatusText.Text = $"Разделитель зафиксирован: {SplitStatus(grip)}. Нажмите «Расставить окна», чтобы применить.";
        }
    }

    /// <summary>Сообщение о подстройке сетки, окнах, которые не поместились, и окнах без прав на перемещение.</summary>
    private static string ApplyNotes(ApplyResult result) =>
        (result.GridAdjusted ? "  Сетка подстроена под размеры окон." : "") +
        (result.Oversized.Count > 0
            ? $"  Не поместились в зоны (мин. размер окна): {string.Join(", ", result.Oversized)}"
            : "") +
        (result.Denied.Count > 0
            ? $"  Нет прав на перемещение (окно от администратора?): {string.Join(", ", result.Denied)}"
            : "");
}
