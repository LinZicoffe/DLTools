using System.IO;
using 烧录上位机程序.Models.Cleanup;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>删除执行器：对 walker 产出的候选逐个执行。每个候选在删除前经 PathGuard 二次校验
/// （此刻重新读属性 + 全链链接检查）；占用/失败逐条记录，不中断整体；模拟运行只写审计不动文件。
/// 候选只含文件——目录一律通过「清空后修剪」处理，杜绝带尾随分隔符删除目录的穿透陷阱。</summary>
public sealed class SafeCleanupExecutor
{
    private readonly PathGuard _guard;
    private readonly IRecycleBinSink _recycleBin;

    public SafeCleanupExecutor(PathGuard? guard = null, IRecycleBinSink? recycleBin = null)
    {
        _guard = guard ?? new PathGuard();
        _recycleBin = recycleBin ?? new ShellFileOperationRecycleBin();
    }

    /// <summary>执行单条规则的候选集。返回逐条结果；审计逐条写入并在关键节点 Check（审计不完整即中止）。</summary>
    public IReadOnlyList<CleanupItemOutcome> Execute(
        string ruleId,
        CleanupWalkResult walk,
        CleanupDeleteMode deleteMode,
        bool simulate,
        CleanupAuditLog audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (deleteMode == CleanupDeleteMode.ReportOnly)
            throw new ArgumentException("仅展示规则不允许进入执行器。", nameof(deleteMode));

        var outcomes = new List<CleanupItemOutcome>(walk.Files.Count);
        audit.Write($"rule {ruleId} mode={deleteMode}{(simulate ? " SIMULATE" : "")} candidates={walk.Files.Count} bytes={walk.TotalBytes}");
        audit.Check();

        foreach (var candidate in walk.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_guard.IsCandidateAllowed(candidate.Path, out var reason))
            {
                outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                    CleanupDecision.Skipped, reason));
                audit.Write($"skipped {candidate.Bytes} \"{candidate.Path}\" reason={reason}");
                continue;
            }

            if (simulate)
            {
                outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                    CleanupDecision.Simulated, null));
                audit.Write($"simulated-delete {candidate.Bytes} \"{candidate.Path}\"");
                continue;
            }

            if (deleteMode == CleanupDeleteMode.RecycleBin)
            {
                if (_recycleBin.TrySend(candidate.Path, out var recycleError))
                {
                    outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                        CleanupDecision.Recycled, null));
                    audit.Write($"recycled {candidate.Bytes} \"{candidate.Path}\"");
                }
                else
                {
                    outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                        CleanupDecision.Failed, recycleError));
                    audit.Write($"failed \"{candidate.Path}\" error={recycleError}");
                }
                continue;
            }

            try
            {
                File.Delete(candidate.Path);
                outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                    CleanupDecision.Deleted, null));
                audit.Write($"deleted {candidate.Bytes} \"{candidate.Path}\"");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 正在使用的缓存/日志被系统锁定：正常情况，记录后继续。
                outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                    CleanupDecision.Failed, ex.Message));
                audit.Write($"failed \"{candidate.Path}\" error={ex.Message}");
            }
        }

        audit.Check();
        PruneEmptyDirectories(ruleId, walk, simulate, audit, cancellationToken);
        audit.Check();
        return outcomes;
    }

    /// <summary>按深度倒序修剪「因本次删除而变空」的目录。只碰 walker 名册里的目录，
    /// 修剪前同样经 PathGuard 复验（重解析点目录永远不修剪）。空目录不含数据，统一永久删除（回收站模式下同理）。</summary>
    private void PruneEmptyDirectories(string ruleId, CleanupWalkResult walk, bool simulate,
        CleanupAuditLog audit, CancellationToken cancellationToken)
    {
        foreach (var directory in walk.Directories.Concat(walk.DeletableWhenEmptyRoots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(directory)) continue;
                if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // 读不了的目录不修剪
            }

            if (!_guard.IsCandidateAllowed(directory, out var reason))
            {
                audit.Write($"prune-skipped \"{directory}\" reason={reason}");
                continue;
            }
            if (simulate)
            {
                audit.Write($"simulated-prune \"{directory}\"");
                continue;
            }
            try
            {
                Directory.Delete(directory);
                audit.Write($"pruned \"{directory}\"");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                audit.Write($"prune-skipped \"{directory}\" error={ex.Message}");
            }
        }
    }
}
