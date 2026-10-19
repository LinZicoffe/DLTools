using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public class ProjectInspectionTests
{
    private static readonly Lazy<Task<BuildTools>> Tools = new(() => new BuildToolLocator().LocateAsync());

    [Fact]
    public async Task SdkConditionalImportAndMultipleFrameworks_UsesEffectiveProperties()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("Directory.Build.props", """
            <Project><PropertyGroup Condition="'$(Configuration)' == 'Release'">
              <Product>条件导入的产品</Product><PlatformTarget>x64</PlatformTarget>
            </PropertyGroup></Project>
            """);
        var project = ProjectFixtures.Sdk(workspace, "中文 WPF", framework: "net8.0-windows;net9.0-windows",
            extraProperties: "<PlatformTarget Condition=\"'$(Configuration)' != 'Release'\">x86</PlatformTarget>");
        // The SDK fixture normally declares AnyCPU; here the imported property is authoritative.
        File.WriteAllText(project, File.ReadAllText(project).Replace("<PlatformTarget>AnyCPU</PlatformTarget>", ""));
        var result = await new ProjectInspectionService().InspectAsync(project, await Tools.Value);
        Assert.Equal(2, result.Count);
        Assert.All(result, item =>
        {
            Assert.True(item.IsSupported, item.UnsupportedReason);
            Assert.Equal("WPF", item.UiFramework);
            Assert.Equal("x64", item.PlatformTarget);
            Assert.Equal("条件导入的产品", item.ProductName);
            Assert.Equal("Release", item.Configuration);
            Assert.False(item.IsNetFramework);
        });
        Assert.Contains(result, item => item.TargetFramework == "net8.0-windows");
        Assert.Contains(result, item => item.TargetFramework == "net9.0-windows");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFramework_RecognizesDesktopReferencesWithoutSdkFlags(bool wpf)
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Framework(workspace, "传统项目", wpf,
            "<Prefer32Bit>true</Prefer32Bit>");
        var descriptor = Assert.Single(await new ProjectInspectionService().InspectAsync(project, await Tools.Value));
        Assert.True(descriptor.IsSupported, descriptor.UnsupportedReason);
        Assert.True(descriptor.IsNetFramework);
        Assert.Equal(wpf ? "WPF" : "WinForms", descriptor.UiFramework);
        Assert.Equal("v4.8", descriptor.FrameworkVersion);
        Assert.Equal("net48", descriptor.TargetFramework);
        Assert.Equal("AnyCPU", descriptor.PlatformTarget);
        Assert.True(descriptor.Prefer32Bit);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release"),
            Path.TrimEndingDirectorySeparator(descriptor.TargetDirectory));
    }

    [Fact]
    public async Task SdkNet48_IsFrameworkRatherThanModernSelfContainedProject()
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Sdk(workspace, "SDK Forms", false, "net48");
        var descriptor = Assert.Single(await new ProjectInspectionService().InspectAsync(project, await Tools.Value));
        Assert.True(descriptor.IsSupported, descriptor.UnsupportedReason);
        Assert.True(descriptor.IsNetFramework);
        Assert.Equal("WinForms", descriptor.UiFramework);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task SolutionAnyCpuMapping_SelectsActualProjectX86Configuration(string newline)
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Framework(workspace, "映射 Forms", extraProperties: "<PlatformTarget>x86</PlatformTarget>");
        const string id = "{BCDE1234-1111-2222-3333-123456789000}";
        var relative = Path.GetRelativePath(workspace.Root, project);
        var solution = workspace.Write("示例.sln", $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "映射 Forms", "{{relative}}", "{{id}}"
            EndProject
            Global
              GlobalSection(SolutionConfigurationPlatforms) = preSolution
                Release|Any CPU = Release|Any CPU
              EndGlobalSection
              GlobalSection(ProjectConfigurationPlatforms) = postSolution
                {{id}}.Release|Any CPU.ActiveCfg = Release|x86
                {{id}}.Release|Any CPU.Build.0 = Release|x86
              EndGlobalSection
            EndGlobal
            """);
        File.WriteAllText(solution, File.ReadAllText(solution).ReplaceLineEndings(newline));
        var item = Assert.Single(await new ProjectInspectionService().InspectAsync(solution, await Tools.Value));
        Assert.True(item.IsSupported, item.UnsupportedReason);
        Assert.Equal("x86", item.Platform);
        Assert.Equal("x86", item.PlatformTarget);
        Assert.Equal(Path.GetFullPath(solution), item.SolutionPath);
    }

    [Fact]
    public async Task DirectoryScan_PreservesCandidatesAndUnsupportedReasonButSkipsOutputs()
    {
        using var workspace = new TestWorkspace();
        ProjectFixtures.Sdk(workspace, "Wpf");
        ProjectFixtures.Sdk(workspace, "Forms", false);
        ProjectFixtures.Sdk(workspace, "Library", executable: false);
        ProjectFixtures.Sdk(workspace, "bin/Ignored");
        ProjectFixtures.Sdk(workspace, ".dltools-backups/IgnoredBackup");
        var result = await new ProjectInspectionService().InspectAsync(workspace.Root, await Tools.Value);
        Assert.Equal(2, result.Count(item => item.IsSupported));
        Assert.Contains(result, item => item.ProjectName == "Library" && !item.IsSupported);
        Assert.DoesNotContain(result, item => item.ProjectName.StartsWith("Ignored"));
    }

    [Fact]
    public async Task UnsupportedCpu_IsExplainedWithoutInventingX64Support()
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Sdk(workspace, "ArmApp", extraProperties: "<PlatformTarget>arm64</PlatformTarget>");
        var item = Assert.Single(await new ProjectInspectionService().InspectAsync(project, await Tools.Value));
        Assert.False(item.IsSupported);
        Assert.Equal("arm64", item.PlatformTarget.ToLowerInvariant());
        Assert.False(string.IsNullOrWhiteSpace(item.UnsupportedReason));
    }

    [Fact]
    public async Task MissingTool_ProducesActionableUnsupportedCandidate()
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Sdk(workspace, "NoSdk");
        var item = Assert.Single(await new ProjectInspectionService().InspectAsync(project, new BuildTools()));
        Assert.False(item.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(item.UnsupportedReason));
    }

    [Fact]
    public async Task FixedArmRidWithAnyCpu_IsNotPresentedAsAnUnconstrainedX64Project()
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Sdk(workspace, "ArmRid", extraProperties: "<RuntimeIdentifier>win-arm64</RuntimeIdentifier>");
        var item = Assert.Single(await new ProjectInspectionService().InspectAsync(project, await Tools.Value));
        Assert.False(item.IsSupported);
        Assert.Contains("arm64", item.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReevaluateSelectedConfiguration_RefreshesConditionalValues()
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Sdk(workspace, "Configuration", extraProperties:
            "<PlatformTarget Condition=\"'$(Configuration)' == 'Debug'\">x86</PlatformTarget>");
        var service = new ProjectInspectionService();
        var release = Assert.Single(await service.InspectAsync(project, await Tools.Value));
        var debug = await service.EvaluateAsync(release with { Configuration = "Debug" }, await Tools.Value);
        Assert.True(debug.IsSupported, debug.UnsupportedReason);
        Assert.Equal("x86", debug.PlatformTarget);
        Assert.Equal("Debug", debug.Configuration);
        Assert.Contains("Debug", debug.TargetDirectory);
    }

    [Fact]
    public async Task LegacyTargetNameOverride_UsesActualExecutableRatherThanAssemblyIdentity()
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Framework(workspace, "Identity",
            extraProperties: "<TargetName>ActualExecutable</TargetName>");
        var item = Assert.Single(await new ProjectInspectionService().InspectAsync(project, await Tools.Value));
        Assert.True(item.IsSupported, item.UnsupportedReason);
        Assert.Equal("Identity", item.AssemblyName);
        Assert.Equal("ActualExecutable.exe", item.MainExecutable);
    }

    [Fact]
    public async Task MixedFrameworks_MissingFrameworkBuildToolDoesNotHideModernCandidate()
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Sdk(workspace, "Mixed", framework: "net48;net8.0-windows");
        var tools = await Tools.Value;
        var items = await new ProjectInspectionService().InspectAsync(project, tools with { MsBuildPath = "" });
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.TargetFramework == "net48" && !item.IsSupported);
        Assert.Contains(items, item => item.TargetFramework == "net8.0-windows" && item.IsSupported);
    }

    [Fact]
    public async Task MissingFrameworkReferenceAssemblies_IsExplainedBeforeBuild()
    {
        using var workspace = new TestWorkspace();
        var missing = Path.Combine(workspace.Root, "missing-reference-pack");
        var project = ProjectFixtures.Framework(workspace, "NoDeveloperPack",
            extraProperties: $"<TargetFrameworkRootPath>{missing}</TargetFrameworkRootPath>");
        var item = Assert.Single(await new ProjectInspectionService().InspectAsync(project, await Tools.Value));
        Assert.False(item.IsSupported);
        Assert.Contains("开发包", item.UnsupportedReason);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task MissingSolutionActiveCfg_IsNotGuessedFromSolutionPlatform(string newline)
    {
        using var workspace = new TestWorkspace();
        var project = ProjectFixtures.Framework(workspace, "NoMapping");
        var solution = workspace.Write("missing-map.sln", $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "NoMapping", "{{Path.GetRelativePath(workspace.Root, project)}}", "{BCDE1234-1111-2222-3333-123456789000}"
            EndProject
            Global
              GlobalSection(SolutionConfigurationPlatforms) = preSolution
                Release|Any CPU = Release|Any CPU
              EndGlobalSection
            EndGlobal
            """);
        File.WriteAllText(solution, File.ReadAllText(solution).ReplaceLineEndings(newline));
        var item = Assert.Single(await new ProjectInspectionService().InspectAsync(solution, await Tools.Value));
        Assert.False(item.IsSupported);
        Assert.Contains("ActiveCfg", item.UnsupportedReason);
    }
}
