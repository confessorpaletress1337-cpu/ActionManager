using System;
using System.Collections.Generic;

namespace ActionManager.Models;

/// <summary>
/// Локальные настройки приложения (хранятся в %LocalAppData%\ActionManager\options.json).
/// Планировщик выключения живёт в Windows Task Scheduler, а вот "отменённые дни" — здесь.
/// </summary>
public sealed class AppOptions
{
    /// <summary>Время ежедневного выключения.</summary>
    public TimeSpan ShutdownTime { get; set; } = new(19, 0, 0);

    /// <summary>Даты, в которые выключение отменено пользователем.</summary>
    public List<DateOnly> SkippedDates { get; set; } = new();

}
