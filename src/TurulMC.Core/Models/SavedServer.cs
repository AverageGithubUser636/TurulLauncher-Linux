using System.Text.Json.Serialization;

namespace TurulMC.Core.Models;

public sealed class SavedServer
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 25565;

    [JsonPropertyName("lastUsed")]
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;

    // Opcionális bővítőmezők (régi/új kliensek round-tripben megőrzik).
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Type { get; set; } = "remote";

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("mcVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string McVersion { get; set; } = string.Empty;

    [JsonPropertyName("ramMb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RamMb { get; set; }

    [JsonPropertyName("dirId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string DirId { get; set; } = string.Empty;

    [JsonPropertyName("eulaAccepted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EulaAccepted { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime CreatedAt { get; set; }
}
