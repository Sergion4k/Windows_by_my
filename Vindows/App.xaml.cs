using System.Windows;
using System.Windows.Threading;
using Vindows.Core;

namespace Vindows;

/// <summary>Точка входа: глобальный перехват непойманных исключений, чтобы программа
/// не падала из-за сбоев в тонких местах (Win32, файлы, фоновая работа).</summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    /// <summary>Исключение в обработчике UI: пишем в лог и продолжаем работу вместо падения.</summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DebugLog.Line("UNHANDLED (UI): " + e.Exception);
        e.Handled = true;
    }

    /// <summary>Фатальное исключение фонового потока — лог пишем, процесс всё равно завершится.</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        DebugLog.Line("UNHANDLED (AppDomain): " + e.ExceptionObject);

    /// <summary>Исключения в забытых задачах помечаем наблюдаемыми, чтобы не накапливались.</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DebugLog.Line("UNHANDLED (Task): " + e.Exception);
        e.SetObserved();
    }
}
