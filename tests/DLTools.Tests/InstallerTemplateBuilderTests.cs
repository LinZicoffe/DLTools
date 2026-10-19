using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public class InstallerTemplateBuilderTests
{
    [Fact]
    public void GeneratedTemplate_PreservesRuntimeResourcesStableIdentityAndPerUserInstall()
    {
        using var workspace = new TestWorkspace();
        var options = Options(workspace);
        var text = new InstallerTemplateBuilder().Build(options, workspace.Root, Path.Combine(workspace.Root, "output"));
        Assert.Contains("AppId={{" + options.AppId.ToString("D") + "}", text);
        Assert.Contains("PrivilegesRequired=lowest", text);
        Assert.Contains("recursesubdirs createallsubdirs", text);
        Assert.DoesNotContain("Excludes:", text);
        Assert.DoesNotContain("[UninstallDelete]", text);
        Assert.Contains("ArchitecturesAllowed=x64compatible", text);
        Assert.Contains("actual entry.exe", text);
        Assert.DoesNotContain("renamed installer.exe\"; Filename", text);
    }

    [Fact]
    public void MetadataContainingIsppExpressions_IsKeptAsLiteralUnderDifferentInlineToken()
    {
        using var workspace = new TestWorkspace();
        var options = Options(workspace) with { AppName = "演示 \"{#1+1}\" {value}", AppVersion = "2.1.0-preview" };
        var text = new InstallerTemplateBuilder().Build(options, workspace.Root, Path.Combine(workspace.Root, "output"));
        Assert.StartsWith("#pragma inlinestart \"DLTOOLS_INLINE_", text);
        Assert.Contains("VersionInfoVersion=2.1.0.0", text);
        Assert.Contains("{{#1+1}", text);
        Assert.Throws<ArgumentException>(() => new InstallerTemplateBuilder().Build(options with
            { AppName = "Bad\n[Code]" }, workspace.Root, workspace.Root));
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("AnyCPU")]
    public void FrameworkX86AndAnyCpu_DoNotEnable64BitInstallMode(string cpu)
    {
        using var workspace = new TestWorkspace();
        var options = Options(workspace);
        options = options with { Project = options.Project with { IsNetFramework = true, FrameworkVersion = "v4.8", PlatformTarget = cpu } };
        var text = new InstallerTemplateBuilder().Build(options, workspace.Root, Path.Combine(workspace.Root, "output"));
        Assert.DoesNotContain("ArchitecturesInstallIn64BitMode", text);
        Assert.Contains(".NET Framework v4.8", text);
    }

    [Fact]
    public void MissingEntryAndEmptyAppId_AreRejectedBeforeCompilingInstaller()
    {
        using var workspace = new TestWorkspace();
        var options = Options(workspace);
        Assert.Throws<ArgumentException>(() => new InstallerTemplateBuilder().Build(options with { AppId = Guid.Empty }, workspace.Root, workspace.Root));
        File.Delete(Path.Combine(workspace.Root, "actual entry.exe"));
        Assert.Throws<FileNotFoundException>(() => new InstallerTemplateBuilder().Build(options, workspace.Root, workspace.Root));
    }

    [PackagingIntegrationFact]
    public async Task ActualInnoCompile_PathsAndMetadataWithInlineExpressionsRemainLiteral()
    {
        using var workspace = new TestWorkspace();
        var staging = Path.Combine(workspace.Root, "中文 {#1+1} 源");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "actual entry.exe"), "fixture payload");
        var tools = await new BuildToolLocator().LocateAsync();
        Assert.True(File.Exists(tools.InnoCompilerPath), "本机集成测试要求安装 Inno Setup。");
        var options = Options(workspace) with { Tools = tools, AppName = "演示 \"{#1+1}\" {value}", AppVersion = "2.1.0-preview" };
        var output = Path.Combine(workspace.Root, "output");
        var script = workspace.Write("generated.iss", new InstallerTemplateBuilder().Build(options, staging, output));
        var result = await new ExternalProcessRunner().RunAsync(tools.InnoCompilerPath, ["/Qp", script], workspace.Root);
        Assert.True(result.ExitCode == 0, result.StandardOutput + "\n" + result.StandardError);
        Assert.True(File.Exists(Path.Combine(output, "renamed installer.exe")));
    }

    private static PackagingOptions Options(TestWorkspace workspace)
    {
        workspace.Write("actual entry.exe", "fixture payload");
        return new PackagingOptions
        {
            Project = new() { AssemblyName = "assembly identity", ResolvedExecutableName = "actual entry.exe", TargetFramework = "net8.0-windows" },
            AppName = "Test App", AppVersion = "1.2.3", AppId = Guid.NewGuid(), InstallerFileName = "renamed installer.exe",
            Tools = new() { InnoCompilerPath = Path.Combine(workspace.Root, "Inno", "ISCC.exe") }
        };
    }
}
