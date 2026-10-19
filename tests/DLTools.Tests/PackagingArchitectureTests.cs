using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class PackagingArchitectureTests
{
    [Theory]
    [InlineData("AnyCPU", "", "win-x64,win-x86")]
    [InlineData("x86", "", "win-x86")]
    [InlineData("x64", "", "win-x64")]
    [InlineData("AnyCPU", "win-x86", "win-x86")]
    [InlineData("x64", "win-x86", "")]
    [InlineData("ARM64", "", "")]
    public void PublishTargets_RespectCpuAndFixedRuntime(string cpu, string fixedRid, string expected)
    {
        var project = new ProjectDescriptor { PlatformTarget = cpu, DeclaredRuntimeIdentifier = fixedRid };
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), PackagingService.AllowedRuntimeIdentifiers(project));
    }

    [Fact]
    public void DeclaredRuntimeList_RestrictsUserSelection()
    {
        var project = new ProjectDescriptor { DeclaredRuntimeIdentifiers = ["linux-x64", "win-x86"] };
        Assert.Equal(["win-x86"], PackagingService.AllowedRuntimeIdentifiers(project));
        Assert.Empty(PackagingService.AllowedRuntimeIdentifiers(project with { IsNetFramework = true }));
    }
}
