using System.Reflection.PortableExecutable;
using Xunit;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

[Collection("Packaging pipeline")]
public sealed class PackagingCpuIntegrationTests
{
    [PackagingIntegrationFact]
    public async Task ModernFixedX86Project_PublishesAndCollectsActualX86AppHost()
    {
        using var ws = new TestWorkspace();
        var project = ProjectFixtures.Sdk(ws, "Modern X86 Forms", wpf: false,
            extraProperties: "<PlatformTarget>x86</PlatformTarget>");
        var options = (await PipelineFixture.Options(ws, project)) with
        {
            RuntimeIdentifier = "win-x86", SelfContained = false
        };
        Assert.False(options.Project.IsNetFramework);
        Assert.Equal("x86", options.Project.PlatformTarget);
        var state = Path.Combine(ws.Root, "state");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = await new PackagingService(stateDirectory: state)
            .PackageAsync(options, null, _ => Task.FromResult(true), timeout.Token);
        Assert.True(result.Succeeded, result.Error + "\n" + result.LogPath);
        Assert.True(new FileInfo(result.InstallerPath!).Length > 10000);
        foreach (var stage in new[] { "publish", "staging" })
        {
            var executable = Assert.Single(Directory.GetFiles(state, options.Project.MainExecutable,
                SearchOption.AllDirectories), file => Path.GetFileName(Path.GetDirectoryName(file)) == stage);
            using var stream = File.OpenRead(executable);
            using var pe = new PEReader(stream);
            Assert.Equal(Machine.I386, pe.PEHeaders.CoffHeader.Machine);
            Assert.Null(pe.PEHeaders.CorHeader); // The entry is a native x86 apphost, not the managed DLL.
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "runtime-data.xml")));
        }
    }

    [PackagingIntegrationFact]
    public async Task FrameworkFixedX86AndX64Projects_BuildRealCpuSpecificExecutablesAndLaunch()
    {
        using var ws = new TestWorkspace();
        var tools = await new BuildToolLocator().LocateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        foreach (var (cpu, expectedMachine) in new[] { ("x86", Machine.I386), ("x64", Machine.Amd64) })
        {
            var project = ProjectFixtures.Framework(ws, "Legacy " + cpu + " Forms",
                extraProperties: "<PlatformTarget>" + cpu + "</PlatformTarget>");
            var options = await PipelineFixture.Options(ws, project, tools);
            Assert.True(options.Project.IsNetFramework);
            Assert.Equal(cpu, options.Project.PlatformTarget);
            var state = Path.Combine(ws.Root, "state-" + cpu);
            var result = await new PackagingService(stateDirectory: state)
                .PackageAsync(options, null, _ => Task.FromResult(true), timeout.Token);
            Assert.True(result.Succeeded, result.Error + "\n" + result.LogPath);
            Assert.True(new FileInfo(result.InstallerPath!).Length > 10000);
            var original = Path.Combine(options.Project.TargetDirectory, options.Project.MainExecutable);
            var staged = Assert.Single(Directory.GetFiles(state, options.Project.MainExecutable,
                SearchOption.AllDirectories), file => Path.GetFileName(Path.GetDirectoryName(file)) == "staging");
            foreach (var executable in new[] { original, staged })
            {
                using var stream = File.OpenRead(executable);
                using var pe = new PEReader(stream);
                Assert.Equal(expectedMachine, pe.PEHeaders.CoffHeader.Machine);
                Assert.NotNull(pe.PEHeaders.CorHeader);
                if (cpu == "x86")
                    Assert.True(pe.PEHeaders.CorHeader.Flags.HasFlag(CorFlags.Requires32Bit));
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "runtime-data.xml")));
            }
            var marker = Path.Combine(ws.Root, cpu + "-started.txt");
            var launch = await new ExternalProcessRunner().RunAsync(original, ["--probe", marker],
                options.Project.TargetDirectory, cancellationToken: timeout.Token);
            Assert.Equal(0, launch.ExitCode);
            Assert.Equal("started", File.ReadAllText(marker));
        }
    }
}
