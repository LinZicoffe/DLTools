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
        WriteAndCheck(audit, $"rule {ruleId} mode={deleteMode}{(simulate ? " SIMULATE" : "")} candidates={walk.Files.Count} bytes={walk.TotalBytes}");

        foreach (var candidate in walk.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? reason;
            try
            {
                if (!_guard.IsCandidateAllowed(candidate.Path, out reason))
                {
                    outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                        CleanupDecision.Skipped, reason));
                    WriteAndCheck(audit, $"skipped {candidate.Bytes} \"{candidate.Path}\" reason={reason}");
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 路径链属性读不了（ACL 拒绝等）：无法证实安全即拒绝该条，不让整次清理中止。
                outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                    CleanupDecision.Skipped, "无法校验路径链"));
                WriteAndCheck(audit, $"skipped {candidate.Bytes} \"{candidate.Path}\" reason=无法校验路径链 error={ex.Message}");
                continue;
            }

            if (simulate)
            {
                outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                    CleanupDecision.Simulated, null));
                WriteAndCheck(audit, $"simulated-delete {candidate.Bytes} \"{candidate.Path}\"");
                continue;
            }

            if (deleteMode == CleanupDeleteMode.RecycleBin)
            {
                if (_recycleBin.TrySend(candidate.Path, out var recycleError))
                {
                    outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                        CleanupDecision.Recycled, null));
                    WriteAndCheck(audit, $"recycled {candidate.Bytes} \"{candidate.Path}\"");
                }
                else
                {
                    outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                        CleanupDecision.Failed, recycleError));
                    WriteAndCheck(audit, $"failed \"{candidate.Path}\" error={recycleError}");
                }
                continue;
            }

            // 句柄式删除：路径解析只发生在打开瞬间，二次校验与删除作用在同一个被钉住的对象上，
            // 封堵「校验通过后父目录被换成 junction」的残余竞态（重解析点/云占位在句柄上再复核一遍）。
            if (SafeFileDelete.TryDelete(candidate.Path, out var deleteError))
            {
                // deleteError 非空 = 极端竞态下的「已删除但带告警」，如实入审计与明细。
                outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                    CleanupDecision.Deleted, deleteError));
                WriteAndCheck(audit, $"deleted {candidate.Bytes} \"{candidate.Path}\"" +
                    (deleteError is null ? "" : $" {deleteError}"));
            }
            else
            {
                // 正在使用的缓存/日志被系统锁定（共享冲突）或只读：正常情况，记录后继续。
                outcomes.Add(new CleanupItemOutcome(ruleId, candidate.Path, candidate.Bytes, false,
                    CleanupDecision.Failed, deleteError));
                WriteAndCheck(audit, $"failed \"{candidate.Path}\" error={deleteError}");
            }
        }

        PruneEmptyDirectories(ruleId, walk, simulate, audit, cancellationToken);
        audit.Check();
        return outcomes;
    }

    /// <summary>写入一行并立即 Check：任何一次审计写入失败都当场中止，杜绝「删了但无审计」的窗口。</summary>
    private static void WriteAndCheck(CleanupAuditLog audit, string line)
    {
        audit.Write(line);
        audit.Check();
    }

    /// <summary>按深度倒序修剪名册内当前已为空的目录——含「清点时就已为空」的历史空目录（它们同样位于
    /// 规则目标范围之内）。只碰 walker 名册里的目录，修剪前同样经 PathGuard 复验（重解析点目录永远不修剪）。
    /// 空目录不含数据，统一永久删除（回收站模式下同理）。</summary>
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
                // 读不了的目录不修剪——留审计行，不静默（用户能从日志解释为何目录残留）。
                WriteAndCheck(audit, $"prune-skipped \"{directory}\" reason=无法确认目录为空 error={ex.Message}");
                continue;
            }

            string? reason;
            try
            {
                if (!_guard.IsCandidateAllowed(directory, out reason))
                {
                    WriteAndCheck(audit, $"prune-skipped \"{directory}\" reason={reason}");
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteAndCheck(audit, $"prune-skipped \"{directory}\" reason=无法校验路径链 error={ex.Message}");
                continue;
            }
            if (simulate)
            {
                WriteAndCheck(audit, $"simulated-prune \"{directory}\"");
                continue;
            }
            try
            {
                Directory.Delete(directory);
                WriteAndCheck(audit, $"pruned \"{directory}\"");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteAndCheck(audit, $"prune-skipped \"{directory}\" reason=删除失败 error={ex.Message}");
            }
        }
    }
}
