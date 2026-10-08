using TurulMC.Core.Models;

namespace TurulMC.Core.Networking;

public interface IServerStatusService
{
    Task<ServerStatus> CheckServerStatusAsync(string host, int port);
}
