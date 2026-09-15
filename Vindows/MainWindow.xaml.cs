using System.Collections.ObjectModel;
using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Vindows.Core;

namespace Vindows;

public partial class MainWindow : Window
{
    private readonly List<MonitorInfo> _monitors = new();
    private readonly List<MonitorLayout> _layouts = new();
    private List<WindowInfo> _windows = new();
    private readonly WindowWatcher _watcher = new();
    private readonly Dictionary<FrameworkElement, GripData> _grips = new();
    private readonly Dictionary<Zone, ZoneVM> _zoneViewModels = new();
    private MonitorInfo? _currentMonitor;
    private MonitorLayout? _currentLayout;
    private GripData? _dragGrip;
    private Point _dragLast;
    private bool _initialized;
    private bool _isExiting;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _windowRefreshTimer;
    private readonly DispatcherTimer _monitorRefreshTimer;
    private readonly DispatcherTimer _externalDragTimer;
    private bool _leftButtonWasDown;
    private WindowInfo? _externalDragWindow;

    /// <summary>Разделитель сетки: вертикальный (между колонками) или горизонтальный (между строками).</summary>
    private sealed record GripData(bool IsVertical, int Index);

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = CreateTrayIcon();
        _windowRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _windowRefreshTimer.Tick += (_, _) => RefreshWindows();
        _monitorRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _monitorRefreshTimer.Tick += (_, _) => RefreshMonitors(false);
        _externalDragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _externalDragTimer.Tick += ExternalDragTimer_Tick;
        Loaded += (_, _) => Initialize();
        SourceInitialized += (_, _) => ApplyRoundedWindowCorners();
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        // Когда пользователь выбирает наше окно — поднимаем его поверх расставленных окон.
        Activated += (_, _) => { if (TopmostCheck.IsChecked == true) RaiseToTopmost(); };
        _watcher.StateChanged += RefreshZoneStatuses;
    }

    private void ApplyRoundedWindowCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = Win32.DWMWCP_ROUND;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            DebugLog.Line("Rounded window corners: " + ex.Message);
        }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Показать Vindows", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitFromTray());

        var icon = Drawing.SystemIcons.Application;
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            try { icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? icon; }
            catch (Exception ex) { DebugLog.Line("Tray icon: " + ex.Message); }
        }

        var trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Vindows",
            ContextMenuStrip = menu,
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => ShowFromTray();
        return trayIcon;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        Hide();
        _trayIcon.ShowBalloonTip(1500, "Vindows", "Приложение свернуто в трей", Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _isExiting = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
        _trayIcon.ShowBalloonTip(1500, "Vindows", "Приложение свернуто в трей", Forms.ToolTipIcon.Info);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _windowRefreshTimer.Stop();
        _monitorRefreshTimer.Stop();
        _externalDragTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _watcher.Dispose();
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
        if (TopmostCheck.IsChecked == true) RaiseToTopmost();
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
            _windowRefreshTimer.Start();
            _monitorRefreshTimer.Start();
            _externalDragTimer.Start();
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
        // Каждый запуск начинается с пустых назначений и стандартной сетки.
        // Сохранённая раскладка загружается только по кнопке «Загрузить».
        var file = new LayoutFile();
        _layouts.Clear();
        DebugLog.Line("Новый сеанс: настройки по умолчанию, приложения не назначены");
        foreach (var m in _monitors)
            GetLayoutFor(m);

        MonitorTabs.ItemsSource = _monitors;
        MonitorTabs.SelectedIndex = _monitors.Count > 0 ? 0 : -1;
        RefreshWindows();

        // Галочка управляет контролем окон; подписываемся после установки значения,
        // чтобы не сработать во время инициализации.
        ControlCheck.IsChecked = file.ControlEnabled;
        TopmostCheck.IsChecked = file.KeepOnTop;
        Topmost = file.KeepOnTop;
        ControlCheck.Checked += ControlCheck_Changed;
        ControlCheck.Unchecked += ControlCheck_Changed;
        TopmostCheck.Checked += TopmostCheck_Changed;
        TopmostCheck.Unchecked += TopmostCheck_Changed;

        _watcher.Enabled = file.ControlEnabled;
        _watcher.MonitorMoveBehavior = MonitorMoveBehavior.ReturnToZone;
        var hookOk = _watcher.Start();

        _watcher.UpdateTargets(_layouts, _monitors);
        string status = $"Мониторов: {_monitors.Count}. Новый сеанс: сетка 2×1, приложения не назначены.";
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
        if (_currentMonitor == null)
        {
            _currentLayout = null;
            _zoneViewModels.Clear();
            RedrawPreview();
            return;
        }
        _currentLayout = GetLayoutFor(_currentMonitor);

        ColsBox.Text = _currentLayout.Columns.ToString();
        RowsBox.Text = _currentLayout.Rows.ToString();
        GapBox.Text = _currentLayout.Gap.ToString();

        RebuildZoneList();
        RedrawPreview();
    }

    private void RebuildZoneList()
    {
        _zoneViewModels.Clear();
        if (_currentLayout == null) return;
        foreach (var (zone, index) in _currentLayout.Zones.Select((zone, index) => (zone, index)))
        {
            var zoneVm = new ZoneVM(zone, ZonePosition(index, _currentLayout.Columns, _currentLayout.Rows), _windows, () =>
            {
                _watcher.ForgetZone(zone);
                RedrawPreview();
            });
            _zoneViewModels[zone] = zoneVm;
        }
    }

    internal static string ZonePosition(int index, int columns, int rows)
    {
        if (columns == 1 && rows == 1) return "весь экран";
        if (rows == 1 && columns == 2) return index == 0 ? "слева" : "справа";
        if (columns == 1 && rows == 2) return index == 0 ? "сверху" : "снизу";
        return $"строка {index / Math.Max(1, columns) + 1}, колонка {index % Math.Max(1, columns) + 1}";
    }

    private void RefreshZoneStatuses()
    {
        foreach (var zone in _zoneViewModels.Values)
            zone.RefreshStatus();
    }

    private void RefreshWindows()
    {
        // Новый снимок не изменяет назначения отсутствующих окон и приложений.
        var ownProcess = Process.GetCurrentProcess().ProcessName;
        _windows = WindowEnumerator.GetOpenWindows()
            .Where(w => !string.Equals(w.ProcessName, ownProcess, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var zone in _layouts.SelectMany(l => l.Zones))
        {
            if (string.IsNullOrWhiteSpace(zone.ProcessName)) continue;
            if (zone.SelectionMode == WindowSelectionMode.SpecificWindow)
            {
                var window = LayoutApplier.ResolveSpecificWindow(zone, _windows, out var reason);
                if (window == null) zone.Status = reason;
                else zone.SelectWindow(window);
            }
            else if (!_windows.Any(w => string.Equals(w.ProcessName, zone.ProcessName, StringComparison.OrdinalIgnoreCase)))
                zone.Status = "Нет открытых окон приложения";
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
            Canvas.SetLeft(rect, ox + (r.X - wa.X) * scale);
            Canvas.SetTop(rect, oy + (r.Y - wa.Y) * scale);
            PreviewCanvas.Children.Add(rect);

            var label = new TextBlock { Text = i.ToString(), Foreground = Brushes.LightGray, FontSize = 11 };
            Canvas.SetLeft(label, ox + (r.X - wa.X) * scale + 4);
            Canvas.SetTop(label, oy + (r.Y - wa.Y) * scale + 2);
            PreviewCanvas.Children.Add(label);

            var dropHint = new TextBlock
            {
                Text = "+",
                Foreground = new SolidColorBrush(Color.FromArgb(150, 0x55, 0xB5, 0xFF)),
                FontSize = 30,
                FontWeight = FontWeights.Light,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(dropHint, ox + (r.X - wa.X) * scale + r.Width * scale / 2 - 9);
            Canvas.SetTop(dropHint, oy + (r.Y - wa.Y) * scale + r.Height * scale / 2 - 19);
            PreviewCanvas.Children.Add(dropHint);
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

    private void MonitorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentMonitor = MonitorTabs.SelectedItem as MonitorInfo;
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
            newZones[i].CopyAssignmentFrom(_currentLayout.Zones[i]);

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
        StatusText.Text = $"Открытых окон: {_windows.Count}. Назначения сохранены.";
    }

    private void RefreshMonitorsBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshMonitors(true);
    }

    private void RefreshMonitors(bool showStatus)
    {
        var selectedDevice = _currentMonitor?.DeviceName;
        _monitors.Clear();
        _monitors.AddRange(Win32.GetMonitors());
        foreach (var m in _monitors)
            GetLayoutFor(m);

        MonitorTabs.ItemsSource = null;
        MonitorTabs.ItemsSource = _monitors;
        MonitorTabs.SelectedItem = selectedDevice != null
            ? _monitors.FirstOrDefault(m => m.DeviceName == selectedDevice) ?? _monitors.FirstOrDefault()
            : _monitors.FirstOrDefault();

        if (ControlCheck.IsChecked == true)
        {
            var result = ReapplyAll();
            if (showStatus)
                StatusText.Text = $"Мониторов: {_monitors.Count}. Перечитано из системы. Размещено окон: {result.Placed}." + ApplyNotes(result);
        }
        else
        {
            _watcher.UpdateTargets(_layouts, _monitors);
            if (showStatus)
                StatusText.Text = $"Мониторов: {_monitors.Count}. Список обновлён из системы.";
        }
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        // Сначала расставляем (Apply сам получает актуальный список окон), затем обновляем UI.
        var result = ReapplyAll();
        RefreshWindows();

        var text = $"Размещено окон: {result.Placed}.";
        if (result.Missing.Count > 0)
            text += $"  Не найдены: {string.Join(", ", result.Missing)}";
        else if (result.Placed == 0)
            text += "  Приложения для областей не назначены.";
        text += ApplyNotes(result);
        StatusText.Text = text;
    }

    private void ControlCheck_Changed(object sender, RoutedEventArgs e)
    {
        _watcher.Enabled = ControlCheck.IsChecked == true;

        if (ControlCheck.IsChecked == true)
        {
            // Контроль включили — возвращаем все привязанные окна в их области.
            var result = ReapplyAll();
            StatusText.Text = $"Контроль включён. Размещено окон: {result.Placed}." + ApplyNotes(result);
        }
        else
        {
            _watcher.RefreshControlStatus();
            StatusText.Text = "Возврат в зоны выключен — окна можно свободно перемещать.";
        }
    }

    private void TopmostCheck_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheck.IsChecked == true;
        _watcher.SetKeepOnTop(Topmost);
        if (Topmost) RaiseToTopmost();
        StatusText.Text = Topmost ? "Режим «Поверх всех» включён для назначенных окон."
            : "Режим «Поверх всех» выключен. Возврат в зоны настраивается отдельно.";
    }

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PreviewCanvas.Clip = new RectangleGeometry(
            new Rect(0, 0, PreviewCanvas.ActualWidth, PreviewCanvas.ActualHeight), 12, 12);
        RedrawPreview();
    }

    private void PreviewCanvas_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(WindowInfo))
            ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void PreviewCanvas_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(WindowInfo)) || _currentLayout == null || _currentMonitor == null)
            return;

        var window = e.Data.GetData(typeof(WindowInfo)) as WindowInfo;
        if (window == null) return;

        var position = e.GetPosition(PreviewCanvas);
        var (scale, ox, oy) = PreviewTransform();
        if (scale <= 0) return;

        var wa = _currentMonitor.WorkArea;
        var x = (position.X - ox) / (wa.Width * scale) * 100.0;
        var y = (position.Y - oy) / (wa.Height * scale) * 100.0;
        var zone = _currentLayout.Zones.FirstOrDefault(z =>
            x >= z.X && x <= z.X + z.Width && y >= z.Y && y <= z.Y + z.Height);
        if (zone == null) return;

        var zoneVm = FindZoneViewModel(zone);
        if (zoneVm != null)
        {
            zoneVm.AssignWindow(window);
            var result = ReapplyAll();
            PlayDropRipple(zone);
            StatusText.Text = $"Окно «{window.Title}» назначено в {zone.Name} и размещено." + ApplyNotes(result);
        }
        e.Handled = true;
    }

    private void ExternalDragTimer_Tick(object? sender, EventArgs e)
    {
        bool leftButtonDown = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        if (leftButtonDown && !_leftButtonWasDown)
        {
            var ownHandle = new WindowInteropHelper(this).Handle;
            if (Win32.GetCursorPos(out var cursor))
            {
                var source = Win32.GetAncestor(Win32.WindowFromPoint(cursor), Win32.GA_ROOT);
                if (source != IntPtr.Zero && source != ownHandle &&
                    WindowEnumerator.TryGetWindowInfo(source, out var window))
                {
                    _externalDragWindow = window;
                    StatusText.Text = $"Перетащите «{window.Title}» в нужную область предпросмотра.";
                }
            }
        }

        if (!leftButtonDown && _leftButtonWasDown && _externalDragWindow != null)
        {
            if (Win32.GetCursorPos(out var cursor))
            {
                var screenPoint = new Point(cursor.X, cursor.Y);
                var previewPoint = PreviewCanvas.PointFromScreen(screenPoint);
                if (previewPoint.X >= 0 && previewPoint.Y >= 0 &&
                    previewPoint.X <= PreviewCanvas.ActualWidth && previewPoint.Y <= PreviewCanvas.ActualHeight)
                    AssignExternalWindowToPreview(_externalDragWindow, previewPoint);
            }
            _externalDragWindow = null;
        }

        _leftButtonWasDown = leftButtonDown;
    }

    private void AssignExternalWindowToPreview(WindowInfo window, Point position)
    {
        if (_currentLayout == null || _currentMonitor == null) return;
        var (scale, ox, oy) = PreviewTransform();
        var wa = _currentMonitor.WorkArea;
        if (scale <= 0 || wa.Width <= 0 || wa.Height <= 0) return;

        var x = (position.X - ox) / (wa.Width * scale) * 100.0;
        var y = (position.Y - oy) / (wa.Height * scale) * 100.0;
        var zone = _currentLayout.Zones.FirstOrDefault(z =>
            x >= z.X && x <= z.X + z.Width && y >= z.Y && y <= z.Y + z.Height);
        var zoneVm = zone == null ? null : FindZoneViewModel(zone);
        if (zone == null || zoneVm == null) return;

        zoneVm.AssignWindow(window);
        var result = ReapplyAll();
        PlayDropRipple(zone);
        StatusText.Text = $"Окно «{window.Title}» назначено в {zone.Name} и размещено." + ApplyNotes(result);
    }

    private void PlayDropRipple(Zone zone)
    {
        if (_currentMonitor == null) return;
        var (scale, ox, oy) = PreviewTransform();
        var r = LayoutApplier.ZoneToPixelRect(zone, _currentMonitor.WorkArea);
        double centerX = ox + (r.X - _currentMonitor.WorkArea.X + r.Width / 2) * scale;
        double centerY = oy + (r.Y - _currentMonitor.WorkArea.Y + r.Height / 2) * scale;

        var ripple = new Ellipse
        {
            Width = 22,
            Height = 22,
            Stroke = new SolidColorBrush(Color.FromArgb(220, 0x78, 0xC7, 0xFF)),
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.25, 0.25),
        };
        var drop = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromArgb(220, 0x78, 0xC7, 0xFF)),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        Canvas.SetLeft(ripple, centerX - ripple.Width / 2);
        Canvas.SetTop(ripple, centerY - ripple.Height / 2);
        Canvas.SetLeft(drop, centerX - drop.Width / 2);
        Canvas.SetTop(drop, centerY - drop.Height / 2);
        PreviewCanvas.Children.Add(ripple);
        PreviewCanvas.Children.Add(drop);

        var duration = new Duration(TimeSpan.FromMilliseconds(720));
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation(0.25, 5.5, duration) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(0.25, 5.5, duration) { EasingFunction = ease };
        var opacity = new DoubleAnimation(0.85, 0, duration) { EasingFunction = ease };
        var transform = (ScaleTransform)ripple.RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        ripple.BeginAnimation(OpacityProperty, opacity);
        drop.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, duration));
        opacity.Completed += (_, _) =>
        {
            PreviewCanvas.Children.Remove(ripple);
            PreviewCanvas.Children.Remove(drop);
        };
    }

    private ZoneVM? FindZoneViewModel(Zone zone)
        => _zoneViewModels.TryGetValue(zone, out var zoneVm) ? zoneVm : null;

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

    private void PreviewCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
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
