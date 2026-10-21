using System;
using System.IO;
using System.Linq;
using Microsoft.Win32.TaskScheduler;

namespace ActionManager.Services;

/// <summary>Снимок состояния задачи в Windows Task Scheduler.</summary>
public sealed record SchedulerInfo(
    bool Exists,
    bool Enabled,
    string State,
    TimeSpan? ShutdownTime,
    DateTime? NextRun,
    DateTime? LastRun,
    int LastResult,
    bool AccessProblem,
    bool ConfigurationCurrent);

/// <summary>
/// Работа с Windows Task Scheduler через библиотеку Microsoft.Win32.TaskScheduler
/// (typed API над COM taskschd.dll — без schtasks.exe и без строк-команд).
/// </summary>
public interface ISchedulerService
{
    SchedulerInfo? TryGetInfo();
    bool TryCreateOrUpdate(TimeSpan time, bool enabled, out string error);
    bool TrySetEnabled(bool enabled, out string error);
    bool TryDelete(out string error);
    string TaskDisplayName { get; }
}

public sealed class TaskSchedulerService : ISchedulerService
{
    private const string FolderName = "ActionManager";
    private const string TaskName = "AutoShutdown";

    /// <summary>Аргумент, с которым планировщик запускает наш exe в момент срабатывания.</summary>
    public const string ScheduledFireArg = "--scheduled-shutdown";
    private const string ScheduleVersionArg = "--schedule-v5";
    public const int ShutdownWarningSeconds = 180;

    private static string TaskPath => $@"\{FolderName}\{TaskName}";

    public string TaskDisplayName => $@"{FolderName}\{TaskName}";

    // ---------------------------------------------------------------------------

    public SchedulerInfo? TryGetInfo()
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(TaskPath);
            if (task is null)
                return null;

            var trigger = task.Definition.Triggers.OfType<DailyTrigger>().FirstOrDefault();
            var action = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
            var expected = ResolveLaunchCommand();
            var rawNextRun = task.NextRunTime;
            var configurationCurrent = trigger is not null
                                       && trigger.DaysInterval == 1
                                       && action is not null
                                       && task.Definition.Principal.LogonType == TaskLogonType.InteractiveToken
                                       && PathsEqual(action.Path, expected.ExecutablePath)
                                       && string.Equals(action.Arguments?.Trim(), expected.Arguments, StringComparison.Ordinal);
            return new SchedulerInfo(
                Exists: true,
                Enabled: task.Enabled,
                State: task.State.ToString(),
                ShutdownTime: trigger?.StartBoundary.AddSeconds(ShutdownWarningSeconds).TimeOfDay,
                NextRun: task.Enabled && rawNextRun != DateTime.MinValue
                    ? rawNextRun.AddSeconds(ShutdownWarningSeconds)
                    : null,
                LastRun: task.LastRunTime == DateTime.MinValue ? null : task.LastRunTime,
                LastResult: task.LastTaskResult,
                AccessProblem: false,
                ConfigurationCurrent: configurationCurrent);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Не удалось прочитать планировщик: {ex.Message}");
            return new SchedulerInfo(false, false, "Ошибка", null, null, null, 0,
                AccessProblem: true, ConfigurationCurrent: false);
        }
    }

    // ---------------------------------------------------------------------------

    /// <summary>
    /// Создаёт (или обновляет) ежедневную задачу: за три минуты до time запускается наш exe,
    /// который уже разбирается — выключать (или пропустить, если день отменён в настройках).
    /// </summary>
    public bool TryCreateOrUpdate(TimeSpan time, bool enabled, out string error)
    {
        try
        {
            using var ts = new TaskService();

            var folder = ts.RootFolder.SubFolders.FirstOrDefault(f => f.Name == FolderName)
                         ?? ts.RootFolder.CreateFolder(FolderName);

            var def = ts.NewTask();
            def.RegistrationInfo.Description =
                "ActionManager: ежедневное выключение в интерактивном сеансе с предупреждением за 3 минуты.";
            def.RegistrationInfo.Source = "ActionManager";

            // Интерактивный токен гарантирует, что системное предупреждение shutdown.exe
            // появится в сеансе вошедшего пользователя, даже когда окно приложения закрыто.
            var userId = $@"{Environment.UserDomainName}\{Environment.UserName}";
            def.Principal.UserId = userId;
            def.Principal.LogonType = TaskLogonType.InteractiveToken;

            var trigger = (DailyTrigger)def.Triggers.Add(new DailyTrigger());
            // Задача срабатывает за три минуты до выбранного времени, чтобы штатное
            // предупреждение shutdown.exe закончилось ровно в нужный момент.
            trigger.StartBoundary = DateTime.Today.Add(time).AddSeconds(-ShutdownWarningSeconds);
            trigger.DaysInterval = 1;
            trigger.Enabled = true;

            // Действие запускает стабильный apphost из каталога приложения. Версия аргументов
            // позволяет безопасно обновить старую задачу при следующем запуске UI.
            var launch = ResolveLaunchCommand();
            def.Actions.Add(new ExecAction(launch.ExecutablePath, launch.Arguments, AppContext.BaseDirectory));

            def.Settings.Enabled = enabled;               // по умолчанию создаём отключённой, включаем через UI
            def.Settings.StartWhenAvailable = false;      // пропущенное время не догоняем при загрузке ПК
            def.Settings.WakeToRun = false;               // будить комп, чтобы его выключить — бессмысленно
            def.Settings.DisallowStartIfOnBatteries = false;
            def.Settings.StopIfGoingOnBatteries = false;
            def.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(10);

            folder.RegisterTaskDefinition(
                TaskName,
                def,
                TaskCreation.CreateOrUpdate,
                userId,
                password: null,
                TaskLogonType.InteractiveToken);

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Write($"Не удалось создать/обновить задачу: {error}");
            return false;
        }
    }

    // ---------------------------------------------------------------------------

    public bool TrySetEnabled(bool enabled, out string error)
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(TaskPath);
            if (task is null)
            {
                error = $"Задача {TaskPath} не найдена";
                return false;
            }

            task.Enabled = enabled;

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------------------

    public bool TryDelete(out string error)
    {
        try
        {
            using var ts = new TaskService();
            var folder = ts.RootFolder.SubFolders.FirstOrDefault(f => f.Name == FolderName);
            if (folder is null)
            {
                error = string.Empty;
                return true;
            }

            folder.DeleteTask(TaskName);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------------------

    /// <summary>Человекочитаемое описание кода завершения задачи (LastTaskResult).</summary>
    public static string DescribeResult(int result) => result switch
    {
        0 => "успешно",
        267009 => "задача выполняется",
        267010 => "задача отключена",
        267011 => "ещё не запускалась",
        _ => $"код {result}",
    };

    private static LaunchCommand ResolveLaunchCommand()
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, "ActionManager.exe");
        if (File.Exists(appHost))
            return new LaunchCommand(appHost, $"{ScheduledFireArg} {ScheduleVersionArg}");

        // Редкий случай запуска через `dotnet ActionManager.dll` без apphost.
        var assemblyPath = typeof(TaskSchedulerService).Assembly.Location;
        var processPath = Environment.ProcessPath ?? "dotnet.exe";
        return new LaunchCommand(processPath,
            $"\"{assemblyPath}\" {ScheduledFireArg} {ScheduleVersionArg}");
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed record LaunchCommand(string ExecutablePath, string Arguments);
}
