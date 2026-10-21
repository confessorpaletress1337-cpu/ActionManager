using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ActionManager.Models;
using ActionManager.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ActionManager.ViewModels;

/// <summary>
/// Корневая ViewModel: расписание, календарь, быстрые действия, статусы.
/// Всё, что ходит в систему, делает через сервисы — сам VM никаких
/// shutdown-строк не содержит.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISystemCommandsService _system;
    private readonly ISchedulerService _scheduler;
    private readonly IOptionsStore _options;

    private bool _initializing;          // подавляем сохранение настроек при первичной загрузке
    private bool _suppressTimeChanged;
    private DispatcherTimer? _confirmTimer;

    public MainWindowViewModel(ISystemCommandsService system, ISchedulerService scheduler, IOptionsStore options)
    {
        _system = system;
        _scheduler = scheduler;
        _options = options;
        Calendar = new CalendarViewModel();
        Calendar.SelectionChanged += _ => RefreshSelectedDay();

        Initialize();
    }

    public CalendarViewModel Calendar { get; }

    // --- состояние расписания ---------------------------------------------------

    [ObservableProperty]
    private TimeSpan? _shutdownTime = new(19, 0, 0);

    [ObservableProperty]
    private bool _schedulerEnabled;

    [ObservableProperty]
    private bool _hasConfigProblem;

    [ObservableProperty]
    private string _configProblemText = string.Empty;

    // --- сводки (следующее выключение и т.п.) ---------------------------------------------------

    [ObservableProperty]
    private string _nextShutdownSummary = "—";

    [ObservableProperty]
    private string _nextShutdownCountdown = string.Empty;

    [ObservableProperty]
    private string _lastRunSummary = string.Empty;

    [ObservableProperty]
    private string _plannerChipText = "загрузка…";

    [ObservableProperty]
    private bool _isChipOk;      // зелёная точка

    [ObservableProperty]
    private bool _isChipWarn;    // жёлтая точка

    [ObservableProperty]
    private bool _isChipOff;     // серая точка

    // --- выбранный день ------------------------------------------------------------

    [ObservableProperty]
    private string _selectedDayTitle = "День не выбран";

    [ObservableProperty]
    private string _selectedDayActionText = "Отменить выключение в этот день";

    [ObservableProperty]
    private bool _canToggleSelectedDay;

    // --- подтверждение выключения (двойное нажатие) ------------------------------------------------------------

    [ObservableProperty]
    private string _powerOffButtonText = "⏻ Выключить сейчас";

    private bool _powerOffArmed;

    // --- журнал ------------------------------------------------------------

    [ObservableProperty]
    private string _statusLogText = "Готово";

    // ===========================================================================

    private void Initialize()
    {
        _initializing = true;
        try
        {
            var options = _options.Load();
            ShutdownTime = options.ShutdownTime;
        }
        finally
        {
            _initializing = false;
        }

        EnsureTaskConfigured();
        SchedulerEnabled = ResolveEnabledFromScheduler();
        RefreshAll();
        Log("Запущен. Планировщик Windows читается напрямую через библиотеку.");
    }

    /// <summary>Создать задачу или обновить устаревшую конфигурацию приложения.</summary>
    private void EnsureTaskConfigured()
    {
        var info = _scheduler.TryGetInfo();
        if (info is { Exists: true, ConfigurationCurrent: true } && !info.AccessProblem)
            return;

        var enabled = info is { Exists: true, AccessProblem: false } && info.Enabled;
        if (_scheduler.TryCreateOrUpdate(ShutdownTime ?? new TimeSpan(19, 0, 0), enabled, out var error))
        {
            Log(info is { Exists: true }
                ? $"Обновлена конфигурация задачи: {_scheduler.TaskDisplayName}."
                : $"Создана задача планировщика: {_scheduler.TaskDisplayName} (отключена по умолчанию).");
            HasConfigProblem = false;
        }
        else
        {
            HasConfigProblem = true;
            ConfigProblemText =
                $"Не удалось создать задачу в планировщике Windows: {error}\n" +
                "Попробуйте запустить приложение один раз от имени администратора и нажать «Повторить».";
        }
    }

    private bool ResolveEnabledFromScheduler()
    {
        var info = _scheduler.TryGetInfo();
        return info is { Exists: true, Enabled: true };
    }

    // --- обновление всего ---------------------------------------------------------------

    private void RefreshAll()
    {
        if (!_options.TryLoad(out var options, out var loadError))
        {
            HasConfigProblem = true;
            ConfigProblemText = $"Не удалось прочитать настройки: {loadError}\nАвтовыключение будет безопасно пропущено.";
        }
        var info = _scheduler.TryGetInfo();

        // Task Scheduler — источник истины для времени. Если задачу поправили руками,
        // TimePicker сразу показывает фактическое значение, не перезаписывая его обратно.
        if (info is { Exists: true, AccessProblem: false, ShutdownTime: { } actualTime }
            && ShutdownTime != actualTime)
        {
            _suppressTimeChanged = true;
            ShutdownTime = actualTime;
            _suppressTimeChanged = false;
        }

        var enabled = info is { Exists: true } && !info.AccessProblem && info.Enabled;
        if (SchedulerEnabled != enabled)
            SchedulerEnabled = enabled; // вызовет OnSchedulerEnabledChanged, который сам освежит UI

        RefreshCalendar(options, enabled);
        RefreshNextShutdown(options, info, enabled);
        RefreshChip(info, enabled);
        RefreshSelectedDay();
    }

    private void RefreshCalendar(AppOptions options, bool enabled)
    {
        HashSet<DateOnly> skips = new(options.SkippedDates);
        Calendar.RefreshCalendar(new CalendarViewModel.CalendarContext(enabled, skips));
    }

    private void RefreshNextShutdown(AppOptions options, SchedulerInfo? info, bool enabled)
    {
        var time = info?.ShutdownTime ?? options.ShutdownTime;

        if (!enabled || info is not { Exists: true })
        {
            NextShutdownSummary = info is { Exists: false } or { AccessProblem: true }
                ? "Задача в планировщике недоступна"
                : "Планировщик отключён";
            NextShutdownCountdown = string.Empty;
            UpdateLastRunSummary(info);
            return;
        }

        // первый незакрытый день в расписании — если день отменён, ищем дальше
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTime.Now;
        var candidate = info.NextRun is { } nextRun && nextRun > now
            ? DateOnly.FromDateTime(nextRun)
            : now.TimeOfDay < time ? today : today.AddDays(1);
        var skips = new HashSet<DateOnly>(options.SkippedDates);
        for (var i = 0; i < 400 && skips.Contains(candidate); i++)
            candidate = candidate.AddDays(1);

        NextShutdownSummary = DescribeDate(candidate, time);
        NextShutdownCountdown = $"через {FormatInterval(candidate.ToDateTime(TimeOnly.FromTimeSpan(time)) - now)}";

        UpdateLastRunSummary(info);
    }

    private void UpdateLastRunSummary(SchedulerInfo? info)
    {
        if (info is not { Exists: true } || info.LastRun is null && info.LastResult == 0)
        {
            LastRunSummary = "Задача ещё не запускалась.";
            return;
        }

        LastRunSummary = info.LastRun is { } run
            ? $"Последний запуск: {run:dd.MM HH:mm} — {TaskSchedulerService.DescribeResult(info.LastResult)}."
            : $"Последний результат: {TaskSchedulerService.DescribeResult(info.LastResult)}.";
    }

    private static string DescribeDate(DateOnly date, TimeSpan time)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var tomorrow = today.AddDays(1);
        var timeText = time.ToString(@"hh\:mm");

        if (date == today) return $"Сегодня в {timeText}";
        if (date == tomorrow) return $"Завтра в {timeText}";
        return $"{date.ToString("d MMMM", Format.Ru)} ({date.ToString("dddd", Format.Ru)}) в {timeText}";
    }

    private static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalMinutes < 1) return "менее минуты";
        var days = (int)interval.TotalDays;
        var hours = interval.Hours;
        var minutes = interval.Minutes;
        if (days > 0) return $"{days} д {hours} ч";
        return hours > 0 ? $"{hours} ч {minutes} мин" : $"{minutes} мин";
    }

    private void RefreshChip(SchedulerInfo? info, bool enabled)
    {
        if (info is not { Exists: true } || info.AccessProblem)
        {
            PlannerChipText = "проблема с задачей";
            IsChipWarn = true;
            IsChipOk = IsChipOff = false;
            return;
        }

        if (enabled)
        {
            PlannerChipText = $"планировщик активен · {_scheduler.TaskDisplayName}";
            IsChipOk = true;
            IsChipWarn = IsChipOff = false;
        }
        else
        {
            PlannerChipText = "планировщик отключён";
            IsChipOff = true;
            IsChipOk = IsChipWarn = false;
        }
    }

    private void RefreshSelectedDay()
    {
        var day = Calendar.SelectedDay;
        if (day is null || day.IsPlaceholder)
        {
            SelectedDayTitle = "День не выбран";
            SelectedDayActionText = "Отменить выключение в этот день";
            CanToggleSelectedDay = false;
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        day.SetSelected(true);

        SelectedDayTitle = day.Date.ToString("d MMMM (dddd)", Format.Ru);
        CanToggleSelectedDay = day.Date >= today;

        var scheduled = SchedulerEnabled && day.State != DayCellState.Past &&
                        !day.IsCanceled;
        SelectedDayActionText = day.IsCanceled
            ? "↩ Вернуть выключение в этот день"
            : scheduled
                ? "✕ Отменить выключение в этот день"
                : "✎ Запомнить отмену на этот день";
    }

    // --- реакции на изменение свойств ---------------------------------------------------------------

    partial void OnSchedulerEnabledChanged(bool value)
    {
        if (_initializing)
            return;

        if (value && _scheduler.TryGetInfo() is not { Exists: true, ConfigurationCurrent: true })
            EnsureTaskConfigured(); // задачу потеряли (например, удалили руками) — воссоздаём

        if (_scheduler.TrySetEnabled(value, out var error))
        {
            Log(value ? "Планировщик включён." : "Планировщик отключён.");
            HasConfigProblem = false;
        }
        else
        {
            HasConfigProblem = true;
            ConfigProblemText = $"Не удалось изменить состояние задачи: {error}";
            Log("Ошибка переключения планировщика.");
        }

        RefreshAll();
    }

    partial void OnShutdownTimeChanged(TimeSpan? value)
    {
        if (_initializing || _suppressTimeChanged || value is not { } time)
            return;

        if (_scheduler.TryCreateOrUpdate(time, SchedulerEnabled, out var error))
        {
            var options = _options.Load();
            options.ShutdownTime = time;
            try
            {
                _options.Save(options);
                HasConfigProblem = false;
                Log($"Время выключения обновлено: {time:hh\\:mm}.");
            }
            catch (Exception ex)
            {
                HasConfigProblem = true;
                ConfigProblemText = $"Задача обновлена, но сохранить локальные настройки не удалось: {ex.Message}";
                Log("Не удалось сохранить время в локальных настройках.");
            }
            RefreshAll();
        }
        else
        {
            HasConfigProblem = true;
            ConfigProblemText = $"Не удалось обновить время задачи: {error}";

            // откатить выбор в TimePicker, чтобы UI соответствовал реальности
            _suppressTimeChanged = true;
            ShutdownTime = _options.Load().ShutdownTime;
            _suppressTimeChanged = false;
        }
    }

    partial void OnHasConfigProblemChanged(bool value)
    {
        if (!value)
            ConfigProblemText = string.Empty;
    }

    // --- команды ---------------------------------------------------------------

    [RelayCommand]
    private void RetryConfigure()
    {
        HasConfigProblem = false;
        if (!_options.TryLoad(out _, out _))
        {
            try
            {
                _options.Save(new AppOptions
                {
                    ShutdownTime = ShutdownTime ?? new TimeSpan(19, 0, 0),
                });
                Log("Повреждённые локальные настройки заменены безопасными значениями.");
            }
            catch (Exception ex)
            {
                HasConfigProblem = true;
                ConfigProblemText = $"Не удалось восстановить настройки: {ex.Message}";
                return;
            }
        }
        EnsureTaskConfigured();
        SchedulerEnabled = ResolveEnabledFromScheduler();
        RefreshAll();
    }

    [RelayCommand]
    private void ToggleSelectedSkip()
    {
        var day = Calendar.SelectedDay;
        if (day is not { IsSelectable: true }) return;

        ToggleSkip(day.Date);
    }

    private void ToggleSkip(DateOnly date)
    {
        var options = _options.Load();
        var added = !options.SkippedDates.Remove(date);
        if (added)
        {
            options.SkippedDates.Add(date);
        }

        try
        {
            _options.Save(options);
            Log(added
                ? $"Выключение отменено на {date.ToString("d MMMM", Format.Ru)}."
                : $"Отмена на {date.ToString("d MMMM", Format.Ru)} снята.");
            HasConfigProblem = false;
        }
        catch (Exception ex)
        {
            HasConfigProblem = true;
            ConfigProblemText = $"Не удалось сохранить настройки: {ex.Message}";
            Log("Изменение даты не сохранено.");
        }

        RefreshAll();
    }

    [RelayCommand]
    private void SleepNow()
    {
        Log("Усыпляю компьютер...");
        ReportSystemResult(_system.Sleep(), "Команда сна передана Windows.");
    }

    [RelayCommand]
    private void HibernateNow()
    {
        Log("Перевожу в гибернацию...");
        ReportSystemResult(_system.Hibernate(), "Команда гибернации передана Windows.");
    }

    [RelayCommand]
    private void LockNow()
    {
        Log("Блокирую компьютер...");
        ReportSystemResult(_system.Lock(), "Компьютер заблокирован.");
    }

    [RelayCommand]
    private void PowerOffNow()
    {
        if (_powerOffArmed)
        {
            _powerOffArmed = false;
            StopConfirmTimer();
            Log("Выключаю компьютер...");
            PowerOffButtonText = "⏻ Выключить сейчас";
            ReportSystemResult(_system.Shutdown(), "Команда выключения передана Windows.");
            return;
        }

        Log("Нажмите ещё раз для подтверждения выключения.");
        _powerOffArmed = true;
        PowerOffButtonText = "Точно выключить? (ещё раз)";
        StartConfirmTimer();
    }

    [RelayCommand]
    private void CancelPendingShutdown()
    {
        var result = _system.CancelPendingShutdown();
        if (!result.Success && result.ErrorCode == 1116)
        {
            Log("Ожидающего выключения сейчас нет.");
            return;
        }
        ReportSystemResult(result, "Ожидающее выключение отменено.");
    }

    private void ReportSystemResult(SystemCommandResult result, string successMessage)
    {
        if (result.Success)
        {
            Log(successMessage);
            return;
        }

        HasConfigProblem = true;
        ConfigProblemText = $"Windows не выполнила команду: {result.Error}";
        Log($"Ошибка системной команды: {result.Error}");
    }

    private void StartConfirmTimer()
    {
        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _confirmTimer.Tick += (_, _) =>
        {
            _confirmTimer?.Stop();
            _confirmTimer = null;
            if (_powerOffArmed)
            {
                _powerOffArmed = false;
                PowerOffButtonText = "⏻ Выключить сейчас";
                Log("Выключение отменено, подтверждение истекло.");
            }
        };
        _confirmTimer.Start();
    }

    private void StopConfirmTimer()
    {
        if (_confirmTimer is { } timer)
        {
            timer.Stop();
            _confirmTimer = null;
        }
    }

    [RelayCommand]
    private void OpenWindowsPlanner()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "control.exe",
                Arguments = "schedtasks",
                UseShellExecute = true,
            });
            Log("Открыл планировщик Windows.");
        }
        catch (Exception ex)
        {
            Log($"Не удалось открыть планировщик: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        StatusLogText = $"{DateTime.Now:H:mm:ss} · {message}";
        AppLog.Write(message);
    }

    /// <summary>Периодическое обновление обратного отсчёта (раз в 30 секунд).</summary>
    private DispatcherTimer? _uiTimer;

    public void StartUiTimer()
    {
        if (_uiTimer is null)
        {
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _uiTimer.Tick += (_, _) => RefreshAll();
        }
        _uiTimer.Start();
    }
}
