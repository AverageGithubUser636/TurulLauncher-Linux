using System.Text.Json;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Core.Recovery;

namespace TurulMC.Launcher;

public sealed partial class MainWindow
{
    private readonly ModRecoveryService _modRecovery = new(Path.Combine(
        TurulMC.Core.Storage.LauncherPaths.DataRoot, "mod-recovery"));
    private bool _instanceOperationBusy;
    private DateTime _launchAttemptStartedUtc;
    private string _launchAttemptInstanceId = "";

    private static bool NeedsInstanceOperationLease(string action) => action is
        "mods.install" or "mods.installVersion" or "mods.remove" or "mods.toggle" or "mods.list" or "mods.update" or
        "modpack.install" or "modpack.import" or "modpack.switchVersion" or "syncModpack" or
        "instances.select" or "instances.create" or "instances.update" or "instances.delete" or "instances.clear" or
        "instances.copyContent" or "settings.save" or "saveSettings" or "data.clear" or
        "game.launch" or "launchGame" or "server.connect" or "instance.repair" or
        "instance.recovery.restore" or "instance.recovery.list" or
        "instance.snapshot" or "instance.backup" or "content.remove" or
        "resourcepack.install" or "resourcepack.remove";

    private static RecoveryLaunchSettings RecoverySettings(LauncherInstance instance) => new()
    {
        MinecraftVersion = instance.MinecraftVersion, Loader = instance.Loader, LoaderVersion = instance.LoaderVersion,
        ModpackProjectId = instance.ModpackProjectId, ModpackVersionId = instance.ModpackVersionId,
        ModpackName = instance.ModpackName
    };

    private LauncherInstance RecoveryInstance(string? data)
    {
        using var doc = JsonDocument.Parse(data ?? "{}");
        var id = doc.RootElement.TryGetProperty("instanceId", out var prop) ? prop.GetString() ?? "" : _activeInstanceId;
        return _instances.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Az Instance már nem található.");
    }

    private static bool HasRepairFix(string? data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data ?? "{}");
            if (!doc.RootElement.TryGetProperty("fix", out var fix) || fix.ValueKind != JsonValueKind.String) return false;
            var mode = (fix.GetString() ?? "").Trim().ToLowerInvariant();
            return mode is "disable" or "quarantine";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async System.Threading.Tasks.Task PrepareModRecoveryAsync(string action, string? data)
    {
        var reason = action switch
        {
            "mods.install" or "mods.installVersion" => "Mod telepítése / frissítése előtt",
            "mods.remove" => "Mod törlése előtt",
            "mods.toggle" => "Mod be- vagy kikapcsolása előtt",
            "mods.update" => "Modok frissítése előtt",
            "modpack.switchVersion" => "Modpack-verzió váltása előtt (modok és konfigurációk)",
            "syncModpack" => "Modpack szinkronizálása előtt (modok és konfigurációk)",
            "instances.copyContent" => "Modok / konfigurációk másolása előtt",
            "instance.repair" => HasRepairFix(data) ? "Smart Repair javítás előtt" : "",
            _ => ""
        };
        if (reason.Length == 0) return;
        if (_launcherService.IsGameRunning)
            throw new InvalidOperationException("Modok módosítása vagy visszaállítása előtt zárd be a Minecraftot.");
        var instance = action is "modpack.switchVersion" or "mods.update" or "instance.repair"
            ? RecoveryInstance(data)
            : RecoveryInstance("{}");
        if (action == "instances.copyContent")
        {
            using var doc = JsonDocument.Parse(data ?? "{}");
            var request = ParseInstanceCopyRequest(doc.RootElement);
            if (!request.Categories.Any(x => x.Key is "mods" or "config")) return;
            instance = request.Target;
        }
        var root = GetInstanceDirectory(instance.Id);
        var settings = RecoverySettings(instance);
        SendRecoveryEvent("recovery.progress", new { message = "Automatikus modmentés készül…" });
        var snapshot = await System.Threading.Tasks.Task.Run(() => _modRecovery.Create(root, instance.Id, reason, settings));
        SendRecoveryEvent("recovery.saved", new { instanceId = instance.Id, snapshotId = snapshot.Id });
    }

    private async System.Threading.Tasks.Task<object?> HandleRecoveryList(string? data)
    {
        var instance = RecoveryInstance(data);
        var root = GetInstanceDirectory(instance.Id);
        var snapshots = await System.Threading.Tasks.Task.Run(() => _modRecovery.List(root, instance.Id));
        return new
        {
            instanceId = instance.Id, instanceName = instance.Name, retention = ModRecoveryService.RetentionCount,
            snapshots = snapshots.Select(x => new
            {
                id = x.Id, createdUtc = x.CreatedUtc, reason = x.Reason, bytes = x.Bytes,
                minecraftVersion = x.Settings.MinecraftVersion, loader = x.Settings.Loader,
                compatible = x.Settings.MinecraftVersion == instance.MinecraftVersion && x.Settings.Loader == instance.Loader
            }).ToArray()
        };
    }

    private async System.Threading.Tasks.Task<object?> HandleRecoveryRestore(string? data)
    {
        if (_launcherService.IsGameRunning)
            throw new InvalidOperationException("Visszaállítás előtt zárd be a Minecraftot.");
        using var doc = JsonDocument.Parse(data ?? "{}");
        if (!doc.RootElement.TryGetProperty("confirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("A visszaállítást előbb erősítsd meg.");
        var instance = RecoveryInstance(data);
        var snapshotId = doc.RootElement.GetProperty("snapshotId").GetString() ?? "";
        var root = GetInstanceDirectory(instance.Id);
        var settings = RecoverySettings(instance);
        var target = _modRecovery.List(root, instance.Id).FirstOrDefault(x => x.Id == snapshotId)
            ?? throw new InvalidOperationException("A visszaállítási pont már nem érhető el.");
        if (target.Settings.MinecraftVersion != instance.MinecraftVersion || target.Settings.Loader != instance.Loader)
            throw new InvalidOperationException($"Ez a mentés Minecraft {target.Settings.MinecraftVersion} / {target.Settings.Loader} környezethez készült. Előbb válaszd ezt a verziót és loadert az Instance beállításaiban.");
        SendRecoveryEvent("recovery.progress", new { message = "Modmentés ellenőrzése és visszaállítása…" });
        await System.Threading.Tasks.Task.Run(() => _modRecovery.Restore(root, instance.Id, snapshotId, settings));
        // Deliberately keep the user's current RAM, account and launch settings. Modpack identity
        // is not rewound: resource packs and other unrelated modpack files are outside this scope.
        LauncherLogger.Info($"Mod recovery restored: {instance.Id}, snapshot {snapshotId}");
        return new { success = true, instanceId = instance.Id };
    }

    private sealed record SavedCrash(string InstanceId, string InstanceName, DateTime CreatedUtc, CrashExplanation Explanation);

    private async System.Threading.Tasks.Task<CrashExplanation> ExplainFailureAsync(string instanceId, DateTime started,
        string details, int? exitCode = null)
    {
        var root = GetInstanceDirectory(instanceId);
        if (!exitCode.HasValue)
        {
            var match = System.Text.RegularExpressions.Regex.Match(details, @"exit code:\s*(-?\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed)) exitCode = parsed;
        }
        var explanation = await System.Threading.Tasks.Task.Run(() =>
            CrashExplanationService.Analyze(details + "\n" + (exitCode.HasValue ? CrashExplanationService.ReadSessionLogs(root, started) : ""), exitCode));
        var instance = _instances.FirstOrDefault(x => x.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (instance == null) return explanation; // Do not recreate an Instance deleted while logs were read.
        var report = new SavedCrash(instanceId, instance.Name, DateTime.UtcNow, explanation);
        try
        {
            Directory.CreateDirectory(root);
            var destination = Path.Combine(root, ".turul-last-crash.json");
            var write = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(
                destination, JsonSerializer.Serialize(report), createBackup: false);
            if (!write.Success)
                LauncherLogger.Warning("A hibaelemzés nem menthető: " + write.Error);
        }
        catch (Exception ex) { LauncherLogger.Warning("A hibaelemzés nem menthető: " + ex.Message); }
        SendRecoveryEvent("recovery.crash", CrashPayload(report));
        return explanation;
    }

    private static object CrashPayload(SavedCrash report) => new
    {
        instanceId = report.InstanceId, instanceName = report.InstanceName, createdUtc = report.CreatedUtc,
        code = report.Explanation.Code, title = report.Explanation.Title, summary = report.Explanation.Summary,
        steps = report.Explanation.Steps, evidence = report.Explanation.Evidence,
        recognized = report.Explanation.Recognized, exitCode = report.Explanation.ExitCode
    };

    private async System.Threading.Tasks.Task<object?> HandleLastCrash(string? data)
    {
        var instance = RecoveryInstance(data);
        var path = Path.Combine(GetInstanceDirectory(instance.Id), ".turul-last-crash.json");
        if (!File.Exists(path)) return new { found = false, instanceId = instance.Id };
        if (new FileInfo(path).Length > 65536) return new { found = false, instanceId = instance.Id };
        try
        {
            var report = JsonSerializer.Deserialize<SavedCrash>(await File.ReadAllTextAsync(path));
            if (report == null || report.InstanceId != instance.Id) return new { found = false, instanceId = instance.Id };
            return new { found = true, report = CrashPayload(report) };
        }
        catch (JsonException) { return new { found = false, instanceId = instance.Id }; }
    }

    private void SendRecoveryEvent(string action, object data)
    {
        if (_webView?.CoreWebView2 != null)
            TryPostWebMessage(JsonSerializer.Serialize(new { action, data }));
    }
}
