using System.Text.Json;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Storage;

namespace TurulMC.Infrastructure.FileSystem;

public class SettingsStorage : ISettingsStorage
{
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SettingsStorage()
    {
        var appData = TurulMC.Core.Storage.LauncherPaths.DataRoot;
        Directory.CreateDirectory(appData);
        _settingsFilePath = Path.Combine(appData, "settings.json");
    }

    public async Task<LauncherSettings> LoadSettingsAsync()
    {
        var backup = _settingsFilePath + ".bak";
        foreach (var candidate in new[] { _settingsFilePath, backup })
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                var json = await File.ReadAllTextAsync(candidate);
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(json);
                if (loaded != null)
                    return loaded;
            }
            catch
            {
                // Try backup before falling back to defaults.
            }
        }
        return new LauncherSettings();
    }

    /// <summary>Az utolsó mentés hibája (null = sikerült). A hívó jelezheti a felhasználónak.</summary>
    public string? LastError { get; private set; }

    /// <summary>Igaz, ha az utolsó mentés sikeres volt.</summary>
    public bool LastSaveSucceeded { get; private set; } = true;

    public async Task SaveSettingsAsync(LauncherSettings settings)
    {
        await _saveLock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            // Robusztus, NEM dobó írás: ha a temp fájl írását egy vírusirtó/Controlled Folder
            // Access blokkolja, az AtomicFile közvetlenül a célfájlba ír (a .bak megmarad).
            // Egy mentési hiba soha nem állíthatja meg a játék indítását.
            var result = AtomicFile.TryWriteAllText(_settingsFilePath, json);
            LastSaveSucceeded = result.Success;
            LastError = result.Success ? null : result.Error;
            if (!result.Success)
                LauncherLogger.Error("A beállítások mentése sikertelen: " + result.Error);
        }
        catch (Exception ex)
        {
            LastSaveSucceeded = false;
            LastError = ex.Message;
            LauncherLogger.Error("A beállítások mentése sikertelen", ex);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
