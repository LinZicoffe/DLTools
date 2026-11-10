using Xunit;
using 烧录上位机程序.Models.Cleanup;
using 烧录上位机程序.Services.Cleanup;

namespace DLTools.Tests;

/// <summary>执行器行为：永久/回收站/模拟三模式、二次校验拦截（含完整金丝雀：扫描后植入 junction）、
/// 占用文件跳过、空目录修剪。回收站用假 sink；真实 SHFileOperationW 在集成门控测试中验证。</summary>
public class DiskCleanupExecutorTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly DiskCleanupTestHelpers.FixedTimeProvider _clock =
        new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    private sealed class FakeRecycleBin : IRecycleBinSink
    {
        public List<string> Sent { get; } = [];

        public bool TrySend(string path, out string? error)
        {
            Sent.Add(path);
            error = null;
            return true;
        }
    }

    private (PathGuard Guard, SafeCleanupExecutor Executor, FakeRecycleBin Sink) Create()
    {
        var guard = new PathGuard(DiskCleanupTestHelpers.CreateEnvironment(_workspace));
        var sink = new FakeRecycleBin();
        return (guard, new SafeCleanupExecutor(guard, sink), sink);
    }

    private CleanupAuditLog NewAudit([System.Runtime.CompilerServices.CallerMemberName] string name = "")
        => CleanupAuditLog.CreateNew(Path.Combine(_workspace.Root, "audit-" + name));

    /// <summary>读取仍被审计 writer 持有的日志（writer 允许 Read 共享；读取方需声明容忍写入）。</summary>
    private static string[] ReadOpenLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Execute_PermanentMode_DeletesOnlyCandidatesAndPrunesEmptyDirsBottomUp()
    {
        var (guard, executor, _) = Create();
        var environment = guard.Environment;
        var root = Path.Combine(environment.Temp, "pkg");
        var a = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "a.bin"), 100);
        var nested = Path.Combine(root, "nest");
        var b = DiskCleanupTestHelpers.WriteFile(Path.Combine(nested, "b.bin"), 50);
        var keep = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "fresh.bin"), 10);

        var walk = new CleanupWalkResult(
            [new WalkFileCandidate(a, 100, _clock.GetUtcNow(), _clock.GetUtcNow()),
             new WalkFileCandidate(b, 50, _clock.GetUtcNow(), _clock.GetUtcNow())],
            [nested], [], 150, 2, 1, 0, []);
        using var audit = NewAudit();
        var outcomes = executor.Execute("test-rule", walk, CleanupDeleteMode.Permanent, simulate: false, audit, CancellationToken.None);

        Assert.Equal(2, outcomes.Count(o => o.Decision == CleanupDecision.Deleted));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.False(Directory.Exists(nested)); // 清空后修剪
        Assert.True(File.Exists(keep));         // 非候选绝不动
        Assert.True(Directory.Exists(root));    // 枚举根保留
        Assert.All(ReadOpenLog(audit.Path), line => Assert.StartsWith("20", line));
    }

    [Fact]
    public void Execute_DeletableWhenEmptyRoot_IsRemovedOnceEmpty()
    {
        var (guard, executor, _) = Create();
        var root = Path.Combine(guard.Environment.Temp, "cursor-updater");
        var file = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "pending.exe"), 200);

        var walk = new CleanupWalkResult(
            [new WalkFileCandidate(file, 200, _clock.GetUtcNow(), _clock.GetUtcNow())],
            [], [root], 200, 1, 0, 0, []);
        using var audit = NewAudit();
        executor.Execute("updater", walk, CleanupDeleteMode.Permanent, simulate: false, audit, CancellationToken.None);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(root)); // 通配匹配根清空后整体移除
    }

    [Fact]
    public void Execute_RecycleMode_SendsEveryCandidateThroughSink()
    {
        var (guard, executor, sink) = Create();
        var root = Path.Combine(guard.Environment.UserProfile, ".claude", "projects");
        var old = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "s1.jsonl"), 500);

        var walk = new CleanupWalkResult(
            [new WalkFileCandidate(old, 500, _clock.GetUtcNow(), _clock.GetUtcNow())],
            [], [], 500, 1, 0, 0, []);
        using var audit = NewAudit();
        var outcomes = executor.Execute("conversations", walk, CleanupDeleteMode.RecycleBin, simulate: false, audit, CancellationToken.None);

        Assert.Equal(CleanupDecision.Recycled, Assert.Single(outcomes).Decision);
        Assert.Equal([old], sink.Sent);
        // 真 sink 会把文件移走；假 sink 只记录——此处验证的是「逐条经过回收站出口」。
    }

    [Fact]
    public void Execute_SimulateMode_OnlyWritesAuditAndTouchesNothing()
    {
        var (guard, executor, _) = Create();
        var root = Path.Combine(guard.Environment.Temp, "vm");
        var bundle = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "sim.bin"), 300);

        var walk = new CleanupWalkResult(
            [new WalkFileCandidate(bundle, 300, _clock.GetUtcNow(), _clock.GetUtcNow())],
            [], [root], 300, 1, 0, 0, []);
        using var audit = NewAudit();
        var outcomes = executor.Execute("vm", walk, CleanupDeleteMode.Permanent, simulate: true, audit, CancellationToken.None);

        Assert.Equal(CleanupDecision.Simulated, Assert.Single(outcomes).Decision);
        Assert.True(File.Exists(bundle));
        Assert.True(Directory.Exists(root));
        Assert.Contains(ReadOpenLog(audit.Path), l => l.Contains("simulated-delete"));
    }

    [Fact]
    public void Execute_JunctionPlantedAfterScan_IsBlockedBySecondPassAndCanarySurvives()
    {
        var (guard, executor, _) = Create();
        var environment = guard.Environment;
        var root = Path.Combine(environment.Temp, "pkg3");
        var target = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "trace.bin"), 400);

        // 「扫描」产出候选后、执行前，把 pkg3 换成指向金丝雀目录的 junction（金丝雀在所有锚点之外）。
        // 替换目录里放置同名 trace.bin：叶属性可读，拦截依赖全链链接检查。
        var canary = Path.Combine(_workspace.Root, "canary-exec");
        var precious = DiskCleanupTestHelpers.WriteFile(Path.Combine(canary, "precious.txt"), 12);
        DiskCleanupTestHelpers.WriteFile(Path.Combine(canary, "trace.bin"), 400);
        File.Delete(target);
        Directory.Delete(root);
        TestJunction.Create(root, canary);

        var walk = new CleanupWalkResult(
            [new WalkFileCandidate(target, 400, _clock.GetUtcNow(), _clock.GetUtcNow())],
            [], [], 400, 1, 0, 0, []);
        using var audit = NewAudit();
        var outcomes = executor.Execute("canary", walk, CleanupDeleteMode.Permanent, simulate: false, audit, CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(CleanupDecision.Skipped, outcome.Decision);
        Assert.Equal("路径链包含链接", outcome.Reason);
        Assert.True(File.Exists(precious)); // 金丝雀完好
        Assert.True(Directory.Exists(root)); // junction 本体也不删
    }

    [Fact]
    public void Execute_LockedFile_FailsIndividuallyAndOthersStillProceed()
    {
        var (guard, executor, _) = Create();
        var root = Path.Combine(guard.Environment.Temp, "busy");
        var locked = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "in-use.db"), 700);
        var normal = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "free.db"), 100);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var walk = new CleanupWalkResult(
            [new WalkFileCandidate(locked, 700, _clock.GetUtcNow(), _clock.GetUtcNow()),
             new WalkFileCandidate(normal, 100, _clock.GetUtcNow(), _clock.GetUtcNow())],
            [], [], 800, 2, 0, 0, []);
        using var audit = NewAudit();
        var outcomes = executor.Execute("busy", walk, CleanupDeleteMode.Permanent, simulate: false, audit, CancellationToken.None);

        Assert.Equal(CleanupDecision.Failed, outcomes.Single(o => o.Path == locked).Decision);
        Assert.Equal(CleanupDecision.Deleted, outcomes.Single(o => o.Path == normal).Decision);
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void Execute_ReportOnlyRule_IsRejectedAtExecutorLevel()
    {
        var (_, executor, _) = Create();
        using var audit = NewAudit();
        Assert.Throws<ArgumentException>(() => executor.Execute("models",
            CleanupWalkResult.Empty, CleanupDeleteMode.ReportOnly, simulate: false, audit, CancellationToken.None));
    }

    [Fact]
    public void AuditLog_CreateNewRejectsExistingFileAndRotatesStaleLogs()
    {
        var directory = Path.Combine(_workspace.Root, "audit-rotate");
        var first = CleanupAuditLog.CreateNew(directory, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        first.Dispose();
        Assert.Throws<IOException>(() => CleanupAuditLog.CreateNew(directory,
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))); // 同名时间戳不允许覆盖

        using var second = CleanupAuditLog.CreateNew(directory, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        second.Write("hello");
        second.Check();
        Assert.Contains("hello", ReadOpenLog(second.Path)[^1]);
    }

    public void Dispose() => _workspace.Dispose();
}
