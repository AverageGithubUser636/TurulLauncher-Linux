using TurulMC.Infrastructure.Authentication;

namespace TurulMC.Core.Tests;

public class UuidTests
{
    [Fact]
    public void GenerateOfflineUuid_Steve_ReturnsExpectedUuid()
    {
        var uuid = LocalAuthenticationService.GenerateOfflineUuid("Steve");
        Assert.Equal("5627dd98e6be3c21b8a8e92344183641", uuid);
    }

    [Fact]
    public void GenerateOfflineUuid_Alex_ReturnsExpectedUuid()
    {
        var uuid = LocalAuthenticationService.GenerateOfflineUuid("Alex");
        Assert.Equal("36532b5ec4423dbba24cc7e55d0f979a", uuid);
    }

    [Fact]
    public void GenerateOfflineUuid_Returns32HexChars()
    {
        var uuid = LocalAuthenticationService.GenerateOfflineUuid("TestUser");
        Assert.Equal(32, uuid.Length);
        Assert.Matches("^[0-9a-f]{32}$", uuid);
    }

    [Fact]
    public void GenerateOfflineUuid_SameUsername_SameUuid()
    {
        var uuid1 = LocalAuthenticationService.GenerateOfflineUuid("Developer1");
        var uuid2 = LocalAuthenticationService.GenerateOfflineUuid("Developer1");
        Assert.Equal(uuid1, uuid2);
    }

    [Fact]
    public void GenerateOfflineUuid_DifferentUsernames_DifferentUuids()
    {
        var uuid1 = LocalAuthenticationService.GenerateOfflineUuid("User1");
        var uuid2 = LocalAuthenticationService.GenerateOfflineUuid("User2");
        Assert.NotEqual(uuid1, uuid2);
    }

    [Fact]
    public void GenerateOfflineUuid_CaseSensitive()
    {
        var uuidLower = LocalAuthenticationService.GenerateOfflineUuid("player");
        var uuidUpper = LocalAuthenticationService.GenerateOfflineUuid("PLAYER");
        Assert.NotEqual(uuidLower, uuidUpper);
    }

    [Fact]
    public void GenerateOfflineUuid_CorrectVersionBits()
    {
        var uuid = LocalAuthenticationService.GenerateOfflineUuid("TestUser");
        var bytes = Convert.FromHexString(uuid);
        Assert.Equal(3, (bytes[6] >> 4) & 0xF);
        Assert.Equal(8, (bytes[8] >> 4) & 0xF);
    }

    [Fact]
    public void GenerateOfflineUuid_LongUsername_Works()
    {
        var longName = new string('A', 16);
        var uuid = LocalAuthenticationService.GenerateOfflineUuid(longName);
        Assert.Equal(32, uuid.Length);
    }
}
