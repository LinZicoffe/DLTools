using 烧录上位机程序.Models.Cleanup;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>内置清理规则目录。规则只描述「锚点内的相对目标」，绝对路径解析与安全校验全部由 PathGuard 负责，
/// 因此规则数据本身不可能表达系统目录或锚点之外的路径。种子规则来自本机实测（2026-11）：
/// .zcode 5.7G（artifacts 828M / db.sqlite 1.4G）、%LocalAppData%\*-updater 3.9G、Claude vm_bundles 10G、
/// codex 追踪日志 16.6G。目录健全性约束由 DiskCleanupRuleCatalogTests 强制（风险类 ↔ 删除方式 ↔ 阈值的合法配对）。</summary>
public static class CleanupRuleCatalog
{
    private const CleanupThresholdKind Fresh = CleanupThresholdKind.Freshness;
    private const CleanupThresholdKind LogAge = CleanupThresholdKind.LogAge;
    private const CleanupThresholdKind ArtifactAge = CleanupThresholdKind.ArtifactAge;
    private const CleanupThresholdKind ConversationAge = CleanupThresholdKind.ConversationAge;
    private const CleanupThresholdKind NoThreshold = CleanupThresholdKind.None;

    private static readonly string[] BrowserCacheNames =
        ["Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache"];

    private static readonly string[] ElectronCacheNames =
        ["Cache", "GPUCache", "Code Cache", "CachedData", "workspace_indexing"];

    /// <summary>v1 内置规则。加载外部规则的入口预留（届时仍必须通过 PathGuard 与目录健全性同一套校验）。</summary>
    public static IReadOnlyList<CleanupRule> Rules => BuiltIn;

    private static readonly IReadOnlyList<CleanupRule> BuiltIn =
    [
        new CleanupRule
        {
            Id = "user-temp",
            Group = CleanupGroup.GeneralCache,
            DisplayName = "用户临时目录",
            Description = "当前用户的 %TEMP% 目录内容。",
            ConsequenceNote = "正在运行的安装程序/程序可能占用其中文件（自动跳过），其余删除后不影响系统。",
            RiskClass = CleanupRiskClass.RegenerableCache,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = Fresh,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.Temp, Kind = CleanupTargetKind.DirectoryContents }]
        },
        new CleanupRule
        {
            Id = "browser-cache-chromium",
            Group = CleanupGroup.GeneralCache,
            DisplayName = "Chromium 系浏览器缓存",
            Description = "Chrome/Edge/Brave 各 profile 的 Cache、Code Cache、GPU/Shader 缓存。",
            ConsequenceNote = "首次访问网站会稍慢（缓存重建）；不影响登录状态、历史与密码。",
            RiskClass = CleanupRiskClass.RegenerableCache,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = Fresh,
            Targets =
            [
                new CleanupTarget { Anchor = CleanupAnchor.LocalAppData, RelativePath = @"Google\Chrome\User Data", Kind = CleanupTargetKind.DescendantDirectoriesByName, Names = BrowserCacheNames },
                new CleanupTarget { Anchor = CleanupAnchor.LocalAppData, RelativePath = @"Microsoft\Edge\User Data", Kind = CleanupTargetKind.DescendantDirectoriesByName, Names = BrowserCacheNames },
                new CleanupTarget { Anchor = CleanupAnchor.LocalAppData, RelativePath = @"BraveSoftware\Brave-Browser\User Data", Kind = CleanupTargetKind.DescendantDirectoriesByName, Names = BrowserCacheNames }
            ]
        },
        new CleanupRule
        {
            Id = "thumbnail-cache",
            Group = CleanupGroup.GeneralCache,
            DisplayName = "资源管理器缩略图缓存",
            Description = "thumbcache_*.db / iconcache_*.db 缩略图与图标缓存。",
            ConsequenceNote = "再次浏览图片文件夹时缩略图会重新生成，首次浏览略慢。",
            RiskClass = CleanupRiskClass.RegenerableCache,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = Fresh,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.LocalAppData, RelativePath = @"Microsoft\Windows\Explorer", Kind = CleanupTargetKind.MatchingFiles, Names = ["thumbcache_*.db", "iconcache_*.db"] }]
        },
        new CleanupRule
        {
            Id = "crash-dumps",
            Group = CleanupGroup.GeneralCache,
            DisplayName = "应用崩溃转储",
            Description = "%LocalAppData%\\CrashDumps 下的用户级崩溃转储（.dmp）。",
            ConsequenceNote = "仅影响事后排查崩溃的能力；正在被占用分析的新转储会自动跳过。",
            RiskClass = CleanupRiskClass.AgedLog,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = LogAge,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.LocalAppData, RelativePath = "CrashDumps", Kind = CleanupTargetKind.DirectoryContents }]
        },
        new CleanupRule
        {
            Id = "windows-error-reports",
            Group = CleanupGroup.GeneralCache,
            DisplayName = "Windows 错误报告队列",
            Description = "%LocalAppData%\\Microsoft\\Windows\\WER 下的报告队列与归档。",
            ConsequenceNote = "删除已上报/未上报的错误报告，不影响系统稳定性。",
            RiskClass = CleanupRiskClass.AgedLog,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = LogAge,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.LocalAppData, RelativePath = @"Microsoft\Windows\WER", Kind = CleanupTargetKind.DirectoryContents }]
        },
        new CleanupRule
        {
            Id = "ai-updater-caches",
            Group = CleanupGroup.AiSteward,
            DisplayName = "AI/桌面工具更新缓存（*-updater）",
            Description = "%LocalAppData% 下 Electron 应用（cursor、comfyui、tracememo、qoder 等）的更新器缓存目录。",
            ConsequenceNote = "下次检查更新时会重新下载安装包；不影响已安装的应用本体。",
            RiskClass = CleanupRiskClass.RegenerableCache,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = Fresh,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.LocalAppData, RelativePath = "*-updater", Kind = CleanupTargetKind.MatchingRootDirectories }]
        },
        new CleanupRule
        {
            Id = "ai-electron-caches",
            Group = CleanupGroup.AiSteward,
            DisplayName = "AI 桌面工具缓存（Cursor/Claude 等）",
            Description = "已知 AI/Electron 工具数据目录里的 Cache、GPUCache、Code Cache、workspace_indexing。",
            ConsequenceNote = "首次启动与首次打开项目会稍慢（索引重建）；不删除配置、登录与扩展。",
            RiskClass = CleanupRiskClass.RegenerableCache,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = Fresh,
            Targets =
            [
                new CleanupTarget { Anchor = CleanupAnchor.RoamingAppData, RelativePath = "Cursor", Kind = CleanupTargetKind.DescendantDirectoriesByName, Names = ElectronCacheNames },
                new CleanupTarget { Anchor = CleanupAnchor.RoamingAppData, RelativePath = "Code", Kind = CleanupTargetKind.DescendantDirectoriesByName, Names = ElectronCacheNames },
                new CleanupTarget { Anchor = CleanupAnchor.RoamingAppData, RelativePath = "Claude", Kind = CleanupTargetKind.DescendantDirectoriesByName, Names = ElectronCacheNames },
                new CleanupTarget { Anchor = CleanupAnchor.LocalAppData, RelativePath = "aionui", Kind = CleanupTargetKind.DescendantDirectoriesByName, Names = ElectronCacheNames },
                new CleanupTarget { Anchor = CleanupAnchor.RoamingAppData, RelativePath = "aionui", Kind = CleanupTargetKind.DescendantDirectoriesByName, Names = ElectronCacheNames }
            ]
        },
        new CleanupRule
        {
            Id = "claude-vm-bundles",
            Group = CleanupGroup.AiSteward,
            DisplayName = "Claude 桌面版虚拟机镜像缓存",
            Description = "Claude 桌面版每次升级都会重新下载的 vm_bundles 镜像（本机实测累计可达 10GB）。",
            ConsequenceNote = "下次启动 Claude 时会自动重新下载所需镜像，期间无法立即使用。",
            RiskClass = CleanupRiskClass.RegenerableCache,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = Fresh,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.RoamingAppData, RelativePath = @"Claude\vm_bundles", Kind = CleanupTargetKind.DirectoryContents }]
        },
        new CleanupRule
        {
            Id = "codex-previous-runtimes",
            Group = CleanupGroup.AiSteward,
            DisplayName = "Codex 旧版运行时副本",
            Description = "~\\.cache\\codex-runtimes 下升级残留的 .previous-* / codex-runtime-install-* 目录。",
            ConsequenceNote = "仅删除已被新版本取代的副本；正在使用的运行时文件会被系统锁定而自动跳过。",
            RiskClass = CleanupRiskClass.RollbackCopy,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = NoThreshold,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".cache\codex-runtimes\.previous-*", Kind = CleanupTargetKind.MatchingRootDirectories },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".cache\codex-runtimes\codex-runtime-install-*", Kind = CleanupTargetKind.MatchingRootDirectories }]
        },
        new CleanupRule
        {
            Id = "ai-trace-logs",
            Group = CleanupGroup.AiSteward,
            DisplayName = "AI 工具追踪日志（.etl/.nettrace）",
            Description = "~\\.codex 与 ~\\.cache 下的 .etl / .nettrace 追踪文件（本机实测曾累计 16.6GB）。",
            ConsequenceNote = "仅删除性能追踪文件；正在写入的追踪会话文件被锁定而自动跳过。",
            RiskClass = CleanupRiskClass.AgedLog,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = LogAge,
            Targets =
            [
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = ".codex", Kind = CleanupTargetKind.FilesByExtension, Names = [".etl", ".nettrace"] },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = ".cache", Kind = CleanupTargetKind.FilesByExtension, Names = [".etl", ".nettrace"] }
            ]
        },
        new CleanupRule
        {
            Id = "zcode-session-artifacts",
            Group = CleanupGroup.AiSteward,
            DisplayName = "ZCode 会话产物与缓存",
            Description = "~\\.zcode\\cli 下会话衍生的 artifacts/rollout/exec/image-cache/pdf-cache/log（本机实测 828M）。",
            ConsequenceNote = "删除历史会话的产物快照与缓存；对话本体（数据库）与记忆（memories）不在本规则内、受引擎保护。",
            RiskClass = CleanupRiskClass.SessionArtifact,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = ArtifactAge,
            Targets =
            [
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\cli\artifacts", Kind = CleanupTargetKind.DirectoryContents },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\cli\rollout", Kind = CleanupTargetKind.DirectoryContents },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\cli\exec", Kind = CleanupTargetKind.DirectoryContents },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\cli\image-cache", Kind = CleanupTargetKind.DirectoryContents },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\cli\pdf-cache", Kind = CleanupTargetKind.DirectoryContents },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\cli\log", Kind = CleanupTargetKind.DirectoryContents }
            ]
        },
        new CleanupRule
        {
            Id = "zcode-dumps",
            Group = CleanupGroup.AiSteward,
            DisplayName = "ZCode 诊断转储",
            Description = "~\\.zcode\\dumps 下的钩子/审查报告等诊断产物（本机实测 73M）。",
            ConsequenceNote = "删除历史诊断报告；不影响程序运行与配置。",
            RiskClass = CleanupRiskClass.SessionArtifact,
            DeleteMode = CleanupDeleteMode.Permanent,
            DefaultChecked = true,
            Threshold = ArtifactAge,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\dumps", Kind = CleanupTargetKind.DirectoryContents }]
        },
        new CleanupRule
        {
            Id = "ai-conversation-files",
            Group = CleanupGroup.AiSteward,
            DisplayName = "文件型 AI 对话记录（>90 天）",
            Description = "~\\.claude\\projects 等散装会话转录文件（.jsonl）。数据库型对话记录（如 ZCode）不在此列、仅展示。",
            ConsequenceNote = "删除 90 天前的历史对话转录，删除后无法恢复（送回收站可短时找回）；记忆文件（CLAUDE.md/AGENTS.md/memories）受引擎保护、永不在候选内。",
            RiskClass = CleanupRiskClass.ConversationArchive,
            DeleteMode = CleanupDeleteMode.RecycleBin,
            DefaultChecked = false,
            Threshold = ConversationAge,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".claude\projects", Kind = CleanupTargetKind.FilesByExtension, Names = [".jsonl"] }]
        },
        new CleanupRule
        {
            Id = "ai-backup-rotation",
            Group = CleanupGroup.AiSteward,
            DisplayName = "AI 工具备份轮换（保留最新 3 份）",
            Description = "~\\.zcode\\backup 与 ~\\.zcode\\cli\\config.json.bak-* 等备份文件，按修改时间保留最新 N 份。",
            ConsequenceNote = "删除较旧的自动备份（送回收站）；当前使用的 config.json 本体受引擎保护、绝不删除。",
            RiskClass = CleanupRiskClass.BackupRotation,
            DeleteMode = CleanupDeleteMode.RecycleBin,
            DefaultChecked = false,
            Threshold = NoThreshold,
            Targets =
            [
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\backup", Kind = CleanupTargetKind.KeepNewestFiles, Names = ["*"] },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\cli", Kind = CleanupTargetKind.KeepNewestFiles, Names = ["config.json.bak-*", "config.json.rea.backup"] }
            ]
        },
        new CleanupRule
        {
            Id = "ai-models",
            Group = CleanupGroup.AiSteward,
            DisplayName = "AI 模型文件（仅展示）",
            Description = "~\\.ollama\\models、~\\.cache\\huggingface、~\\.cache\\modelscope 的本地模型库。",
            ConsequenceNote = "模型是你花带宽下载的资产：本工具只统计大小，不提供删除。",
            RiskClass = CleanupRiskClass.ModelFile,
            DeleteMode = CleanupDeleteMode.ReportOnly,
            DefaultChecked = false,
            Threshold = NoThreshold,
            Targets =
            [
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".ollama\models", Kind = CleanupTargetKind.ReportPath },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".cache\huggingface", Kind = CleanupTargetKind.ReportPath },
                new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".cache\modelscope", Kind = CleanupTargetKind.ReportPath }
            ]
        },
        new CleanupRule
        {
            Id = "zcode-session-db",
            Group = CleanupGroup.AiSteward,
            DisplayName = "ZCode 对话数据库（仅展示）",
            Description = "~\\.zcode\\cli\\db\\db.sqlite：全部对话记录存于这一个活跃数据库（本机实测 1.4G，含 -wal/-shm）。",
            ConsequenceNote = "外部删除正在使用的数据库会损坏全部历史，因此只统计大小；请通过应用内的会话管理功能整理。",
            RiskClass = CleanupRiskClass.ConversationDatabase,
            DeleteMode = CleanupDeleteMode.ReportOnly,
            DefaultChecked = false,
            Threshold = NoThreshold,
            Targets = [new CleanupTarget { Anchor = CleanupAnchor.UserProfile, RelativePath = @".zcode\cli\db", Kind = CleanupTargetKind.ReportPath }]
        }
    ];
}
