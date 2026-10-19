using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class RecoveryOverlapTests
{
    private static ProjectDescriptor Project(TestWorkspace ws) => new()
    {
        ProjectPath = ws.Write("source/app.csproj", "<Project/>"),
        SourceRoot = Path.Combine(ws.Root, "source")
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Begin_OverlappingTargetsAcrossInstances_AreBlocked(bool childFirst)
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var parent = Path.Combine(ws.Root, "Publish");
        var child = Path.Combine(parent, "Plugins");
        var state = Path.Combine(ws.Root, "state");
        var backupRoot = Path.Combine(ws.Root, ".dltools-backups");
        var first = DirectoryTransaction.Begin(childFirst ? child : parent, project, backupRoot, new RecoveryRegistry(state));
        try
        {
            Assert.Throws<IOException>(() => DirectoryTransaction.Begin(childFirst ? parent : child,
                project, backupRoot, new RecoveryRegistry(state), true));
        }
        finally { first.Rollback(); }
    }

    [Fact]
    public void PendingRecovery_ProtectsTargetBackupAndFailedOutputSubtrees()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var old = ws.Write("Publish/old.dll", "original");
        var state = Path.Combine(ws.Root, "state");
        var transaction = DirectoryTransaction.Begin(Path.GetDirectoryName(old)!, project,
            Path.Combine(ws.Root, ".dltools-backups"), new RecoveryRegistry(state), true);
        Directory.Delete(transaction.TargetDirectory);
        File.WriteAllText(transaction.TargetDirectory, "block rollback");
        Assert.Throws<IOException>(transaction.Rollback);
        var registry = new RecoveryRegistry(state);
        Assert.Throws<IOException>(() => registry.EnsureUnblocked(Path.Combine(transaction.TargetDirectory, "Plugins")));
        Assert.Throws<IOException>(() => registry.EnsureUnblocked(transaction.BackupDirectory!));
        Assert.Throws<IOException>(() => registry.EnsureUnblocked(Path.GetDirectoryName(transaction.BackupDirectory!)!));
        Assert.Equal("original", File.ReadAllText(Path.Combine(transaction.BackupDirectory!, "old.dll")));
        File.Delete(transaction.TargetDirectory);
        transaction.Rollback();
    }

    [Fact]
    public async Task Begin_ConcurrentNestedTargets_OnlyOneRegisters()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var state = Path.Combine(ws.Root, "state");
        var backups = Path.Combine(ws.Root, ".dltools-backups");
        using var start = new ManualResetEventSlim();
        Task<DirectoryTransaction?> Attempt(string target) => Task.Run(() =>
        {
            start.Wait();
            try { return DirectoryTransaction.Begin(target, project, backups, new RecoveryRegistry(state), true); }
            catch (IOException) { return null; }
        });
        var parent = Attempt(Path.Combine(ws.Root, "Publish"));
        var child = Attempt(Path.Combine(ws.Root, "Publish", "Plugins"));
        start.Set();
        var results = await Task.WhenAll(parent, child);
        var winner = Assert.Single(results, t => t is not null);
        winner!.Rollback();
    }

    [Fact]
    public void DisjointTargets_CanShareBackupContainer()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var state = Path.Combine(ws.Root, "state");
        var backups = Path.Combine(ws.Root, ".dltools-backups");
        var first = DirectoryTransaction.Begin(Path.Combine(ws.Root, "one"), project, backups, new RecoveryRegistry(state));
        var second = DirectoryTransaction.Begin(Path.Combine(ws.Root, "two"), project, backups, new RecoveryRegistry(state));
        second.Rollback();
        first.Rollback();
    }

    [Fact]
    public void CorruptJournal_BlocksEvenUnrelatedTarget()
    {
        using var ws = new TestWorkspace();
        ws.Write("state/broken.json", "broken");
        Assert.Throws<IOException>(() => new RecoveryRegistry(Path.Combine(ws.Root, "state"))
            .EnsureUnblocked(Path.Combine(ws.Root, "unrelated")));
    }
}
