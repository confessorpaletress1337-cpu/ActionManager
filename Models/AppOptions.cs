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

    /// <summary>Приложения, проекты, папки и ссылки рабочего пула.</summary>
    public List<LaunchPoolEntry> LaunchPool { get; set; } = new();
}

/// <summary>Один элемент рабочего пула.</summary>
public sealed class LaunchPoolEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool SkipIfRunning { get; set; } = true;
    public bool Enabled { get; set; } = true;
}
