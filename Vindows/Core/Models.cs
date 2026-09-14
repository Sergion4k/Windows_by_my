using System.Windows;
using System.Text.Json.Serialization;

namespace Vindows.Core;

/// <summary>Физический монитор системы.</summary>
public sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }

    public required string DeviceName { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Весь монитор (физические пиксели).</summary>
    public required Rect Bounds { get; init; }

    /// <summary>Рабочая область — без панели задач (физические пиксели).</summary>
    public required Rect WorkArea { get; init; }

    public override string ToString() => DisplayName;
}

/// <summary>Область экрана: координаты и размер в процентах от рабочей области.</summary>
public sealed class Zone
{
    public required string Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>Имя процесса (exe), окно которого размещается в этой области. null — область пустая.</summary>
    public string? ProcessName { get; set; }
    public WindowSelectionMode SelectionMode { get; set; }
    public string? WindowTitle { get; set; }

    // HWND и PID действительны только в текущем сеансе. В JSON остаётся название окна.
    [JsonIgnore] public IntPtr WindowHandle { get; set; }
    [JsonIgnore] public uint WindowProcessId { get; set; }
    [JsonIgnore] public string Status { get; set; } = "Не назначено";

    public void SelectWindow(WindowInfo window)
    {
        ProcessName = window.ProcessName;
        WindowHandle = window.Handle;
        WindowProcessId = window.ProcessId;
        WindowTitle = window.Title;
    }

    public void CopyAssignmentFrom(Zone source)
    {
        ProcessName = source.ProcessName;
        SelectionMode = source.SelectionMode;
        WindowTitle = source.WindowTitle;
        WindowHandle = source.WindowHandle;
        WindowProcessId = source.WindowProcessId;
    }
}

public enum WindowSelectionMode { AnyWindow, SpecificWindow }
public enum MonitorMoveBehavior { ReturnToZone, ReleaseWindow }

/// <summary>Раскладка одного монитора: сетка + список областей.</summary>
public sealed class MonitorLayout
{
    public required string DeviceName { get; set; }
    public int Columns { get; set; } = 2;
    public int Rows { get; set; } = 1;

    /// <summary>Зазор между областями, пиксели.</summary>
    public int Gap { get; set; } = 8;

    public List<Zone> Zones { get; set; } = new();
}

/// <summary>Открытое окно приложения.</summary>
public sealed class WindowInfo
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public uint ProcessId { get; init; }

    public string DisplayName => $"{Title}  [{ProcessName}]";

    public override string ToString() => DisplayName;
}

/// <summary>Корневой объект файла раскладки (JSON).</summary>
public sealed class LayoutFile
{
    public List<MonitorLayout> Monitors { get; set; } = new();

    /// <summary>Контроль окон: возвращать ли привязанные окна в области, пока программа запущена.</summary>
    public bool ControlEnabled { get; set; } = true;
    public bool KeepOnTop { get; set; }
    public MonitorMoveBehavior MonitorMoveBehavior { get; set; }
}

/// <summary>Итог расстановки окон: сколько размещено и для каких областей окна не найдены.</summary>
public sealed class ApplyResult
{
    public int Placed { get; set; }
    public List<string> Missing { get; set; } = new();

    /// <summary>Зоны, в которые окно не поместилось (минимальный размер окна больше зоны).</summary>
    public List<string> Oversized { get; set; } = new();

    /// <summary>Зоны, окна которых нельзя переместить (нет прав — окно запущено от администратора).</summary>
    public List<string> Denied { get; set; } = new();

    /// <summary>Сетка была автоматически подстроена под размеры окон.</summary>
    public bool GridAdjusted { get; set; }
}

/// <summary>Окно и его целевая область в пикселях — карта для контроля привязки.</summary>
public readonly record struct WindowTarget(IntPtr Handle, Rect Rect, MonitorLayout Layout, Zone Zone, Rect WorkArea);
