namespace TurulMC.Core.Models;

public class LoginResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public LocalProfile? Profile { get; set; }

    public static LoginResult Ok(LocalProfile profile) => new() { Success = true, Profile = profile };
    public static LoginResult Fail(string error) => new() { Success = false, ErrorMessage = error };
}
