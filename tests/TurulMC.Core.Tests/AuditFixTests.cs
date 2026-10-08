using TurulMC.Core.Models;
using TurulMC.Core.Minecraft;
using TurulMC.Core.Modpacks;
using TurulMC.Core.Security;
using TurulMC.Core.Servers;
using TurulMC.Core.Validation;

namespace TurulMC.Core.Tests;

public class AuditFixTests
{
    [Fact]
    public void PathSecurity_Traversal_ThrowsOrStaysInside()
    {
        var root = Path.Combine(Path.GetTempPath(), "turul-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var traversed = PathSecurity.ResolveInsideRoot(root, "../../etc/passwd");
            Assert.StartsWith(Path.GetFullPath(root), traversed); // stripelve, bent marad
            Assert.Throws<InvalidOperationException>(() => PathSecurity.ResolveInsideRoot(root, "C:/x:y"));
            var ok = PathSecurity.ResolveInsideRoot(root, "mods/a.jar");
            Assert.StartsWith(Path.GetFullPath(root), ok);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Validator_Rejects_JavaPath_WithArgs()
    {
        Assert.Throws<ArgumentException>(() => LauncherSettingsValidator.ValidateJavaPath("C:\\Java\\javaw.exe -agentlib:evil"));
        Assert.Throws<ArgumentException>(() => LauncherSettingsValidator.ValidateJavaPath("C:\\Java\\notjava.bat"));
    }

    [Fact]
    public void Validator_Rejects_BadJvmArgs()
    {
        Assert.Throws<ArgumentException>(() => LauncherSettingsValidator.ValidateJvmArgs(new[] { "-javaagent:evil.jar" }));
        LauncherSettingsValidator.ValidateJvmArgs(new[] { "-Xms512M" });
    }

    [Fact]
    public async Task ServerListStorage_Add_List_Remove()
    {
        var file = Path.Combine(Path.GetTempPath(), "servers-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new ServerListStorage(file);
        var added = await store.AddAsync("Teszt", "example.hu", 25565);
        Assert.Equal("example.hu", added.Host);
        var list = await store.LoadAsync();
        Assert.Contains(list, s => s.Name == "Teszt");
        Assert.True(await store.RemoveAsync("Teszt"));
    }

    [Fact]
    public void ModpackPackager_Hash_Stable()
    {
        var m = new ModpackManifest { Id = "a", Version = "1", Files = new() { new ModpackFile { Path = "mods/a.jar", Url = "https://x/y.jar", Sha256 = "abc", Size = 1 } } };
        Assert.Equal(ModpackPackager.ComputeManifestHash(m), ModpackPackager.ComputeManifestHash(m));
    }

    [Fact]
    public void PathSecurity_EnsureSafeHttps_Rejects_Http()
    {
        Assert.Throws<InvalidOperationException>(() => PathSecurity.EnsureSafeHttpsUrl("http://example.com/x.json"));
        PathSecurity.EnsureSafeHttpsUrl("https://example.com/x.json");
    }

    [Fact]
    public void GameLoadingStatus_Maps_Known_Lines()
    {
        Assert.Equal("Profil beállítása…", GameLoadingStatus.MapLogLine("[Render thread/INFO]: Setting user: Pisti2303"));
        Assert.Equal("Grafikus motor (LWJGL) betöltése…", GameLoadingStatus.MapLogLine("Backend library: LWJGL version 3.3.3"));
        Assert.Equal("Modok betöltése…", GameLoadingStatus.MapLogLine("[main/INFO]: Loading 42 mods"));
        Assert.Null(GameLoadingStatus.MapLogLine("valami érdektelen sor"));
        Assert.Null(GameLoadingStatus.MapLogLine(""));
        Assert.Equal(161, GameLoadingStatus.TrimForLog(new string('x', 500)).Length);
    }

    [Fact]
    public void SavedServer_RoundTrips_Extended_Fields()
    {
        // A mezőben lévő richer servers.json (type/provider/mcVersion/...) nem veszhet el.
        var json = """{"id":"x","name":"TurulNetwork","type":"remote","host":"play.turulnetwork.hu","port":25565,"provider":"","mcVersion":"","ramMb":2048,"dirId":"","eulaAccepted":false}""";
        var loaded = System.Text.Json.JsonSerializer.Deserialize<SavedServer>(json);
        Assert.NotNull(loaded);
        Assert.Equal("TurulNetwork", loaded!.Name);
        Assert.Equal("play.turulnetwork.hu", loaded.Host);
        var back = System.Text.Json.JsonSerializer.Serialize(loaded);
        Assert.Contains("\"host\":\"play.turulnetwork.hu\"", back);
        Assert.Contains("\"type\":\"remote\"", back);
    }

    [Fact]
    public void GetProfiles_Payload_Uses_Username_Contract()
    {
        // A UI p.username / currentProfileId mezőket olvas: a backend szerződés ezt kell adja.
        var profiles = new List<LocalProfile>
        {
            new() { Id = "abc", Username = "Pisti2303", Uuid = "uuid-1" },
            new() { Id = "def", Username = "", Uuid = "uuid-2" } // sérült: a UI/backend szűri
        };
        var payload = new { profiles, currentProfileId = "abc" };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.Contains("\"username\":\"Pisti2303\"", json);
        Assert.Contains("\"currentProfileId\":\"abc\"", json);
        var visible = profiles.Where(p => !string.IsNullOrWhiteSpace(p.Username)).ToList();
        Assert.Single(visible);
    }
}
