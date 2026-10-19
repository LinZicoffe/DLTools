using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class DirectoryTransactionTests
{
    private static ProjectDescriptor Project(TestWorkspace ws) => new() { ProjectPath = ws.Write("source/fixture.csproj", "<Project/>"), SourceRoot = Path.Combine(ws.Root, "source") };

    [Fact]
    public void Begin_NonemptyWithoutConfirmation_ChangesNothing()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var old = ws.Write("source/bin/Release/old.dll", "old");
        var registry = new RecoveryRegistry(Path.Combine(ws.Root, "state"));
        Assert.Throws<IOException>(() => DirectoryTransaction.Begin(Path.GetDirectoryName(old)!, project, Path.Combine(ws.Root, ".dltools-backups"), registry));
        Assert.Equal("old", File.ReadAllText(old));
        Assert.False(Directory.Exists(Path.Combine(ws.Root, "state")));
        Assert.False(Directory.Exists(Path.Combine(ws.Root, ".dltools-backups")));
    }

    [Fact]
    public void Rollback_RestoresOriginalAndPreservesNewOutput()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var old = ws.Write("source/bin/Release/old.dll", "old");
        var registry = new RecoveryRegistry(Path.Combine(ws.Root, "state"));
        var transaction = DirectoryTransaction.Begin(Path.GetDirectoryName(old)!, project, Path.Combine(ws.Root, ".dltools-backups"), registry, true);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(transaction.BackupDirectory!, "old.dll")));
        ws.Write("source/bin/Release/new.json", "new");
        Assert.Throws<IOException>(() => registry.EnsureUnblocked(transaction.TargetDirectory));
        transaction.Rollback();
        transaction.Rollback();
        Assert.Equal("old", File.ReadAllText(old));
        Assert.Single(transaction.PreservedPaths);
        Assert.Equal("new", File.ReadAllText(Path.Combine(transaction.PreservedPaths[0], "new.json")));
        registry.EnsureUnblocked(transaction.TargetDirectory);
    }

    [Fact]
    public void Commit_KeepsNewOutputAndOldBackup()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var old = ws.Write("source/bin/Release/old.dll", "old");
        var registry = new RecoveryRegistry(Path.Combine(ws.Root, "state"));
        var transaction = DirectoryTransaction.Begin(Path.GetDirectoryName(old)!, project, Path.Combine(ws.Root, ".dltools-backups"), registry, true);
        ws.Write("source/bin/Release/new.dll", "new");
        Assert.Null(transaction.Commit());
        transaction.Rollback();
        Assert.Equal("old", File.ReadAllText(Path.Combine(transaction.BackupDirectory!, "old.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(transaction.TargetDirectory, "new.dll")));
        registry.EnsureUnblocked(transaction.TargetDirectory);
    }

    [Fact]
    public void Rollback_NoOriginalDirectory_PreservesNewOutputAndRemovesTarget()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var target = Path.Combine(project.SourceRoot, "bin/Release");
        var transaction = DirectoryTransaction.Begin(target, project, Path.Combine(ws.Root, ".dltools-backups"), new RecoveryRegistry(Path.Combine(ws.Root, "state")));
        ws.Write("source/bin/Release/new.dll", "new");
        transaction.Rollback();
        Assert.False(Directory.Exists(target));
        Assert.Null(transaction.BackupDirectory);
        Assert.True(File.Exists(Path.Combine(Assert.Single(transaction.PreservedPaths), "new.dll")));
    }

    [Fact]
    public void Rollback_BlockedTarget_RetainsJournalAndBackupForRetry()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var old = ws.Write("source/bin/Release/old.dll", "old");
        var target = Path.GetDirectoryName(old)!;
        var registry = new RecoveryRegistry(Path.Combine(ws.Root, "state"));
        var transaction = DirectoryTransaction.Begin(target, project, Path.Combine(ws.Root, ".dltools-backups"), registry, true);
        Directory.Delete(target); // Empty directory created by this test's transaction.
        File.WriteAllText(target, "external blocker");
        var error = Assert.Throws<IOException>(transaction.Rollback);
        Assert.Contains(transaction.BackupDirectory!, error.Message);
        Assert.Equal("old", File.ReadAllText(Path.Combine(transaction.BackupDirectory!, "old.dll")));
        Assert.Throws<IOException>(() => registry.EnsureUnblocked(target));
        Assert.Throws<IOException>(() => DirectoryTransaction.Begin(target, project, Path.Combine(ws.Root, ".dltools-backups"), registry, true));
        File.Delete(target); // Remove only our explicit blocker so recovery can be retried.
        transaction.Rollback();
        Assert.Equal("old", File.ReadAllText(old));
        registry.EnsureUnblocked(target);
    }

    [Fact]
    public void Begin_RejectsBackupInsideProjectOrNestedTarget()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var target = Path.Combine(project.SourceRoot, "bin/Release");
        var registry = new RecoveryRegistry(Path.Combine(ws.Root, "state"));
        Assert.Throws<IOException>(() => DirectoryTransaction.Begin(target, project, Path.Combine(project.SourceRoot, ".dltools-backups"), registry));
        Assert.Throws<IOException>(() => DirectoryTransaction.Begin(target, project, Path.Combine(target, "backups"), registry));
        Assert.False(Directory.Exists(target));
    }
    [Fact]
    public void Begin_SecondInstanceIsBlockedByDurableJournal()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var target = Path.Combine(project.SourceRoot, "bin/Release");
        var state = Path.Combine(ws.Root, "state");
        var backups = Path.Combine(ws.Root, ".dltools-backups");
        var first = DirectoryTransaction.Begin(target, project, backups, new RecoveryRegistry(state));
        var error = Assert.Throws<IOException>(() => DirectoryTransaction.Begin(target, project, backups, new RecoveryRegistry(state), true));
        Assert.Contains(".json", error.Message);
        Assert.Contains(Path.GetFullPath(target), error.Message);
        first.Rollback();
    }

    [Fact]
    public void Rollback_JournalDeletionFailure_CanRetryWithoutMovingRestoredOriginal()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var old = ws.Write("source/bin/Release/old.dll", "old");
        var state = Path.Combine(ws.Root, "state");
        var registry = new RecoveryRegistry(state);
        var transaction = DirectoryTransaction.Begin(Path.GetDirectoryName(old)!, project, Path.Combine(ws.Root, ".dltools-backups"), registry, true);
        // No new output: rollback must still remember that the original was restored before cleanup failed.
        Directory.Delete(transaction.TargetDirectory);
        using (var journalLock = new FileStream(Assert.Single(Directory.GetFiles(state)), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(transaction.Rollback);
        Assert.Equal("old", File.ReadAllText(old));
        transaction.Rollback();
        Assert.Equal("old", File.ReadAllText(old));
        Assert.Empty(transaction.PreservedPaths);
        registry.EnsureUnblocked(transaction.TargetDirectory);
    }

    [Fact]
    public void Commit_JournalDeletionFailure_ReturnsWarningAndNeverRollsBackCommittedOutput()
    {
        using var ws = new TestWorkspace();
        var project = Project(ws);
        var old = ws.Write("source/bin/Release/old.dll", "old");
        var state = Path.Combine(ws.Root, "state");
        var transaction = DirectoryTransaction.Begin(Path.GetDirectoryName(old)!, project, Path.Combine(ws.Root, ".dltools-backups"), new RecoveryRegistry(state), true);
        ws.Write("source/bin/Release/new.dll", "new");
        using (var journalLock = new FileStream(Assert.Single(Directory.GetFiles(state)), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Contains("安装包已保存", transaction.Commit());
            transaction.Rollback();
        }
        Assert.Equal("new", File.ReadAllText(Path.Combine(transaction.TargetDirectory, "new.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(transaction.BackupDirectory!, "old.dll")));
        Assert.Throws<IOException>(() => new RecoveryRegistry(state).EnsureUnblocked(transaction.TargetDirectory));
    }
}

