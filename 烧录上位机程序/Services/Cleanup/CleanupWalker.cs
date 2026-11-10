using System.IO;
using 烧录上位机程序.Models.Cleanup;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>一次遍历请求。Root 必须是通过 PathGuard 解析/复验后的枚举根。</summary>
public sealed record CleanupWalkRequest(
    string Root,
    CleanupTargetKind Kind,
    IReadOnlyList<string> Names,
    CleanupThresholdKind Threshold,
    CleanupSettings Settings,
    bool RootDeletableWhenEmpty = false);

    public sealed record WalkFileCandidate(string Path, long Bytes, DateTimeOffset LastWriteUtc, DateTimeOffset CreatedUtc);

/// <summary>遍历结果。Files 是删除候选（只含文件；目录一律通过「清空后修剪」处理，杜绝带尾随分隔符删目录的穿透陷阱）。</summary>
public sealed record CleanupWalkResult(
    IReadOnlyList<WalkFileCandidate> Files,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> DeletableWhenEmptyRoots,
    long TotalBytes,
    int FileCount,
    int DirectoryCount,
    int SkippedCount,
    IReadOnlyList<CleanupRootSummary> RootSummaries)
{
    public static CleanupWalkResult Empty { get; } = new([], [], [], 0, 0, 0, 0, []);

    /// <summary>深度倒序（先深后浅）+ 路径序：执行器按此顺序修剪空目录即天然自底向上。</summary>
    private static string[] DepthDescending(IEnumerable<string> directories) =>
        directories.OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>合并同一规则多个目标/匹配根的遍历结果。目录与文件都按路径去重（OrdinalIgnoreCase）——
    /// 同一规则多个 target 的枚举范围重叠时，同一文件不得双计字节、也不得作为候选删两次；
    /// 聚合数字一律从去重后的集合重算，保证与候选列表一致。</summary>
    public static CleanupWalkResult Merge(IEnumerable<CleanupWalkResult> results)
    {
        var files = new Dictionary<string, WalkFileCandidate>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletableRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedCount = 0;
        var summaries = new List<CleanupRootSummary>();
        foreach (var result in results)
        {
            foreach (var file in result.Files)
                files.TryAdd(file.Path, file);
            directories.UnionWith(result.Directories);
            deletableRoots.UnionWith(result.DeletableWhenEmptyRoots);
            skippedCount += result.SkippedCount;
            summaries.AddRange(result.RootSummaries);
        }
        var mergedFiles = files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        // ReportPath 走「Files 恒空、TotalBytes/FileCount 直填」的只读统计通道（不产生候选），
        // 此类结果的聚合沿用累加；候选类结果一律从去重后的文件列表重算，保证与候选一致。
        long totalBytes;
        int fileCount;
        if (mergedFiles.Length > 0)
        {
            totalBytes = mergedFiles.Sum(f => f.Bytes);
            fileCount = mergedFiles.Length;
        }
        else
        {
            var resultsList = results.ToList();
            totalBytes = resultsList.Sum(r => r.TotalBytes);
            fileCount = resultsList.Sum(r => r.FileCount);
        }
        return new CleanupWalkResult(
            mergedFiles,
            DepthDescending(directories),
            deletableRoots.ToArray(),
            totalBytes, fileCount, directories.Count, skippedCount,
            summaries.OrderByDescending(s => s.Bytes).ThenBy(s => s.Root, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

/// <summary>容错目录遍历（Stack DFS，仿 ProjectInspectionService.Scan 的结构）：
/// 重解析点在扫描阶段一律不跟随、也不作为候选（junction 防线第一环）；
/// 单目录 IOException/UnauthorizedAccessException 记入跳过计数后继续；云占位与受保护文件名同样跳过。
/// 时钟经 TimeProvider 注入，年龄/新鲜度测试无需等待真实时间。</summary>
public sealed class CleanupWalker
{
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<string> _protectedFileNames;

    public CleanupWalker(TimeProvider? timeProvider = null, IReadOnlyList<string>? protectedFileNames = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _protectedFileNames = new HashSet<string>(
            protectedFileNames ?? CleanupEnvironment.DefaultProtectedFileNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>仅支持单个 * 的文件名 glob（OrdinalIgnoreCase），与规则目录测试共用同一实现。
    /// 空模式不匹配任何名字（防御注入规则）；`?` 按字面字符处理；含多个 `*` 时段间贪心无回溯（漏配方向安全）。</summary>
    public static bool MatchesGlob(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var parts = pattern.Split('*');
        var index = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            index = name.IndexOf(parts[i], index, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            if (i == 0 && index != 0) return false;
            index += parts[i].Length;
        }
        var lastPart = parts[^1];
        return lastPart.Length == 0 || name.EndsWith(lastPart, StringComparison.OrdinalIgnoreCase);
    }

    public CleanupWalkResult Walk(CleanupWalkRequest request, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return request.Kind switch
        {
            CleanupTargetKind.DirectoryContents => WalkContents(request, now, cancellationToken),
            CleanupTargetKind.DescendantDirectoriesByName => WalkNamedDirectories(request, now, cancellationToken),
            CleanupTargetKind.FilesByExtension => WalkByExtension(request, now, cancellationToken),
            CleanupTargetKind.MatchingFiles => WalkMatchingFiles(request, now, cancellationToken),
            CleanupTargetKind.KeepNewestFiles => WalkKeepNewest(request, now, cancellationToken),
            CleanupTargetKind.ReportPath => WalkReportSize(request, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "未知目标种类")
        };
    }

    private CleanupWalkResult WalkContents(CleanupWalkRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var collector = new Collector();
        CollectContents(request.Root, request.Threshold, request.Settings, collector, now, ct);
        return collector.ToResult(request.Root, request.RootDeletableWhenEmpty);
    }

    private CleanupWalkResult WalkNamedDirectories(CleanupWalkRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var names = new HashSet<string>(request.Names, StringComparer.OrdinalIgnoreCase);
        var collector = new Collector();
        var stack = new Stack<string>();
        stack.Push(request.Root);
        while (stack.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            string[] entries;
            if (!TryListEntries(current, collector, out entries)) continue;
            foreach (var path in entries.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                Entry entry;
                if (!TryStat(path, collector, out entry)) continue;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (!entry.IsDirectory) continue;
                // 名字命中的目录整体作为一个可清空根：不再深入其内部找嵌套命中，避免重复计数；
                // 内部子目录照常入册（含深度信息），清空后自底向上修剪才能让缓存目录本身变空。
                if (names.Contains(Path.GetFileName(path)))
                {
                    CollectContents(path, request.Threshold, request.Settings, collector, now, ct);
                    collector.DeletableWhenEmpty.Add(path);
                    continue;
                }
                collector.AddDirectory(path);
                stack.Push(path);
            }
        }
        return collector.ToResult(request.Root, rootDeletableWhenEmpty: false);
    }

    private CleanupWalkResult WalkByExtension(CleanupWalkRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var extensions = new HashSet<string>(request.Names, StringComparer.OrdinalIgnoreCase);
        var collector = new Collector();
        // 文件匹配类不依赖目录修剪：不入册子目录，避免空目录修剪越出本规则的语义边界
        //（例如 ai-trace-logs 遍历整个 ~/.cache 时，模型目录里的空骨架不得被顺手移除）。
        foreach (var entry in EnumerateTree(request.Root, collector, ct, recordSubdirectories: false))
        {
            if (entry.IsDirectory) continue;
            if (!extensions.Contains(Path.GetExtension(entry.Path))) continue;
            AddFiltered(entry, request.Threshold, request.Settings, collector, now);
        }
        return collector.ToResult(request.Root, rootDeletableWhenEmpty: false);
    }

    private CleanupWalkResult WalkMatchingFiles(CleanupWalkRequest request, DateTimeOffset now, CancellationToken ct)
    {
        // 非递归：仅枚举根目录一层（缩略图缓存场景）。
        var collector = new Collector();
        if (!TryListEntries(request.Root, collector, out var entries)) return collector.ToResult(request.Root, rootDeletableWhenEmpty: false);
        foreach (var path in entries.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            Entry entry;
            if (!TryStat(path, collector, out entry)) continue;
            if (entry.IsDirectory) continue;
            if (!request.Names.Any(pattern => MatchesGlob(Path.GetFileName(path), pattern))) continue;
            AddFiltered(entry, request.Threshold, request.Settings, collector, now);
        }
        return collector.ToResult(request.Root, rootDeletableWhenEmpty: false);
    }

    private CleanupWalkResult WalkKeepNewest(CleanupWalkRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var keep = Math.Max(1, request.Settings.BackupKeepCount);
        var matched = new List<Entry>();
        var collector = new Collector();
        // 同 WalkByExtension：keep-N 是文件轮换，不入册子目录、不做空目录修剪。
        foreach (var entry in EnumerateTree(request.Root, collector, ct, recordSubdirectories: false))
        {
            if (entry.IsDirectory) continue;
            if (!request.Names.Any(pattern => MatchesGlob(Path.GetFileName(entry.Path), pattern))) continue;
            // 与其他 walker 路径同一套过滤（AddFiltered 语义）：云占位/受保护文件名不入候选也不参与保留轮换，
            // 否则扫描预览与执行侧数字不一致。reparse 已被 EnumerateTree 拦截。
            if (PathGuard.IsCloudPlaceholder(entry.Attributes)) { collector.Skipped++; continue; }
            if (_protectedFileNames.Contains(Path.GetFileName(entry.Path))) { collector.Skipped++; continue; }
            collector.Skipped++; // 先按「跳过（保留）」计数，进入保留名单的最终保持跳过，落选者改记候选。
            matched.Add(entry);
        }
        foreach (var loser in matched.OrderByDescending(e => e.LastWriteUtc)
                     .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Skip(keep))
        {
            collector.Skipped--;
            collector.Files.Add(new WalkFileCandidate(loser.Path, loser.Bytes, loser.LastWriteUtc, loser.CreatedUtc));
            collector.TotalBytes += loser.Bytes;
        }
        return collector.ToResult(request.Root, rootDeletableWhenEmpty: false);
    }

    private CleanupWalkResult WalkReportSize(CleanupWalkRequest request, CancellationToken ct)
    {
        var collector = new Collector();
        // 只统计：不产生候选也不修剪目录，无需入册子目录。
        foreach (var entry in EnumerateTree(request.Root, collector, ct, recordSubdirectories: false))
        {
            if (entry.IsDirectory) continue;
            collector.TotalBytes += entry.Bytes;
            collector.FileCount++;
        }
        // ReportPath 的语义是「只统计」：ToResult 会带 TotalBytes/FileCount，但 Files 始终为空。
        return collector.ToReportResult(request.Root);
    }

    /// <summary>整目录内容收集：所有文件按阈值过滤为候选，全部子目录入册（深度倒序修剪用）。</summary>
    private void CollectContents(string root, CleanupThresholdKind threshold, CleanupSettings settings,
        Collector collector, DateTimeOffset now, CancellationToken ct, bool recordDirectories = true)
    {
        foreach (var entry in EnumerateTree(root, collector, ct, recordSubdirectories: recordDirectories))
            if (!entry.IsDirectory)
                AddFiltered(entry, threshold, settings, collector, now);
    }

    private void AddFiltered(Entry entry, CleanupThresholdKind threshold, CleanupSettings settings,
        Collector collector, DateTimeOffset now)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { collector.Skipped++; return; }
        if (PathGuard.IsCloudPlaceholder(entry.Attributes)) { collector.Skipped++; return; }
        if (_protectedFileNames.Contains(Path.GetFileName(entry.Path))) { collector.Skipped++; return; }
        if (!MeetsThreshold(threshold, settings, entry.LastWriteUtc, entry.CreatedUtc, now)) { collector.Skipped++; return; }
        collector.Files.Add(new WalkFileCandidate(entry.Path, entry.Bytes, entry.LastWriteUtc, entry.CreatedUtc));
        collector.TotalBytes += entry.Bytes;
    }

    public static bool MeetsThreshold(CleanupThresholdKind kind, CleanupSettings settings,
        DateTimeOffset lastWriteUtc, DateTimeOffset createdUtc, DateTimeOffset now)
    {
        switch (kind)
        {
            case CleanupThresholdKind.None:
                return true;
            case CleanupThresholdKind.Freshness:
                // 取写入/创建时间较新者：刚出生但还没写过的文件同样视为新鲜。
                var newest = lastWriteUtc > createdUtc ? lastWriteUtc : createdUtc;
                return newest <= now - TimeSpan.FromHours(Math.Max(0, settings.FreshnessHours));
            case CleanupThresholdKind.LogAge:
                return lastWriteUtc <= now - TimeSpan.FromDays(Math.Max(0, settings.LogAgeDays));
            case CleanupThresholdKind.ArtifactAge:
                return lastWriteUtc <= now - TimeSpan.FromDays(Math.Max(0, settings.ArtifactAgeDays));
            case CleanupThresholdKind.ConversationAge:
                return lastWriteUtc <= now - TimeSpan.FromDays(Math.Max(0, settings.ConversationAgeDays));
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知阈值种类");
        }
    }

    private sealed record Entry(string Path, bool IsDirectory, FileAttributes Attributes, long Bytes,
        DateTimeOffset LastWriteUtc, DateTimeOffset CreatedUtc);

    private IEnumerable<Entry> EnumerateTree(string root, Collector collector, CancellationToken ct,
        bool recordSubdirectories = true)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            if (!TryListEntries(current, collector, out var entries)) continue;
            foreach (var path in entries.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryStat(path, collector, out var entry)) continue;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue; // 不跟随、不候选
                if (entry.IsDirectory)
                {
                    if (recordSubdirectories) collector.AddDirectory(path);
                    stack.Push(path);
                }
                else yield return entry;
            }
        }
    }

    private static bool TryListEntries(string directory, Collector collector, out string[] entries)
    {
        try { entries = Directory.GetFileSystemEntries(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            collector.Skipped++;
            entries = [];
            return false;
        }
        return true;
    }

    private static bool TryStat(string path, Collector collector, out Entry entry)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                var info = new DirectoryInfo(path);
                entry = new Entry(path, true, attributes, 0, info.LastWriteTimeUtc, info.CreationTimeUtc);
            }
            else
            {
                var info = new FileInfo(path);
                entry = new Entry(path, false, attributes, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            collector.Skipped++;
            entry = new Entry(path, false, 0, 0, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
            return false;
        }
    }

    private sealed class Collector
    {
        public readonly List<WalkFileCandidate> Files = [];
        public readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> DeletableWhenEmpty = new(StringComparer.OrdinalIgnoreCase);
        public long TotalBytes;
        public int FileCount;
        public int DirectoryCount;
        public int Skipped;

        public void AddDirectory(string path)
        {
            if (Directories.Add(path)) DirectoryCount++;
        }

        public CleanupWalkResult ToResult(string root, bool rootDeletableWhenEmpty)
        {
            FileCount = Files.Count;
            var deletable = DeletableWhenEmpty.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (rootDeletableWhenEmpty) deletable.Add(root);
            return new CleanupWalkResult(
                Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                DepthDescending(Directories),
                deletable.ToArray(),
                TotalBytes, FileCount, DirectoryCount, Skipped,
                [new CleanupRootSummary(root, TotalBytes, FileCount)]);
        }

        public CleanupWalkResult ToReportResult(string root) => new(
            [],
            DepthDescending(Directories),
            [],
            TotalBytes, FileCount, DirectoryCount, Skipped,
            [new CleanupRootSummary(root, TotalBytes, FileCount)]);

        /// <summary>深度倒序（先深后浅）+ 路径序：执行器按此顺序修剪空目录即天然自底向上。</summary>
        private static string[] DepthDescending(IEnumerable<string> directories) =>
            directories.OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
