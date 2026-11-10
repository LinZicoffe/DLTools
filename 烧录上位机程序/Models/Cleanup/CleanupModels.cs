namespace 烧录上位机程序.Models.Cleanup;

/// <summary>清理规则允许锚定的用户级基座。任何候选路径都必须解析到这四个根之下，结构上排除系统目录。</summary>
public enum CleanupAnchor
{
    Temp,
    LocalAppData,
    RoamingAppData,
    UserProfile
}

/// <summary>UI 分组：通用缓存 / AI 缓存管家。</summary>
public enum CleanupGroup
{
    GeneralCache,
    AiSteward
}

/// <summary>风险分类。分类与删除方式的合法配对由规则目录健全性测试强制约束。</summary>
public enum CleanupRiskClass
{
    /// <summary>可再生缓存：删了下次使用会重建/重下。</summary>
    RegenerableCache,
    /// <summary>日志/追踪：按文件年龄清理。</summary>
    AgedLog,
    /// <summary>升级残留的旧版本回滚副本。</summary>
    RollbackCopy,
    /// <summary>AI 工具会话的衍生物（产物目录、执行缓存、转储），非对话本体。</summary>
    SessionArtifact,
    /// <summary>文件型对话记录本体（散装文件，可按年龄清，走回收站）。</summary>
    ConversationArchive,
    /// <summary>工具自身备份的保留轮换（keep-N）。</summary>
    BackupRotation,
    /// <summary>模型文件：用户花带宽下载的资产，仅展示。</summary>
    ModelFile,
    /// <summary>单体数据库型对话记录：活跃数据库，仅展示，绝不删除文件。</summary>
    ConversationDatabase
}

/// <summary>删除方式。ReportOnly 仅统计展示，UI 禁止勾选，引擎也不会产生候选。</summary>
public enum CleanupDeleteMode
{
    Permanent,
    RecycleBin,
    ReportOnly
}

public enum CleanupTargetKind
{
    /// <summary>清理目录内容（按阈值过滤后删除文件，自底向上修剪空目录），目录本身保留。</summary>
    DirectoryContents,
    /// <summary>RelativePath 末段含通配符；每个匹配目录按 DirectoryContents 处理。</summary>
    MatchingRootDirectories,
    /// <summary>遍历根子树，所有名字命中的目录按 DirectoryContents 处理（浏览器/Electron 的 Cache 目录族）。</summary>
    DescendantDirectoriesByName,
    /// <summary>在目录内（不递归）按文件名 glob 匹配候选文件。</summary>
    MatchingFiles,
    /// <summary>遍历根子树，按扩展名（含点）匹配候选文件。</summary>
    FilesByExtension,
    /// <summary>按 glob 匹配文件（递归），按修改时间保留最新 N 份（BackupKeepCount），其余为候选。</summary>
    KeepNewestFiles,
    /// <summary>仅统计大小，不产生任何候选。</summary>
    ReportPath
}

/// <summary>阈值来源：规则引用设置项而不是写死天数，用户调整设置后扫描/清理立即生效。</summary>
public enum CleanupThresholdKind
{
    None,
    Freshness,
    LogAge,
    ArtifactAge,
    ConversationAge
}

/// <summary>单条清理目标：锚点内的相对路径 + 匹配方式。绝对路径解析与安全校验全部由 PathGuard 负责。</summary>
public sealed record CleanupTarget
{
    public CleanupAnchor Anchor { get; init; }
    /// <summary>相对锚点的路径，分隔符用 \；仅 MatchingRootDirectories 允许末段含 *。</summary>
    public string RelativePath { get; init; } = "";
    public CleanupTargetKind Kind { get; init; }
    /// <summary>含义随 Kind 变化：子目录名 / 扩展名（含点）/ 文件名 glob。</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
}

public sealed record CleanupRule
{
    public string Id { get; init; } = "";
    public CleanupGroup Group { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>UI 提示「删除后会发生什么」，如：下次启动会重新下载。</summary>
    public string ConsequenceNote { get; init; } = "";
    public CleanupRiskClass RiskClass { get; init; }
    public CleanupDeleteMode DeleteMode { get; init; }
    public bool DefaultChecked { get; init; }
    public CleanupThresholdKind Threshold { get; init; }
    public IReadOnlyList<CleanupTarget> Targets { get; init; } = [];
}

public enum CleanupStage
{
    Idle,
    ResolvingRules,
    Scanning,
    Cleaning,
    Completed,
    Cancelled,
    Failed
}

public sealed record CleanupProgress(CleanupStage Stage, string Message, int Percent);

/// <summary>扫描阶段单个根目录的聚合（按 OrdinalIgnoreCase 路径排序，帮助 UI 展示 Top 根）。</summary>
public sealed record CleanupRootSummary(string Root, long Bytes, int Files);

public sealed record CleanupRuleSummary(
    string RuleId,
    long TotalBytes,
    int FileCount,
    int DirectoryCount,
    int SkippedCount,
    IReadOnlyList<CleanupRootSummary> Roots,
    string? Error = null);

public sealed record CleanupScanResult(
    IReadOnlyList<CleanupRuleSummary> Summaries,
    bool Cancelled,
    string? Error,
    DateTimeOffset FinishedUtc)
{
    public static CleanupScanResult FromSucceeded(IReadOnlyList<CleanupRuleSummary> summaries, DateTimeOffset finishedUtc) =>
        new(summaries, false, null, finishedUtc);

    public static CleanupScanResult FromCancellation(DateTimeOffset finishedUtc) =>
        new([], true, null, finishedUtc);

    public static CleanupScanResult FromFailure(string error, DateTimeOffset finishedUtc) =>
        new([], false, error, finishedUtc);
}

public enum CleanupDecision
{
    /// <summary>已永久删除。</summary>
    Deleted,
    /// <summary>已送入回收站。</summary>
    Recycled,
    /// <summary>跳过（占用/新鲜度/年龄/云占位/受保护等，原因见 Reason）。</summary>
    Skipped,
    /// <summary>模拟运行：仅记录，未做任何改动。</summary>
    Simulated,
    /// <summary>删除失败（记录原因，不中断整体）。</summary>
    Failed
}

public sealed record CleanupItemOutcome(string RuleId, string Path, long Bytes, bool IsDirectory, CleanupDecision Decision, string? Reason);

public sealed record CleanupResult(
    bool Completed,
    bool Cancelled,
    string? Error,
    long ReclaimedBytes,
    int DeletedCount,
    int RecycledCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<CleanupItemOutcome> Outcomes,
    string AuditLogPath,
    DateTimeOffset FinishedUtc)
{
    public static CleanupResult FromCancellation(string auditLogPath, IReadOnlyList<CleanupItemOutcome> outcomes, DateTimeOffset finishedUtc) =>
        new(false, true, null, 0, 0, 0, 0, 0, outcomes, auditLogPath, finishedUtc);

    public static CleanupResult FromFailure(string error, string auditLogPath, IReadOnlyList<CleanupItemOutcome> outcomes, DateTimeOffset finishedUtc) =>
        new(false, false, error, 0, 0, 0, 0, 0, outcomes, auditLogPath, finishedUtc);
}

/// <summary>清理设置。阈值被规则按 CleanupThresholdKind 引用；数值在持久化层钳制到合法区间。</summary>
public sealed record CleanupSettings
{
    public int FreshnessHours { get; init; } = 24;
    public int LogAgeDays { get; init; } = 7;
    public int ArtifactAgeDays { get; init; } = 30;
    public int ConversationAgeDays { get; init; } = 90;
    public int BackupKeepCount { get; init; } = 3;

    public static CleanupSettings Default { get; } = new();
}
