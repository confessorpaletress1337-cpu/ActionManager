using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ActionManager.Models;

namespace ActionManager.Services;

public sealed record LaunchPoolResult(int Started, int AlreadyRunning, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

public interface ILaunchPoolService
{
    LaunchPoolResult Launch(IEnumerable<LaunchPoolEntry> entries);
}

/// <summary>Открывает элементы пула через стандартную оболочку Windows.</summary>
public sealed class LaunchPoolService : ILaunchPoolService
{
    public LaunchPoolResult Launch(IEnumerable<LaunchPoolEntry> entries)
    {
        var started = 0;
        var alreadyRunning = 0;
        var errors = new List<string>();

        foreach (var entry in entries.Where(item => item.Enabled))
        {
            var displayName = string.IsNullOrWhiteSpace(entry.Name) ? entry.Target : entry.Name;
            try
            {
                var target = Environment.ExpandEnvironmentVariables(entry.Target.Trim());
                if (IsExecutableAlreadyRunning(target))
                {
                    alreadyRunning++;
                    AppLog.Write($"Рабочий пул: «{displayName}» уже запущено — пропускаем.");
                    continue;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = target,
                    Arguments = entry.Arguments?.Trim() ?? string.Empty,
                    UseShellExecute = true,
                };

                if (File.Exists(target) && Path.GetDirectoryName(target) is { Length: > 0 } directory)
                    startInfo.WorkingDirectory = directory;

                Process.Start(startInfo);
                started++;
                AppLog.Write($"Рабочий пул: запущено «{displayName}» ({target}).");
            }
            catch (Exception ex)
            {
                errors.Add($"{displayName}: {ex.Message}");
                AppLog.Write($"Рабочий пул: не удалось запустить «{displayName}»: {ex.Message}");
            }
        }

        return new LaunchPoolResult(started, alreadyRunning, errors);
    }

    private static bool IsExecutableAlreadyRunning(string target)
    {
        if (!string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(target))
            return false;

        var expectedPath = Path.GetFullPath(target);
        var processName = Path.GetFileNameWithoutExtension(target);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var runningPath = process.MainModule?.FileName;
                    if (runningPath is not null
                        && string.Equals(
                            Path.GetFullPath(runningPath),
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Некоторые системные процессы не разрешают читать MainModule.
                    // Совпавшее имя в таком случае безопаснее считать уже запущенным.
                    return true;
                }
            }
        }

        return false;
    }
}
