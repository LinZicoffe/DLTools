using Xunit;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

[Collection("Packaging pipeline")]
public sealed class PackagingFailureTests
{
    [PackagingIntegrationFact]
    public async Task RecoveryFailure_ReportsBackupAndBlocksNestedRetry()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "RecoveryFail"));
        var input = options.Project.TargetDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "old.json"), "original");
        var runner = new InterceptRunner((file, args) => Path.GetFileName(file).Equals("ISCC.exe", StringComparison.OrdinalIgnoreCase)
            && !args.Contains("/?"), () =>
            {
                Directory.Move(input, Path.Combine(ws.Root, "external-moved-output"));
                File.WriteAllText(input, "external blocker");
            });
        var result = await new PackagingService(runner, Path.Combine(ws.Root, "state"))
            .PackageAsync(options, null, _ => Task.FromResult(true));
        Assert.True(runner.Intercepted, result.Error);
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Contains("恢复失败", result.Error);
        Assert.Contains(result.BackupPaths, p => File.Exists(Path.Combine(p, "old.json")));
        Assert.Throws<IOException>(() => new RecoveryRegistry(Path.Combine(ws.Root, "state", "recovery"))
            .EnsureUnblocked(Path.Combine(input, "child")));
    }

    [PackagingIntegrationFact]
    public async Task RestoreFailure_RestoresFrameworkOutputAndKeepsOldInstaller()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "RestoreFail"));
        Directory.CreateDirectory(options.Project.TargetDirectory);
        var old = Path.Combine(options.Project.TargetDirectory, "old.json");
        File.WriteAllText(old, "before build");
        var package = ws.Write(Path.GetRelativePath(ws.Root, Path.Combine(options.OutputDirectory, options.InstallerFileName)), "old package");
        var runner = new InterceptRunner((_, args) => args.Any(a => a.Contains("-t:Restore", StringComparison.OrdinalIgnoreCase)));
        var result = await new PackagingService(runner, Path.Combine(ws.Root, "state"))
            .PackageAsync(options, null, _ => Task.FromResult(true));
        Assert.True(runner.Intercepted, result.Error);
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Equal("before build", File.ReadAllText(old));
        Assert.Equal("old package", File.ReadAllText(package));
    }

    [PackagingIntegrationFact]
    public async Task BuildFailure_AfterFreshOutput_RestoresOriginalAndPreservesAttempt()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "BuildFail"));
        Directory.CreateDirectory(options.Project.TargetDirectory);
        var old = Path.Combine(options.Project.TargetDirectory, "old.json");
        File.WriteAllText(old, "original");
        var runner = new InterceptRunner((_, args) => args.Any(a => a.Contains("Rebuild", StringComparison.OrdinalIgnoreCase)), () =>
            File.WriteAllText(Path.Combine(options.Project.TargetDirectory, "partial.dll"), "partial"));
        var result = await new PackagingService(runner, Path.Combine(ws.Root, "state"))
            .PackageAsync(options, null, _ => Task.FromResult(true));
        Assert.True(runner.Intercepted, result.Error);
        Assert.False(result.Succeeded);
        Assert.Equal("original", File.ReadAllText(old));
        Assert.Contains(result.BackupPaths, p => File.Exists(Path.Combine(p, "partial.dll")));
    }

    [PackagingIntegrationFact]
    public async Task FinalSaveFailure_RestoresScriptInputAndKeepsLockedOldPackage()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Sdk(ws, "SaveFail"));
        var input = Path.Combine(ws.Root, "script", "Publish");
        var script = ws.Write("script/setup.iss", PipelineFixture.Script(input));
        var oldInput = ws.Write("script/Publish/old.txt", "original input");
        var package = ws.Write(Path.GetRelativePath(ws.Root, Path.Combine(options.OutputDirectory, options.InstallerFileName)), "original installer");
        using (var locked = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await new PackagingService(stateDirectory: Path.Combine(ws.Root, "state"))
                .PackageAsync(options with { Mode = 烧录上位机程序.Models.Packaging.InstallerMode.ExistingScript,
                    ExistingScriptPath = script, ScriptInputDirectory = input }, null, _ => Task.FromResult(true));
            Assert.False(result.Succeeded);
            Assert.Equal("original input", File.ReadAllText(oldInput));
        }
        Assert.Equal("original installer", File.ReadAllText(package));
        Assert.Empty(Directory.GetFiles(options.OutputDirectory, ".dltools-*.pending"));
    }

    [PackagingIntegrationFact]
    public async Task CancelBuildWithLiveDescendant_DrainsJobBeforeRestoringDirectory()
    {
        using var ws = new TestWorkspace();
        var options = await PipelineFixture.Options(ws, ProjectFixtures.Framework(ws, "CancelBuild"));
        Directory.CreateDirectory(options.Project.TargetDirectory);
        var original = Path.Combine(options.Project.TargetDirectory, "old.dll");
        File.WriteAllText(original, "original");
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ProcessTestHost/bin/Release/net8.0/ProcessTestHost.exe"));
        var childLock = Path.Combine(options.Project.TargetDirectory, "child.lock");
        var runner = new CancelBuildRunner(helper, childLock, started);
        var running = new PackagingService(runner, Path.Combine(ws.Root, "state"))
            .PackageAsync(options, null, _ => Task.FromResult(true), cancel.Token);
        try
        {
            var first = await Task.WhenAny(started.Task, running).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(first == started.Task, running.IsCompleted ? (await running).Error : "Build child did not start");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                while (true)
                {
                    if (File.Exists(childLock))
                    {
                        try { using var file = File.Open(childLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                        catch (IOException) { break; }
                    }
                    await Task.Delay(25, timeout.Token);
                }
            }
            cancel.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(result.Succeeded);
            Assert.True(result.Cancelled, result.Error);
            Assert.Equal("original", File.ReadAllText(original));
            Assert.NotEmpty(result.BackupPaths);
            new RecoveryRegistry(Path.Combine(ws.Root, "state", "recovery")).EnsureUnblocked(options.Project.TargetDirectory);
        }
        finally
        {
            cancel.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class InterceptRunner(Func<string, IReadOnlyList<string>, bool> matches, Action? action = null) : IExternalProcessRunner
    {
        private readonly ExternalProcessRunner _real = new();
        internal bool Intercepted { get; private set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            Action<string>? log = null, CancellationToken cancellationToken = default)
        {
            if (!matches(fileName, arguments)) return _real.RunAsync(fileName, arguments, workingDirectory, log, cancellationToken);
            Intercepted = true;
            action?.Invoke();
            log?.Invoke("injected build failure");
            return Task.FromResult(new ProcessResult(17, "", "injected build failure"));
        }
    }

    private sealed class CancelBuildRunner(string helper, string lockedFile, TaskCompletionSource started) : IExternalProcessRunner
    {
        private readonly ExternalProcessRunner _real = new();
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            Action<string>? log = null, CancellationToken cancellationToken = default)
        {
            if (!arguments.Any(a => a.Contains("Rebuild", StringComparison.OrdinalIgnoreCase)))
                return _real.RunAsync(fileName, arguments, workingDirectory, log, cancellationToken);
            return _real.RunAsync(helper, ["spawn-and-exit", lockedFile], workingDirectory,
                line => { log?.Invoke(line); if (line.StartsWith("child:")) started.TrySetResult(); }, cancellationToken);
        }
    }
}
