using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using wetwiLAG.Models;

namespace wetwiLAG.Services;

public sealed class AppDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);

    public AppDataStore(string? rootDirectory = null)
    {
        Root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wetwiLAG");
        BackupsDirectory = Path.Combine(Root, "backups");
        Directory.CreateDirectory(BackupsDirectory);
    }

    public string Root { get; }
    public string BackupsDirectory { get; }
    public string? LastLoadWarning { get; private set; }

    public Task<AppSettings> LoadSettingsAsync() =>
        LoadAsync("settings.json", new AppSettings());

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await _settingsLock.WaitAsync();
        try
        {
            await SaveAsync("settings.json", settings);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public Task<List<UserProfile>> LoadProfilesAsync() =>
        LoadAsync("profiles.json", new List<UserProfile>());

    public Task SaveProfilesAsync(IEnumerable<UserProfile> profiles) =>
        SaveAsync("profiles.json", profiles.ToList());

    public Task<List<MeasurementRecord>> LoadHistoryAsync() =>
        LoadAsync("history.json", new List<MeasurementRecord>());

    public async Task SaveHistoryAsync(IEnumerable<MeasurementRecord> history)
    {
        await _historyLock.WaitAsync();
        try
        {
            await SaveAsync("history.json", history.ToList());
        }
        finally
        {
            _historyLock.Release();
        }
    }

    public async Task AppendMeasurementAsync(MeasurementRecord measurement)
    {
        await _historyLock.WaitAsync();
        try
        {
            var history = await LoadHistoryAsync();
            history.Add(measurement);
            if (history.Count > 5000)
            {
                history.RemoveRange(0, history.Count - 5000);
            }

            await SaveAsync("history.json", history);
        }
        finally
        {
            _historyLock.Release();
        }
    }

    public Task<List<OptimizerOperation>> LoadOperationsAsync() =>
        LoadAsync("optimizer-operations.json", new List<OptimizerOperation>());

    public async Task AppendOperationAsync(OptimizerOperation operation)
    {
        await _operationLock.WaitAsync();
        try
        {
            var operations = await LoadOperationsAsync();
            operations.Add(operation);
            await SaveAsync("optimizer-operations.json", operations);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<string> SaveBackupAsync(OptimizerBackup backup)
    {
        Directory.CreateDirectory(BackupsDirectory);
        var fileName = $"{backup.CreatedAt:yyyyMMdd-HHmmssfff}_{backup.Id:N}.json";
        var path = Path.Combine(BackupsDirectory, fileName);
        backup.BackupFile = fileName;
        await SaveNewFileAsync(path, backup);
        return fileName;
    }

    public async Task<List<OptimizerBackup>> LoadBackupsAsync()
    {
        Directory.CreateDirectory(BackupsDirectory);
        var backups = new List<OptimizerBackup>();
        foreach (var path in Directory.EnumerateFiles(BackupsDirectory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var backup = await JsonSerializer.DeserializeAsync<OptimizerBackup>(stream, JsonOptions);
                if (backup is not null)
                {
                    backup.BackupFile = Path.GetFileName(path);
                    backups.Add(backup);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                LogError("load-backup", exception);
                LastLoadWarning = $"A backup could not be read: {Path.GetFileName(path)}. The file was preserved.";
            }
        }

        return backups.OrderByDescending(backup => backup.CreatedAt).ToList();
    }

    public void LogError(string operation, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(
                Path.Combine(Root, "application.log"),
                $"{DateTimeOffset.Now:O} ERROR {operation}: {exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
            System.Diagnostics.Trace.WriteLine($"Unable to write application error log ({operation}): {exception}");
        }
    }

    public void LogInformation(string operation, string details)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(
                Path.Combine(Root, "application.log"),
                $"{DateTimeOffset.Now:O} INFO {operation}: {details}{Environment.NewLine}");
        }
        catch (IOException)
        {
            System.Diagnostics.Trace.WriteLine($"Unable to write application log ({operation}): {details}");
        }
    }

    private async Task<T> LoadAsync<T>(string fileName, T fallback)
    {
        var path = Path.Combine(Root, fileName);
        if (!File.Exists(path))
        {
            return fallback;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions) ?? fallback;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            LogError($"load-{fileName}", exception);
            LastLoadWarning = $"Saved data in {fileName} could not be read. The original file was preserved.";
            return fallback;
        }
    }

    private Task SaveAsync<T>(string fileName, T value) =>
        SaveReplacingFileAsync(Path.Combine(Root, fileName), value);

    private async Task SaveReplacingFileAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Root);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
                await stream.FlushAsync();
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task SaveNewFileAsync<T>(string path, T value)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
                await stream.FlushAsync();
            }

            File.Move(temporaryPath, path, false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
