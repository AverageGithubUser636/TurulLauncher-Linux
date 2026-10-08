using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurulMC.Core.Authentication;
using TurulMC.Core.Models;

namespace TurulMC.Infrastructure.Authentication;

public class LocalAuthenticationService : IAuthenticationService
{
    private readonly string _profilesFilePath;
    private List<LocalProfile> _profiles = new();
    private LocalProfile? _currentProfile;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LocalAuthenticationService()
    {
        var appData = TurulMC.Core.Storage.LauncherPaths.DataRoot;
        Directory.CreateDirectory(appData);
        _profilesFilePath = Path.Combine(appData, "profiles.json");
        LoadProfiles();
    }

    public async Task<LoginResult> CreateLocalProfileAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
        if (string.IsNullOrWhiteSpace(username))
            return LoginResult.Fail("Username cannot be empty.");

        if (username.Length < 3 || username.Length > 16)
            return LoginResult.Fail("Username must be between 3 and 16 characters.");

        if (_profiles.Any(p => p.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
            return LoginResult.Fail("A profile with this username already exists.");

        var uuid = GenerateOfflineUuid(username);

        var profile = new LocalProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            Uuid = uuid,
            AccessToken = "0",
            UserType = "legacy",
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow
        };

        _profiles.Add(profile);
        _currentProfile = profile;
        SaveProfiles();

        await Task.CompletedTask;
        return LoginResult.Ok(profile);
        }
        finally { _lock.Release(); }
    }

    public async Task<LoginResult> RenameProfileAsync(string profileId, string username)
    {
        await _lock.WaitAsync();
        try
        {
        username = username?.Trim() ?? "";
        if (username.Length < 3 || username.Length > 16)
            return LoginResult.Fail("A névnek 3 és 16 karakter között kell lennie.");

        if (!username.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            return LoginResult.Fail("A név csak betűt, számot és alsóvonást tartalmazhat.");

        if (_profiles.Any(p => p.Id != profileId &&
                               p.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
            return LoginResult.Fail("Már létezik profil ezzel a névvel.");

        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null)
            return LoginResult.Fail("A profil nem található.");

        profile.Username = username;
        profile.Uuid = GenerateOfflineUuid(username);
        profile.LastUsed = DateTime.UtcNow;
        _currentProfile = profile;
        SaveProfiles();

        await Task.CompletedTask;
        return LoginResult.Ok(profile);
        }
        finally { _lock.Release(); }
    }

    public async Task<LoginResult> SelectProfileAsync(string profileId)
    {
        await _lock.WaitAsync();
        try
        {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null)
            return LoginResult.Fail("Profile not found.");

        profile.LastUsed = DateTime.UtcNow;
        _currentProfile = profile;
        SaveProfiles();

        await Task.CompletedTask;
        return LoginResult.Ok(profile);
        }
        finally { _lock.Release(); }
    }

    public async Task<IEnumerable<LocalProfile>> GetProfilesAsync()
    {
        await Task.CompletedTask;
        return _profiles.AsReadOnly();
    }

    public async Task<LocalProfile?> GetCurrentProfileAsync()
    {
        await Task.CompletedTask;
        return _currentProfile;
    }

    public async Task<bool> DeleteProfileAsync(string profileId)
    {
        await _lock.WaitAsync();
        try
        {
        var removed = _profiles.RemoveAll(p => p.Id == profileId) > 0;
        if (removed)
        {
            if (_currentProfile?.Id == profileId)
                _currentProfile = null;
            SaveProfiles();
        }
        await Task.CompletedTask;
        return removed;
        }
        finally { _lock.Release(); }
    }

    public static string GenerateOfflineUuid(string username)
    {
        var input = Encoding.UTF8.GetBytes("OfflinePlayer:" + username);
        var hash = MD5.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void LoadProfiles()
    {
        var backup = _profilesFilePath + ".bak";
        foreach (var candidate in new[] { _profilesFilePath, backup })
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                var json = File.ReadAllText(candidate);
                var loaded = JsonSerializer.Deserialize<List<LocalProfile>>(json);
                if (loaded == null)
                    continue;

                _profiles = loaded
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Username))
                    .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
                _currentProfile = _profiles
                    .OrderByDescending(p => p.LastUsed)
                    .FirstOrDefault();
                return;
            }
            catch
            {
                // Try backup. Never overwrite a recoverable profile file with an empty list.
            }
        }

        _profiles = new List<LocalProfile>();
        _currentProfile = null;
    }

    private void SaveProfiles()
    {
        var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true });
        // Robusztus írás: egyedi temp fájl + írásvédett/elárvult temp kezelés, nem dob.
        var result = TurulMC.Core.Storage.AtomicFile.TryWriteAllText(_profilesFilePath, json);
        if (!result.Success)
            TurulMC.Core.Logging.LauncherLogger.Error("A profilok mentése sikertelen: " + result.Error);
    }
}
