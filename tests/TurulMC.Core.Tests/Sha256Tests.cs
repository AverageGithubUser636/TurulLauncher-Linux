using TurulMC.Core.Security;

namespace TurulMC.Core.Tests;

public class Sha256Tests
{
    [Fact]
    public async Task ComputeHash_KnownInput_ReturnsExpectedHash()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "Hello, TurulMC!");
            var hash = await Sha256Service.ComputeHashAsync(tempFile);
            Assert.Equal("09a0d783f4139cc95016ffd35585d8d75052fe6f894715c58874ae53af81b6a9", hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ComputeHash_ByteArray_ReturnsExpectedHash()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("test");
        var hash = Sha256Service.ComputeHash(data);
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", hash);
    }

    [Fact]
    public async Task ComputeHash_Stream_ReturnsExpectedHash()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("stream test");
        using var stream = new MemoryStream(bytes);
        var hash = await Sha256Service.ComputeHashAsync(stream);
        Assert.Equal("3ab4d125d8a85630da9f96be5350e01baf1547c183fdb05da37f458d16685695", hash);
    }

    [Fact]
    public async Task VerifyFile_CorrectHash_ReturnsTrue()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "verify me");
            var hash = await Sha256Service.ComputeHashAsync(tempFile);
            var result = await Sha256Service.VerifyFileAsync(tempFile, hash);
            Assert.True(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyFile_WrongHash_ReturnsFalse()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "verify me");
            var result = await Sha256Service.VerifyFileAsync(tempFile, "0000000000000000000000000000000000000000000000000000000000000000");
            Assert.False(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyFile_NonExistentFile_ReturnsFalse()
    {
        var result = await Sha256Service.VerifyFileAsync("/nonexistent/file.txt", "abc");
        Assert.False(result);
    }

    [Fact]
    public async Task ComputeHash_EmptyFile_ReturnsKnownHash()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var hash = await Sha256Service.ComputeHashAsync(tempFile);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ComputeHash_DifferentInputs_ProduceDifferentHashes()
    {
        var hash1 = Sha256Service.ComputeHash(System.Text.Encoding.UTF8.GetBytes("abc"));
        var hash2 = Sha256Service.ComputeHash(System.Text.Encoding.UTF8.GetBytes("def"));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_SameInput_ProducesSameHash()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("consistent");
        var hash1 = Sha256Service.ComputeHash(data);
        var hash2 = Sha256Service.ComputeHash(data);
        Assert.Equal(hash1, hash2);
    }
}
