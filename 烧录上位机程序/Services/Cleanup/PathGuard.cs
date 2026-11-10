using System.IO;
using 烧录上位机程序.Models.Cleanup;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>清理安全模型的中枢（参考 BitBroom PathGuard 的两遍校验设计，架构借鉴、代码自写）：
/// 第一遍在规则目标解析时拒绝越界；第二遍在每次删除前对具体路径重验。
/// 链接/重解析点在解析与删除两遍都被拒绝（对应 CCleaner CVE-2025-3025 的 junction 逃逸类缺陷）。</summary>
public sealed class PathGuard
{
    // 云占位属性（OneDrive 按需文件）：删除占位会删除云端副本，一律拒绝。
    private const int FileAttributeRecallOnDataAccess = 0x00400000;
    private const int FileAttributeRecallOnOpen = 0x00040000;

    private readonly CleanupEnvironment _environment;
    private readonly string[] _anchorRoots;

    public PathGuard(CleanupEnvironment? environment = null)
    {
        _environment = environment ?? CleanupEnvironment.Default;
        _anchorRoots = new[]
        {
            _environment.Temp, _environment.LocalAppData, _environment.RoamingAppData, _environment.UserProfile
        }.Select(CleanupEnvironment.TryNormalize).Where(p => p is not null).Select(p => p!).ToArray();
    }

    public CleanupEnvironment Environment => _environment;

    /// <summary>规则目标解析（第一遍校验）。返回枚举根：普通相对路径即目标根；
    /// 末段含通配符时返回其父目录（匹配由调用方执行，且每个匹配结果仍需 IsResolvedRootAllowed 复验）。
    /// 抛 IOException 表示该规则目标非法，调用方应跳过整条规则并记录原因。</summary>
    public string ResolveTargetRoot(CleanupAnchor anchor, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new IOException($"规则相对路径不能是绝对路径：{relativePath}");
        if (relativePath.Contains("..", StringComparison.Ordinal))
            throw new IOException($"规则相对路径不允许包含 ..：{relativePath}");

        var anchorRoot = CleanupEnvironment.TryNormalize(_environment.AnchorRoot(anchor))
            ?? throw new IOException($"锚点基座不可用：{anchor}");
        // 通配符只允许出现在末段（目录发现）：只规范化字面父路径，通配段由调用方执行匹配。
        var segments = relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var hasWildcard = segments.Length > 0 && segments[^1].Contains('*', StringComparison.Ordinal);
        var literalRelative = hasWildcard ? string.Join('\\', segments[..^1]) : relativePath;
        var normalized = CleanupEnvironment.TryNormalize(string.IsNullOrEmpty(literalRelative)
            ? anchorRoot
            : Path.Combine(anchorRoot, literalRelative))
            ?? throw new IOException($"规则目标路径非法：{relativePath}");

        if (!CleanupEnvironment.IsWithinOrEqual(normalized, anchorRoot))
            throw new IOException($"规则目标越出锚点基座：{normalized}");
        // 锚点根等值拒绝：除了 Temp（语义就是清空临时目录），规则不得把整个锚点基座当作目标。
        // 通配规则不受此限——它的枚举根是锚点根，实际清理对象只是匹配出的子目录。
        if (!hasWildcard && normalized.Equals(anchorRoot, StringComparison.OrdinalIgnoreCase) && anchor != CleanupAnchor.Temp)
            throw new IOException($"规则目标不得是锚点根目录本身：{normalized}");
        foreach (var rejected in _environment.RejectedRoots)
            if (CleanupEnvironment.IsWithinOrEqual(normalized, rejected))
                throw new IOException($"规则目标落在结构拒绝区（{rejected}）：{normalized}");
        foreach (var protectedPath in _environment.ProtectedPaths)
            if (CleanupEnvironment.IsWithinOrEqual(normalized, protectedPath))
                throw new IOException($"规则目标命中永不名单（{protectedPath}）：{normalized}");
        if (HasLinkInChain(normalized))
            throw new IOException($"规则目标路径包含链接或重解析点：{normalized}");

        // 含通配符时 normalized 即匹配父目录（枚举根）；否则即目标根本身。
        return normalized;
    }

    /// <summary>通配符匹配出的目录根逐个复验：返回 false 表示该匹配结果被跳过（不使整条规则失败）。</summary>
    public bool IsResolvedRootAllowed(string directory)
    {
        var normalized = CleanupEnvironment.TryNormalize(directory);
        if (normalized is null) return false;
        if (!IsUnderAnyAnchor(normalized)) return false;
        foreach (var rejected in _environment.RejectedRoots)
            if (CleanupEnvironment.IsWithinOrEqual(normalized, rejected)) return false;
        foreach (var protectedPath in _environment.ProtectedPaths)
            if (CleanupEnvironment.IsWithinOrEqual(normalized, protectedPath)) return false;
        if (HasLinkInChain(normalized)) return false;
        return true;
    }

    /// <summary>删除前二次校验（第二遍）。此刻重新读取属性（TOCTOU），任何一项不满足都返回 false，调用方跳过该条目。</summary>
    public bool IsCandidateAllowed(string path, out string? reason)
    {
        reason = null;
        var normalized = CleanupEnvironment.TryNormalize(path);
        if (normalized is null) { reason = "路径非法"; return false; }

        FileAttributes attributes;
        try { attributes = File.GetAttributes(normalized); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "无法读取属性";
            return false;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0) { reason = "链接或重解析点"; return false; }
        if (!IsUnderAnyAnchor(normalized)) { reason = "越出锚点基座"; return false; }
        foreach (var rejected in _environment.RejectedRoots)
            if (CleanupEnvironment.IsWithinOrEqual(normalized, rejected)) { reason = $"结构拒绝区（{rejected}）"; return false; }
        foreach (var protectedPath in _environment.ProtectedPaths)
            if (CleanupEnvironment.IsWithinOrEqual(normalized, protectedPath)) { reason = "永不名单"; return false; }
        if (IsProtectedFileName(normalized)) { reason = "受保护文件名"; return false; }
        if (IsCloudPlaceholder(attributes)) { reason = "云占位文件"; return false; }
        // 全链链接复验：封堵「扫描之后、删除之前」目录树里被植入 junction 的 TOCTOU 场景。
        if (HasLinkInChain(normalized)) { reason = "路径链包含链接"; return false; }
        return true;
    }

    /// <summary>云占位判断与新鲜度/年龄谓词供 walker 复用（同一套语义）。</summary>
    public static bool IsCloudPlaceholder(FileAttributes attributes) =>
        (attributes & FileAttributes.Offline) != 0 ||
        ((int)attributes & FileAttributeRecallOnDataAccess) != 0 ||
        ((int)attributes & FileAttributeRecallOnOpen) != 0;

    private bool IsProtectedFileName(string normalized) =>
        _environment.ProtectedFileNames.Contains(Path.GetFileName(normalized), StringComparer.OrdinalIgnoreCase);

    private bool IsUnderAnyAnchor(string normalized) => _anchorRoots.Any(anchor =>
        CleanupEnvironment.IsWithinOrEqual(normalized, anchor));

    private static bool HasLinkInChain(string normalized)
    {
        var current = normalized;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
        return false;
    }
}
