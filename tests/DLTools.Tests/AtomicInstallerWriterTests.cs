using Xunit;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class AtomicInstallerWriterTests
{
    [Fact]
    public async Task Commit_CopiesAndPublishesSingleExe()
    {
        using var ws = new TestWorkspace();
        var source = ws.Write("compiler/setup.exe", "new package");
        var output = Path.Combine(ws.Root, "中文 output");
        var result = await AtomicInstallerWriter.CommitAsync(source, output, "安装包", _ => throw new Exception("No confirmation expected"));
        Assert.Equal(Path.Combine(output, "安装包.exe"), result);
        Assert.Equal("new package", File.ReadAllText(result));
        Assert.Single(Directory.GetFiles(output));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Commit_RejectingOverwrite_LeavesOldPackageUnchanged()
    {
        using var ws = new TestWorkspace();
        var source = ws.Write("compiler/setup.exe", "new");
        var old = ws.Write("output/setup.exe", "old");
        await Assert.ThrowsAsync<OperationCanceledException>(() => AtomicInstallerWriter.CommitAsync(source, Path.GetDirectoryName(old)!, "setup", _ => Task.FromResult(false)));
        Assert.Equal("old", File.ReadAllText(old));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(old)!));
    }

    [Fact]
    public async Task Commit_ConfirmedOverwrite_ReplacesOldPackage()
    {
        using var ws = new TestWorkspace();
        var source = ws.Write("compiler/setup.exe", "new");
        var old = ws.Write("output/setup.exe", "old");
        await AtomicInstallerWriter.CommitAsync(source, Path.GetDirectoryName(old)!, "setup.exe", confirmation =>
        {
            Assert.Equal(Path.GetFullPath(old), confirmation.Path);
            return Task.FromResult(true);
        });
        Assert.Equal("new", File.ReadAllText(old));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(old)!));
    }

    [Fact]
    public async Task Commit_TargetChangesDuringConfirmation_RequiresNewConfirmation()
    {
        using var ws = new TestWorkspace();
        var source = ws.Write("compiler/setup.exe", "new");
        var old = ws.Write("output/setup.exe", "old");
        await Assert.ThrowsAsync<IOException>(() => AtomicInstallerWriter.CommitAsync(source, Path.GetDirectoryName(old)!, "setup.exe", _ =>
        {
            File.WriteAllText(old, "unconfirmed change");
            return Task.FromResult(true);
        }));
        Assert.Equal("unconfirmed change", File.ReadAllText(old));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(old)!));
    }

    [Fact]
    public async Task Commit_LockedFinalTarget_ProtectsOldPackage()
    {
        using var ws = new TestWorkspace();
        var source = ws.Write("compiler/setup.exe", "new");
        var old = ws.Write("output/setup.exe", "old");
        using (var fileLock = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => AtomicInstallerWriter.CommitAsync(source, Path.GetDirectoryName(old)!, "setup", _ => Task.FromResult(true)));
        }
        Assert.Equal("old", File.ReadAllText(old));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(old)!));
    }

    [Fact]
    public async Task Commit_CancelDuringConfirmation_ProtectsOldPackage()
    {
        using var ws = new TestWorkspace();
        using var cancel = new CancellationTokenSource();
        var source = ws.Write("compiler/setup.exe", "new");
        var old = ws.Write("output/setup.exe", "old");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicInstallerWriter.CommitAsync(source, Path.GetDirectoryName(old)!, "setup", _ =>
        {
            cancel.Cancel();
            return Task.FromResult(true);
        }, cancel.Token));
        Assert.Equal("old", File.ReadAllText(old));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(old)!));
    }

    [Fact]
    public async Task Commit_CopyFailureOrEmptyCompilerOutput_ProtectsOldPackage()
    {
        using var ws = new TestWorkspace();
        var source = ws.Write("compiler/setup.exe", "");
        var old = ws.Write("output/setup.exe", "old");
        await Assert.ThrowsAsync<IOException>(() => AtomicInstallerWriter.CommitAsync(source, Path.GetDirectoryName(old)!, "setup", _ => Task.FromResult(true)));
        Assert.Equal("old", File.ReadAllText(old));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(old)!));
        await Assert.ThrowsAsync<FileNotFoundException>(() => AtomicInstallerWriter.CommitAsync(Path.Combine(ws.Root, "missing.exe"), Path.GetDirectoryName(old)!, "setup", _ => Task.FromResult(true)));
        Assert.Equal("old", File.ReadAllText(old));
    }
}

