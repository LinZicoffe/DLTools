# 离线授权 DEMO 功能更新日志

## 当前目标

在 DEMO 下提供 .NET 8 WinForms 和 WPF 客户验证端，与现有六位离线授权生成器互通。使用公开测试档案，演示申请、激活、保存和错误限次，不修改正式授权档案或主程序协议。

## 开发约束

- 日志滚动修复已于 2026-10-19 提交为 45e1b5e，并按用户要求快进合并到 main。
- DEMO 在 codex/offline-authorization-demos 开发；保留原有未跟踪图标、教程及 .zcodeignore。不推送。
- 沿用用户对离线授权功能的简化要求，不另写实施方案或执行多轮 Agent 评审；记录实际验证及未验收边界。

## 待办列表

| ID | 内容 | 位置 | 状态 |
|---|---|---|---|
| D01 | 共享验证状态、公开档案导出及自动化测试 | DEMO/OfflineAuthorization.Demo.Core、DEMO/OfflineAuthorization.Demo.Tests | 已完成 |
| D02 | WinForms/WPF 窗口、说明、构建与界面验证 | DEMO/OfflineAuthorization.WinForms、DEMO/OfflineAuthorization.Wpf、DEMO/README.md | 已完成 |

## 已知风险

- 公开测试密钥和测试重置入口不能用于真实交付，无法抵抗调试、删除状态或快照回滚。
- 真实客户预置密钥及重装后的密钥恢复不在本次 DEMO 范围。

## 更新记录

### 2026年10月19日（更新3）

用户反馈“测试发现，没什么问题，可以合并到主分支”，确认两套 DEMO 的本轮测试无问题并授权合并到 main，未授权推送。此前“授权码错误”已定位为生成器未导入 DEMO 公开测试档案、使用另一设备档案计算；用公开测试档案生成响应后，在两份实际状态的副本上均验证通过，未修改真实档案或实际激活状态。用户随后按说明测试并确认。

本次只补充验收及合并授权记录，没有修改代码，沿用更新2的构建、自动化及窗口验证结果，不重复执行或扩大未验收事项。记录提交说明：`docs: record user acceptance of offline authorization demos`；随后快进合并到 main，具体哈希与分支状态以 Git 记录为准。原有未跟踪文件、忽略的本地发布文件和测试档案保留。

### 2026年10月19日（更新2）

D02 完成：两套可运行窗口、独立解决方案、启动脚本及使用说明；本机已发布到 `DEMO/Output/WinForms` 和 `DEMO/Output/WPF`，并通过发布版程序生成 `DEMO/DEMO公开测试档案.dlauth`，输出与备份均保持 Git 忽略。只在临时目录导入公开档案，没有修改正式生成器档案库。

DEMO 与主解决方案 Release 构建均为 0 警告、0 错误；最终自动化测试 18/18 通过、0 跳过。临时 STA 窗口检查另有 18 项通过，实际触发 WinForms/WPF 按钮并用真实生成器档案服务产生响应，验证错误、申请刷新、前导零、激活后受控功能启用、重开保持状态及小窗口滚动；已查看两套窗口渲染。未验证异机、真实断电或 150% DPI，不将其记录为通过。按简化流程自行检查，无独立 Agent 评审。

验证程序及截图位于本机临时目录 `DLTools.DemoUiCheck-439aa330c1104f938eb810bece210988`；自动化 TRX 位于 DEMO 测试项目的 `TestResults/Demo-final.trx`。D01 提交为 `44f8c79`；D02 提交说明为 `feat(D02): deliver WinForms and WPF offline authorization demos`。DEMO 开发完成，停在功能分支，未推送或自动合并 DEMO。

### 2026年10月19日（更新1）

D01 完成：共享实际生成器协议及档案源文件编译，使用公开测试档案；DPAPI 状态原子保存、跨实例互斥、六位申请历史、五次失败冷却 30 分钟、时间回拨检测、显式测试重置留档。18/18 xUnit 测试通过，包括真实 AuthorizationProfileStore 恢复导出的档案再生成响应、前导零、重启、并发和保存失败。未操作真实 AppData 档案；按简化流程自行检查，无独立 Agent 评审。

Git Commit：`feat(D01): add offline authorization demo client and interoperability tests`。
