using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ActionManager.Services;

/// <summary>
/// Все команды операционной системе (выключение, сон, блокировка...) — только отсюда.
/// Ничего такого не должно вызываться напрямую из ViewModel.
/// </summary>
public readonly record struct SystemCommandResult(bool Success, string Error, int? ErrorCode = null)
{
    public static SystemCommandResult Ok => new(true, string.Empty);
    public static SystemCommandResult Failed(string error, int? errorCode = null) => new(false, error, errorCode);
}

public interface ISystemCommandsService
{
    SystemCommandResult Shutdown(bool force = false, int delaySeconds = 0, string? message = null);
    SystemCommandResult Restart(bool force = false, int delaySeconds = 0);
    SystemCommandResult Sleep();
    SystemCommandResult Hibernate();
    SystemCommandResult Lock();
    SystemCommandResult LogOff();
    SystemCommandResult CancelPendingShutdown();
}

/// <summary>
/// Реализация через штатный shutdown.exe и WinAPI (powrprof / user32).
/// </summary>
public sealed class SystemCommandsService : ISystemCommandsService
{
    // --- shutdown.exe / restart -------------------------------------------------

    public SystemCommandResult Shutdown(bool force = false, int delaySeconds = 0, string? message = null)
    {
        if (delaySeconds < 0)
            return SystemCommandResult.Failed("Задержка выключения не может быть отрицательной.");
        var args = new System.Collections.Generic.List<string> { "/s", "/t", delaySeconds.ToString() };
        if (force)
            args.Add("/f");
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("/c");
            args.Add(message);
        }
        return RunShutdownTool(args.ToArray());
    }

    public SystemCommandResult Restart(bool force = false, int delaySeconds = 0)
    {
        if (delaySeconds < 0)
            return SystemCommandResult.Failed("Задержка перезагрузки не может быть отрицательной.");
        var args = new System.Collections.Generic.List<string> { "/r", "/t", delaySeconds.ToString() };
        if (force)
            args.Add("/f");
        return RunShutdownTool(args.ToArray());
    }

    /// <summary>Отмена ранее запланированного shutdown с ненулевым таймером (/a = abort).</summary>
    public SystemCommandResult CancelPendingShutdown() => RunShutdownTool("/a");

    private static SystemCommandResult RunShutdownTool(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return SystemCommandResult.Failed("Windows не запустила shutdown.exe.");
            if (!process.WaitForExit(5000))
                return SystemCommandResult.Failed("shutdown.exe не завершилась за 5 секунд.");

            var output = (process.StandardError.ReadToEnd() + " " + process.StandardOutput.ReadToEnd()).Trim();
            return process.ExitCode == 0
                ? SystemCommandResult.Ok
                : SystemCommandResult.Failed(string.IsNullOrWhiteSpace(output)
                    ? $"shutdown.exe завершилась с кодом {process.ExitCode}."
                    : output, process.ExitCode);
        }
        catch (Exception ex)
        {
            return SystemCommandResult.Failed(ex.Message);
        }
    }

    // --- сон / гибернация (SetSuspendState ------------------------------------------------------

    public SystemCommandResult Sleep() => FromWin32Result(
        Native.SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false));

    public SystemCommandResult Hibernate() => FromWin32Result(
        Native.SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false));

    // --- блокировка / логаут ------------------------------------------------------

    public SystemCommandResult Lock() => FromWin32Result(Native.LockWorkStation());

    public SystemCommandResult LogOff() => FromWin32Result(
        Native.ExitWindowsEx(nativeFlags: Native.EwxLogoff, dwReserved: 0));

    private static SystemCommandResult FromWin32Result(bool success) => success
        ? SystemCommandResult.Ok
        : SystemCommandResult.Failed(Marshal.GetLastWin32Error() is var code && code != 0
            ? $"Ошибка Windows {code}: {new System.ComponentModel.Win32Exception(code).Message}"
            : "Windows отклонила команду.", code == 0 ? null : code);

    private static class Native
    {
        internal const uint EwxLogoff = 0x00000000;

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LockWorkStation();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ExitWindowsEx(uint nativeFlags, uint dwReserved);
    }
}
