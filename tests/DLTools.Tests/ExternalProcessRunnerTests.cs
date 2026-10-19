using System.ComponentModel;
using System.Text.Json;
using Xunit;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class ExternalProcessRunnerTests
{
    private readonly ExternalProcessRunner _runner = new();

    [Fact]
    public async Task RunAsync_ArgumentsArePassedLiterally_AndBothStreamsAreRead()
    {
        var arguments = new[] { "", "中文 空格", "a\"b", "C:\\folder with space\\", "\\\\", "& echo injected", "$(calc)", "a\\\"b\\" };
        var lines = new List<string>();
        var result = await _runner.RunAsync(Helper(), new[] { "echo" }.Concat(arguments).ToArray(),
            Environment.CurrentDirectory, lines.Add).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim()));
        Assert.Contains("stderr 中文", result.StandardError);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task RunAsync_NonzeroExit_ReturnsExitCodeAndError()
    {
        var result = await _runner.RunAsync(Helper(), new[] { "fail" }, Environment.CurrentDirectory)
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(17, result.ExitCode);
        Assert.Contains("expected failure", result.StandardError);
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_ReportsWin32Error()
    {
        await Assert.ThrowsAsync<Win32Exception>(() => _runner.RunAsync("missing-" + Guid.NewGuid() + ".exe",
            Array.Empty<string>(), Environment.CurrentDirectory));
    }

    [Fact]
    public async Task RunAsync_PreCancelled_DoesNotStartProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(Helper(),
            new[] { "fail" }, Environment.CurrentDirectory, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_NulArgument_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _runner.RunAsync(Helper(),
            new[] { "echo", "bad\0value" }, Environment.CurrentDirectory));
    }

    [Fact]
    public async Task RunAsync_ParentExits_CancellationStillKillsDescendantBeforeReturning()
    {
        var path = Path.Combine(Path.GetTempPath(), "DLTools-job-" + Guid.NewGuid() + ".lock");
        using var cancellation = new CancellationTokenSource();
        var childStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _runner.RunAsync(Helper(), new[] { "spawn-and-exit", path }, Environment.CurrentDirectory,
            line =>
            {
                if (line.StartsWith("child:")) childStarted.TrySetResult(int.Parse(line[6..]));
                if (line.StartsWith("parent:")) parentStarted.TrySetResult(int.Parse(line[7..]));
            }, cancellation.Token);
        try
        {
            var childId = await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var parentId = await parentStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitUntilProcessExitedAsync(parentId);
            await WaitUntilLockedAsync(path).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(running.IsCompleted);
            Assert.Throws<IOException>(() => { using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(15)));
            using var reopened = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            AssertProcessExited(childId);
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { }
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_LogCallbackThrows_StopsProcessAndReleasesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "DLTools-log-" + Guid.NewGuid() + ".lock");
        var expected = new InvalidOperationException("log failed");
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunAsync(Helper(),
                new[] { "wait", path }, Environment.CurrentDirectory, _ => throw expected)
                .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Same(expected, exception);
            using var reopened = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_DotnetSdkProbe_Completes()
    {
        var result = await _runner.RunAsync("dotnet", new[] { "--list-sdks" }, Environment.CurrentDirectory)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput);
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException) { }
    }

    private static async Task WaitUntilLockedAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (File.Exists(path))
            {
                try { using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { return; }
            }
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task WaitUntilProcessExitedAsync(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException) { } // The parent can already have exited before it is looked up.
    }

    private static string Helper()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "ProcessTestHost")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar)
            .FirstOrDefault(part => part is "Release" or "Debug") ?? "Release";
        var path = Path.Combine(directory.FullName, "tests", "ProcessTestHost", "bin", configuration,
            "net8.0", "ProcessTestHost.exe");
        Assert.True(File.Exists(path), path);
        return path;
    }
}
