# J-Link RTT Viewer C# WPF 移植映射文档

> 生成时间：2026-09-12 | 源项目：`D:\C#_text\烧录程序上位机` | 目标：`D:\C#_text\WPF\烧录上位机程序`

---

## 0. 环境事实（已核实）

| 项 | 实际情况 |
|---|---|
| .NET SDK | **8.0.418** 与 **9.0.311** 均已安装 |
| WPF 运行时 | `Microsoft.WindowsDesktop.App` 8.0.13 可用 |
| J-Link DLL | `C:\iar\ewarm-9.60.4\arm\bin\JLink_x64.dll` —— **SEGGER 原厂完整版 V8.14，x64** |
| SEGGER 独立安装 | **不存在**（`C:\Program Files\SEGGER` 无此目录，注册表无 SEGGER 项） |
| 构建方式 | 无 Visual Studio，用 `dotnet build` / `dotnet run` 即可 |
| pylink-square | 未安装（Python venv 不在当前环境） |

> ⚠️ **DLL 位置的重要含义**：目标机器上 J-Link DLL 不在标准目录，`DllImport("JLink_x64.dll")` 会失败。必须实现 DLL 路径解析（见 §3.1）。

---

## 1. 项目骨架映射

| Python 源文件 | C# 对应文件/类 | 复杂度 | 注意事项 |
|---|---|---|---|
| `src/main.py` | `App.xaml.cs` + 启动引导 | 低 | WPF 启动入口不同，但初始化顺序必须一致 |
| `src/ui/main_window.py` | `MainWindow.xaml` + `MainWindow.xaml.cs` + `MainWindowViewModel.cs` | 中 | PySide6 `FluentWindow` → WPF 需用 WPF-UI 或自绘导航 |
| `src/ui/rtt_monitor_page.py` | `Pages/RttMonitorPage.xaml` + `RttMonitorPage.xaml.cs` + `RttMonitorViewModel.cs` | **极高 (95.3 KB)** | 最大模块，ANSI 解析、RTT 读写、自动滚动、搜索 |
| `src/ui/flash_page.py` | `Pages/FlashPage.xaml` + `FlashPage.xaml.cs` + `FlashViewModel.cs` | **高 (74.4 KB)** | 烧录闭环，含文件解析+进度+日志 |
| `src/ui/memory_viewer_page.py` | `Pages/MemoryViewerPage.xaml` + `MemoryViewerPage.xaml.cs` + `MemoryViewerViewModel.cs` | 高 (43.8 KB) | hex dump、类型解析、hover tooltip |
| `src/ui/settings_page.py` | `Pages/SettingsPage.xaml` + `SettingsPage.xaml.cs` + `SettingsViewModel.cs` | 中 (30.5 KB) | 配置持久化、主题、字体 |
| `src/ui/pack_manager_page.py` | `Pages/PackManagerPage.xaml` + `PackManagerPage.xaml.cs` + `PackManagerViewModel.cs` | 中 (22.8 KB) | CMSIS-Pack 管理 |
| `src/ui/about_page.py` | `Pages/AboutPage.xaml` + `AboutPage.xaml.cs` + `AboutViewModel.cs` | 低 (10.7 KB) | 关于页 |
| `src/core/jlink_worker.py` | `Services/JLink/JLinkWorker.cs` + `JLinkWorkerViewModel.cs` | **极高** | 所有 pylink 调用在此，必须用 P/Invoke |
| `src/core/flash_worker.py` | `Services/JLink/FlashWorker.cs` + `FlashWorkerViewModel.cs` | 高 | 烧录流程编排，独立线程 |
| `src/core/flash_file_parser.py` | `Models/FlashFileParser.cs` + `Models/FlashFileInfo.cs` | 高 | hex/bin/elf/axf 解析 |
| `src/core/config_service.py` | `Services/ConfigService.cs` | 中 | 配置持久化，节流落盘 |
| `src/core/target_discovery.py` | `Services/TargetDiscovery.cs` | 中 | 设备枚举 |
| `src/core/memory_service.py` | `Services/MemoryService.cs` | 中 | 内存读写、hex dump 格式化 |
| `src/core/i18n_service.py` | `Services/I18nService.cs` + `Resources/i18n/*.json` | 低 | JSON 翻译表直接复用 |
| `src/core/probe/base.py` | `Models/ProbeBase.cs`（接口） | 低 | 抽象基类/协议 |
| `src/core/probe/jlink_backend.py` | `Services/JLink/JLinkBackend.cs` | 高 | **DLL API 清单来源** |
| `src/core/probe/pylink_rtt_backend.py` | `Services/JLink/PylinkRttBackend.cs` | 高 | **RTT API 清单来源** |
| `src/core/probe/factory.py` | `Services/ProbeFactory.cs` | 低 | 后端工厂 |
| `src/core/probe/enumerator.py` | `Services/ProbeEnumerator.cs` | 低 | pyOCD 枚举（J-Link 不走这里） |
| `src/core/probe/ob_adapter.py` | `Services/OptionBytes/ObAdapter.cs` | 中 | STM32 选项字适配器 |
| `src/core/option_bytes/` | `Services/OptionBytes/` | 高 | 71 颗芯片 OB 操作 |
| `src/core/ansi_parser.py` | `Services/AnsiParser.cs` | 中 | ANSI 转义解析 |
| `src/core/crc_utils.py` | `Services/CrcUtils.cs` | 低 | CRC 工具 |
| `src/core/logger.py` | `Services/LoggerService.cs` | 低 | 日志 |
| `src/core/_paths.py` | `Services/PathsService.cs` | 低 | 路径解析 |
| `src/core/_ui_font.py` | `Services/FontService.cs` | 中 | 字体管理 |
| `src/core/_dll_global.py` | `Services/JLink/DllLoader.cs` | **关键** | **DLL 加载与符号解析** |
| `src/core/screen_keeper.py` | `Services/ScreenKeeper.cs` | 低 | 防息屏 |
| `src/i18n/*.json` | `Resources/i18n/*.json` | **直接复用** | 6 个语言文件，无需重写 |
| `src/core/pack_service.py` | `Services/PackService.cs` | 中 | CMSIS-Pack 解析 |

---

## 2. 核心模块详细映射

### 2.1 `src/core/jlink_worker.py` → `Services/JLink/JLinkWorker.cs`

**复杂度：极高（944 行 Python 源码）**

**信号/槽 → C# 事件/命令映射：**

| Python Signal | C# 对应 |
|---|---|
| `connect_requested = Signal(str,str,str,int,int,str)` | `event Action<string,string,string,int,int,string> ConnectRequested` |
| `connection_state_changed = Signal(bool)` | `event Action<bool> ConnectionStateChanged` |
| `rtt_data_received = Signal(int,str)` | `event Action<int,string> RttDataReceived` |
| `log_message = Signal(str,str)` | `event Action<string,string> LogMessage` |
| `command_result = Signal(str,bool,str)` | `event Action<string,bool,string> CommandResult` |
| `target_infos_ready = Signal()` | `event Action TargetInfosReady` |
| `memory_read_finished = Signal(int,bytes)` | `event Action<int,byte[]> MemoryReadFinished` |
| `devices_enumerated = Signal(str)` | `event Action<string> DevicesEnumerated` |
| `reset_requested = Signal(str)` | `event Action<string> ResetRequested` |

**关键线程模型迁移：**

| Python (PySide6) | C# (WPF) |
|---|---|
| `QObject` + `moveToThread(QThread)` | `DispatcherObject` + `Task` / `BackgroundWorker` |
| `thread.started.connect(worker.initialize)` | `Task.Factory.StartNew` + 初始化逻辑 |
| `self.thread().quit()` | `CancellationTokenSource.Cancel()` |
| `thread.wait(5000)` | `task.Wait(timeout)` 或 `await task` |
| `threading.Thread + time.sleep(0.1)` RTT 读循环 | `Task.Run` + `CancellationToken` + `await Task.Delay` |
| `QTimer` 50ms drain | `DispatcherTimer` 或 `PeriodicTimer` |

**关键设计决策：**
- Python 的 `QObject` 信号/槽机制 → C# 的 `event` + `Dispatcher` 调度
- Python 的 `moveToThread` 线程亲和性 → C# 的 `Task.Run` 在线程池线程执行，UI 更新通过 `Dispatcher.Invoke`
- Python 的 `IncrementalDecoder` → C# 的 `System.Text.UTF8Encoding` + 状态保持

### 2.2 `src/core/probe/jlink_backend.py` → `Services/JLink/JLinkBackend.cs`

**复杂度：高（198 行 Python）**

**这是 P/Invoke API 清单的核心来源。必须逐个映射以下 pylink 调用到 JLINK_* DLL 函数：**

| pylink 调用 | 对应 J-Link DLL 函数 | 说明 |
|---|---|---|
| `pylink.JLink()` | `JLINKARM_GetDLLVersion()` + 加载 DLL | 构造时加载 DLL |
| `j.connected_emulators()` | `JLINKARM_EMU_GetList()` | 枚举 J-Link 设备 |
| `j.open()` / `j.open(serial_no=...)` / `j.open(ip_addr=...)` | `JLINKARM_Open()` / `JLINKARM_OpenWithSerial()` | 打开连接 |
| `j.close()` | `JLINKARM_Close()` | 关闭连接 |
| `j.set_tif(SWD/JTAG)` | `JLINKARM_SetTIF()` | 设置接口 |
| `j.set_speed(khz)` | `JLINKARM_SetSpeed()` | 设置速度 |
| `j.connect(target)` | `JLINKARM_Connect()` | 连接目标 |
| `j.reset(halt)` | `JLINKARM_Reset()` | 复位 |
| `j.halt()` | `JLINKARM_Halt()` | 暂停 |
| `j.restart()` | `JLINKARM_Go()` | 运行 |
| `j.flash(data, addr)` | `JLINKARM_Flash()` | 烧录 |
| `j.memory_read(addr, n, nbits=32)` | `JLINKARM_MemoryRead()` | 读内存 |
| `j.memory_write32(addr, words)` | `JLINKARM_MemoryWrite()` | 写内存 |
| `j.rtt_start()` | `JLINKARM_RTT_Start()` | 启动 RTT |
| `j.rtt_stop()` | `JLINKARM_RTT_Stop()` | 停止 RTT |
| `j.rtt_read(channel, max_bytes)` | `JLINKARM_RTT_Read()` | 读 RTT |
| `j.rtt_write(channel, data)` | `JLINKARM_RTT_Write()` | 写 RTT |
| `j.rtt_get_num_up_buffers()` | `JLINKARM_RTT_GetNumUpBuffers()` | 获取通道数 |
| `j.rtt_get_buf_descriptor(ch, up=True)` | `JLINKARM_RTT_GetBufDesc()` | 获取缓冲区描述 |
| `j.firmware_version` | `JLINKARM_GetFirmwareVersion()` | 固件版本 |
| `j.hardware_version` | `JLINKARM_GetHardwareVersion()` | 硬件版本 |
| `j.serial_number` | `JLINKARM_GetSerialNumber()` | 序列号 |
| `j.core_name()` | `JLINKARM_GetCoreName()` | 核心名称 |
| `j.core_id()` | `JLINKARM_GetCoreId()` | 核心 ID |
| `j.core_cpu()` | `JLINKARM_GetCoreCPU()` | CPU 类型 |
| `j.power_on()` / `j.power_off()` | `JLINKARM_PowerOn()` / `JLINKARM_PowerOff()` | 电源控制 |
| `j.num_supported_devices()` / `j.supported_device(i)` | `JLINKARM_GetNumDevices()` / `JLINKARM_GetDeviceInfo()` | 设备枚举 |

> ⚠️ **函数名可能不完全匹配** — 实际 DLL 导出名需用 `dumpbin /exports` 验证。SEGGER 官方 SDK 内含 `JLinkSDK.cs`（约 30 KB），可直接作为参考。

### 2.3 `src/core/probe/pylink_rtt_backend.py` → `Services/JLink/PylinkRttBackend.cs`

**复杂度：高（267 行 Python）**

**RTT 连接序列（必须严格保持）：**
1. `j.open()` → `j.close()` → `j.open(serial)` → `j.rtt_start()`（双开模式）
2. `j.set_tif(SWD/JTAG)` → `j.set_speed(khz)` → `j.connect(target)`
3. 探测 `rtt_get_num_up_buffers()` + `rtt_get_buf_descriptor()` 得到实际通道数
4. `rtt_read(channel, max_bytes)` 循环读

**关键：`rtt_start()` 必须在 `connect(target)` 之前调用**（pylink 1.6.0 硬性要求）

---

## 3. 关键技术决策

### 3.1 J-Link 后端：P/Invoke 实现方案

**方案 A（推荐）：参考 `JLinkSDK.cs` + 自行封装**

- 从 SEGGER 官方 SDK 提取 `JLinkSDK.cs` 的 P/Invoke 声明
- 添加 `SetDllDirectory` / `AddDllDirectory` 解决 DLL 搜索路径
- 实现 `DllLoader` 类：`LoadLibrary` → `GetProcAddress` → 委托封装

**方案 B：直接引用官方 SDK**

- SEGGER J-Link SDK 包含 C# 示例，但需确认是否可商用

**DLL 路径解析（必须实现）：**
```csharp
// 搜索路径优先级：
// 1. 配置文件中指定的 JLINK_DLL_PATH
// 2. 环境变量 JLINK_DLL_PATH
// 3. 已知 IAR 安装路径: C:\iar\ewarm-9.60.4\arm\bin\JLink_x64.dll
// 4. 常见 SEGGER 安装路径
// 5. 系统 PATH
```

**同一进程只能加载一次 DLL 的限制：**
- 当前项目只需一个 J-Link 连接，可暂时不处理多探针
- 如需支持多探针，需采用 pylink 的"复制为临时文件"策略

### 3.2 UI 风格库选型

**选择：WPF-UI (`lepoco/wpfui`)**

- 理由：Fluent 2 风格，活跃度高，与 qfluentwidgets 最接近
- 备选：HandyControl（中文社区活跃）
- 选定后全项目统一

### 3.3 MVVM 框架

**选择：`CommunityToolkit.Mvvm`（源生成器）**

- `[ObservableProperty]` / `[RelayCommand]`
- 与 Python 的信号/槽模式最接近

### 3.4 线程模型

| Python | C# |
|---|---|
| `QObject.moveToThread(QThread)` | `Task.Run` + `CancellationToken` |
| `QTimer` (worker 线程) | `PeriodicTimer` 或 `DispatcherTimer` |
| `threading.Thread + time.sleep` | `Task.Run` + `await Task.Delay` |
| `QThread.quit()` / `thread.wait()` | `CancellationTokenSource` / `Task.Wait` |
| `QObject.deleteLater()` | `Dispose` + `GC` |
| `threading.Lock` | `lock` / `SemaphoreSlim` |

---

## 4. i18n 映射

| Python | C# |
|---|---|
| `src/i18n/en.json` 等 6 个文件 | `Resources/i18n/en.json` 等 6 个文件——**直接拷贝复用** |
| `JsonTranslator(QTranslator)` | `JsonTranslator : IStringLookup` 或自定义 `IValueConverter` |
| `QApplication.installTranslator()` | `WPF Localization` 或手动 `ResourceDictionary` 切换 |
| `self.tr("中文源文本")` | `ResourceProvider.GetString("中文源文本")` 或 `x:Static` 绑定 |
| `switch_language(lang)` | `I18nService.SwitchLanguage(lang)` |

**语言文件内容：**
- `en.json`: 443 条
- `zh_CN.json`: 7 条（仅补翻框架自带串）
- `ja.json` / `ko.json` / `fr.json` / `zh_TW.json`: 各约 400 条

---

## 5. 关键硬约束（不可违反）

| # | 约束 | 说明 |
|---|---|---|
| 1 | 双镜像布局 | STM32G474: Bootloader `0x08000000–0x08000DB7`, App `0x08004000–0x0800E2AE` |
| 2 | 扇区擦除为默认 | 绝不可全片擦除，会擦掉 bootloader |
| 3 | 逐字节回读校验 | `extra_verify` 功能，不可省略 |
| 4 | J-Link 假性错误 | 必须靠独立回读比对判断成功，不能只信工具返回码 |
| 5 | 中文路径处理 | 需复制到 ASCII 临时目录 |
| 6 | 速度可配置 | 默认不要写死 4000 kHz |
| 7 | pylink 1.6.0 连接序列 | `open→close→open(serial)→rtt_start→set_tif→set_speed→connect` |
| 8 | DLL 路径解析 | 目标机器上 DLL 不在标准目录 |
| 9 | 同一进程只能加载一次 DLL | 官方限制 |
| 10 | `set_tif` 不可 OR | SWD 和 JTAG 是互斥枚举值 |

---

## 6. 分阶段实施计划

| 阶段 | C# 文件 | 验收方式 |
|---|---|---|
| **P1** 项目骨架 | `App.xaml.cs`, `MainWindow.xaml/.cs`, `ConfigService.cs`, `I18nService.cs`, 6 个 Page 占位 | 能启动、能切页、能切语言 |
| **P2** 烧录闭环 | `JLinkBackend.cs`, `PylinkRttBackend.cs`, `JLinkWorker.cs`, `FlashWorker.cs`, `FlashFileParser.cs`, `FlashPage.xaml/.cs` | 实烧 STM32G474 成功 |
| **P3** RTT 监控 | `RttMonitorPage.xaml/.cs`, `AnsiParser.cs`, 内存/统计/日志 | 能实时接收 RTT 输出 |
| **P4** 内存查看 | `MemoryViewerPage.xaml/.cs`, `MemoryService.cs` | hex dump、类型解析 |
| **P5** 设置+关于 | `SettingsPage.xaml/.cs`, `AboutPage.xaml/.cs` | 配置持久化 |
| **P6** CMSIS-Pack+OB | `PackManagerPage.xaml/.cs`, `OptionBytes/` | 读取/修改选项字 |

---

## 7. 源项目参考链接

- 设计文档：`D:\C#_text\烧录程序上位机\docs\superpowers\specs\`
- 实现计划：`D:\C#_text\烧录程序上位机\docs\superpowers\plans\`
- 踩坑笔记：`D:\C#_text\烧录程序上位机\docs\pitfalls.md`
- 用户指南：`D:\C#_text\烧录程序上位机\docs\USER_GUIDE.md`
- 代码规范：`D:\C#_text\烧录程序上位机\docs\coding-style.md`
- 设备命名：`D:\C#_text\烧录程序上位机\docs\target-device-name-conventions.md`
- 烧录指南：`D:\C#_text\烧录程序上位机\docs\flashing-guide.md`
