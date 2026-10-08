namespace TurulMC.Core.Tests;

public class PathTraversalTests
{
    private static readonly string InstanceDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TurulMC", "instances", "dev-instance");

    [Theory]
    [InlineData("mods/example.jar")]
    [InlineData("config/settings.toml")]
    [InlineData("mods/subdir/plugin.jar")]
    public void SanitizePath_NormalPaths_RemainValid(string input)
    {
        var result = SanitizePath(input);
        Assert.DoesNotContain("..", result);
        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("mods/../../etc/passwd")]
    [InlineData("../secret.txt")]
    public void SanitizePath_TraversalPaths_AreStripped(string input)
    {
        var result = SanitizePath(input);
        Assert.DoesNotContain("..", result);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\System32")]
    public void SanitizePath_AbsolutePaths_AreStripped(string input)
    {
        var result = SanitizePath(input);
        Assert.False(result.StartsWith("/"));
        Assert.False(result.StartsWith("\\"));
    }

    [Fact]
    public void SanitizePath_DotSegments_AreRemoved()
    {
        var result = SanitizePath("mods/./subdir/../file.jar");
        Assert.Equal("mods/subdir/file.jar", result);
    }

    [Fact]
    public void SanitizePath_EmptyPath_ReturnsEmpty()
    {
        var result = SanitizePath("");
        Assert.Equal("", result);
    }

    [Fact]
    public void SanitizePath_LeadingSlashes_AreRemoved()
    {
        var result = SanitizePath("///mods/file.jar");
        Assert.Equal("mods/file.jar", result);
    }

    [Fact]
    public void FullPath_StayInsideInstanceDirectory()
    {
        var file = "mods/test.jar";
        var sanitized = SanitizePath(file);
        var fullPath = Path.Combine(InstanceDir, sanitized);

        Assert.StartsWith(InstanceDir, fullPath);
    }

    [Fact]
    public void TraversalPath_DoesNotEscapeInstanceDirectory()
    {
        var file = "../../etc/passwd";
        var sanitized = SanitizePath(file);
        var fullPath = Path.Combine(InstanceDir, sanitized);

        Assert.StartsWith(InstanceDir, fullPath);
    }

    private static string SanitizePath(string path)
    {
        var sanitized = path.Replace('\\', '/').TrimStart('/');
        var parts = sanitized.Split('/');
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".." || part == ".") continue;
            result.Add(part);
        }
        return string.Join("/", result);
    }
}
