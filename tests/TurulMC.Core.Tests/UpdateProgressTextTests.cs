using TurulMC.Core.Update;

namespace TurulMC.Core.Tests;

public class UpdateProgressTextTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(38_631_928, "36.84 MB")]
    public void FormatBytes_Formats(long bytes, string expected)
    {
        Assert.Equal(expected, UpdateProgressText.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(-5, "—")]
    [InlineData(25_800_000, "24.6 MB/s")]
    public void FormatSpeed_Formats(double bps, string expected)
    {
        Assert.Equal(expected, UpdateProgressText.FormatSpeed(bps));
    }

    [Theory]
    [InlineData(16_000_000, 1_000_000, "16 mp")]
    [InlineData(180_000_000, 1_000_000, "3 p 0 mp")]
    [InlineData(0, 1_000_000, "—")]
    [InlineData(10_000_000, 0, "—")]
    public void FormatEta_Formats(long remaining, double bps, string expected)
    {
        Assert.Equal(expected, UpdateProgressText.FormatEta(remaining, bps));
    }

    [Fact]
    public void FormatDownload_ContainsSizeSpeedAndEta()
    {
        var text = UpdateProgressText.FormatDownload(812_000_000, 1_200_000_000, 24_600_000);
        Assert.Contains("MB", text);
        Assert.Contains("/s", text);
        Assert.Contains("mp", text);
    }

    [Fact]
    public void FormatDownload_WithoutSpeed_ShowsOnlySize()
    {
        Assert.Equal("5 MB / 10 MB", UpdateProgressText.FormatDownload(
            5 * 1024 * 1024, 10 * 1024 * 1024, 0));
    }
}
