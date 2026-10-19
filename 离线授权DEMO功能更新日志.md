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
| D02 | WinForms/WPF 窗口、说明、构建与界面验证 | DEMO/OfflineAuthorization.WinForms、DEMO/OfflineAuthorization.Wpf、DEMO/README.md | 进行中 |

## 已知风险

- 公开测试密钥和测试重置入口不能用于真实交付，无法抵抗调试、删除状态或快照回滚。
- 真实客户预置密钥及重装后的密钥恢复不在本次 DEMO 范围。

## 更新记录

### 2026年10月19日（更新1）

D01 完成：共享实际生成器协议及档案源文件编译，使用公开测试档案；DPAPI 状态原子保存、跨实例互斥、六位申请历史、五次失败冷却 30 分钟、时间回拨检测、显式测试重置留档。18/18 xUnit 测试通过，包括真实 AuthorizationProfileStore 恢复导出的档案再生成响应、前导零、重启、并发和保存失败。未操作真实 AppData 档案；按简化流程自行检查，无独立 Agent 评审。

Git Commit：`feat(D01): add offline authorization demo client and interoperability tests`。
