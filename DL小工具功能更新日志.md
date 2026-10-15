# DL小工具功能更新日志

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
| P01 | 外壳/缓存导航/延迟JLink/任务宿主 | 主窗口、App、FlashWorker、Services | 未开始 |
| P02 | 模型/Job进程执行/工具定位/测试基础 | Models/Packaging、Services/Packaging、tests | 未开始 |
| P03 | 项目识别和配置映射 | Services/Packaging/ProjectInspectionService | 未开始 |
| P04 | 文件事务/模板/profiles | Services/Packaging | 未开始 |
| P05 | 两条构建链和两种安装模式编排 | Services/Packaging/PackagingService | 未开始 |
| P06 | 打包页完整交互 | Pages、ViewModels | 未开始 |
| P07 | 回归/文档/三路最终审查 | tests、docs、README、AGENTS | 未开始 |

## 已知风险

1. 真机烧录、干净目标机安装和缩放验收尚未执行，开发期间逐项记录。
2. Framework入口输出和已有脚本Source输入非空时，需要产品界面明确确认及备份；恢复失败必须可定位。

## 其他

- .zcodeignore、打包教程.md保留未跟踪；AGENTS按计划最终更新。

## 更新记录

### 2026年10月15日（更新1）

**更新内容**：建立隔离分支、批准方案及两轮方案评审记录，锁定P00～P07。

**涉及位置**：docs/实施步骤-DL小工具.md、docs/评审记录-DL小工具.md、当前日志。

**对应待办**：P00。

**审查结果**：通过（第1轮）；采纳Framework支持范围澄清。

**Git Commit**：feat(P00): document approved DL tools implementation plan
