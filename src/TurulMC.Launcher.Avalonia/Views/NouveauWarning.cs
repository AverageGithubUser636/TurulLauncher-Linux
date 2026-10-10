using Avalonia.Controls;
using TurulMC.Core.Diagnostics;
using TurulMC.Core.Logging;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>
/// Nouveau-figyelmeztetés indítás előtt: ha nyílt NVIDIA driver aktív,
/// rákérdez, de <b>az indítást engedi</b> ("Indítás mindenképp").
/// Igaz érték = mehet az indítás; hamis = a felhasználó megszakította.
/// </summary>
public static class NouveauWarning
{
    public static async Task<bool> EnsureAcknowledgedAsync(Window? owner)
    {
        GpuDriverReport report;
        try
        {
            report = await GpuDriverInfo.ProbeAsync();
        }
        catch (Exception ex)
        {
            LauncherLogger.Debug($"Nouveau-ellenőrzés hiba: {ex.Message}");
            return true;
        }

        if (!report.NouveauDetected) return true;

        // UI nélkül nincs kin megmutatni — ilyenkor is engedjük az indítást.
        if (owner is null) return true;

        var renderer = string.IsNullOrWhiteSpace(report.Renderer)
            ? ""
            : $"\n\nRenderer: {report.Renderer}";
        var message =
            "Nouveau nyílt forrású NVIDIA driver aktív a rendszeren. " +
            "A Minecraft ettől még elindul, de fagyás, villogás vagy " +
            "alacsony FPS előfordulhat." + renderer +
            "\n\nStabilabb működéshez telepítsd az NVIDIA zárt driverét.";

        var choice = await ConfirmWindow.AskAsync(
            owner,
            "Nouveau driver észlelve",
            message,
            "Indítás mindenképp",
            "Mégse");

        return choice == 0;
    }
}
