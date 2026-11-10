using Xunit;
using 烧录上位机程序.Models.Cleanup;
using 烧录上位机程序.Services.Cleanup;

namespace DLTools.Tests;

/// <summary>PathGuard 两遍校验：规则目标解析（第一遍）与删除前复验（第二遍）。
/// 全部使用临时根内的假环境，绝不触碰真实用户目录。</summary>
public class DiskCleanupPathGuardTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly DiskCleanupTestHelpers.FixedTimeProvider _clock =
        new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    private (CleanupEnvironment Environment, PathGuard Guard) Create()
    {
        var environment = DiskCleanupTestHelpers.CreateEnvironment(_workspace);
        return (environment, new PathGuard(environment));
    }

    [Fact]
    public void ResolveTargetRoot_TempAnchorItself_IsAllowed()
    {
        var (_, guard) = Create();
        // user-temp 规则语义就是清空 %TEMP%，目标根等于锚点必须放行。
        Assert.Equal(guard.ResolveTargetRoot(CleanupAnchor.Temp, ""), Path.Combine(_workspace.Root, "temp"));
    }

    [Fact]
    public void ResolveTargetRoot_ProfileOrLocalAppDataAnchorRoot_IsRejected()
    {
        var (_, guard) = Create();
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.UserProfile, ""));
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.LocalAppData, ""));
    }

    [Fact]
    public void ResolveTargetRoot_RejectedSubtreeUnderAnchor_IsRejected()
    {
        var (_, guard) = Create();
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.UserProfile, "Downloads"));
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @"Documents\notes"));
    }

    [Fact]
    public void ResolveTargetRoot_NormalSubtreeUnderAnchor_IsAllowed()
    {
        var (_, guard) = Create();
        var root = guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @".zcode\cli\artifacts");
        Assert.Equal(Path.Combine(_workspace.Root, "profile", ".zcode", "cli", "artifacts"), root);
    }

    [Fact]
    public void ResolveTargetRoot_WildcardLastSegment_ReturnsParentAsEnumerationRoot()
    {
        var (_, guard) = Create();
        var root = guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @".cache\codex-runtimes\.previous-*");
        Assert.Equal(Path.Combine(_workspace.Root, "profile", ".cache", "codex-runtimes"), root);
    }

    [Fact]
    public void ResolveTargetRoot_ProtectedPath_IsRejected()
    {
        var (_, guard) = Create();
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @".zcode\cli\memories"));
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @".zcode\cli\config.json"));
        // 保护目标的祖先目录不受影响（KeepNewestFiles 规则以 .zcode\cli 为根是合法的）。
        guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @".zcode\cli");
    }

    [Fact]
    public void ResolveTargetRoot_DotDotOrAbsolutePath_IsRejected()
    {
        var (_, guard) = Create();
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @"..\evil"));
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @"C:\Windows\Temp"));
        // ".." 按段精确判定：foo..bar 是合法目录名，不得被子串规则误伤。
        guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @"foo..bar\cache");
    }

    [Fact]
    public void DefaultEnvironment_ProtectsZCodeSessionDatabaseFiles()
    {
        // 单体数据库型对话记录本体（db.sqlite 及 -wal/-shm）不依赖「仅展示规则」的间接保护：
        // 守卫层永不名单按文件直接覆盖，未来任何规则形状误配也碰不到它们。
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "db.sqlite", "db.sqlite-wal", "db.sqlite-shm" })
        {
            var expected = Path.GetFullPath(Path.Combine(profile, ".zcode", "cli", "db", name));
            Assert.Contains(CleanupEnvironment.Default.ProtectedPaths,
                path => string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ResolveTargetRoot_LinkInsidePathChain_IsRejected()
    {
        var (environment, guard) = Create();
        var outside = Path.Combine(_workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(environment.UserProfile, "linked");
        TestJunction.Create(link, outside);
        Assert.Throws<IOException>(() => guard.ResolveTargetRoot(CleanupAnchor.UserProfile, @"linked\sub"));
    }

    [Fact]
    public void IsResolvedRootAllowed_WildcardMatchInRejectedOrProtectedArea_ReturnsFalse()
    {
        var (environment, guard) = Create();
        Directory.CreateDirectory(Path.Combine(environment.UserProfile, "Downloads"));
        Assert.False(guard.IsResolvedRootAllowed(Path.Combine(environment.UserProfile, "Downloads", "x-updater")));
        Assert.True(guard.IsResolvedRootAllowed(Path.Combine(environment.LocalAppData, "cursor-updater")));
    }

    [Fact]
    public void IsCandidateAllowed_OrdinaryTempFile_IsAllowed()
    {
        var (environment, guard) = Create();
        var file = DiskCleanupTestHelpers.WriteFile(Path.Combine(environment.Temp, "a.tmp"));
        Assert.True(guard.IsCandidateAllowed(file, out _));
    }

    [Fact]
    public void IsCandidateAllowed_ProtectedFileName_IsRejectedEverywhere()
    {
        var (environment, guard) = Create();
        // CLAUDE.md 是用户资产，无论长在哪个锚点下都不允许成为候选。
        var file = DiskCleanupTestHelpers.WriteFile(Path.Combine(environment.Temp, "CLAUDE.md"));
        Assert.False(guard.IsCandidateAllowed(file, out var reason));
        Assert.Equal("受保护文件名", reason);
    }

    [Fact]
    public void IsCandidateAllowed_ProtectedPath_IsRejected()
    {
        var (environment, guard) = Create();
        var memories = Path.Combine(environment.UserProfile, ".zcode", "cli", "memories");
        var file = DiskCleanupTestHelpers.WriteFile(Path.Combine(memories, "memory.md"));
        Assert.False(guard.IsCandidateAllowed(file, out var reason));
        Assert.Equal("永不名单", reason);
    }

    [Fact]
    public void IsCandidateAllowed_JunctionCandidate_IsRejectedIncludingTrailingSeparatorVariant()
    {
        var (environment, guard) = Create();
        var canary = Path.Combine(_workspace.Root, "canary");
        Directory.CreateDirectory(canary);
        DiskCleanupTestHelpers.WriteFile(Path.Combine(canary, "precious.txt"));
        var junction = Path.Combine(environment.Temp, "escape");
        TestJunction.Create(junction, canary);

        // 直接路径：属性带重解析点，拒绝。
        Assert.False(guard.IsCandidateAllowed(junction, out var directReason));
        Assert.Equal("链接或重解析点", directReason);

        // 尾随分隔符变体：这是 CCleaner CVE-2025-3025 一类穿透攻击的载体；标准化后必须同样拒绝。
        var trailing = junction + Path.DirectorySeparatorChar;
        Assert.False(guard.IsCandidateAllowed(trailing, out var trailingReason));
        Assert.Equal("链接或重解析点", trailingReason);
    }

    [Fact]
    public void IsCandidateAllowed_PlantedJunctionInChainAfterScan_IsRejected()
    {
        var (environment, guard) = Create();
        // 扫描时是普通目录，删除前被换成 junction（TOCTOU）：二次校验的全链链接检查必须拦下。
        var sub = Path.Combine(environment.Temp, "pkg");
        var file = DiskCleanupTestHelpers.WriteFile(Path.Combine(sub, "cache.bin"));
        Assert.True(guard.IsCandidateAllowed(file, out _));

        // 替换目录里放置同名文件：GetAttributes 经 junction 能读到（叶属性正常），
        // 但全链链接检查会发现 pkg 已是重解析点——这正是 TOCTOU 拦截的最后一道防线。
        var replacement = Path.Combine(_workspace.Root, "replacement");
        DiskCleanupTestHelpers.WriteFile(Path.Combine(replacement, "cache.bin"));
        File.Delete(file);
        Directory.Delete(sub);
        TestJunction.Create(sub, replacement);
        Assert.False(guard.IsCandidateAllowed(file, out var reason));
        Assert.Equal("路径链包含链接", reason);
    }

    [Fact]
    public void IsCloudPlaceholder_OfflineAndRecallAttributes_AreDetected()
    {
        Assert.True(PathGuard.IsCloudPlaceholder(FileAttributes.Offline | FileAttributes.Archive));
        Assert.True(PathGuard.IsCloudPlaceholder((FileAttributes)0x00400000));
        Assert.True(PathGuard.IsCloudPlaceholder((FileAttributes)0x00040000));
        Assert.False(PathGuard.IsCloudPlaceholder(FileAttributes.Archive | FileAttributes.Normal));
        Assert.False(PathGuard.IsCloudPlaceholder(FileAttributes.Hidden | FileAttributes.System));
    }

    public void Dispose() => _workspace.Dispose();
}
