# 评审记录 - C盘清理

- 基线分支：main（在高速下载 F05 合并之后开发）
- 评审日期：2026年11月10日
- 参考项目：[BitBroom](https://github.com/pwnapplehat/BitBroom)（MIT）——安全模型（PathGuard 两遍校验 / junction 不跟随 / 云占位拒删 / 审计与模拟）架构借鉴、代码自写，无代码搬用；.NET 8 适配。

## 安全模型审查（本功能的核心）

三道闸门层层独立，任何一道失效都不足以造成越界删除：

| 闸门 | 位置 | 强制手段 |
|---|---|---|
| 数据层 | `DiskCleanupRuleCatalogTests` | 风险类↔删除方式↔阈值配对契约、锚点内相对路径形状（禁 `..`/绝对路径/非末段通配）、备份 glob 不得匹配 config.json 本体、仅展示规则禁默认勾选 |
| 解析层 | `PathGuard.ResolveTargetRoot` / `IsResolvedRootAllowed` | 四锚点基座白名单（%TEMP%/%LocalAppData%/%AppData%/%UserProfile%）；结构性拒绝（Windows/Program Files/用户根/Desktop/Documents/Downloads/图片视频音乐/OneDrive/自家 JLinkRTTViewer 子树）；锚点根等值拒绝（Temp 除外，通配规则豁免）；永不名单（memories、config.json 本体、CLAUDE.md/settings）；路径逐级重解析点检查 |
| 执行层 | `PathGuard.IsCandidateAllowed` + `SafeCleanupExecutor` | 删除前重读属性（TOCTOU）：重解析点、全链链接（拦截扫描后植入的 junction）、云占位（OFFLINE/RECALL_ON_DATA_ACCESS/RECALL_ON_OPEN）、越锚、永不名单、受保护文件名（任意位置的 CLAUDE.md/AGENTS.md）；目录一律经「清空后修剪」（深度倒序、修剪前复验），杜绝带尾随分隔符删目录的穿透载体 |

## 金丝雀测试（BitBroom 安全套件的对标物）

1. `Walk_JunctionInsideTarget_IsNeverTraversedAndNeverCandidate`（walker 级）：待清理目录内埋指向锚点外金丝雀目录的 junction，候选集必须不含 junction 及其目标内容。
2. `IsCandidateAllowed_JunctionCandidate_IsRejectedIncludingTrailingSeparatorVariant`（守卫级）：直接路径与尾随分隔符变体都必须以「链接或重解析点」拒绝。
3. `Execute_JunctionPlantedAfterScan_IsBlockedBySecondPassAndCanarySurvives`（执行级）：扫描产出候选后、执行前把目录替换为 junction（内含同名文件诱导叶属性可读），全链链接检查必须拦截，金丝雀完好，junction 本体不删。

## 实现评审发现与处置

| # | 发现 | 处置 |
|---|---|---|
| 1 | 初版把 UserProfile 放进子树拒绝列表，会连带拒绝整个锚点（`.zcode` 也在 profile 下） | 改为「锚点根等值拒绝」语义（Temp 例外）；已补回归测试 |
| 2 | `Path.GetFullPath` 不接受含 `*` 的路径，通配规则解析抛异常 | PathGuard 先剥离通配末段、只规范化字面父路径；通配匹配结果逐个 `IsResolvedRootAllowed` 复验 |
| 3 | 匹配出的缓存目录（DescendantDirectoriesByName）若内部子目录不入册，清空后目录永不为空、无法修剪 | 命中目录内部子目录照常入册（含深度倒序），自底向上修剪 |
| 4 | 「打开审计目录」用 `explorer /select,"{path}"` 拼接启动，被 Mimosa 命令注入检测拦截（`ArgumentList` 形式同样被拦） | 改为「复制审计路径」按钮（Clipboard）；仓库存量 3 处同模式为提交门既有发现，新代码不沿用 |
| 5 | `Progress<T>` 回调经线程池投递，取消时序测试不稳定 | 测试内改用同步 `InlineProgress`；VM 生产代码仍用 Dispatcher 投递 |
| 6 | 审计日志写失败若静默会造成「删了但无据可查」 | 沿用打包 RunLog 的 Check 语义：写入失败即抛 IOException 中止清理 |
| 7 | 模拟运行的审计与真实运行同格式 | `simulate` 关键字写入规则头与逐条 `simulated-delete`/`simulated-prune` 前缀，事后可区分 |

## 已知边界（有意的设计取舍）

- **通配/名字匹配目录命中后不再深入**：防止嵌套同名缓存目录重复计数，理论上漏掉「缓存目录套缓存目录」的极罕见布局；宁可漏删不可重复计数。
- **回收站类默认不勾选**：对话记录与备份是用户可能珍视的数据，永久/回收站总量在确认框分列显示。
- **`zcode-session-db` 只统计不清理**：删除活跃 SQLite 会损坏全部对话历史；正确出路是应用内会话管理，UI 文案已指引。
- **扫描为全量即时遍历**（无增量缓存）：保证预览真实性；大目录树耗时由进度条缓解，全量清理前必然重新遍历（两遍校验的意义所在）。
- **外置 JSON 规则目录（C 后续）**：当前 `CleanupRuleCatalog.Rules` 是唯一入口；外置化时必须过同一套 PathGuard + 目录健全性校验（BitBroom 明确拒绝 winapp2 式未审查规则导入，本功能同立场）。

## 回归验证

- `dotnet build "DL小工具.sln" -c Release`：0 警告。
- `dotnet test`：441 项，414 通过 / 0 失败 / 27 跳过（跳过均为各功能集成门控项；新增 59 项清理单元测试全绿）。
- `DLTOOLS_DISKCLEAN_INTEGRATION=1 --filter DiskCleanupIntegration`：2/2 通过（真实环境只读扫描断言 `zcode-session-db` 非零；%TEMP% 小文件经真实 SHFileOperationW 进入回收站、原路径消失）。
