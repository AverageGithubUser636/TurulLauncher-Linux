using TurulMC.Core.Models;

namespace TurulMC.Core.Authentication;

public interface IAuthenticationService
{
    Task<LoginResult> CreateLocalProfileAsync(string username);
    Task<LoginResult> SelectProfileAsync(string profileId);
    Task<LoginResult> RenameProfileAsync(string profileId, string username);
    Task<IEnumerable<LocalProfile>> GetProfilesAsync();
    Task<LocalProfile?> GetCurrentProfileAsync();
    Task<bool> DeleteProfileAsync(string profileId);
}
