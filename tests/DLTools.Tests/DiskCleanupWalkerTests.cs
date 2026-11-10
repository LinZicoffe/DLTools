using Xunit;
using 烧录上位机程序.Models.Cleanup;
using 烧录上位机程序.Services.Cleanup;

namespace DLTools.Tests;

/// <summary>Walker 行为测试：金丝雀（junction 不跟随）、阈值过滤、keep-N 轮换、云占位谓词、容错。
/// 全部数据位于 GUID 临时根；金丝雀目录刻意放在所有锚点之外。</summary>
public class DiskCleanupWalkerTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly DateTimeOffset _now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private (CleanupEnvironment Environment, CleanupWalker Walker) Create()
    {
        var environment = DiskCleanupTestHelpers.CreateEnvironment(_workspace);
        var walker = new CleanupWalker(new DiskCleanupTestHelpers.FixedTimeProvider(_now));
        return (environment, walker);
    }

    private CleanupWalkRequest Contents(string root, CleanupThresholdKind threshold = CleanupThresholdKind.Freshness,
        bool rootDeletable = false) =>
        new(root, CleanupTargetKind.DirectoryContents, [], threshold, CleanupSettings.Default, rootDeletable);

    [Fact]
    public void Walk_JunctionInsideTarget_IsNeverTraversedAndNeverCandidate()
    {
        var (environment, walker) = Create();
        var junk = DiskCleanupTestHelpers.WriteFile(Path.Combine(environment.Temp, "junk.tmp"));
        DiskCleanupTestHelpers.SetAge(junk, _now, TimeSpan.FromDays(2));

        // 金丝雀：放在所有锚点之外的目录，用 junction 埋进待清理目录。
        var canary = Path.Combine(_workspace.Root, "canary-outside");
        DiskCleanupTestHelpers.WriteFile(Path.Combine(canary, "precious.txt"));
        TestJunction.Create(Path.Combine(environment.Temp, "escape"), canary);

        var result = walker.Walk(Contents(environment.Temp), CancellationToken.None);

        var candidatePaths = result.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(junk, candidatePaths);
        // junction 本身与其目标内容都不在候选里。
        Assert.DoesNotContain(candidatePaths, p => p.Contains("escape", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidatePaths, p => p.Contains("canary-outside", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(canary, "precious.txt")));
        Assert.Single(result.Files);
    }

    [Fact]
    public void Walk_FreshnessThreshold_SkipsFilesWrittenWithinWindow()
    {
        var (environment, walker) = Create();
        var old = DiskCleanupTestHelpers.WriteFile(Path.Combine(environment.Temp, "old.tmp"));
        var fresh = DiskCleanupTestHelpers.WriteFile(Path.Combine(environment.Temp, "fresh.tmp"));
        DiskCleanupTestHelpers.SetAge(old, _now, TimeSpan.FromDays(2));
        DiskCleanupTestHelpers.SetAge(fresh, _now, TimeSpan.FromHours(1));

        var result = walker.Walk(Contents(environment.Temp), CancellationToken.None);

        Assert.Equal([old], result.Files.Select(f => f.Path).ToArray());
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void Walk_LogAgeThreshold_KeepsFilesNewerThanDays()
    {
        var (environment, walker) = Create();
        var recent = DiskCleanupTestHelpers.WriteFile(Path.Combine(environment.Temp, "recent.etl"));
        var ancient = DiskCleanupTestHelpers.WriteFile(Path.Combine(environment.Temp, "ancient.etl"));
        DiskCleanupTestHelpers.SetAge(recent, _now, TimeSpan.FromDays(3));
        DiskCleanupTestHelpers.SetAge(ancient, _now, TimeSpan.FromDays(10));

        var result = walker.Walk(Contents(environment.Temp, CleanupThresholdKind.LogAge), CancellationToken.None);

        Assert.Equal([ancient], result.Files.Select(f => f.Path).ToArray());
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void Walk_ProtectedFileName_IsSkippedEvenWhenOld()
    {
        var (environment, walker) = Create();
        var memory = DiskCleanupTestHelpers.WriteFile(Path.Combine(environment.Temp, "CLAUDE.md"));
        DiskCleanupTestHelpers.SetAge(memory, _now, TimeSpan.FromDays(90));

        var result = walker.Walk(Contents(environment.Temp, CleanupThresholdKind.ConversationAge), CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void Walk_DescendantDirectoriesByName_CollectsMatchedCacheDirsAsDeletableRoots()
    {
        var (environment, walker) = Create();
        var userData = Path.Combine(environment.LocalAppData, "Google", "Chrome", "User Data");
        var cache = Path.Combine(userData, "Default", "Cache");
        var stale = DiskCleanupTestHelpers.WriteFile(Path.Combine(cache, "data_0.db"), 128);
        DiskCleanupTestHelpers.SetAge(stale, _now, TimeSpan.FromDays(3));
        // 命中名单之外的目录不受影响。
        var profileData = DiskCleanupTestHelpers.WriteFile(Path.Combine(userData, "Default", "Login Data"), 64);
        DiskCleanupTestHelpers.SetAge(profileData, _now, TimeSpan.FromDays(300));

        var request = new CleanupWalkRequest(userData, CleanupTargetKind.DescendantDirectoriesByName,
            ["Cache", "GPUCache", "Code Cache"], CleanupThresholdKind.Freshness, CleanupSettings.Default);
        var result = walker.Walk(request, CancellationToken.None);

        Assert.Equal([stale], result.Files.Select(f => f.Path).ToArray());
        Assert.Contains(cache, result.DeletableWhenEmptyRoots);
        Assert.DoesNotContain(profileData, result.Files.Select(f => f.Path));
    }

    [Fact]
    public void Walk_FilesByExtension_MatchesOnlyGivenExtensions()
    {
        var (environment, walker) = Create();
        var threads = Path.Combine(environment.UserProfile, ".codex", "sessions");
        var trace = DiskCleanupTestHelpers.WriteFile(Path.Combine(threads, "a.nettrace"), 1024);
        var etl = DiskCleanupTestHelpers.WriteFile(Path.Combine(threads, "b.etl"), 2048);
        var config = DiskCleanupTestHelpers.WriteFile(Path.Combine(threads, "config.toml"), 8);
        foreach (var f in new[] { trace, etl, config }) DiskCleanupTestHelpers.SetAge(f, _now, TimeSpan.FromDays(30));

        var request = new CleanupWalkRequest(threads, CleanupTargetKind.FilesByExtension,
            [".etl", ".nettrace"], CleanupThresholdKind.LogAge, CleanupSettings.Default);
        var result = walker.Walk(request, CancellationToken.None);

        // 顺序按路径序：a.nettrace 在 b.etl 之前。扩展名不匹配的文件不属于「命中后被跳过」，不计入 Skipped。
        Assert.Equal([trace, etl], result.Files.Select(f => f.Path).ToArray());
        Assert.Equal(1024 + 2048, result.TotalBytes);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void Walk_MatchingFiles_IsNonRecursiveAndGlobMatched()
    {
        var (environment, walker) = Create();
        var explorer = Path.Combine(environment.LocalAppData, "Microsoft", "Windows", "Explorer");
        var top = DiskCleanupTestHelpers.WriteFile(Path.Combine(explorer, "thumbcache_123.db"), 512);
        var nested = DiskCleanupTestHelpers.WriteFile(Path.Combine(explorer, "nested", "thumbcache_456.db"), 512);
        foreach (var f in new[] { top, nested }) DiskCleanupTestHelpers.SetAge(f, _now, TimeSpan.FromDays(2));

        var request = new CleanupWalkRequest(explorer, CleanupTargetKind.MatchingFiles,
            ["thumbcache_*.db"], CleanupThresholdKind.Freshness, CleanupSettings.Default);
        var result = walker.Walk(request, CancellationToken.None);

        Assert.Equal([top], result.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Walk_KeepNewestFiles_RetainsNewestBackupsOnly()
    {
        var (environment, walker) = Create();
        var cli = Path.Combine(environment.UserProfile, ".zcode", "cli");
        var stamps = new[] { 10, 2, 7, 1, 5 };
        var files = stamps.Select((days, i) =>
        {
            var path = DiskCleanupTestHelpers.WriteFile(Path.Combine(cli, $"config.json.bak-{i}"), 16);
            DiskCleanupTestHelpers.SetAge(path, _now, TimeSpan.FromDays(days));
            return (Path: path, Days: days);
        }).ToArray();
        // 本体永远不是候选（glob 不匹配）。
        var live = DiskCleanupTestHelpers.WriteFile(Path.Combine(cli, "config.json"), 16);

        var request = new CleanupWalkRequest(cli, CleanupTargetKind.KeepNewestFiles,
            ["config.json.bak-*"], CleanupThresholdKind.None, CleanupSettings.Default);
        var result = walker.Walk(request, CancellationToken.None);

        // 备份 5 份保留最新 3 份（days 1、2、5），候选是最老的 2 份（days 7、10）。
        var expected = files.Where(f => f.Days is 7 or 10).Select(f => f.Path)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expected, result.Files.Select(f => f.Path).ToArray());
        Assert.Equal(3, result.SkippedCount);
        Assert.True(File.Exists(live));
    }

    [Fact]
    public void Walk_ReportPath_SumsBytesWithoutProducingCandidates()
    {
        var (environment, walker) = Create();
        var models = Path.Combine(environment.UserProfile, ".ollama", "models");
        var a = DiskCleanupTestHelpers.WriteFile(Path.Combine(models, "a.bin"), 4096);
        var b = DiskCleanupTestHelpers.WriteFile(Path.Combine(models, "b.bin"), 8192);

        var request = new CleanupWalkRequest(models, CleanupTargetKind.ReportPath,
            [], CleanupThresholdKind.None, CleanupSettings.Default);
        var result = walker.Walk(request, CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Equal(4096 + 8192, result.TotalBytes);
        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Walk_MissingRoot_ReportsZeroWithoutThrowing()
    {
        var (environment, walker) = Create();
        var missing = Path.Combine(environment.LocalAppData, "never-created-updater");
        var result = walker.Walk(Contents(missing, CleanupThresholdKind.Freshness, rootDeletable: true), CancellationToken.None);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.TotalBytes);
    }

    [Fact]
    public void Merge_AggregatesMultipleRootsAndKeepsDirectoriesDeduplicated()
    {
        var (environment, walker) = Create();
        var root1 = Path.Combine(environment.Temp, "one");
        var root2 = Path.Combine(environment.Temp, "two");
        // 候选文件各放进一层子目录，验证多根目录合并去重与深度倒序。
        var f1 = DiskCleanupTestHelpers.WriteFile(Path.Combine(root1, "nest", "a.tmp"));
        var f2 = DiskCleanupTestHelpers.WriteFile(Path.Combine(root2, "nest", "b.tmp"));
        foreach (var f in new[] { f1, f2 }) DiskCleanupTestHelpers.SetAge(f, _now, TimeSpan.FromDays(3));

        var merged = CleanupWalkResult.Merge(new[]
        {
            walker.Walk(Contents(root1), CancellationToken.None),
            walker.Walk(Contents(root2), CancellationToken.None)
        });

        Assert.Equal(2, merged.Files.Count);
        Assert.Equal(
        [
            Path.Combine(root1, "nest"), Path.Combine(root2, "nest")
        ], merged.Directories);
        Assert.Equal(64, merged.TotalBytes);
        Assert.Equal(2, merged.RootSummaries.Count);
    }

    [Fact]
    public void MeetsThreshold_FreshnessUsesNewerOfWriteAndCreate()
    {
        var settings = CleanupSettings.Default; // 24h
        var createdRecently = _now - TimeSpan.FromHours(1);
        var writtenLongAgo = _now - TimeSpan.FromDays(5);
        // 创建时间新（刚落盘还没写过）：视为新鲜，不清。
        Assert.False(CleanupWalker.MeetsThreshold(CleanupThresholdKind.Freshness, settings, writtenLongAgo, createdRecently, _now));
        // 两者都旧：可清。
        Assert.True(CleanupWalker.MeetsThreshold(CleanupThresholdKind.Freshness, settings, writtenLongAgo, writtenLongAgo, _now));
    }

    public void Dispose() => _workspace.Dispose();
}
