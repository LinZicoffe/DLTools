using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public class BuildToolLocatorTests
{
    [Fact]
    public async Task InnoHelpExitOne_WithValidVersion_IsAvailable()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("ISCC.exe", "fixture");
        var result = await new BuildToolLocator(new InnoHelpRunner()).DiagnoseAsync(new BuildTools { InnoCompilerPath = path });
        Assert.True(result.Single(d => d.Name == "Inno Setup").Available);
    }

    private sealed class InnoHelpRunner : IExternalProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            Action<string>? log = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, "Inno Setup 6 Command-Line Compiler", "Usage: iscc [options] scriptfile.iss"));
    }

    [Fact]
    public async Task ExplicitMissingTool_IsPreservedAndDiagnosedRatherThanSilentlyReplaced()
    {
        using var workspace = new TestWorkspace();
        var missing = Path.Combine(workspace.Root, "missing.exe");
        var locator = new BuildToolLocator(new ProbeRunner());
        var tools = await locator.LocateAsync(new BuildTools
            { DotNetPath = missing, MsBuildPath = missing, InnoCompilerPath = missing });
        Assert.Equal(missing, tools.DotNetPath);
        Assert.All(await locator.DiagnoseAsync(tools), item => Assert.False(item.Available));
    }

    [Fact]
    public async Task InstalledToolChecks_RejectOldMsBuildAndMissingChineseLanguage()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("tool.exe", "placeholder for injected runner");
        var result = await new BuildToolLocator(new ProbeRunner()).DiagnoseAsync(new BuildTools
            { DotNetPath = path, MsBuildPath = path, InnoCompilerPath = path });
        Assert.True(result.Single(d => d.Name == ".NET SDK").Available);
        Assert.False(result.Single(d => d.Name == "Visual Studio MSBuild").Available);
        Assert.True(result.Single(d => d.Name == "Inno Setup").Available);
        Assert.False(result.Single(d => d.Name.Contains("语言")).Available);
    }

    private sealed class ProbeRunner : IExternalProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            Action<string>? log = null, CancellationToken cancellationToken = default) => Task.FromResult(
            new ProcessResult(0, arguments[0] switch
            {
                "--list-sdks" => "8.0.418 [C:\\dotnet\\sdk]",
                "-nologo" => "17.7.1",
                "/?" => "Inno Setup 6 Command-Line Compiler",
                _ => ""
            }, ""));
    }
}
