using System.Text.Json.Serialization;
using TurulMC.Core.Models;

namespace TurulMC.Core.Minecraft;

public interface IMinecraftLauncherService
{
    event EventHandler<MinecraftProcessExitedEventArgs>? MinecraftExited;
    event EventHandler<string>? ProcessOutput;
    bool IsGameRunning { get; }
    MinecraftProcessStatus GetStatus();
    Task LaunchAsync(LaunchConfig config, CancellationToken cancellationToken = default);
    Task<LaunchConfig> BuildLaunchConfigAsync(LocalProfile profile, LauncherSettings settings);
    List<string> BuildJvmArguments(LaunchConfig config);
    List<string> BuildGameArguments(LaunchConfig config);
    string BuildClasspath(LaunchConfig config);
}

public sealed class MinecraftProcessExitedEventArgs : EventArgs
{
    public int ExitCode { get; init; }
    public bool WasVerified { get; init; }
    public string OutputTail { get; init; } = string.Empty;
    public string InstanceId { get; init; } = string.Empty;
    [JsonPropertyName("sessionStartedUtc")]
    public DateTime? SessionStartedUtc { get; init; }
    [JsonPropertyName("sessionSeconds")]
    public long SessionSeconds { get; init; }
}

public sealed class MinecraftProcessStatus
{
    [JsonPropertyName("running")]
    public bool Running { get; init; }
    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }
    [JsonPropertyName("runningInstanceId")]
    public string RunningInstanceId { get; init; } = string.Empty;
    [JsonPropertyName("sessionStartedUtc")]
    public DateTime? SessionStartedUtc { get; init; }
    [JsonPropertyName("sessionSeconds")]
    public long SessionSeconds { get; init; }
    [JsonPropertyName("totalPlaytimeSeconds")]
    public long TotalPlaytimeSeconds { get; init; }
    [JsonPropertyName("launchCount")]
    public int LaunchCount { get; init; }
    [JsonPropertyName("lastPlayedAtUtc")]
    public DateTime? LastPlayedAtUtc { get; init; }
    [JsonPropertyName("reattached")]
    public bool Reattached { get; init; }
}
