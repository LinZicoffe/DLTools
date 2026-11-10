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

## 多 agent 全面审查（2026-11-10，合并后三轮收敛）

用户验收简单测试通过并授权合并（C01–C06 提交 8bca84e→0b2752b）后，按用户要求启动多 agent 全面审查，**检查出来就修、修完复审，直至零新发现**：

| 轮次 | 代理与视角 | 发现 | 处置 |
|---|---|---|---|
| R1 | 4 并行：安全对抗 / 引擎正确性 / UI 并发 / 测试质量 | 2×P1 + 4×P2 + 约 15×P3 | 全部修复或按依据豁免，测试 +14 |
| R2 | 2 并行：R1 修复逐项验证 / 新鲜眼全链路 | 8/8 修复验证成立；9×P3 新发现 | 7 项修复（含 F2 越界修剪、F1 审计口径），2 项文档化，测试 +2 |
| R3 | 1 定向：R2 修复差异验证 | 7/7 修复验证成立、零功能性回归；3×P3 观察 | 3 项全部落实（F2 断言补强、告警措辞、审计行格式） |

**P1/P2 修复要点**（实现细节见代码注释）：

1. **审计中止粒度（P1）**：原先审计写失败要到批次末 Check 才中止，期间文件照删且无审计行；现执行器/引擎每个写入点（含取消/失败收尾行）写后即 Check，测试 `Execute_AuditWriteFailure_AbortsBeforeAnyDeletion` 锁定「第一条写失败 → 零删除」。
2. **守卫异常中止整 run（P1）**：路径链祖先目录 ACL 拒读会让整次清理失败；现执行器把守卫调用的 IOException/UAE 降级为该条 Skipped("无法校验路径链")，扫描侧降级为规则 Error（既有行为）。
3. **检查-删除微窗口（P2）**：永久删除从 `File.Delete` 改为**两段句柄式**——探针句柄（`OPEN_REPARSE_POINT|BACKUP_SEMANTICS`）读 `BY_HANDLE_FILE_INFORMATION` 验重解析点/云占位 → 布防句柄（`DELETE_ON_CLOSE|OPEN_REPARSE_POINT`）复核后关闭即删。路径解析只发生在打开瞬间，之后父目录被换成 junction 也只可能误删植入的链接本体（`OPEN_REPARSE_POINT` 不穿透目标），绝不穿透删目标数据。**本机实测依据**（Win11 26200）：`SetFileInformationByHandle(FileDispositionInfo)` 全访问掩码/长度/标志变体均拒绝访问（其中 info class 须为 4，误用 6 会得 ERROR_BAD_LENGTH；FILE_DISPOSITION_INFO.Delete 是 1 字节 BOOLEAN），`FILE_FLAG_DELETE_ON_CLOSE` 可用——故采用后者。极端竞态下「已布防后复核失败」按已删除+告警上报，审计与事实一致。
4. **长路径回收（P2）**：SHFileOperationW 对 `\\?\` 前缀兼容性无官方保证，改为原样路径先试、失败且 ≥260 才前缀重试、两次皆败才 fail-closed；门控集成新增 >260 字符真实回收用例（3/3 通过）。
5. **Merge 去重（P2）**：重叠 target 的同一文件原先双计字节且产生幽灵 Deleted 审计；现按路径去重并从去重集重算聚合，ReportPath 直填统计通道单独累加（目录健全性测试新增 ReportPath↔ReportOnly 形状互斥锁）。
6. **清理后陈旧摘要（P2/UI）**：清理完成（成功/取消/失败）后行内扫描数字整体失效，页头摘要与二次确认不再引用已删除内容的「可释放」量。
7. **db 永不名单（P3→防御纵深）**：db.sqlite/-wal/-shm 按文件列入引擎级 ProtectedPaths（目录形态保留给只读统计入口），未来任何规则形状误配都碰不到对话数据库本体。

**R2 新发现中的关键项**：文件匹配（FilesByExtension）/keep-N/只统计三类遍历原先会把整个根的子目录入册，空目录修剪因此可越出规则语义边界（如 ai-trace-logs 遍历 `~/.cache` 时删掉模型目录的空骨架）——三处改 `recordSubdirectories:false` 并以带子目录场景的测试锁定；损坏设置在清理路径改为中止（不静默改用可能更激进的默认阈值）；确认框补 owner；剪贴板降级 catch 修正为 ExternalException。

**已知边界（审查中判定保留并记录依据）**：
- 扫描合计为**上界**：跨规则覆盖区域（如 `.previous-*` 目录同时位于 ai-trace-logs 的 `~/.cache` 树内）会在不同规则各计一次；执行侧因「每规则串行重走」绝不重复删除。
- 空目录修剪会移除名册内「清点时就已为空」的历史空目录——范围限于规则目标之内且修剪前复验，注释与文档如实表述。
- 两段删除在布防后复核发现对象已换的极端竞态下，被移除的是被钉住的对象本体（含被植入的链接），审计按「已删除+告警」如实记录。
- `TestJunction.cs` 的 `WaitForExit` 返回值忽略：该文件被 Mimosa 写入门冻结（mklink 模式 ArgumentList 形式同样被拦，与仓库存量同类），`Directory.Exists` 断言兜底。
- `HasLinkInChain` 每候选 O(链深) 属性查询为纯性能项；keep-N 跨子目录全局计数与散文件备份布局自洽（布局漂移时需改按组）。

## 回归验证（审查加固后）

- `dotnet build "DL小工具.sln" -c Release`：0 警告。
- `dotnet test`：457 项，429 通过 / 0 失败 / 28 跳过（新增 18 项审查加固用例：审计中止即停、轮换真覆盖、keep-N 并列确定性、取消中态、clamp 边界、跨规则目标降级、损坏设置中止、长路径回收等）。
- `DLTOOLS_DISKCLEAN_INTEGRATION=1 --filter DiskCleanupIntegration`：3/3 通过（真实只读扫描 `zcode-session-db` 非零；%TEMP% 短路径真实回收；**>260 字符长路径经原样→\\?\ 重试真实回收**）。
