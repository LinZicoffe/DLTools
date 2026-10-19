# DL小工具验证与验收记录

## P06 实际窗口检查（2026-10-15）

使用 computer-use/sky 操作隔离预览程序，不改实际业务项目。测试工程为临时中文空格路径下的 .NET 8 WinForms 项目。

| 检查 | 结果 |
|---|---|
| 默认烧录页、两页导航及选中样式 | 通过；预览进程 J-Link 模块数为 0 |
| 项目识别与解释 | 通过；WinForms/net8.0-windows/SDK/Release/AnyCPU、实际入口示例.exe及MSBuild依据可见 |
| 输出目录及包名 | 通过；中文空格输出目录与自定义 安装包.exe，未重命名示例.exe |
| 部署方式 | 默认独立部署；切换依赖运行时后显示 Desktop Runtime 要求 |
| 完整界面打包 | 通过；真实 dotnet 发布及 Inno 6.7.1 编译成功 |
| 切页与状态 | 打包时可切至烧录再返回；输入、结果、日志保留 |
| 最小窗口 | 1000×700 实际拖动验证，表单滚动，按钮及日志可见；默认1280×800通过 |
| 已有脚本模式 | 应用信息/额外资源禁用，脚本与输入目录启用；实际脚本编译另由集成测试覆盖 |
| 重启配置 | 上次源路径、项目包名、输出目录、独立部署和稳定AppId恢复 |
| 跨页关窗与覆盖确认 | 修正原生MessageBox叠窗竞态后通过：退出问题显示时打包停在最终提交，未再叠加覆盖问题；确认退出后任务取消、窗口关闭 |
| 旧安装包保护 | 关窗取消前后SHA-256均为 D4F02811964190059B77DD93DAEEA25E97E6F87FBE721395C243B202A4B2DBE6 |

实测发现并修正只读 MainExecutable 的 TwoWay 启动绑定错误；只读展示均显式 OneWay。关窗门闩与宿主针对测试6/6通过，构建零警告错误。

临时验证根目录：`%LOCALAPPDATA%/Temp/DLTools-UI验证-da69a394d1734df6abb5c6819bcb4087`。

成功任务日志：`%APPDATA%/JLinkRTTViewer/packaging/runs/065aa15b90dc4e8da5c944e79999e0fc/packaging.log`。

关窗竞态修正后日志：`%APPDATA%/JLinkRTTViewer/packaging/runs/5af8a1f02581428497c3b140b6809ffd/packaging.log`（Committing后Cancelled，旧包未替换）。

## 尚未验收的现场边界

本次尚未执行 J-Link/STM32 真机烧录回归、干净目标机的运行时依赖安装验证及指定150%缩放验收。历史版本的硬件通过记录不能替代本次回归。

真实安装、同AppId升级及卸载保留用户数据已于R1补记；最终自动测试由P07汇总。指定150%缩放和现场硬件边界继续未验收。

## R1 真实安装生命周期（2026-10-15）

使用Inno Setup6.7.1、独占临时目录和全新GUID，开启DLTOOLS_INTEGRATION及DLTOOLS_INSTALL_INTEGRATION。生命周期与模板测试7/7零跳过；独立复跑模板6/6零跳过。

- 安装v1.0.0：安装后的中文、双引号、花括号及{#1+1}显示名称按字面保真；运行WPF入口--probe写入启动标记。
- 升级v1.0.1：保持AppId，修改安装包文件名；同一卸载登记版本更新，再次启动成功；自建user-data.keep保留。
- 卸载：真实卸载器移除应用及卸载登记，未在安装清单中的用户数据保留。
- finally及实查确认自有安装临时根、卸载登记和验证进程均无残留。未安装教程UDP应用，也未使用其真实AppId。
- 首次失败捕获安装名重复双引号，R1修复后保持原断言重跑通过；不是放宽验收条件。

本机已具备开发SDK/运行时，不能据此证明干净目标机上的依赖运行时部署通过。原现场边界继续未验收。

## P07 自动化与脚本兼容验证（2026-10-15）

- 工具环境：SDK8.0.418/9.0.311、VS MSBuild17.14、Framework4.8 Developer Pack、Inno Setup6.7.1及ChineseSimplified.isl。
- `dotnet build DL小工具.sln -c Release --no-restore`：0警告、0错误。
- 最终双开关完整测试：119/119通过、0失败、0跳过，耗时2分14秒，含新增CPU架构检查及真实安装生命周期；本地TRX为`tests/DLTools.Tests/TestResults/P07-final-full-validation.trx`，不纳入Git。补CPU前的117/117阶段记录仍保留。
- 真实构建矩阵：.NET8 WPF、.NET9 WinForms独立部署、传统Framework4.8 WPF/Forms及SDK格式net48；解决方案条件TargetDir、外部引用x86映射、多目标框架及条件导入通过。
- 失败/取消矩阵：还原/构建/ISCC/最终保存失败、父退出子持锁、恢复失败屏障、旧包保护、跨卷保存及资源冲突/链接通过。
- 两个真实进程在ready屏障后同时注册嵌套恢复目标，仅一方成功；记录持有至release，结束后无恢复记录残留。
- 双流压力每流4096行×256汉字，约6.3MB，逐行核对stdout/stderr无丢失、无死锁。
- CPU补充实测2/2零跳过：现代.NET8固定x86发布及收集入口均为原生I386 apphost；Framework固定x86/x64的原入口及收集产物PE架构正确，x86带Requires32Bit，两种入口均实际启动成功，运行时XML保留。记录为`tests/DLTools.Tests/TestResults/p07-cpu-validation.trx`。
- R1生命周期在完整测试中再次执行通过，验证应用已卸载；未安装业务UDP应用。

### 教程脚本的原规则

只读核对教程指向的实际脚本：`D:/C#_text/WPF/UDP-数据采集系统-交付版 -UI升级/setup.iss`。AppId仍为`{2748AB67-35B7-4A87-874E-EC881B4418CF}`；授权使用LOCALAPPDATA/ADC_Client/license.txt，通过[Files]安装为{app}/license.txt；原升级、图标及卸载规则保持由脚本管理。

在独占临时目录用受控构建输入验证脚本副本，仅将授权路径改成不存在的missing-required-license.txt。Inno退出2，并明确提示第117行Source文件不存在，未生成最终包。此验证不等于实际UDP业务项目构建/安装验收，未执行其程序。原脚本前后SHA-256均为`5082647EC8D9C2DE7F05A5E48927E6B9278130822A76E6ADE2D11345B70545B5`。

诊断目录：`%LOCALAPPDATA%/Temp/DLTools-TutorialCompatibility-7ad40aaced6748e7bdf59f725b887120`。首次测试夹具未成功替换CRLF行而编译成功，已删除该自有临时EXE；修正夹具后保持缺授权即失败的断言通过。没有打印授权文件内容，原脚本及授权文件未修改。

## 用户实测确认（2026-10-15）

用户按README项目打包流程对实际WPF工程（`D:\C#_text\WPF\算账`，.NET8单项目解决方案）完成打包实测，确认符合要求。本记录为用户现场确认；真机烧录、干净目标机运行时安装及150%缩放显示继续按上文边界列为未验收。
