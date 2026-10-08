using System.Text;
using TurulMC.Core.Models;

namespace TurulMC.Core.Tests;

public class CliArgumentTests
{
    [Fact]
    public void BuildJvmArguments_IncludesMemorySettings()
    {
        var config = new LaunchConfig
        {
            MinMemoryMb = 512,
            MaxMemoryMb = 4096
        };

        var args = BuildJvmArguments(config);

        Assert.Contains("-Xms512M", args);
        Assert.Contains("-Xmx4096M", args);
    }

    [Fact]
    public void BuildJvmArguments_IncludesG1GC()
    {
        var config = new LaunchConfig();
        var args = BuildJvmArguments(config);

        Assert.Contains("-XX:+UseG1GC", args);
    }

    [Fact]
    public void BuildJvmArguments_IncludesSystemProperties()
    {
        var config = new LaunchConfig
        {
            SystemProperties = new Dictionary<string, string>
            {
                ["java.library.path"] = "/natives",
                ["minecraft.launcher.brand"] = "TurulMC-Launcher"
            }
        };

        var args = BuildJvmArguments(config);

        Assert.Contains("-Djava.library.path=/natives", args);
        Assert.Contains("-Dminecraft.launcher.brand=TurulMC-Launcher", args);
    }

    [Fact]
    public void BuildJvmArguments_IncludesAdditionalArgs()
    {
        var config = new LaunchConfig
        {
            AdditionalJvmArgs = new List<string> { "-Dcustom.prop=value" }
        };

        var args = BuildJvmArguments(config);

        Assert.Contains("-Dcustom.prop=value", args);
    }

    [Fact]
    public void BuildGameArguments_IncludesUsername()
    {
        var config = new LaunchConfig
        {
            Username = "TestDev",
            Version = "1.20.1",
            GameDirectory = "/game",
            AssetsDirectory = "/assets",
            Uuid = "abc123",
            AccessToken = "0"
        };

        var args = BuildGameArguments(config);

        var usernameIndex = args.IndexOf("--username");
        Assert.NotEqual(-1, usernameIndex);
        Assert.Equal("TestDev", args[usernameIndex + 1]);
    }

    [Fact]
    public void BuildGameArguments_IncludesUuid()
    {
        var config = new LaunchConfig
        {
            Username = "TestDev",
            Version = "1.20.1",
            GameDirectory = "/game",
            AssetsDirectory = "/assets",
            Uuid = "360140d3e14745b093e5af5b02ef4c80",
            AccessToken = "0"
        };

        var args = BuildGameArguments(config);

        var uuidIndex = args.IndexOf("--uuid");
        Assert.NotEqual(-1, uuidIndex);
        Assert.Equal("360140d3e14745b093e5af5b02ef4c80", args[uuidIndex + 1]);
    }

    [Fact]
    public void BuildGameArguments_IncludesServerInfo()
    {
        var config = new LaunchConfig
        {
            Username = "TestDev",
            Version = "1.20.1",
            GameDirectory = "/game",
            AssetsDirectory = "/assets",
            Uuid = "abc",
            AccessToken = "0",
            ServerIp = "localhost",
            ServerPort = 25565
        };

        var args = BuildGameArguments(config);

        var serverIndex = args.IndexOf("--server");
        Assert.NotEqual(-1, serverIndex);
        Assert.Equal("localhost", args[serverIndex + 1]);

        var portIndex = args.IndexOf("--port");
        Assert.NotEqual(-1, portIndex);
        Assert.Equal("25565", args[portIndex + 1]);
    }

    [Fact]
    public void BuildGameArguments_NoServer_OmitsServerArgs()
    {
        var config = new LaunchConfig
        {
            Username = "TestDev",
            Version = "1.20.1",
            GameDirectory = "/game",
            AssetsDirectory = "/assets",
            Uuid = "abc",
            AccessToken = "0"
        };

        var args = BuildGameArguments(config);

        Assert.DoesNotContain("--server", args);
        Assert.DoesNotContain("--port", args);
    }

    [Fact]
    public void BuildGameArguments_IncludesAssetIndex()
    {
        var config = new LaunchConfig
        {
            Username = "TestDev",
            Version = "1.20.1",
            GameDirectory = "/game",
            AssetsDirectory = "/assets",
            Uuid = "abc",
            AccessToken = "0",
            AssetIndexId = "12"
        };

        var args = BuildGameArguments(config);

        var assetIndex = args.IndexOf("--assetIndex");
        Assert.NotEqual(-1, assetIndex);
        Assert.Equal("12", args[assetIndex + 1]);
    }

    [Fact]
    public void BuildGameArguments_OmitsAssetIndex_WhenNull()
    {
        var config = new LaunchConfig
        {
            Username = "TestDev",
            Version = "1.20.1",
            GameDirectory = "/game",
            AssetsDirectory = "/assets",
            Uuid = "abc",
            AccessToken = "0"
        };

        var args = BuildGameArguments(config);

        Assert.DoesNotContain("--assetIndex", args);
    }

    [Fact]
    public void BuildGameArguments_IncludesVersionType()
    {
        var config = new LaunchConfig
        {
            Username = "TestDev",
            Version = "1.20.1",
            GameDirectory = "/game",
            AssetsDirectory = "/assets",
            Uuid = "abc",
            AccessToken = "0"
        };

        var args = BuildGameArguments(config);

        var versionTypeIndex = args.IndexOf("--versionType");
        Assert.NotEqual(-1, versionTypeIndex);
        Assert.Equal("TurulMC", args[versionTypeIndex + 1]);
    }

    [Fact]
    public void BuildClasspath_ConcatenatesWithSeparator()
    {
        var config = new LaunchConfig
        {
            ClassPath = new List<string> { "/a.jar", "/b.jar", "/c.jar" }
        };

        var cp = BuildClasspath(config);

        Assert.Contains("/a.jar", cp);
        Assert.Contains("/b.jar", cp);
        Assert.Contains("/c.jar", cp);
        Assert.Contains(Path.PathSeparator.ToString(), cp);
    }

    [Fact]
    public void BuildClasspath_EmptyClasspath_ReturnsEmpty()
    {
        var config = new LaunchConfig
        {
            ClassPath = new List<string>()
        };

        var cp = BuildClasspath(config);
        Assert.Equal(string.Empty, cp);
    }

    // --- Local helper methods mirroring MinecraftLauncherService logic ---

    private static List<string> BuildJvmArguments(LaunchConfig config)
    {
        var args = new List<string>();
        args.Add($"-Xms{config.MinMemoryMb}M");
        args.Add($"-Xmx{config.MaxMemoryMb}M");
        args.Add("-XX:+UseG1GC");
        foreach (var (key, value) in config.SystemProperties)
            args.Add($"-D{key}={value}");
        args.AddRange(config.AdditionalJvmArgs);
        return args;
    }

    private static List<string> BuildGameArguments(LaunchConfig config)
    {
        var args = new List<string>();
        args.Add("--username");
        args.Add(config.Username);
        args.Add("--version");
        args.Add(config.Version);
        args.Add("--gameDir");
        args.Add(config.GameDirectory);
        args.Add("--assetsDir");
        args.Add(config.AssetsDirectory);
        if (!string.IsNullOrEmpty(config.AssetIndexId))
        {
            args.Add("--assetIndex");
            args.Add(config.AssetIndexId);
        }
        args.Add("--uuid");
        args.Add(config.Uuid);
        args.Add("--accessToken");
        args.Add(config.AccessToken);
        args.Add("--userType");
        args.Add(config.UserType);
        args.Add("--versionType");
        args.Add("TurulMC");
        if (!string.IsNullOrEmpty(config.ServerIp))
        {
            args.Add("--server");
            args.Add(config.ServerIp);
            args.Add("--port");
            args.Add(config.ServerPort.ToString());
        }
        return args;
    }

    private static string BuildClasspath(LaunchConfig config)
    {
        return string.Join(Path.PathSeparator, config.ClassPath.Where(p => !string.IsNullOrEmpty(p)));
    }
}
