using TurulMC.Core.Models;

namespace TurulMC.Core.Storage;

public interface ISettingsStorage
{
    Task<LauncherSettings> LoadSettingsAsync();
    Task SaveSettingsAsync(LauncherSettings settings);
}
