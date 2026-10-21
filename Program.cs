using System;
using System.Threading;
using ActionManager.Services;
using Avalonia;
using Avalonia.Threading;

namespace ActionManager;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\ActionManager.SingleInstance";
    private const string ActivateWindowEventName = @"Local\ActionManager.ActivateWindow";

    [STAThread]
    public static int Main(string[] args)
    {
        // Режим "нас разбудил планировщик Windows" (см. TaskSchedulerService.ScheduledFireArg).
        // ВАЖНО: UI не запускаем вообще — тихо выполняем действие и выходим.
        if (Array.IndexOf(args, TaskSchedulerService.ScheduledFireArg) >= 0)
            return RunScheduledShutdown();

        // одиночный экземпляр с окном
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        using var activateWindowEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivateWindowEventName);
        if (!createdNew)
        {
            activateWindowEvent.Set();
            AppLog.Write("Второй экземпляр передал команду поднять уже открытое окно.");
            return 0;
        }

        using var listenerStop = new CancellationTokenSource();
        var listener = StartActivationListener(activateWindowEvent, listenerStop.Token);
        var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        listenerStop.Cancel();
        listener.Join(millisecondsTimeout: 1000);
        Dispatcher.UIThread.InvokeShutdown();
        return exitCode;
    }

    /// <summary>Выполняется по расписанию из Windows Task Scheduler.</summary>
    private static int RunScheduledShutdown()
    {
        var store = new JsonOptionsStore();
        if (!store.TryLoad(out var options, out var loadError))
        {
            AppLog.Write($"Планировщик сработал, но настройки прочитать нельзя — безопасно пропускаем выключение: {loadError}");
            return 2;
        }

        var scheduler = new TaskSchedulerService();
        var schedulerInfo = scheduler.TryGetInfo();
        if (schedulerInfo is not { Exists: true, Enabled: true, AccessProblem: false })
        {
            AppLog.Write("Запуск с аргументом планировщика отклонён: активная задача ActionManager не найдена.");
            return 3;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);

        if (options.SkippedDates.Contains(today))
        {
            AppLog.Write($"Планировщик сработал, но выключение на {today:dd.MM} отменено пользователем — пропускаем.");
            return 0;
        }

        const int warningSeconds = TaskSchedulerService.ShutdownWarningSeconds;
        AppLog.Write($"Планировщик сработал — выключение через {warningSeconds} секунд, его можно отменить командой shutdown /a.");
        var result = new SystemCommandsService().Shutdown(
            delaySeconds: warningSeconds,
            message: $"ActionManager: автоматическое выключение через {warningSeconds / 60} минуты. Сохраните работу или отмените выключение в приложении.");
        if (result.Success)
            return 0;

        AppLog.Write($"Не удалось запланировать выключение: {result.Error}");
        return 4;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static Thread StartActivationListener(EventWaitHandle activateEvent, CancellationToken cancellationToken)
    {
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { activateEvent, cancellationToken.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
                Dispatcher.UIThread.Post(App.ActivateMainWindow);
        })
        {
            IsBackground = true,
            Name = "ActionManager window activation listener",
        };
        thread.Start();
        return thread;
    }
}
