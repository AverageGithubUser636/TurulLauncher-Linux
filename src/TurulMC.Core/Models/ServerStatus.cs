namespace TurulMC.Core.Models;

public class ServerStatus
{
    public bool IsOnline { get; set; }
    public string? Motd { get; set; }
    public int? OnlinePlayers { get; set; }
    public int? MaxPlayers { get; set; }
    public string? Version { get; set; }
    public long ResponseTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
}
