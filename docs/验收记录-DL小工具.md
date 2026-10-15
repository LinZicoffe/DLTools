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

真实安装、同AppId升级、卸载保留用户数据及最终完整自动测试将在P07补记。
