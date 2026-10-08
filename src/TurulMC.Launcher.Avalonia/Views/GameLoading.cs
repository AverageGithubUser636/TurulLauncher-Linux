using Avalonia.Controls;
using TurulMC.Core.Logging;
using TurulMC.Core.Minecraft;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>
/// A betöltőfigyelő megnyitása indítás után (Játék- és Szerver-nézetből).
/// Külön segéd, hogy a szolgáltatási rétegnek ne kelljen nézetszintre
/// hivatkoznia — a VM-ek innen hívják.
/// </summary>
public static class GameLoading
{
    /// <returns>Az ablak, vagy null ha nincs mihez kötni.</returns>
    public static GameLoadingWindow? Show(
        Window? owner,
        string instanceName,
        string detail,
        IMinecraftLauncherService? launcher,
        string startBehavior)
    {
        if (launcher is null)
        {
            LauncherLogger.Warning("Betöltőfigyelő: nincs launcher-szolgáltatás.");
            return null;
        }

        try
        {
            var window = new GameLoadingWindow(owner, instanceName, detail, launcher, startBehavior);
            if (owner is not null) window.Show(owner);
            else window.Show();
            return window;
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Betöltőfigyelő megnyitási hiba: " + ex.Message);
            return null;
        }
    }
}
