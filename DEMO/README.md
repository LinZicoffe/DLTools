# 六位离线授权 DEMO

两套 .NET 8 / Windows x64 客户验证示例：WinForms、WPF。与 DL小工具当前离线授权协议一致，无需联网或 J-Link。**只使用公开测试密钥，不能用于真实客户交付。**

## 直接测试

1. 双击本目录的 `启动WinForms.cmd` 或 `启动WPF.cmd`。本机已生成可运行文件；需要 .NET 8 Windows 桌面运行时。
2. 首次测试时，在 **DL小工具 → 离线授权 → 恢复档案** 导入本目录的 `DEMO公开测试档案.dlauth`。如果文件不存在，点击任一 DEMO 的“导出生成器测试档案”生成。
3. 测试备份密码为 **`DLTools-Demo-Only-2026!`**。确认恢复后，选择 **`DEMO公开测试设备（禁止生产使用）`**，不要新建一个随机档案代替。
4. 将 DEMO 显示的六位申请码输入 DL小工具，点击生成；把得到的六位授权码填回 DEMO，点击“确认授权”。保留前导零。
5. 点击“运行受控演示功能”，确认授权后功能可用。关闭并重开 DEMO，验证激活仍然保持。
6. 点击“重置测试授权”可重新演示；旧状态会保留一份副本。WinForms 与 WPF 的测试状态彼此独立。

**档案文件只是我方测试配对时导入一次；客户申请授权的流程仍只需交换六位申请码和六位授权码。** 本次不会自动导入真实生成器档案库。测试 `.dlauth` 文件及构建产物按仓库规则不提交，可从 DEMO 随时重建。

## 错误和重启测试

- 未激活时受控功能禁用；受控操作执行前也重新检查持久化状态。
- 错误输入计为失败，累计五次后暂停 30 分钟。关闭、重启、更换申请码均不清除次数；冷却结束后点击“刷新状态”。
- 每次申请码先保存再展示；关闭重开继续当前申请。更换申请使旧申请失效，同一状态生命周期内不重复分配历史申请码。
- 系统时间倒退时暂停授权，校正时间后刷新。没有修改电脑时间的必要，可运行自动化测试验证该场景。
- 保存失败不会报告授权成功；损坏状态不会静默重建。可明确使用“重置测试授权”开始新一轮演示。
- 示例里的重置入口会刻意清除申请历史和失败次数，**正式客户软件不得保留该入口**。

## 项目与构建

```text
DEMO/
  离线授权DEMO.sln
  OfflineAuthorization.Demo.Core/   # 共享验证状态与公开档案导出
  OfflineAuthorization.WinForms/    # WinForms 窗口
  OfflineAuthorization.Wpf/         # WPF 窗口
  OfflineAuthorization.Demo.Tests/  # 状态及生成器互通测试
  Output/WinForms/                  # 本机可运行文件，未提交
  Output/WPF/                       # 本机可运行文件，未提交
```

在仓库根目录运行：

```powershell
dotnet build "DEMO/离线授权DEMO.sln" -c Release
dotnet test "DEMO/离线授权DEMO.sln" -c Release
dotnet publish "DEMO/OfflineAuthorization.WinForms/OfflineAuthorization.WinForms.csproj" -c Release --no-self-contained -o "DEMO/Output/WinForms"
dotnet publish "DEMO/OfflineAuthorization.Wpf/OfflineAuthorization.Wpf.csproj" -c Release --no-self-contained -o "DEMO/Output/WPF"
```

可在 Visual Studio 打开 DEMO 解决方案，分别设为启动项目调试。共享项目链接仓库现有授权协议、档案和任务宿主源文件，避免两套算法漂移；编译源码需要保留完整仓库路径。运行时无需 DL小工具主程序 DLL。复制给测试机时复制整个对应 `Output` 子目录，不能只复制 EXE。

## 状态与公开测试参数

状态以当前 Windows 用户 DPAPI 加密、互斥及原子保存，不存入安装目录：

- `%LOCALAPPDATA%/DLTools/OfflineAuthorizationDemo/WinForms/state.dat`
- `%LOCALAPPDATA%/DLTools/OfflineAuthorizationDemo/WPF/state.dat`

与 `%APPDATA%/JLinkRTTViewer/authorization/` 正式档案分开；重置不会清除正式档案。

测试设备标识为 `11112222333344445555666677778888`，公开测试密钥为字节 `00` 至 `1F`。这些是原协议文档里的固定向量，仅供测试。申请码 `000006` 对应授权码 `011864`；运行时默认安全随机生成申请码。

客户正式接入时必须由我方可靠预置每台设备的独立密钥和标识，删除公开参数、导出及重置功能，按实际需求保护密钥和授权状态。纯本地软件不承诺抵抗逆向、删除状态或历史快照回滚；重装后是否保留授权取决于密钥及状态的交付恢复机制。

## 本次验证

2026-10-19：DEMO 解决方案与原主解决方案 Release 构建均为 0 警告、0 错误；18/18 自动化测试通过。临时隔离目录中的真实 WinForms/WPF 窗口另通过 18 项检查，覆盖生成器响应启用功能、前导零、错误、刷新申请、重启保存和小窗口滚动，并检查窗口渲染。

没有修改真实授权库，没有执行硬件烧录或安装。异机运行、重装恢复、150% DPI 和真实断电仍需现场验证；上述测试不代表已验收这些场景。
