using Xunit;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class RecoveryCrossProcessTests
{
    [Fact]
    public async Task RecoveryRegistry_ConcurrentProcessesWithNestedTargets_OnlyOneRegisters()
    {
        using var ws = new TestWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var start = Path.Combine(ws.Root, "start");
        var release = Path.Combine(ws.Root, "release");
        var runner = new ExternalProcessRunner();
        Task<ProcessResult> Attempt(string target, string id) => runner.RunAsync(ProcessFixture.Helper(),
            ["recovery-register", typeof(RecoveryRegistry).Assembly.Location, Path.Combine(ws.Root, "state"),
                target, Path.Combine(ws.Root, "backup-" + id), Path.Combine(ws.Root, "failed-" + id), start, release,
                Path.Combine(ws.Root, "ready-" + id)],
            ws.Root, cancellationToken: timeout.Token);
        var first = Attempt(Path.Combine(ws.Root, "Publish"), "one");
        var second = Attempt(Path.Combine(ws.Root, "Publish", "Plugins"), "two");
        try
        {
            while (!File.Exists(Path.Combine(ws.Root, "ready-one")) || !File.Exists(Path.Combine(ws.Root, "ready-two")))
                await Task.Delay(10, timeout.Token);
            File.WriteAllText(start, "start together");
            var completed = await Task.WhenAny(first, second).WaitAsync(timeout.Token);
            var blocked = await completed;
            Assert.Equal(17, blocked.ExitCode);
            Assert.Contains("blocked", blocked.StandardOutput);
            Assert.Single(Directory.GetFiles(Path.Combine(ws.Root, "state"), "*.json"));
            File.WriteAllText(release, "release winner");
            var results = await Task.WhenAll(first, second);
            Assert.Single(results, result => result.ExitCode == 0 && result.StandardOutput.Contains("registered"));
            Assert.Single(results, result => result.ExitCode == 17);
            Assert.Empty(Directory.GetFiles(Path.Combine(ws.Root, "state"), "*.json"));
        }
        finally
        {
            File.WriteAllText(release, "release winner");
            timeout.Cancel();
            foreach (var task in new[] { first, second })
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
