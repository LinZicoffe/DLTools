using System.IO;
using 烧录上位机程序.Models.Cleanup;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>清理引擎编排：规则解析 → 安全遍历 →（扫描：聚合预览 / 清理：二次遍历 + 执行 + 审计）。
/// 清理永远重新解析重新遍历，不复用扫描缓存——预览与执行之间文件系统可能已变化，两遍校验才有意义。
/// 同步 API：调用方（ViewModel）负责放入 BackgroundTaskHost 的后台任务。
/// Scan/Clean 为 virtual：ViewModel 测试注入可阻塞的替身引擎验证取消中态。</summary>
public class CleanupEngine
{
    private const int MaxRootsPerSummary = 8;

    private readonly IReadOnlyList<CleanupRule> _rules;
    private readonly PathGuard _guard;
    private readonly CleanupWalker _walker;
    private readonly SafeCleanupExecutor _executor;
    private readonly string? _auditDirectory;
    private readonly TimeProvider _timeProvider;

    public CleanupEngine(CleanupEnvironment? environment = null, TimeProvider? timeProvider = null,
        IRecycleBinSink? recycleBin = null, string? auditDirectory = null, IReadOnlyList<CleanupRule>? rules = null)
    {
        environment ??= CleanupEnvironment.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rules = rules ?? CleanupRuleCatalog.Rules;
        _guard = new PathGuard(environment);
        _walker = new CleanupWalker(_timeProvider, environment.ProtectedFileNames);
        _executor = new SafeCleanupExecutor(_guard, recycleBin);
        _auditDirectory = auditDirectory;
    }

    /// <summary>只读扫描：逐规则解析目标并遍历聚合，产出预览数字。任何单规则/单目标失败都降级为该规则的 Error，不中断整体。
    /// virtual 供 ViewModel 测试注入可控时钟的替身引擎（取消中态等用例）。</summary>
    public virtual CleanupScanResult Scan(CleanupSettings settings, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var summaries = new List<CleanupRuleSummary>(_rules.Count);
            for (var i = 0; i < _rules.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rule = _rules[i];
                progress?.Report(new CleanupProgress(CleanupStage.Scanning,
                    $"扫描：{rule.DisplayName}（{i + 1}/{_rules.Count}）", (i + 1) * 100 / _rules.Count));
                summaries.Add(ScanRule(rule, settings, cancellationToken));
            }
            return CleanupScanResult.FromSucceeded(summaries, _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            return CleanupScanResult.FromCancellation(_timeProvider.GetUtcNow());
        }
    }

    /// <summary>执行清理：重新解析 + 重新遍历选中规则，每个候选经 PathGuard 二次校验后删除/回收/模拟。
    /// OperationCanceled 返回带已完成明细的取消结果；审计日志无论成功/取消/失败都完整落盘。
    /// virtual 供 ViewModel 测试注入可控替身引擎（取消中态等用例）。</summary>
    public virtual CleanupResult Clean(IReadOnlyCollection<string> ruleIds, CleanupSettings settings, bool simulate,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruleIds);
        var selectedIds = ruleIds.ToHashSet(StringComparer.Ordinal);
        var outcomes = new List<CleanupItemOutcome>();
        using var audit = CleanupAuditLog.CreateNew(_auditDirectory, _timeProvider.GetLocalNow(), _timeProvider);
        try
        {
            audit.Write($"run start simulate={simulate} rules={string.Join(",", ruleIds.OrderBy(id => id, StringComparer.Ordinal))}");
            audit.Check();

            var selected = _rules.Where(r => selectedIds.Contains(r.Id)).ToList();
            var unsupported = selected.Where(r => r.DeleteMode == CleanupDeleteMode.ReportOnly).ToList();
            foreach (var rule in unsupported)
            {
                audit.Write($"skipped-rule {rule.Id} reason=仅展示规则不可执行");
                audit.Check();
            }
            foreach (var unknownId in selectedIds.Except(_rules.Select(r => r.Id), StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
            {
                audit.Write($"skipped-rule {unknownId} reason=未知规则");
                audit.Check();
            }

            var executable = selected.Where(r => r.DeleteMode != CleanupDeleteMode.ReportOnly).ToList();
            for (var i = 0; i < executable.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rule = executable[i];
                progress?.Report(new CleanupProgress(CleanupStage.Cleaning,
                    $"清理：{rule.DisplayName}（{i + 1}/{executable.Count}）", (i + 1) * 100 / executable.Count));
                outcomes.AddRange(RunRule(rule, settings, simulate, audit, cancellationToken));
            }

            audit.Write($"run end deleted={outcomes.Count(o => o.Decision == CleanupDecision.Deleted)} " +
                $"recycled={outcomes.Count(o => o.Decision == CleanupDecision.Recycled)} " +
                $"skipped={outcomes.Count(o => o.Decision == CleanupDecision.Skipped)} " +
                $"failed={outcomes.Count(o => o.Decision == CleanupDecision.Failed)}");
            audit.Check();
            return BuildResult(outcomes, audit.Path, completed: true, cancelled: false, error: null);
        }
        catch (OperationCanceledException)
        {
            audit.Write("run cancelled");
            audit.Check();
            return BuildResult(outcomes, audit.Path, completed: false, cancelled: true, error: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            audit.Write($"run failed error={ex.Message}");
            audit.Check();
            return BuildResult(outcomes, audit.Path, completed: false, cancelled: false, error: ex.Message);
        }
    }

    private CleanupRuleSummary ScanRule(CleanupRule rule, CleanupSettings settings, CancellationToken cancellationToken)
    {
        var walks = new List<CleanupWalkResult>();
        string? error = null;
        foreach (var target in rule.Targets)
        {
            try
            {
                var (walk, targetError) = ResolveAndWalk(rule, target, settings, cancellationToken);
                if (walk is not null) walks.Add(walk);
                if (targetError is not null) error ??= targetError;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个目标失败（路径非法/不可读）：降级为该规则带 Error 的空结果，其余目标继续。
                error ??= ex.Message;
            }
        }
        var merged = CleanupWalkResult.Merge(walks);
        return new CleanupRuleSummary(rule.Id, merged.TotalBytes, merged.FileCount,
            merged.DirectoryCount, merged.SkippedCount,
            merged.RootSummaries.Take(MaxRootsPerSummary).ToArray(), error);
    }

    private IReadOnlyList<CleanupItemOutcome> RunRule(CleanupRule rule, CleanupSettings settings, bool simulate,
        CleanupAuditLog audit, CancellationToken cancellationToken)
    {
        var walks = new List<CleanupWalkResult>();
        foreach (var target in rule.Targets)
        {
            try
            {
                var (walk, targetError) = ResolveAndWalk(rule, target, settings, cancellationToken, audit);
                if (walk is not null) walks.Add(walk);
                if (targetError is not null)
                {
                    audit.Write($"target-warning {rule.Id} error={targetError}");
                    audit.Check();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理阶段目标解析失败（与扫描阶段同样可能）：跳过该目标，规则其余目标继续。
                audit.Write($"skipped-target {rule.Id} \"{target.RelativePath}\" error={ex.Message}");
                audit.Check();
            }
        }
        return _executor.Execute(rule.Id, CleanupWalkResult.Merge(walks), rule.DeleteMode, simulate, audit, cancellationToken);
    }

    /// <summary>把规则目标解析成枚举根并遍历。返回 (null, error) 表示该目标整体不可用（含通配目录枚举失败）。
    /// 清理阶段传入 audit：通配匹配目录被守卫拒绝时留下 skipped-root 审计行，不再静默跳过。</summary>
    private (CleanupWalkResult? Walk, string? Error) ResolveAndWalk(
        CleanupRule rule, CleanupTarget target, CleanupSettings settings, CancellationToken cancellationToken,
        CleanupAuditLog? audit = null)
    {
        var enumerationRoot = _guard.ResolveTargetRoot(target.Anchor, target.RelativePath);

        if (target.Kind == CleanupTargetKind.MatchingRootDirectories)
        {
            var pattern = target.RelativePath.Split('\\')[^1];
            var matched = new List<CleanupWalkResult>();
            try
            {
                foreach (var directory in Directory.GetDirectories(enumerationRoot, pattern)
                             .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_guard.IsResolvedRootAllowed(directory))
                    {
                        if (audit is not null)
                        {
                            audit.Write($"skipped-root {rule.Id} \"{directory}\" reason=解析拒绝");
                            audit.Check();
                        }
                        continue;
                    }
                    matched.Add(_walker.Walk(new CleanupWalkRequest(directory, CleanupTargetKind.DirectoryContents,
                        [], rule.Threshold, settings, RootDeletableWhenEmpty: true), cancellationToken));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (CleanupWalkResult.Merge(matched), $"无法枚举 {enumerationRoot}：{ex.Message}");
            }
            return (CleanupWalkResult.Merge(matched), null);
        }

        var walk = _walker.Walk(new CleanupWalkRequest(enumerationRoot, target.Kind, target.Names,
            rule.Threshold, settings), cancellationToken);
        return (walk, null);
    }

    /// <summary>计数一律从已完成明细计算（含失败/取消的运行）：同一对象内「计数」与「明细」不再自相矛盾，
    /// Error 只承担失败原因说明。</summary>
    private CleanupResult BuildResult(List<CleanupItemOutcome> outcomes, string auditLogPath,
        bool completed, bool cancelled, string? error)
    {
        var deleted = outcomes.Where(o => o.Decision == CleanupDecision.Deleted).ToList();
        var recycled = outcomes.Where(o => o.Decision == CleanupDecision.Recycled).ToList();
        return new CleanupResult(completed, cancelled, error,
            deleted.Sum(o => o.Bytes) + recycled.Sum(o => o.Bytes),
            deleted.Count, recycled.Count,
            outcomes.Count(o => o.Decision == CleanupDecision.Skipped),
            outcomes.Count(o => o.Decision == CleanupDecision.Failed),
            outcomes, auditLogPath, _timeProvider.GetUtcNow());
    }
}
