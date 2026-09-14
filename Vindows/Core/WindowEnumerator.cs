using System.Diagnostics;
using System.Text;

namespace Vindows.Core;

/// <summary>Перечисление открытых окон верхнего уровня, пригодных для размещения.</summary>
public static class WindowEnumerator
{
    /// <summary>Все видимые окна с заголовком (скрытые, свёрнутые UWP и служебные отбрасываются).</summary>
    public static List<WindowInfo> GetOpenWindows()
    {
        var windows = new List<WindowInfo>();
        var shell = Win32.GetShellWindow();
        try
        {
            Win32.EnumWindows((h, _) =>
            {
                try
                {
                    if (h != shell && TryGetWindowInfoCore(h, out var info))
                        windows.Add(info);
                }
                catch (Exception ex)
                {
                    // Окно могло быть закрыто прямо во время перечисления — пропускаем его.
                    DebugLog.Line("EnumWindows callback: " + ex.Message);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            DebugLog.Line("GetOpenWindows: " + ex.Message);
        }
        return windows;
    }

    public static bool TryGetWindowInfo(IntPtr handle, out WindowInfo info) =>
        TryGetWindowInfoCore(handle, out info);

    private static bool TryGetWindowInfoCore(IntPtr h, out WindowInfo info)
    {
        info = null!;

        if (!Win32.IsWindowVisible(h)) return false;
        if (Win32.GetWindowTextLength(h) == 0) return false;

        // Окна виртуальных рабочих столов и свёрнутые UWP-приложения скрываются через DWM.
        if (Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (!Win32.GetWindowRect(h, out var rect)) return false;
        if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return false;

        Win32.GetWindowThreadProcessId(h, out var pid);
        if (pid == 0) return false;
        string process;
        try
        {
            using var owner = Process.GetProcessById((int)pid);
            process = owner.ProcessName;
        }
        catch
        {
            return false;
        }

        var sb = new StringBuilder(256);
        Win32.GetWindowText(h, sb, sb.Capacity);
        var title = sb.ToString();
        if (string.IsNullOrWhiteSpace(title)) return false;

        info = new WindowInfo { Handle = h, Title = title, ProcessName = process, ProcessId = pid };
        return true;
    }
}
