# J-Link 烧录上位机（C# WPF）

> C# WPF 复刻自 Python/PySide6 版本的 SEGGER J-Link 固件烧录工具，**只做固件烧录**（RTT/内存查看等页面已按需求裁剪）。
> 已真机实测通过：J-Link + STM32H750VB，SWD 4000 kHz，烧录 → 强制读回校验 → 复位运行全链路。

## 功能特性

- **固件格式**：`.elf` / `.axf`（ELF32 LOAD 段）、`.hex`（Intel HEX）、`.bin`（需填写烧录起始地址，如 `0x08000000`）
- **连接参数**：目标设备名（SEGGER 官方命名）、SWD/JTAG、下载速度、J-Link 序列号（留空自动选第一个）
- **擦除模式**：整片擦除 / 扇区擦除（默认，下载事务内按扇区隐式擦除，保护 bootloader）
- **强制读回校验**：烧录完成后自动读回 Flash 与固件文件逐字节比对（4096 字节分块），不一致即报失败——DLL 层不报错不代表数据真的落进了 Flash
- **完成后动作**：复位运行 / 复位暂停 / 仅烧录
- **HEX 解析完整性防护**：逐行校验和验证、强制 EOF 记录（截断文件直接拒绝）、type 02/04 记录长度与互斥校验、空洞地址不固化
- **安全关窗**：烧录进行中关窗会弹窗确认，停止任务并等待其退出（15s 超时）后再关闭，避免进程在原生调用中途被杀导致芯片半擦除/半写入
- **全局异常兜底**：UI 线程 / AppDomain / 未观察 Task 三类未处理异常统一写日志
- **六语言界面**：简体中文 / 繁體中文 / 日本語 / 한국어 / English / Français，XAML 经 `{i18n:Loc 'key'}` 标记扩展绑定，切换即时生效
- **配置持久化**：`%APPDATA%\JLinkRTTViewer\user_prefs.json`，防抖落盘 + 临时文件原子替换，退出时强制 Flush

## 技术栈

| 组件 | 选型 |
|------|------|
| UI 框架 | WPF + **WPF-UI 3.1.0**（Fluent 2 风格控件） |
| MVVM | **CommunityToolkit.Mvvm 8.2.2**（源生成器 `[ObservableProperty]`） |
| 后端 | C# P/Invoke 直调 `JLink_x64.dll`（实测 V9.76，不依赖 pylink） |
| .NET | 8.0（`net8.0-windows`，**PlatformTarget=x64**） |
| 图标 | Material.Icons.WPF |

## 项目结构

```
烧录上位机程序/
├── App.xaml(.cs)             # 启动：DLL 加载、全局异常钩子、退出 Flush
├── MainWindow.xaml(.cs)      # 主窗口：标题栏 + 语言切换 + 关窗安全流程
├── Pages/
│   ├── BasePage.cs           # 页面基类（XAML 根元素）
│   └── FlashPage.xaml(.cs)   # 固件烧录页（唯一功能页）
├── ViewModels/
│   └── MainWindowViewModel.cs
├── Models/
│   ├── FlashFileParser.cs    # ELF32/Intel HEX/bin 解析（纯函数、零 UI 依赖）
│   └── ProbeParams.cs        # 探测器连接参数
├── Services/
│   ├── ConfigService.cs      # 配置读写（防抖 + 原子落盘）
│   ├── I18nService.cs        # 翻译加载（六语言 JSON）
│   ├── LocExtension.cs       # {i18n:Loc 'key'} 标记扩展 + INPC 绑定源
│   ├── LoggerService.cs      # 按天滚动日志（跨天切文件、共享读）
│   └── JLink/
│       ├── DllLoader.cs      # DLL 定位/加载 + P/Invoke 声明与导出封装
│       ├── JLinkBackend.cs   # 烧录后端：Connect/Erase/Program/Verify/Reset/Close
│       └── FlashWorker.cs    # 后台任务编排：阶段事件 + 取消 + 关窗等待
└── Resources/
    ├── UiStyles.xaml         # 画刷与控件样式
    └── i18n/*.json           # 六语言词条（五语 490 key 完全对齐；zh_CN 走 key 回退）
```

## 烧录流程

```
Connect（USB 双开协议 / 远程 IP）→ [整片擦除] → 每段：halt(容忍) → BeginDownload → WriteMem → EndDownload
    → Verify（4096 块读回逐字节比对，强制执行）→ [复位] → Close
```

- 后台跑在 `Task.Run` 线程池线程，经 `Dispatcher.Invoke` 封送 UI 更新；`CancellationToken` 在段/校验块边界生效
- P/Invoke 返回值判错口径逐项对齐 `pylink-square` 参考实现（`set_tif !=0`、`connect <0`、`EraseChip <0`、`EndDownload <0`、`WriteMem` 容忍不等、`DEVICE_GetIndex <=0` 等）
- 异常路径上 J-Link 会话经 `backend?.Close()` 兜底关闭，无句柄泄漏

## 环境要求

- Windows 10/11 **x64**、.NET 8.0 SDK
- SEGGER J-Link 驱动（`JLink_x64.dll`）。搜索顺序：
  1. exe 同目录（绿色部署：把 DLL 放程序旁即可）
  2. `C:\Program Files\SEGGER\JLinkARM_DLL\`
  3. `C:\Program Files (x86)\SEGGER\JLinkARM_DLL\`
  4. IAR EWARM 捆绑目录、`PATH`

## 构建与运行

```bash
dotnet build 烧录上位机程序.sln
dotnet run --project 烧录上位机程序
```

日志与用户配置位于 `%APPDATA%\JLinkRTTViewer\`（`logs/rtt_yyyyMMdd.log`、`user_prefs.json`）。

## 质量记录

- **真机实测**（2026-09-29）：STM32H750VB 烧录 + 强制校验 PASS + 复位运行，详见根目录 `烧录链修复功能更新日志.md`
- **多 agent 三轮审查收敛**（2026-09-29，发现数 21 → 10 → 0）：共修复 31 项，含 3 个 P0——配置系统读写双向失效（JsonElement 装箱 + `File.Replace` 首次落盘必失败，均独立复现）、烧录中关窗杀进程；4 个 P1——HEX 缺 EOF 静默成功、bin 起始地址硬编码 0、语言切换功能空转、全局异常钩子缺失
- **回归验证**：HEX 解析器 35 项独立测试全过（往返/跨 64K/空洞保留/缺 EOF 拒绝/BOM/CRLF）；ConfigService 两进程写→读回测试；i18n 五语 key 集合对齐校验；构建 0 警告 0 错误

## 源项目参考

- 源项目（Python/PySide6）：`D:\C#_text\烧录程序上位机`
- 移植映射：`docs/PORTING_MAP.md`
- J-Link DLL 导出表（637 项）：`docs/JLink_DLL_exports.txt`
- pylink-square（返回值语义参考）：https://github.com/square/pylink
