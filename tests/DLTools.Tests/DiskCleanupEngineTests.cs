using Xunit;
using 烧录上位机程序.Models.Cleanup;
using 烧录上位机程序.Services.Cleanup;

namespace DLTools.Tests;

/// <summary>引擎编排：扫描聚合、清理重走（不复用扫描缓存）、规则级容错、取消保审计、ReportOnly 拦截。</summary>
public class DiskCleanupEngineTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly DateTimeOffset _now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingRecycleBin : IRecycleBinSink
    {
        public List<string> Sent { get; } = [];

        public bool TrySend(string path, out string? error)
        {
            Sent.Add(path);
            File.Delete(path); // 行为对齐真实 sink：移入回收站后原路径消失
            error = null;
            return true;
        }
    }

    /// <summary>同步 IProgress（Progress&lt;T&gt; 投递到线程池，取消测试会不稳定）。</summary>
    private sealed class InlineProgress(Action<CleanupProgress> handler) : IProgress<CleanupProgress>
    {
        public void Report(CleanupProgress value) => handler(value);
    }

    private (CleanupEngine Engine, RecordingRecycleBin Sink, CleanupEnvironment Environment) Create(
        IReadOnlyList<CleanupRule>? rules = null)
    {
        var environment = DiskCleanupTestHelpers.CreateEnvironment(_workspace);
        var sink = new RecordingRecycleBin();
        var engine = new CleanupEngine(environment,
            new DiskCleanupTestHelpers.FixedTimeProvider(_now), sink,
            Path.Combine(_workspace.Root, "audit"), rules);
        return (engine, sink, environment);
    }

    private static CleanupRule Rule(string id, CleanupRiskClass riskClass, CleanupDeleteMode deleteMode,
        CleanupThresholdKind threshold, params CleanupTarget[] targets) => new()
    {
        Id = id,
        Group = CleanupGroup.AiSteward,
        DisplayName = id,
        Description = id,
        ConsequenceNote = id,
        RiskClass = riskClass,
        DeleteMode = deleteMode,
        Threshold = threshold,
        Targets = targets
    };

    private static CleanupTarget Target(CleanupAnchor anchor, string relative, CleanupTargetKind kind,
        IReadOnlyList<string>? names = null) => new() { Anchor = anchor, RelativePath = relative, Kind = kind, Names = names ?? [] };

    [Fact]
    public void Scan_AggregatesBytesAndFileCountPerRule()
    {
        var artifacts = Path.Combine(_workspace.Root, "profile", ".zcode", "cli", "artifacts", "sess-1");
        var old1 = DiskCleanupTestHelpers.WriteFile(Path.Combine(artifacts, "a.txt"), 100);
        var old2 = DiskCleanupTestHelpers.WriteFile(Path.Combine(artifacts, "b.txt"), 200);
        foreach (var f in new[] { old1, old2 }) DiskCleanupTestHelpers.SetAge(f, _now, TimeSpan.FromDays(40));
        var (engine, _, _) = Create([Rule("artifacts", CleanupRiskClass.SessionArtifact,
            CleanupDeleteMode.Permanent, CleanupThresholdKind.ArtifactAge,
            Target(CleanupAnchor.UserProfile, @".zcode\cli\artifacts", CleanupTargetKind.DirectoryContents))]);

        var result = engine.Scan(CleanupSettings.Default, null, CancellationToken.None);

        var summary = Assert.Single(result.Summaries);
        Assert.Equal("artifacts", summary.RuleId);
        Assert.Equal(300, summary.TotalBytes);
        Assert.Equal(2, summary.FileCount);
        Assert.Null(summary.Error);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public void Scan_MissingRootsYieldZeroAndProtectedTargetCarriesError()
    {
        var (engine, _, _) = Create(
        [
            Rule("missing", CleanupRiskClass.RegenerableCache, CleanupDeleteMode.Permanent, CleanupThresholdKind.Freshness,
                Target(CleanupAnchor.LocalAppData, "never-there", CleanupTargetKind.DirectoryContents)),
            Rule("memories", CleanupRiskClass.RegenerableCache, CleanupDeleteMode.Permanent, CleanupThresholdKind.Freshness,
                Target(CleanupAnchor.UserProfile, @".zcode\cli\memories", CleanupTargetKind.DirectoryContents))
        ]);

        var result = engine.Scan(CleanupSettings.Default, null, CancellationToken.None);

        Assert.Equal(2, result.Summaries.Count);
        var missing = result.Summaries.Single(s => s.RuleId == "missing");
        Assert.Equal(0, missing.TotalBytes);
        Assert.Null(missing.Error); // 不存在的根是正常情况，不是错误
        var memories = result.Summaries.Single(s => s.RuleId == "memories");
        Assert.Equal(0, memories.TotalBytes);
        Assert.NotNull(memories.Error); // 永不名单目标必须显式报错
        Assert.Contains("永不名单", memories.Error);
    }

    [Fact]
    public void Clean_ReWalksFreshFilesystemInsteadOfReusingScan()
    {
        var root = Path.Combine(_workspace.Root, "profile", ".zcode", "cli", "artifacts");
        var rules = new[]
        {
            Rule("artifacts", CleanupRiskClass.SessionArtifact, CleanupDeleteMode.Permanent,
                CleanupThresholdKind.ArtifactAge,
                Target(CleanupAnchor.UserProfile, @".zcode\cli\artifacts", CleanupTargetKind.DirectoryContents))
        };
        var (engine, _, _) = Create(rules);

        var first = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "old.txt"), 10);
        DiskCleanupTestHelpers.SetAge(first, _now, TimeSpan.FromDays(60));
        engine.Scan(CleanupSettings.Default, null, CancellationToken.None);

        // 扫描之后新增一个同样符合条件的文件：清理必须重新遍历并删掉它。
        var second = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "late.txt"), 20);
        DiskCleanupTestHelpers.SetAge(second, _now, TimeSpan.FromDays(60));

        var result = engine.Clean(["artifacts"], CleanupSettings.Default, simulate: false, null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, result.DeletedCount);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Contains("deleted", File.ReadAllText(result.AuditLogPath));
    }

    [Fact]
    public void Clean_UpdaterWildcardRule_RemovesMatchedDirectoriesEntirely()
    {
        var updater = Path.Combine(_workspace.Root, "profile", "AppData", "Local", "cursor-updater", "packages");
        var pkg = DiskCleanupTestHelpers.WriteFile(Path.Combine(updater, "app-1.exe"), 5000);
        DiskCleanupTestHelpers.SetAge(pkg, _now, TimeSpan.FromDays(2));
        var (engine, _, _) = Create([Rule("updaters", CleanupRiskClass.RegenerableCache,
            CleanupDeleteMode.Permanent, CleanupThresholdKind.Freshness,
            Target(CleanupAnchor.LocalAppData, "*-updater", CleanupTargetKind.MatchingRootDirectories))]);

        var scan = engine.Scan(CleanupSettings.Default, null, CancellationToken.None);
        var scanSummary = Assert.Single(scan.Summaries);
        Assert.True(scanSummary.TotalBytes == 5000, $"扫描聚合不符，Error={scanSummary.Error}");

        var result = engine.Clean(["updaters"], CleanupSettings.Default, simulate: false, null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(5000, result.ReclaimedBytes);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(pkg)))); // 通配根整体消失
    }

    [Fact]
    public void Clean_RecycleModeRule_GoesThroughRecycleSinkAndKeepsFreshFiles()
    {
        var projects = Path.Combine(_workspace.Root, "profile", ".claude", "projects", "p1");
        var old = DiskCleanupTestHelpers.WriteFile(Path.Combine(projects, "session.jsonl"), 999);
        DiskCleanupTestHelpers.SetAge(old, _now, TimeSpan.FromDays(120));
        var fresh = DiskCleanupTestHelpers.WriteFile(Path.Combine(projects, "today.jsonl"), 5);
        // 显式拨到固定时钟的 1 小时前：依赖「真实写入时间晚于固定时钟−24h」会在时钟漂移环境翻车。
        DiskCleanupTestHelpers.SetAge(fresh, _now, TimeSpan.FromHours(1));
        var (engine, sink, _) = Create([Rule("conversations", CleanupRiskClass.ConversationArchive,
            CleanupDeleteMode.RecycleBin, CleanupThresholdKind.ConversationAge,
            Target(CleanupAnchor.UserProfile, @".claude\projects", CleanupTargetKind.FilesByExtension, [".jsonl"]))]);

        var result = engine.Clean(["conversations"], CleanupSettings.Default, simulate: false, null, CancellationToken.None);

        Assert.Equal(1, result.RecycledCount);
        Assert.Equal([old], sink.Sent);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh)); // 新鲜会话不动
        Assert.True(Directory.Exists(projects));
    }

    [Fact]
    public void Clean_ReportOnlyRuleIsSkippedAndAuditExplains()
    {
        var models = Path.Combine(_workspace.Root, "profile", ".ollama", "models");
        DiskCleanupTestHelpers.WriteFile(Path.Combine(models, "gemma.bin"), 4096);
        var (engine, _, _) = Create([Rule("models", CleanupRiskClass.ModelFile, CleanupDeleteMode.ReportOnly,
            CleanupThresholdKind.None, Target(CleanupAnchor.UserProfile, @".ollama\models", CleanupTargetKind.ReportPath))]);

        var result = engine.Clean(["models"], CleanupSettings.Default, simulate: false, null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Empty(result.Outcomes);
        Assert.True(File.Exists(Path.Combine(models, "gemma.bin")));
        Assert.Contains("仅展示规则不可执行", File.ReadAllText(result.AuditLogPath));
    }

    [Fact]
    public void Clean_CancelMidRun_ReturnsPartialOutcomesWithCompleteAudit()
    {
        var root = Path.Combine(_workspace.Root, "profile", ".zcode", "cli", "artifacts");
        var a = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "a.txt"), 10);
        var b = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "b.txt"), 10);
        foreach (var f in new[] { a, b }) DiskCleanupTestHelpers.SetAge(f, _now, TimeSpan.FromDays(90));
        var (engine, _, _) = Create([Rule("artifacts", CleanupRiskClass.SessionArtifact,
            CleanupDeleteMode.Permanent, CleanupThresholdKind.ArtifactAge,
            Target(CleanupAnchor.UserProfile, @".zcode\cli\artifacts", CleanupTargetKind.DirectoryContents))]);

        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cts.Cancel()); // 第一次进度上报即取消
        var result = engine.Clean(["artifacts"], CleanupSettings.Default, simulate: false, progress, cts.Token);

        Assert.False(result.Completed);
        Assert.True(result.Cancelled);
        Assert.True(File.Exists(result.AuditLogPath));
        Assert.Contains("run cancelled", File.ReadAllText(result.AuditLogPath));
    }

    [Fact]
    public void Clean_SimulateReportsReclaimedBytesWithoutDeleting()
    {
        var root = Path.Combine(_workspace.Root, "profile", ".zcode", "cli", "dumps");
        var report = DiskCleanupTestHelpers.WriteFile(Path.Combine(root, "r.json"), 77);
        DiskCleanupTestHelpers.SetAge(report, _now, TimeSpan.FromDays(60));
        var (engine, _, _) = Create([Rule("dumps", CleanupRiskClass.SessionArtifact,
            CleanupDeleteMode.Permanent, CleanupThresholdKind.ArtifactAge,
            Target(CleanupAnchor.UserProfile, @".zcode\cli\dumps", CleanupTargetKind.DirectoryContents))]);

        var scan = engine.Scan(CleanupSettings.Default, null, CancellationToken.None);
        var result = engine.Clean(["dumps"], CleanupSettings.Default, simulate: true, null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(0, result.DeletedCount);
        Assert.True(File.Exists(report));
        Assert.Equal(77, Assert.Single(scan.Summaries).TotalBytes);
    }

    [Fact]
    public void Clean_CancelAfterFirstRule_RetainsCompletedWorkAndAudits()
    {
        var rootA = Path.Combine(_workspace.Root, "profile", ".zcode", "cli", "artifacts");
        var rootB = Path.Combine(_workspace.Root, "temp");
        var a = DiskCleanupTestHelpers.WriteFile(Path.Combine(rootA, "a.txt"), 10);
        var b = DiskCleanupTestHelpers.WriteFile(Path.Combine(rootB, "b.tmp"), 10);
        foreach (var f in new[] { a, b }) DiskCleanupTestHelpers.SetAge(f, _now, TimeSpan.FromDays(90));
        var (engine, _, _) = Create(
        [
            Rule("artifacts", CleanupRiskClass.SessionArtifact, CleanupDeleteMode.Permanent,
                CleanupThresholdKind.ArtifactAge,
                Target(CleanupAnchor.UserProfile, @".zcode\cli\artifacts", CleanupTargetKind.DirectoryContents)),
            Rule("usertemp", CleanupRiskClass.RegenerableCache, CleanupDeleteMode.Permanent,
                CleanupThresholdKind.Freshness,
                Target(CleanupAnchor.Temp, "", CleanupTargetKind.DirectoryContents))
        ]);

        using var cts = new CancellationTokenSource();
        var reports = 0;
        var progress = new InlineProgress(_ =>
        {
            // 第二次进度上报（进入第二条规则前）才取消：第一条规则的删除必须已经完成。
            if (Interlocked.Increment(ref reports) >= 2) cts.Cancel();
        });
        var result = engine.Clean(["artifacts", "usertemp"], CleanupSettings.Default, simulate: false, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.DeletedCount); // 已完成的部分如实保留
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        var audit = File.ReadAllText(result.AuditLogPath);
        Assert.Contains("deleted", audit);
        Assert.Contains("run cancelled", audit);
    }

    [Fact]
    public void Clean_TargetFailure_SkipsThatTargetAndCleansTheRest()
    {
        var temp = Path.Combine(_workspace.Root, "temp");
        var good = DiskCleanupTestHelpers.WriteFile(Path.Combine(temp, "good.tmp"), 64);
        DiskCleanupTestHelpers.SetAge(good, _now, TimeSpan.FromDays(2));
        var (engine, _, _) = Create([Rule("mixed", CleanupRiskClass.RegenerableCache,
            CleanupDeleteMode.Permanent, CleanupThresholdKind.Freshness,
            Target(CleanupAnchor.Temp, "", CleanupTargetKind.DirectoryContents),
            Target(CleanupAnchor.UserProfile, "Documents", CleanupTargetKind.DirectoryContents))]); // 拒绝区目标

        var result = engine.Clean(["mixed"], CleanupSettings.Default, simulate: false, null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.False(File.Exists(good)); // 坏目标只跳过自己，同规则其余目标照常清理
        Assert.Contains("skipped-target", File.ReadAllText(result.AuditLogPath));
    }

    [Fact]
    public void Clean_UnknownRuleId_IsAuditedAndIgnored()
    {
        var (engine, _, _) = Create([Rule("real", CleanupRiskClass.RegenerableCache,
            CleanupDeleteMode.Permanent, CleanupThresholdKind.Freshness,
            Target(CleanupAnchor.Temp, "", CleanupTargetKind.DirectoryContents))]);

        var result = engine.Clean(["real", "ghost-rule"], CleanupSettings.Default, simulate: false, null, CancellationToken.None);

        Assert.True(result.Completed); // 未知规则不使运行失败
        Assert.Contains("skipped-rule ghost-rule reason=未知规则", File.ReadAllText(result.AuditLogPath));
    }

    public void Dispose() => _workspace.Dispose();
}
