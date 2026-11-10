using System.IO;
using 烧录上位机程序.Models.Cleanup;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>清理功能可见的用户级环境：四个锚点基座的绝对路径、结构性拒绝根、永不名单。
/// 默认实现取自当前进程环境；测试注入位于临时根下的假环境，绝不触碰真实目录。</summary>
public sealed record CleanupEnvironment
{
    /// <summary>任意位置命中文件名即受保护（用户资产，与所在目录无关）。</summary>
    public static readonly IReadOnlyList<string> DefaultProtectedFileNames = ["CLAUDE.md", "AGENTS.md"];

    public string Temp { get; init; } = "";
    public string LocalAppData { get; init; } = "";
    public string RoamingAppData { get; init; } = "";
    public string UserProfile { get; init; } = "";
    /// <summary>结构性拒绝根：目标落在这些根之下（或等于）即拒绝。不包含盘根——「必须在锚点之内」天然排除盘根；
    /// 也不包含用户根本身——用户根的等值拒绝由 PathGuard 的锚点根规则处理，否则会连带拒绝整个 UserProfile 锚点。</summary>
    public IReadOnlyList<string> RejectedRoots { get; init; } = [];
    /// <summary>永不名单：绝对路径（目录或文件），任何规则目标与候选都不得命中。</summary>
    public IReadOnlyList<string> ProtectedPaths { get; init; } = [];
    public IReadOnlyList<string> ProtectedFileNames { get; init; } = DefaultProtectedFileNames;

    public static CleanupEnvironment Default { get; } = CreateDefault();

    private static CleanupEnvironment CreateDefault()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

        var rejected = new List<string>();
        void Reject(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { rejected.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))); }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { }
        }
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Reject(Path.Combine(profile, "Downloads"));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Reject(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Reject(Environment.GetEnvironmentVariable("OneDrive"));
        Reject(Path.Combine(roaming, "JLinkRTTViewer")); // 本程序自有子树不参与清理

        var protectedPaths = new List<string>
        {
            Path.Combine(profile, ".zcode", "cli", "memories"),
            Path.Combine(profile, ".zcode", "cli", "config.json"),
            // 单体数据库型对话记录本体（db.sqlite 及活跃 -wal/-shm）按文件列入永不名单：
            // 不依赖「仅展示规则」的间接保护，未来任何规则误把它们变成候选都会被守卫直接拒绝；
            // 只保护文件而不保护 db 目录本身，zcode-session-db 的只读统计（ReportPath）仍可走目录入口。
            Path.Combine(profile, ".zcode", "cli", "db", "db.sqlite"),
            Path.Combine(profile, ".zcode", "cli", "db", "db.sqlite-wal"),
            Path.Combine(profile, ".zcode", "cli", "db", "db.sqlite-shm"),
            Path.Combine(profile, ".claude", "CLAUDE.md"),
            Path.Combine(profile, ".claude", "settings.json")
        };

        return new CleanupEnvironment
        {
            Temp = temp,
            LocalAppData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(local)),
            RoamingAppData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(roaming)),
            UserProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)),
            RejectedRoots = rejected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProtectedPaths = protectedPaths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToArray()
        };
    }

    public string AnchorRoot(CleanupAnchor anchor) => anchor switch
    {
        CleanupAnchor.Temp => Temp,
        CleanupAnchor.LocalAppData => LocalAppData,
        CleanupAnchor.RoamingAppData => RoamingAppData,
        CleanupAnchor.UserProfile => UserProfile,
        _ => throw new ArgumentOutOfRangeException(nameof(anchor), anchor, "未知锚点")
    };

    /// <summary>标准化为无尾随分隔符的绝对路径。空/非法路径返回 null。</summary>
    public static string? TryNormalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return null; }
    }

    public static bool IsWithinOrEqual(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
