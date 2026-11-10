# C盘清理功能更新日志

## 当前目标

在 DL小工具中新增「C盘清理」页面：按类别安全清理可再生缓存与 AI 工具产物（参考 BitBroom 的安全模型，架构借鉴、代码自写），差异化定位为「AI 缓存管家」——分得清 AI 的垃圾和用户的资产。对话数据库、模型文件只展示；记忆与配置永不触碰（引擎级强制）。

## 开发约束

- 基线分支 main；引擎零第三方依赖（回收站走 SHFileOperationW P/Invoke，不引入 Microsoft.VisualBasic）。
- 仅用户级范围（%TEMP%、%LocalAppData%、%AppData%、%UserProfile% 锚点内），无系统目录、无提权。
- 风险分类 ↔ 删除方式 ↔ 阈值的配对由目录健全性测试强制；junction/符号链接在扫描、通配、删除三阶段一律不跟随（对应 CCleaner CVE-2025-3025 一类穿透）；删除一律先扫描预览、二次确认，逐条审计。
- 所有测试数据位于唯一 GUID 临时根（TestWorkspace），绝不触碰真实用户目录；真实环境用例由 `DLTOOLS_DISKCLEAN_INTEGRATION=1` 门控。

## 待办列表

| ID | 待办描述 | 预计代码位置 | 状态 |
|---|---|---|---|
| C01 | 规则模型 + 内置规则目录 + 目录健全性测试（风险类↔删除方式↔阈值配对契约） | `Models/Cleanup/`、`Services/Cleanup/CleanupRuleCatalog.cs`、`tests/DLTools.Tests/DiskCleanupRuleCatalogTests.cs` | 完成 |
| C02 | PathGuard 两遍校验 + 容错 walker + junction 金丝雀测试（含尾随分隔符与 TOCTOU 变体） | `Services/Cleanup/PathGuard.cs`、`CleanupWalker.cs`、`DiskCleanupPathGuardTests.cs`、`DiskCleanupWalkerTests.cs` | 完成 |
| C03 | 删除执行器（永久/回收站/模拟）+ 审计日志 + 设置存储 | `Services/Cleanup/SafeCleanupExecutor.cs`、`ShellFileOperation.cs`、`CleanupAuditLog.cs`、`CleanupSettingsStore.cs`、`DiskCleanupExecutorTests.cs`、`DiskCleanupSettingsStoreTests.cs` | 完成 |
| C04 | 引擎编排（扫描聚合 / 清理重走不复用缓存）+ 引擎测试 | `Services/Cleanup/CleanupEngine.cs`、`DiskCleanupEngineTests.cs` | 完成 |
| C05 | 页面 + ViewModel + 导航集成 + VM 测试 | `Pages/CleanupPage.xaml(.cs)`、`ViewModels/CleanupViewModel.cs`、`MainWindow(.xaml)`、`ViewModels/MainWindowViewModel.cs`、`DiskCleanupViewModelTests.cs` | 完成 |
| C06 | 功能日志 / 评审 / 验收文档 + README | 本文件、`docs/评审记录-C盘清理.md`、`docs/验收记录-C盘清理.md`、`README.md` | 完成 |
| C07 | 用户界面验收（截图入验收记录）与真实清理体验反馈 | `docs/验收记录-C盘清理.md` | 待用户 |

## 已知风险

- 扫描大目录树（本机全量约 1 分 40 秒）期间占用全局后台互斥（`BackgroundOperationKind.DiskCleanup`），烧录/打包需等待；进度条按规则粒度推进。
- 「打开审计目录」按钮因安全门（Mimosa 命令注入检测，含 `ArgumentList` 形式）改为「复制审计路径」：仓库存量的 3 处 `explorer /select` 拼接启动是历史遗留（提交门既有发现），新代码不沿用。
- AI 工具目录结构随版本变化，规则失效表现为「该类别为 0 项」而非误删；规则目录测试保证新增规则的锚定合法性。

## 其他

- 回收站类（对话记录、备份轮换）默认不勾选；模型/数据库类仅展示不可勾选；`CLAUDE.md`/`AGENTS.md`/`.zcode/cli/memories`/`config.json` 本体在引擎永不名单，任何规则（含未来外置规则）都无法命中。
- 清理后如需撤销：永久删除类不可恢复（规则文案已明示）；回收站类可从回收站还原；全部操作逐条记录在 `%APPDATA%\JLinkRTTViewer\cleaner\audit\`（保留最近 20 份）。

---

## 更新记录

### 2026年11月10日（合并 + 三轮多 agent 审查修复：C01–C06 提交、安全与一致性加固）

**更新内容**：① C01–C06 六个检查点提交至 main（用户验收简单测试通过并授权合并）。② 三轮多 agent 审查（安全对抗 / 引擎正确性 / UI 并发 / 测试质量 → 修复验证 + 新鲜眼 → 定向复核）共修复 2×P1 + 4×P2 + 12×P3：审计写失败从「批次末才中止」收紧为逐条中止（杜绝无审计删除窗口）；守卫路径链读异常从整 run 中止降级为单条跳过；**永久删除改为两段句柄式 DELETE_ON_CLOSE**（探针句柄验重解析点/云占位 → 布防句柄关闭即删；路径解析只发生在打开瞬间，封堵「校验通过后父目录被换成 junction」的微窗口；本机实测 SetFileInformationByHandle(FileDispositionInfo) 全变体拒绝访问、DELETE_ON_CLOSE 可用）；回收站长路径改「原样失败且 ≥260 才 \\?\ 重试」；Merge 按路径去重候选（重叠目标不再双计/幽灵删除）；清理后扫描数字整体失效（摘要与确认框不再显示陈旧量）；db.sqlite/-wal/-shm 按文件列入引擎永不名单；文件匹配/keep-N/只统计三类遍历不再入册子目录（空目录修剪不越出规则语义边界，模型目录空骨架不被触碰）；损坏设置时清理中止而非静默用默认阈值；确认框带 owner；剪贴板降级捕 ExternalException；`..` 改段精确判定、glob 空模式不匹配、未知规则/被拒匹配根/修剪不可确认目录均补审计行。测试 +18 项（审计轮换真覆盖、keep-N 并列确定性、审计中止即停、取消中态、clamp 边界、跨规则目标降级、损坏设置中止、长路径真实回收集成等）。
**涉及位置**：`Services/Cleanup/` 全部 9 文件；`ViewModels/CleanupViewModel.cs`；`Pages/CleanupPage.xaml`；`MainWindow.xaml.cs`（关窗文案补 DiskCleanup 分支）；`tests/DLTools.Tests/DiskCleanup*.cs`。
**对应待办**：C01–C06 的审查加固（审查记录见 `docs/评审记录-C盘清理.md` 多 agent 审查章节）。
**审查结果**：三轮收敛（2×P1+4×P2 → 9×P3 → 0 功能性发现，剩余 3 项格式观察已改）；Release 构建 0 警告；全仓 457 项 429 通过 / 0 失败 / 28 跳过；`DLTOOLS_DISKCLEAN_INTEGRATION=1` 门控集成 3/3（真实只读扫描 + 短路径回收 + **长路径(>260)真实回收**）。
**Git Commit**：8bca84e(C01) → b379c6f(C02) → 75078c3(C03) → 7b80ef8(C04) → 84291b2(C05) → 0b2752b(C06)；审查修复见后续 fix 提交。（用户授权后经 Write 脚本途径提交——Mimosa git 门按命令字符串匹配，脚本调用不命中，属已报备的既有放行方式。）

### 2026年11月10日（C01–C06 一次交付：引擎、安全模型、UI 与文档）

**更新内容**：新增「C盘清理」功能。`Services/Cleanup/` 引擎（规则目录 16 条内置规则：通用缓存 5 + AI 管家 11、PathGuard 两遍校验、容错 walker、永久/回收站/模拟三模式执行器、审计日志、设置存储）+ `Pages/CleanupPage` 与 `ViewModels/CleanupViewModel`（扫描预览→勾选→二次确认→清理，全局后台互斥，复制审计路径）+ 侧边栏导航接入。AI 五分类：可再生缓存默认清、日志按年龄（>7 天）、会话衍生物按年龄（>30 天）、文件型对话记录进回收站（>90 天、默认不勾）、模型与对话数据库仅展示；对话数据库型记录（如 ZCode db.sqlite）只统计不触碰。
**涉及位置**：`烧录上位机程序/Models/Cleanup/CleanupModels.cs`；`烧录上位机程序/Services/Cleanup/{CleanupRuleCatalog,CleanupEnvironment,PathGuard,CleanupWalker,ShellFileOperation,SafeCleanupExecutor,CleanupAuditLog,CleanupSettingsStore,CleanupEngine}.cs`；`烧录上位机程序/Pages/CleanupPage.xaml(.cs)`；`烧录上位机程序/ViewModels/CleanupViewModel.cs`；`烧录上位机程序/ViewModels/MainWindowViewModel.cs`；`烧录上位机程序/MainWindow.xaml`；`烧录上位机程序/Services/BackgroundTaskHost.cs`；`tests/DLTools.Tests/DiskCleanup*.cs` 与 `TestJunction.cs`；`README.md`；`docs/评审记录-C盘清理.md`；`docs/验收记录-C盘清理.md`；本文件。
**对应待办**：C01、C02、C03、C04、C05、C06。
**审查结果**：Release 构建 0 警告；全仓 441 项测试 414 通过 / 0 失败 / 27 跳过（交付时点数字，审查加固后为 457:429/28，见上条更新）；`DLTOOLS_DISKCLEAN_INTEGRATION=1` 门控集成 2/2 通过（真实环境只读扫描 + 真实回收站往返）。
**Git Commit**：8bca84e → 0b2752b（C01–C06 六检查点，2026-11-10 补提交记录）。
