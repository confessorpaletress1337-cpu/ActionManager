using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ActionManager.Models;

namespace ActionManager.Services;

/// <summary>Хранилище локальных настроек.</summary>
public interface IOptionsStore
{
    string FilePath { get; }
    AppOptions Load();
    bool TryLoad(out AppOptions options, out string error);
    void Save(AppOptions options);
}

/// <summary>JSON-файл в %LocalAppData%\ActionManager\options.json.</summary>
public sealed class JsonOptionsStore : IOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string FilePath { get; }

    public JsonOptionsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ActionManager");
        FilePath = Path.Combine(dir, "options.json");
    }

    public AppOptions Load()
    {
        return TryLoad(out var options, out _) ? options : new AppOptions();
    }

    public bool TryLoad(out AppOptions options, out string error)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                options = new AppOptions();
                error = string.Empty;
                return true;
            }

            using var stream = File.OpenRead(FilePath);
            options = JsonSerializer.Deserialize<AppOptions>(stream, JsonOptions)
                      ?? throw new JsonException("Файл настроек пуст.");
            Normalize(options);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            options = new AppOptions();
            AppLog.Write($"Не удалось прочитать настройки: {error}");
            return false;
        }
    }

    public void Save(AppOptions options)
    {
        Normalize(options);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Пишем во временный файл и заменяем исходный одним перемещением. Поэтому
        // планировщик никогда не увидит наполовину записанный JSON.
        var tempPath = $"{FilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using var mutex = new Mutex(false, @"Local\ActionManager.OptionsFile");
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
                throw new IOException("Настройки заняты другим экземпляром приложения.");

            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, options, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally
        {
            if (lockTaken)
                mutex.ReleaseMutex();
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void Normalize(AppOptions options)
    {
        if (options.ShutdownTime < TimeSpan.Zero || options.ShutdownTime >= TimeSpan.FromDays(1))
            throw new JsonException("Время выключения должно находиться в пределах суток.");
        options.SkippedDates = (options.SkippedDates ?? new())
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        options.LaunchPool = (options.LaunchPool ?? new())
            .Where(entry => entry is not null)
            .Select(entry =>
            {
                if (entry.Id == Guid.Empty)
                    entry.Id = Guid.NewGuid();
                entry.Name = entry.Name?.Trim() ?? string.Empty;
                entry.Target = entry.Target?.Trim() ?? string.Empty;
                entry.Arguments = entry.Arguments?.Trim() ?? string.Empty;
                return entry;
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Target))
            .GroupBy(entry => entry.Id)
            .Select(group => group.First())
            .ToList();
    }
}
