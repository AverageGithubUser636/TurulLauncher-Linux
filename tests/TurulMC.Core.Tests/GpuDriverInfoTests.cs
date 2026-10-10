using TurulMC.Core.Diagnostics;

namespace TurulMC.Core.Tests;

public class GpuDriverInfoTests
{
    [Theory]
    [InlineData("NV168 (Nouveau)", true)]
    [InlineData("nouveau", true)]
    [InlineData("NOUVEAU Gallium", true)]
    [InlineData("NVIDIA GeForce RTX 4060/PCIe/SSE2", false)]
    [InlineData("AMD Radeon (radeonsi)", false)]
    [InlineData("Mesa Intel UHD Graphics", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RendererLooksLikeNouveau_MatchesOnlyNouveau(string? renderer, bool expected)
    {
        Assert.Equal(expected, GpuDriverInfo.RendererLooksLikeNouveau(renderer));
    }

    [Fact]
    public void LspciUsesNouveau_FindsDriverLine()
    {
        const string output = """
            01:00.0 VGA compatible controller: NVIDIA Corporation GA106 [GeForce RTX 3060]
            	Subsystem: CardExpert Technology Device 09a5
            	Kernel driver in use: nouveau
            	Kernel modules: nvidiafb, nouveau
            """;
        Assert.True(GpuDriverInfo.LspciUsesNouveau(output));
    }

    [Fact]
    public void LspciUsesNouveau_ProprietaryDriver_NoMatch()
    {
        const string output = """
            01:00.0 VGA compatible controller: NVIDIA Corporation GA106
            	Kernel driver in use: nvidia
            	Kernel modules: nvidiafb, nouveau, nvidia_drm, nvidia
            """;
        Assert.False(GpuDriverInfo.LspciUsesNouveau(output));
    }

    [Fact]
    public void LspciUsesNouveau_Empty_NoMatch()
    {
        Assert.False(GpuDriverInfo.LspciUsesNouveau(null));
        Assert.False(GpuDriverInfo.LspciUsesNouveau(""));
    }

    [Fact]
    public void ProcModulesHasNouveau_FindsModule()
    {
        const string modules = """
            nvidia_drm 123456 0 - Live 0x0000000000000000
            nouveau 1234567 0 - Live 0x0000000000000000
            mxm_wmi 16384 1 nouveau, Live 0x0000000000000000
            """;
        Assert.True(GpuDriverInfo.ProcModulesHasNouveau(modules));
    }

    [Fact]
    public void ProcModulesHasNouveau_WithoutModule_NoMatch()
    {
        const string modules = "nvidia 123456 1 - Live 0x0000000000000000\n";
        Assert.False(GpuDriverInfo.ProcModulesHasNouveau(modules));
        Assert.False(GpuDriverInfo.ProcModulesHasNouveau(null));
    }
}
