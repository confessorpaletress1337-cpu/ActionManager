using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
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
public sealed partial class LaunchPoolService : ILaunchPoolService
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
                var launch = ResolveLaunchTarget(entry);
                if (entry.SkipIfRunning && IsAlreadyOpen(launch))
                {
                    alreadyRunning++;
                    AppLog.Write($"Рабочий пул: «{displayName}» уже открыто — пропускаем.");
                    continue;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = launch.Target,
                    Arguments = launch.Arguments,
                    WorkingDirectory = launch.WorkingDirectory,
                    UseShellExecute = true,
                });
                started++;
                AppLog.Write($"Рабочий пул: запущено «{displayName}» ({launch.Target}).");
            }
            catch (Exception ex)
            {
                errors.Add($"{displayName}: {ex.Message}");
                AppLog.Write($"Рабочий пул: не удалось запустить «{displayName}»: {ex.Message}");
            }
        }

        return new LaunchPoolResult(started, alreadyRunning, errors);
    }

    private static ResolvedLaunchTarget ResolveLaunchTarget(LaunchPoolEntry entry)
    {
        var target = Environment.ExpandEnvironmentVariables(entry.Target.Trim());
        var arguments = Environment.ExpandEnvironmentVariables(entry.Arguments?.Trim() ?? string.Empty);
        var workingDirectory = Environment.ExpandEnvironmentVariables(entry.WorkingDirectory?.Trim() ?? string.Empty);

        if (string.Equals(Path.GetExtension(target), ".lnk", StringComparison.OrdinalIgnoreCase)
            && TryResolveShortcut(target, out var shortcut))
        {
            target = Environment.ExpandEnvironmentVariables(shortcut.Target);
            arguments = JoinArguments(shortcut.Arguments, arguments);
            if (string.IsNullOrWhiteSpace(workingDirectory))
                workingDirectory = shortcut.WorkingDirectory;
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = File.Exists(target)
                ? Path.GetDirectoryName(Path.GetFullPath(target)) ?? AppContext.BaseDirectory
                : AppContext.BaseDirectory;
        }

        return new ResolvedLaunchTarget(target, arguments, workingDirectory);
    }

    private static bool IsAlreadyOpen(ResolvedLaunchTarget launch)
    {
        var extension = Path.GetExtension(launch.Target);
        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
            return TryActivateOpenVisualStudioSolution(launch.Target);

        var executableName = Path.GetFileNameWithoutExtension(launch.Target);
        if (string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase))
            return IsDotNetProjectAlreadyRunning(launch.Arguments, launch.WorkingDirectory);

        return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
               && IsExecutableAlreadyRunning(launch.Target);
    }

    private static bool IsDotNetProjectAlreadyRunning(string arguments, string workingDirectory)
    {
        var match = DotNetProjectArgumentRegex().Match(arguments);
        if (!match.Success)
            return false;

        var projectPath = Environment.ExpandEnvironmentVariables(match.Groups["project"].Value);
        if (!Path.IsPathRooted(projectPath))
            projectPath = Path.GetFullPath(Path.Combine(workingDirectory, projectPath));

        return IsProcessNameRunning(Path.GetFileNameWithoutExtension(projectPath));
    }

    private static bool IsExecutableAlreadyRunning(string target)
    {
        var processName = Path.GetFileNameWithoutExtension(target);
        var expectedPath = File.Exists(target) ? Path.GetFullPath(target) : null;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (expectedPath is null)
                    return true;
                try
                {
                    var runningPath = process.MainModule?.FileName;
                    if (runningPath is not null
                        && string.Equals(Path.GetFullPath(runningPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Для защищённого процесса путь прочитать нельзя. Совпавшее имя
                    // безопаснее считать уже запущенным, чем плодить экземпляры.
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsProcessNameRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static bool TryResolveShortcut(string shortcutPath, out ShortcutTarget shortcut)
    {
        if (!OperatingSystem.IsWindows())
        {
            shortcut = default;
            return false;
        }

        object? shell = null;
        object? link = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                shortcut = default;
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            link = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });
            if (link is null)
            {
                shortcut = default;
                return false;
            }

            var linkType = link.GetType();
            shortcut = new ShortcutTarget(
                GetComStringProperty(linkType, link, "TargetPath"),
                GetComStringProperty(linkType, link, "Arguments"),
                GetComStringProperty(linkType, link, "WorkingDirectory"));
            return !string.IsNullOrWhiteSpace(shortcut.Target);
        }
        catch
        {
            shortcut = default;
            return false;
        }
        finally
        {
            ReleaseComObject(link);
            ReleaseComObject(shell);
        }
    }

    private static string GetComStringProperty(Type type, object target, string propertyName)
        => type.InvokeMember(propertyName, BindingFlags.GetProperty, null, target, null) as string
           ?? string.Empty;

    private static bool TryActivateOpenVisualStudioSolution(string solutionPath)
    {
        var expectedPath = Path.GetFullPath(solutionPath);
        IRunningObjectTable? runningObjects = null;
        IEnumMoniker? monikers = null;
        IBindCtx? bindContext = null;
        try
        {
            if (GetRunningObjectTable(0, out runningObjects) != 0
                || CreateBindCtx(0, out bindContext) != 0)
                return IsVisualStudioWindowOpen(solutionPath);

            runningObjects.EnumRunning(out monikers);
            var current = new IMoniker[1];
            while (monikers.Next(1, current, IntPtr.Zero) == 0)
            {
                object? dte = null;
                object? solution = null;
                object? mainWindow = null;
                try
                {
                    current[0].GetDisplayName(bindContext, null, out var displayName);
                    if (!displayName.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase))
                        continue;

                    runningObjects.GetObject(current[0], out dte);
                    solution = dte.GetType().InvokeMember(
                        "Solution", BindingFlags.GetProperty, null, dte, null);
                    var openPath = solution?.GetType().InvokeMember(
                        "FullName", BindingFlags.GetProperty, null, solution, null) as string;
                    if (string.IsNullOrWhiteSpace(openPath)
                        || !string.Equals(Path.GetFullPath(openPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    mainWindow = dte.GetType().InvokeMember(
                        "MainWindow", BindingFlags.GetProperty, null, dte, null);
                    mainWindow?.GetType().InvokeMember(
                        "Activate", BindingFlags.InvokeMethod, null, mainWindow, null);
                    return true;
                }
                catch
                {
                    // Один недоступный экземпляр Visual Studio не мешает проверить остальные.
                }
                finally
                {
                    ReleaseComObject(mainWindow);
                    ReleaseComObject(solution);
                    ReleaseComObject(dte);
                    ReleaseComObject(current[0]);
                }
            }
        }
        catch
        {
            // Если DTE недоступен, используем менее точную проверку по заголовку окна.
        }
        finally
        {
            ReleaseComObject(monikers);
            ReleaseComObject(bindContext);
            ReleaseComObject(runningObjects);
        }

        return IsVisualStudioWindowOpen(solutionPath);
    }

    private static bool IsVisualStudioWindowOpen(string solutionPath)
    {
        var solutionName = Path.GetFileNameWithoutExtension(solutionPath);
        foreach (var process in Process.GetProcessesByName("devenv"))
        {
            using (process)
            {
                if (process.MainWindowTitle.Contains(solutionName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string JoinArguments(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first.Trim()} {second.Trim()}";
    }

    private static void ReleaseComObject(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    [GeneratedRegex("(?:^|\\s)--project(?:\\s+|=)(?:\\\"(?<project>[^\\\"]+)\\\"|(?<project>\\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetProjectArgumentRegex();

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable runningObjectTable);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

    private readonly record struct ResolvedLaunchTarget(string Target, string Arguments, string WorkingDirectory);
    private readonly record struct ShortcutTarget(string Target, string Arguments, string WorkingDirectory);
}
