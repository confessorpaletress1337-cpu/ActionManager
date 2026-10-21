using System;
using System.IO;

namespace ActionManager.Services;

/// <summary>Простое файловое журналирование в %LocalAppData%\ActionManager\actions.log.</summary>
public static class AppLog
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ActionManager");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(
                Path.Combine(Dir, "actions.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{message}{Environment.NewLine}");
        }
        catch
        {
            // логирование не должно ронять приложение
        }
    }
}
