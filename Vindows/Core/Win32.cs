using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Vindows.Core;

/// <summary>Обёртки над Win32 API: мониторы и окна.</summary>
internal static class Win32
{
    // ---------- Мониторы ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTONULL = 0;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>Все мониторы системы с разрешением и рабочей областью (в физических пикселях).</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        int index = 0;

        // Лямбда не поддерживает ref-параметры, поэтому локальная функция.
        bool Callback(IntPtr h, IntPtr hdc, ref RECT rc, IntPtr data)
        {
            // Исключение внутри нативного колбэка EnumDisplayMonitors перехватить снаружи нельзя —
            // оборачиваем тело, чтобы сбой одного монитора не ронял приложение.
            try
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref mi))
                {
                    var b = mi.rcMonitor;
                    var w = mi.rcWork;
                    monitors.Add(new MonitorInfo
                    {
                        Handle = h,
                        DeviceName = mi.szDevice,
                        Index = index + 1,
                        DisplayName = MonitorDisplayName(mi.szDevice, b, (mi.dwFlags & 1) != 0, index + 1),
                        Bounds = new Rect(b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top),
                        WorkArea = new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top),
                    });
                    index++;
                }
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Line("GetMonitors callback: " + ex.Message);
                return true;
            }
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return monitors;
    }

    internal static string MonitorDisplayName(string deviceName, RECT bounds, bool primary, int fallbackNumber)
    {
        var digits = new string(deviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        var number = int.TryParse(digits, out var parsed) ? parsed : fallbackNumber;
        var position = new List<string>();
        if (primary) position.Add("основной");
        else
        {
            if (bounds.Right <= 0) position.Add("слева");
            else if (bounds.Left > 0) position.Add("справа");
            if (bounds.Top < 0) position.Add("выше");
            else if (bounds.Top > 0) position.Add("ниже");
            if (position.Count == 0) position.Add("дополнительный");
        }
        return $"Монитор {number} · {bounds.Right - bounds.Left}×{bounds.Bottom - bounds.Top} · {string.Join(", ", position)}";
    }

    /// <summary>Монитор, на котором находится окно (по Win32, с учётом ближайшего при пересечении границ).</summary>
    public static MonitorInfo? FindMonitorForWindow(IntPtr hwnd, IReadOnlyList<MonitorInfo> monitors)
    {
        if (hwnd == IntPtr.Zero || monitors.Count == 0) return null;
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return null;
        return monitors.FirstOrDefault(m => m.Handle == hMon);
    }

    // ---------- Окна ----------

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    public const uint GA_ROOT = 2;
    public const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    public static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x00000008) != 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // ---------- События окон (SetWinEventHook) ----------

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_RESTORE = 9;
    public const int DWMWA_CLOAKED = 14;
    /// <summary>Предпочтение скругления внешних углов окна Windows 11.</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;
    /// <summary>Видимые границы окна — без невидимых рамок тени DWM.</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
}
