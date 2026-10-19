using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

[CollectionDefinition("Packaging pipeline", DisableParallelization = true)]
public sealed class PackagingPipelineCollection { }

[Collection("Packaging pipeline")]
public sealed class PackagingPipelineTests
{
    [PackagingIntegrationFact]
    public async Task DesktopProjectMatrix_BuildsRealInstallersAndPreservesRuntimeContent()
    {
        using var ws = new TestWorkspace();
        var tools = await new BuildToolLocator().LocateAsync();
        var paths = new[]
        {
            ProjectFixtures.Sdk(ws, "中文 WPF8"),
            ProjectFixtures.Sdk(ws, "Forms9", false, "net9.0-windows"),
            ProjectFixtures.Framework(ws, "LegacyForms"),
            ProjectFixtures.Framework(ws, "LegacyWpf", true),
            ProjectFixtures.Sdk(ws, "SdkFramework", false, "net48")
        };
        foreach (var path in paths)
        {
            var options = await PipelineFixture.Options(ws, path, tools);
            if (options.Project.ProjectName == "Forms9") options = options with { SelfContained = true };
            var progress = new List<PackagingProgress>();
            var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state"))
                .PackageAsync(options, new InlineProgress(progress.Add), _ => Task.FromResult(true));
            Assert.True(result.Succeeded, result.Error + "\n" + result.LogPath);
            Assert.False(result.Cancelled);
            Assert.Equal(Path.Combine(options.OutputDirectory, options.InstallerFileName), result.InstallerPath);
            Assert.True(new FileInfo(result.InstallerPath!).Length > 10000);
            Assert.Contains(progress, p => p.Stage == PackagingStage.Building);
            Assert.Contains(progress, p => p.Stage == PackagingStage.Compiling);
            Assert.Contains(progress, p => p.Stage == PackagingStage.Succeeded);
            Assert.True(File.Exists(result.LogPath));
            var stages = Directory.GetFiles(Path.Combine(ws.Root, "state"), "runtime-data.xml", SearchOption.AllDirectories);
            Assert.NotEmpty(stages);
            Assert.Contains(stages, file => File.ReadAllText(file).Contains("keep me"));
        }
    }

    [PackagingIntegrationFact]
    public async Task SolutionMapping_BuildsExternalReferenceUsingItsMappedPlatform()
    {
        using var ws = new TestWorkspace();
        const string appGuid = "{BCDE1234-1111-2222-3333-123456789000}";
        const string libraryGuid = "{87654321-1111-2222-3333-123456789000}";
        var project = ProjectFixtures.Framework(ws, "App",
            extraProperties: "<OutputPath Condition=\"'$(CurrentSolutionConfigurationContents)' != ''\">bin\\SolutionMapped\\</OutputPath>",
            additionalItems: $$"""
            <ProjectReference Include="..\External Library\Library.csproj">
              <Project>{{libraryGuid}}</Project><Name>Library</Name>
            </ProjectReference>
            """);
        ws.Write("External Library/Library.csproj", $$"""
            <Project ToolsVersion="14.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <ProjectGuid>{{libraryGuid}}</ProjectGuid><OutputType>Library</OutputType>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion><AssemblyName>MappedLibrary</AssemblyName>
                <OutputPath>bin\$(Configuration)\$(Platform)\</OutputPath>
              </PropertyGroup>
              <ItemGroup><Reference Include="System"/><Compile Include="Library.cs"/></ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets"/>
              <Target Name="CheckMappedPlatform" BeforeTargets="CoreCompile">
                <Error Condition="'$(Platform)' != 'x86'" Text="Library requires solution-mapped x86 platform."/>
              </Target>
            </Project>
            """);
        ws.Write("External Library/Library.cs", "public class FixtureLibrary { public static int Value = 42; }");
        var solution = ws.Write("Mapped.sln", $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{{appGuid}}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Library", "External Library\Library.csproj", "{{libraryGuid}}"
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Release|x64 = Release|x64
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {{appGuid}}.Release|x64.ActiveCfg = Release|Any CPU
                    {{appGuid}}.Release|x64.Build.0 = Release|Any CPU
                    {{libraryGuid}}.Release|x64.ActiveCfg = Release|x86
                    {{libraryGuid}}.Release|x64.Build.0 = Release|x86
                EndGlobalSection
            EndGlobal
            """);
        var tools = await new BuildToolLocator().LocateAsync();
        var stale = ws.Write("App/bin/SolutionMapped/old.txt", "original mapped output");
        var selected = Assert.Single((await new ProjectInspectionService().InspectAsync(solution, tools)), p => p.IsSupported);
        Assert.EndsWith("bin\\SolutionMapped\\", selected.TargetDirectory);
        var options = (await PipelineFixture.Options(ws, project, tools)) with { Project = selected };
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state"))
            .PackageAsync(options, null, _ => Task.FromResult(true));
        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(selected.TargetDirectory, "MappedLibrary.dll")));
        Assert.False(File.Exists(stale));
        Assert.Contains(result.BackupPaths, p => File.Exists(Path.Combine(p, "old.txt")));
        Assert.True(File.Exists(Path.Combine(ws.Root, "External Library/bin/Release/x86/MappedLibrary.dll")));
    }

    [PackagingIntegrationFact]
    public async Task FrameworkOutput_RebuildExcludesStaleFilesAndKeepsOriginalBackup()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "Legacy"));
        Directory.CreateDirectory(options.Project.TargetDirectory);
        var stale = Path.Combine(options.Project.TargetDirectory, "old-user-file.json");
        File.WriteAllText(stale, "original");
        var confirmations = new List<PackagingConfirmation>();
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state")).PackageAsync(options,
            null, c => { confirmations.Add(c); return Task.FromResult(true); });
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(confirmations, c => c.Path.TrimEnd('\\') == options.Project.TargetDirectory.TrimEnd('\\'));
        Assert.False(File.Exists(stale));
        Assert.Contains(result.BackupPaths, p => File.Exists(Path.Combine(p, "old-user-file.json")));
    }

    [PackagingIntegrationFact]
    public async Task ExistingScript_PreservesIdLicenseAndOriginalTextAndOverridesOutputName()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Sdk(ws, "Script Wpf"));
        var input = Path.Combine(ws.Root, "script", "Publish");
        var secret = "LOCAL-ONLY-LICENSE-CONTENT-DO-NOT-LOG";
        var license = ws.Write("script/license.txt", secret);
        var script = ws.Write("script/setup.iss", PipelineFixture.Script(input, license));
        var original = File.ReadAllBytes(script);
        ws.Write("script/Publish/old.txt", "original input");
        options = options with { Mode = InstallerMode.ExistingScript, ExistingScriptPath = script,
            ScriptInputDirectory = input, InstallerFileName = "自定义包名.exe" };
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state"))
            .PackageAsync(options, null, _ => Task.FromResult(true));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(original, File.ReadAllBytes(script));
        Assert.Equal(secret, File.ReadAllText(license));
        Assert.DoesNotContain(secret, File.ReadAllText(result.LogPath));
        Assert.True(File.Exists(Path.Combine(input, options.Project.MainExecutable)));
        Assert.True(File.Exists(Path.Combine(input, "runtime-data.xml")));
        Assert.EndsWith("自定义包名.exe", result.InstallerPath);
        Assert.Contains(result.BackupPaths, p => File.Exists(Path.Combine(p, "old.txt")));
    }

    [PackagingIntegrationFact]
    public async Task MissingScriptLicense_FailsAndRestoresOriginalInputAndPackage()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Sdk(ws, "MissingLicense", false));
        var input = Path.Combine(ws.Root, "script", "Publish");
        var script = ws.Write("script/setup.iss", PipelineFixture.Script(input, Path.Combine(ws.Root, "missing-license.txt")));
        var oldInput = ws.Write("script/Publish/old.txt", "old input");
        var oldPackage = ws.Write(Path.GetRelativePath(ws.Root, Path.Combine(options.OutputDirectory, options.InstallerFileName)), "old installer");
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state")).PackageAsync(
            options with { Mode = InstallerMode.ExistingScript, ExistingScriptPath = script, ScriptInputDirectory = input },
            null, _ => Task.FromResult(true));
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Contains("license", File.ReadAllText(result.LogPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old input", File.ReadAllText(oldInput));
        Assert.Equal("old installer", File.ReadAllText(oldPackage));
    }

    [PackagingIntegrationFact]
    public async Task ExistingScript_MacroAndIncludeExternalPayload_IsRejected()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "ExternalPayload"));
        var input = Path.Combine(ws.Root, "script", "Publish");
        var originalInput = ws.Write("script/Publish/old.txt", "original");
        ws.Write("script/external.iss", "#define PayloadFlags \"external ignoreversion\"\n"
            + "Source: \"{src}\\required-payload.dat\"; DestDir: \"{app}\"; Flags: {#PayloadFlags}\n");
        var script = ws.Write("script/setup.iss", PipelineFixture.Script(input) + "\n#include \"external.iss\"\n");
        var original = File.ReadAllBytes(script);
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state")).PackageAsync(
            options with { Mode = InstallerMode.ExistingScript, ExistingScriptPath = script, ScriptInputDirectory = input },
            null, _ => Task.FromResult(true));
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Equal(original, File.ReadAllBytes(script));
        Assert.Equal("original", File.ReadAllText(originalInput));
        Assert.False(File.Exists(Path.Combine(options.OutputDirectory, options.InstallerFileName)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(script)!, ".dltools-*"));
    }

    [PackagingIntegrationFact]
    public async Task ExistingScript_DirectExternalFlagWithoutSrcConstant_IsRejected()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "DirectExternal"));
        var input = Path.Combine(ws.Root, "script", "Publish");
        var script = ws.Write("script/setup.iss", PipelineFixture.Script(input)
            + "\nSource: \"required.dat\"; DestDir: \"{app}\"; Flags: external ignoreversion\n");
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state")).PackageAsync(
            options with { Mode = InstallerMode.ExistingScript, ExistingScriptPath = script, ScriptInputDirectory = input },
            null, _ => Task.FromResult(true));
        Assert.False(result.Succeeded);
        Assert.Contains("external", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(options.OutputDirectory, options.InstallerFileName)));
    }

    [PackagingIntegrationFact]
    public async Task ExistingScript_NormalRelativeIncludeAndMacros_ArePreserved()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "Included"));
        var input = Path.Combine(ws.Root, "script", "Publish");
        ws.Write("script/extra.txt", "included content");
        ws.Write("script/files.iss", "#define ExtraFile \"extra.txt\"\n"
            + "Source: \"{#ExtraFile}\"; DestDir: \"{app}\"; Flags: ignoreversion\n");
        var script = ws.Write("script/setup.iss", PipelineFixture.Script(input) + "\n#include \"files.iss\"\n");
        var original = File.ReadAllBytes(script);
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state")).PackageAsync(
            options with { Mode = InstallerMode.ExistingScript, ExistingScriptPath = script, ScriptInputDirectory = input },
            null, _ => Task.FromResult(true));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(original, File.ReadAllBytes(script));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(script)!, ".dltools-*"));
    }

    [PackagingIntegrationFact]
    public async Task ExistingFrameworkScript_UsesOneTransactionForIdenticalInputAndBuildOutput()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "SameInput"));
        var input = options.Project.TargetDirectory;
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "old.txt"), "old");
        var script = ws.Write("script/setup.iss", PipelineFixture.Script(input));
        var prompts = 0;
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state")).PackageAsync(
            options with { Mode = InstallerMode.ExistingScript, ExistingScriptPath = script, ScriptInputDirectory = input },
            null, _ => { prompts++; return Task.FromResult(true); });
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, prompts);
        Assert.Single(result.BackupPaths);
        Assert.True(File.Exists(Path.Combine(input, options.Project.MainExecutable)));
    }

    [PackagingIntegrationFact]
    public async Task ExistingDiskSpanningScript_IsRejectedAndInputRestored()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Sdk(ws, "Spanned"));
        var input = Path.Combine(ws.Root, "script", "Publish");
        var script = ws.Write("script/setup.iss", PipelineFixture.Script(input).Replace("[Files]", "DiskSpanning=yes\n[Files]"));
        var old = ws.Write("script/Publish/old.txt", "old");
        var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state")).PackageAsync(
            options with { Mode = InstallerMode.ExistingScript, ExistingScriptPath = script, ScriptInputDirectory = input },
            null, _ => Task.FromResult(true));
        Assert.False(result.Succeeded);
        Assert.Equal("old", File.ReadAllText(old));
        Assert.False(File.Exists(Path.Combine(options.OutputDirectory, options.InstallerFileName)));
    }
}

internal sealed class InlineProgress(Action<PackagingProgress> callback) : IProgress<PackagingProgress>
{
    public void Report(PackagingProgress value) => callback(value);
}

internal static class PipelineFixture
{
    internal static async Task<PackagingOptions> Options(TestWorkspace ws, string projectPath, BuildTools? tools = null)
    {
        tools ??= await new BuildToolLocator().LocateAsync();
        var project = Assert.Single(await new ProjectInspectionService().InspectAsync(projectPath, tools));
        Assert.True(project.IsSupported, project.UnsupportedReason);
        return new PackagingOptions
        {
            Project = project, Tools = tools, AppId = Guid.NewGuid(), AppName = project.ProjectName,
            AppVersion = "2.3.4", OutputDirectory = Path.Combine(ws.Root, "installers"),
            InstallerFileName = project.ProjectName + "_Setup.exe", SelfContained = false,
            LaunchAfterInstall = false, CreateDesktopShortcut = false
        };
    }

    internal static string Script(string input, string? license = null) => $$"""
        [Setup]
        AppId=FEA12345-1234-1234-1234-123456789ABC
        AppName=ScriptFixture
        AppVersion=3.0
        DefaultDirName={localappdata}\Programs\ScriptFixture
        PrivilegesRequired=lowest
        OutputDir=OriginalOutput
        OutputBaseFilename=OriginalName
        Uninstallable=yes
        [Files]
        Source: "{{input}}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
        {{(license is null ? "" : $"Source: \"{license}\"; DestDir: \"{{app}}\"; DestName: \"license.txt\"; Flags: ignoreversion")}}
        """;
}
