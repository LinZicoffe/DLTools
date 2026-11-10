using Xunit;
using 烧录上位机程序.Models.Cleanup;
using 烧录上位机程序.Services.Cleanup;

namespace DLTools.Tests;

/// <summary>规则目录健全性（对应 BitBroom CatalogSanityTests 的守门测试）：
/// 风险类 ↔ 删除方式 ↔ 阈值的合法配对、锚点内相对路径的形状约束，全部在数据层强制，
/// 新增规则违反任一约束都会在这里失败。</summary>
public class DiskCleanupRuleCatalogTests
{
    // 安全契约：分类决定处置。可再生垃圾/日志/回滚/会话衍生物永久删；
    // 对话本体文件与备份走回收站；模型与数据库型对话记录只展示。
    private static readonly IReadOnlyDictionary<CleanupRiskClass, CleanupDeleteMode> ExpectedDeleteMode =
        new Dictionary<CleanupRiskClass, CleanupDeleteMode>
        {
            [CleanupRiskClass.RegenerableCache] = CleanupDeleteMode.Permanent,
            [CleanupRiskClass.AgedLog] = CleanupDeleteMode.Permanent,
            [CleanupRiskClass.RollbackCopy] = CleanupDeleteMode.Permanent,
            [CleanupRiskClass.SessionArtifact] = CleanupDeleteMode.Permanent,
            [CleanupRiskClass.ConversationArchive] = CleanupDeleteMode.RecycleBin,
            [CleanupRiskClass.BackupRotation] = CleanupDeleteMode.RecycleBin,
            [CleanupRiskClass.ModelFile] = CleanupDeleteMode.ReportOnly,
            [CleanupRiskClass.ConversationDatabase] = CleanupDeleteMode.ReportOnly
        };

    private static readonly IReadOnlyDictionary<CleanupRiskClass, CleanupThresholdKind> ExpectedThreshold =
        new Dictionary<CleanupRiskClass, CleanupThresholdKind>
        {
            [CleanupRiskClass.RegenerableCache] = CleanupThresholdKind.Freshness,
            [CleanupRiskClass.AgedLog] = CleanupThresholdKind.LogAge,
            [CleanupRiskClass.RollbackCopy] = CleanupThresholdKind.None,
            [CleanupRiskClass.SessionArtifact] = CleanupThresholdKind.ArtifactAge,
            [CleanupRiskClass.ConversationArchive] = CleanupThresholdKind.ConversationAge,
            [CleanupRiskClass.BackupRotation] = CleanupThresholdKind.None,
            [CleanupRiskClass.ModelFile] = CleanupThresholdKind.None,
            [CleanupRiskClass.ConversationDatabase] = CleanupThresholdKind.None
        };

    [Fact]
    public void Catalog_ReportPathTargets_BelongOnlyToReportOnlyRules()
    {
        // Merge 的聚合通道以「Files 是否为空」区分候选类与 ReportPath 直填统计：
        // 同一规则混用两类目标会让直填统计静默丢失——数据层强制形状互斥，防止未来误配。
        foreach (var rule in CleanupRuleCatalog.Rules)
        {
            var hasReportPath = rule.Targets.Any(t => t.Kind == CleanupTargetKind.ReportPath);
            if (rule.DeleteMode == CleanupDeleteMode.ReportOnly)
                Assert.True(rule.Targets.All(t => t.Kind == CleanupTargetKind.ReportPath),
                    $"仅展示规则 {rule.Id} 的目标应全为 ReportPath");
            else
                Assert.False(hasReportPath, $"可执行规则 {rule.Id} 不得混入 ReportPath 目标");
        }
    }

    [Fact]
    public void Catalog_HasBothGroupsAndUniqueIds()
    {
        var rules = CleanupRuleCatalog.Rules;
        Assert.Contains(rules, r => r.Group == CleanupGroup.GeneralCache);
        Assert.Contains(rules, r => r.Group == CleanupGroup.AiSteward);
        Assert.Equal(rules.Select(r => r.Id).Count(), rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Catalog_EveryRuleMeetsSafetyPairingContract()
    {
        foreach (var rule in CleanupRuleCatalog.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Id), $"规则 {rule.Id} 缺少 Id");
            Assert.False(string.IsNullOrWhiteSpace(rule.DisplayName), $"规则 {rule.Id} 缺少显示名");
            Assert.False(string.IsNullOrWhiteSpace(rule.Description), $"规则 {rule.Id} 缺少描述");
            Assert.False(string.IsNullOrWhiteSpace(rule.ConsequenceNote), $"规则 {rule.Id} 缺少「删除后会发生什么」说明");
            Assert.Equal(ExpectedDeleteMode[rule.RiskClass], rule.DeleteMode);
            Assert.Equal(ExpectedThreshold[rule.RiskClass], rule.Threshold);
        }
    }

    [Fact]
    public void Catalog_ReportOnlyRulesAreNeverDefaultChecked()
    {
        foreach (var rule in CleanupRuleCatalog.Rules)
        {
            if (rule.DeleteMode == CleanupDeleteMode.ReportOnly)
                Assert.False(rule.DefaultChecked, $"仅展示规则 {rule.Id} 不允许默认勾选");
            if (rule.DeleteMode == CleanupDeleteMode.RecycleBin)
                Assert.False(rule.DefaultChecked, $"回收站规则 {rule.Id} 默认不勾选，交由用户显式确认");
        }
    }

    [Fact]
    public void Catalog_TargetsStayAnchorRelativeAndWellFormed()
    {
        foreach (var rule in CleanupRuleCatalog.Rules)
        {
            Assert.NotEmpty(rule.Targets);
            foreach (var target in rule.Targets)
            {
                Assert.False(string.IsNullOrWhiteSpace(target.RelativePath) && target.Kind != CleanupTargetKind.DirectoryContents,
                    $"规则 {rule.Id} 的目标必须提供相对路径");
                var relative = target.RelativePath;
                Assert.False(relative.Contains("..", StringComparison.Ordinal), $"规则 {rule.Id} 的相对路径不允许包含 ..：{relative}");
                Assert.False(relative.Contains('/', StringComparison.Ordinal), $"规则 {rule.Id} 的相对路径统一使用 \\ 分隔：{relative}");
                Assert.False(relative.EndsWith('\\'), $"规则 {rule.Id} 的相对路径不允许尾随分隔符：{relative}");
                var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                foreach (var segment in segments)
                {
                    Assert.False(segment.Contains('/', StringComparison.Ordinal) || segment.Contains("..", StringComparison.Ordinal),
                        $"规则 {rule.Id} 的相对路径段非法：{segment}");
                    // 通配符只允许出现在末段（目录发现），且每段至多一个 *。
                    if (segment.Contains('*'))
                    {
                        Assert.True(Array.IndexOf(segments, segment) == segments.Length - 1,
                            $"规则 {rule.Id} 通配符只允许出现在末段：{relative}");
                        Assert.True(segment.Count(c => c == '*') == 1,
                            $"规则 {rule.Id} 每段至多一个 *：{segment}");
                    }
                }

                // 通配符只属于 MatchingRootDirectories（目录发现），其余种类必须字面路径。
                var hasWildcard = segments.Any(s => s.Contains('*'));
                if (hasWildcard)
                    Assert.Equal(CleanupTargetKind.MatchingRootDirectories, target.Kind);
                if (target.Kind == CleanupTargetKind.MatchingRootDirectories)
                    Assert.True(hasWildcard, $"规则 {rule.Id} 的 MatchingRootDirectories 目标必须带通配符：{relative}");

                // Names 约束：按类型必须提供且形状正确。
                switch (target.Kind)
                {
                    case CleanupTargetKind.DescendantDirectoriesByName:
                    case CleanupTargetKind.FilesByExtension:
                    case CleanupTargetKind.MatchingFiles:
                    case CleanupTargetKind.KeepNewestFiles:
                        Assert.NotEmpty(target.Names);
                        break;
                    case CleanupTargetKind.DirectoryContents:
                    case CleanupTargetKind.MatchingRootDirectories:
                    case CleanupTargetKind.ReportPath:
                        Assert.Empty(target.Names);
                        break;
                    default:
                        Assert.Fail($"未知的目标种类：{target.Kind}");
                        break;
                }
                if (target.Kind == CleanupTargetKind.FilesByExtension)
                    foreach (var name in target.Names)
                        Assert.True(name.StartsWith('.') && name.Length > 1 && !name.Contains('*', StringComparison.Ordinal),
                            $"规则 {rule.Id} 的扩展名必须是含点的字面扩展名：{name}");
            }
        }
    }

    [Fact]
    public void Catalog_KeepNewestGlobsCannotMatchLiveConfigFiles()
    {
        // 备份轮换规则必须按目标分别约束：与 config.json 本体同目录的目标（.zcode\cli）里，
        // glob 绝不能匹配本体（本体另受 PathGuard 永不名单保护，这里做数据层双保险）；
        // .zcode\backup 目录里的 * 是合理的（整个目录都是备份）。
        var backupRule = Assert.Single(CleanupRuleCatalog.Rules, r => r.Id == "ai-backup-rotation");
        var liveDirTarget = Assert.Single(backupRule.Targets, t => t.RelativePath == @".zcode\cli");
        Assert.NotEmpty(Assert.Single(backupRule.Targets, t => t.RelativePath == @".zcode\backup").Names);
        foreach (var pattern in liveDirTarget.Names)
        {
            Assert.False(CleanupWalker.MatchesGlob("config.json", pattern), $"glob {pattern} 不应匹配 config.json 本体");
            Assert.True(CleanupWalker.MatchesGlob("config.json.bak-20260921", pattern) ||
                        CleanupWalker.MatchesGlob("config.json.rea.backup", pattern),
                $"glob {pattern} 应命中至少一种备份命名形态");
        }
    }
}
