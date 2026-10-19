# DL小工具功能更新日志

## 2026年10月17日：修复烧录日志滚动

- 原因：共享 GroupBox 模板的正文使用 Auto 行，烧录日志随文本无限增高，内部没有可滚动范围，导致手动拖动和 ScrollToEnd 均无效。
- 修复：正文改用星号行，在页面内占用剩余高度；滚动表单中的卡片仍按内容布局。沿用现有新日志自动跟随逻辑。
- 验证：真实 WPF 页面修复前复现日志高度 2308px、可滚动范围 0；修复后烧录和打包两页在 1000×700、1280×800、1920×1080 下通过滑块拖动、顶部/底部滚动、新日志跟随、长行横向滚动及切页保留日志检查（临时验证程序 60 项断言）。打包页未复现该缺陷，无需单独修改。
- Release 解决方案构建通过，0 警告、0 错误。此次未连接硬件或执行真实打包任务；修复保留在 codex/fix-log-scrolling 分支。

## 当前目标

按 docs/实施步骤-DL小工具.md 完成工具外壳与WinForms/WPF项目打包，保留烧录、配置及原工程语义，支持指定输出/包名和可恢复的取消/失败处理。

## 开发约束

1. 隔离分支 codex/dl-tools-packaging，逐步验证/独立审查/定向提交，不自动push/merge。
2. 保留源码目录/namespace/AppData与烧录验证语义；现有未跟踪.zcodeignore/AGENTS.md/打包教程.md不盲目提交。
3. 缺少现场环境的安装/硬件项目单独列未验收，不能由build替代。

## 待办列表

| ID | 待办描述 | 预计代码位置 | 状态 |
|---|---|---|---|
| P00 | 方案与记录/分支 | docs、当前日志 | 已完成 |
| P01 | 外壳/缓存导航/延迟JLink/任务宿主 | 主窗口、App、FlashWorker、Services | 已完成 |
| P02 | 模型/Job进程执行/工具定位/测试基础 | Models/Packaging、Services/Packaging、tests | 已完成 |
| P03 | 项目识别和配置映射 | Services/Packaging/ProjectInspectionService | 已完成 |
| P04 | 文件事务/模板/profiles | Services/Packaging | 已完成 |
| P05 | 两条构建链和两种安装模式编排 | Services/Packaging/PackagingService | 已完成 |
| P06 | 打包页完整交互 | Pages、ViewModels | 已完成 |
| R1 | 修正安装名称引号保真并验证安装生命周期 | InstallerTemplateBuilder、InstallerLifecycleTests | 已完成 |
| P07 | 回归、文档与交付验证 | tests、docs、README、AGENTS | 已完成 |

## 最终全项目审查

通过（收尾复核），无阻塞。原第1轮三个并行Reviewer因交付会话中断未回收结论，改由收尾复核替代完成：Release构建0警告0错误，双开关全量119/119复跑通过、0失败、0跳过；对照批准方案抽查命名/导航/尺寸/三名称分离/两种模式/稳定AppId等需求项，及UI不拼命令、烧录语义与固件解析器零改动、packaging子树隔离等架构约束均符合。真机烧录、干净目标机运行时及150%缩放继续未验收，不宣称通过。

## 已知风险

- P07安装发现的Setup引号显示问题已由R1修复并审查通过；跨进程、管道压力及真实CPU测试已通过独立审查。

1. 真机烧录、干净目标机安装和缩放验收尚未执行，开发期间逐项记录。
2. Framework入口输出和已有脚本Source输入非空时，需要产品界面明确确认及备份；恢复失败必须可定位。

## 其他

- .zcodeignore、打包教程.md保留未跟踪；AGENTS已按计划更新为Repository Guidelines并纳入P07。

## 更新记录

### 2026年10月15日（更新10）

**更新内容**：交付会话在三路最终审查派发后中断，Reviewer结论未回收；收尾复核替代完成最终检查：Release构建0警告0错误，双开关全量测试119/119复跑通过、0失败、0跳过（2分08秒，含真实发布、Inno编译及安装生命周期），并对照批准方案完成需求、架构与风险抽查。用户随后按README流程对实际WPF工程完成打包实测，确认符合要求。最终审查结论如实记录，未验收现场边界不变。

**涉及位置**：当前日志、验收记录。

**对应待办**：P07最终审查收尾。

**审查结果**：通过（收尾复核），无阻塞；原三Reviewer结论未回收已注明。用户实测打包通过；现场硬件、干净目标机和150%缩放仍未验收。

**Git Commit**：docs: finalize delivery with finishing verification

### 2026年10月15日（更新9）

**更新内容**：补充真实跨进程恢复注册竞争、双流大输出及现代x86/Framework x86/x64产物架构和入口验证；更新README、343词AGENTS及构建/安装/UI/现场分开的验收记录，忽略TestResults。

**涉及位置**：tests、README、AGENTS、.gitignore、验收与评审记录。

**对应待办**：P07。

**审查结果**：通过（第1轮），无阻塞；Release零警告错误，双开关全量119/119零跳过，独立压力/跨进程2/2通过并核对最终TRX。现场硬件、干净目标机和150%缩放仍未验收；最终三路项目审查尚待执行。

**Git Commit**：feat(P07): complete packaging regression and contributor documentation（c07575a）

### 2026年10月15日（更新8）

**更新内容**：真实安装发现[Setup]单值误用参数段双引号转义，已拆出SetupValue保留内部引号，参数段继续正确双引号编码。新增双开关真实安装生命周期测试，使用新GUID/独占临时根，验证中文、引号、花括号及ISPP字面值；安装/两次真实启动/同ID改包名升级/注册版本更新/卸载保留用户数据全部通过，并检查清理无残留。

**涉及位置**：InstallerTemplateBuilder、InstallerLifecycleTests、验收与评审记录。

**对应待办**：R1（P07验证中新发现的必要修复）。

**审查结果**：通过（第1轮），无阻塞；生命周期+模板7/7零跳过，独立真实模板6/6零跳过，构建零警告。跨进程/管道草稿未纳入本提交。

**Git Commit**：feat(R1): preserve literal setup metadata and verify installer lifecycle（15244d1）


### 2026年10月15日（更新7）

**更新内容**：完成四步打包页、候选与配置选择、识别说明、工具路径、两种模式、额外资源、配置记忆和实时结果/日志。保护候选对应的配置加载与稳定AppId，源/配置变化禁止旧描述符打包。实测修正只读TextBox双向绑定启动异常、原生关窗确认期间叠加覆盖确认的竞态；新增可取消关窗门闩。中文空格项目从UI真实打包成功、导航缓存、重启配置、1000×700滚动及跨页关窗取消验证通过，旧包SHA不变。

**涉及位置**：PackagingPage、PackagingViewModel、主窗导航/关闭、CloseConfirmationGate及测试、验收记录。

**对应待办**：P06。

**审查结果**：通过（第1轮），无阻塞；独立门闩/宿主6/6通过、零跳过，Release零警告错误。建议后续增加VM状态自动测试；实际窗口记录已保留。

**Git Commit**：feat(P06): deliver packaging workflow UI and coordinated shutdown prompts（a153e61）


### 2026年10月15日（更新6）

**更新内容**：现代dotnet restore/publish、Framework VS MSBuild Restore/Rebuild、完整收集及两种Inno模式串联；全组目录预检、实时阶段、取消整组退出后逆序恢复，最终包提交才提交目录。修正真实ISCC帮助退出码1，新增有效ISPP规则检查以拒绝外部附属内容，并统一识别/构建解决方案属性，保护条件TargetDir及外部引用平台映射。5类真实项目构建/ISCC矩阵通过；最新回归109/109（长矩阵此前通过）零跳过。独立最终针对检查14/14通过。

**涉及位置**：PackagingService、SolutionBuildContext、ExistingScriptValidator、ProjectInspectionService、BuildToolLocator与对应测试。

**对应待办**：P05。

**审查结果**：通过（第3轮）；第1轮external侧载漏检、第2轮评估/构建上下文不一致均已修。已有脚本须支持ISPP有效规则导出，无法验证时明确拒绝；展开内容不记录日志。

**Git Commit**：feat(P05): orchestrate desktop builds and safe installer compilation（a1f6630）


### 2026年10月15日（更新5）

**更新内容**：实现安全路径和资源映射、可恢复目录事务、跨卷暂存校验与同卷原子提交、Inno模板与稳定AppId配置。修复恢复记录只保护完全相同目录的漏洞：命名mutex原子扫描/注册，保护目标、备份及失败输出的父子路径，损坏记录保守阻止。全量81/81及新增6/6通过；独立P04复跑55/55和新增6/6，真实Inno启用，零跳过、零警告。

**涉及位置**：Services/Packaging文件保护、模板、profile服务及对应测试。

**对应待办**：P04。

**审查结果**：通过（第2轮）；第1轮目录重叠阻塞已修。真实跨进程并发回归列P07。

**Git Commit**：feat(P04): protect packaging files and persist installer identity（c4e9293）


### 2026年10月15日（更新4）

**更新内容**：真实MSBuild评估SDK/legacy Framework WPF/Forms；安全发现目录/sln/csproj、Release映射、多TFM独立候选、CPU/RID、开发包诊断。修正Framework有效EXE名称、单TFM失败丢失其他目标、CLR实现目录误作DeveloperPack。vswhere UTF8与损坏发现工具降级。独立复审Release识别14/14、完整26/26通过零跳过。

**涉及位置**：ProjectInspectionService、SolutionProjectReader、PackagingModels、BuildToolLocator、ProjectFixtures/ProjectInspectionTests。

**对应待办**：P03。

**审查结果**：通过（第2轮），2个审查阻塞和1个父验证边界已修；实际restore/build依赖仍由P05验证。

**Git Commit**：feat(P03): inspect effective desktop project metadata and solution mappings（d37736d）



### 2026年10月15日（更新3）

**更新内容**：新增打包模型、工具定位/版本诊断、原子Job归组无窗口执行器、双流日志、取消整树等待和xUnit基础。Release全量12/12通过、0跳过；父先退出后子持锁取消加强用例另通过1/1；独立审查复跑全量12/12通过。

**涉及位置**：Models/Packaging、Services/Packaging/BuildToolLocator、ExternalProcessRunner、WindowsJobNative、tests。

**对应待办**：P02。

**审查结果**：通过（第1轮），无阻塞。

**Git Commit**：feat(P02): add job-contained process runner and packaging tool diagnostics（236b013）



### 2026年10月15日（更新2）

**更新内容**：统一产品/工程名，缓存两页导航；J-Link延迟至烧录并释放；真实Task终态全局互斥与跨页关窗；表单有界滚动。修正确认框期间Task完成导致的重入Close，最终关闭排入Dispatcher。Release 0警告0错误，宿主并发取消100×5及终态检查通过，启动标题正确/JLink模块0/正常关闭。

**涉及位置**：DL小工具.sln、主工程、MainWindow、App、FlashWorker、BackgroundTaskHost、FlashPage与PackagingPage。

**对应待办**：P01。

**审查结果**：通过（第2轮）；第1轮关窗重入阻塞已修。硬件/150%缩放待最终现场验收。

**Git Commit**：feat(P01): add DL tools shell and safe background task ownership（74459db）



### 2026年10月15日（更新1）

**更新内容**：建立隔离分支、批准方案及两轮方案评审记录，锁定P00～P07。

**涉及位置**：docs/实施步骤-DL小工具.md、docs/评审记录-DL小工具.md、当前日志。

**对应待办**：P00。

**审查结果**：通过（第1轮）；采纳Framework支持范围澄清。

**Git Commit**：feat(P00): document approved DL tools implementation plan（8a66dda）
