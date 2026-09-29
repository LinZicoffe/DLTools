# J-Link RTT Viewer (C# WPF)

> C# WPF 复刻自 Python/PySide6 版本的 J-Link 固件烧录 + RTT 实时监控工具。

## 技术栈

| 组件 | 选型 |
|------|------|
| UI 框架 | WPF + **WPF-UI** (Fluent 2 风格) |
| MVVM | **CommunityToolkit.Mvvm** (源生成器) |
| 后端 | C# P/Invoke → `JLink_x64.dll` (V8.14) |
| .NET | 8.0 |
| 编码 | UTF-8 / GBK 支持 |

## 项目结构

```
烧录上位机程序/
├── Pages/              # WPF 页面 (XAML + 代码隐藏)
│   ├── RttMonitorPage.xaml
│   ├── FlashPage.xaml
│   ├── MemoryViewerPage.xaml
│   ├── SettingsPage.xaml
│   ├── PackManagerPage.xaml
│   └── AboutPage.xaml
├── ViewModels/         # MVVM ViewModel
├── Models/             # 数据模型
│   ├── FlashFileParser.cs
│   ├── ProbeParams.cs
│   └── ...
├── Services/           # 业务服务层
│   ├── ConfigService.cs
│   ├── I18nService.cs
│   ├── JLink/          # J-Link 相关
│   │   ├── DllLoader.cs        # DLL 加载与 P/Invoke
│   │   ├── JLinkBackend.cs     # 烧录后端
│   │   ├── PylinkRttBackend.cs # RTT 后端
│   │   └── JLinkWorker.cs      # 工作线程编排
│   └── MemoryService.cs
├── Resources/          # 资源
│   ├── i18n/           # 翻译 JSON 文件 (直接复用源项目)
│   └── app.ico
└── MainWindow.xaml     # 主窗口 + 导航
```

## 阶段进度

| 阶段 | 状态 | 验收方式 |
|------|------|----------|
| **P1** 项目骨架 | 🔲 待开始 | 能启动、能切页、能切语言 |
| **P2** 固件烧录闭环 | 🔲 待开始 | 实烧 STM32G474 成功 |
| **P3** RTT 监控页 | 🔲 待开始 | 能实时接收 RTT 输出 |
| **P4** 内存查看页 | 🔲 待开始 | hex dump、类型解析 |
| **P5** 设置+关于 | 🔲 待开始 | 配置持久化 |
| **P6** CMSIS-Pack+OB | 🔲 待开始 | 读取/修改选项字 |

## 关键决策

1. **J-Link 后端**：C# P/Invoke 直接调用 `JLink_x64.dll`，不从 `pylink-square` 间接调用
2. **UI 风格**：WPF-UI (Fluent 2)
3. **MVVM**：CommunityToolkit.Mvvm 源生成器
4. **i18n**：源项目 `src/i18n/*.json` 直接复用
5. **DLL 路径**：需实现路径解析（目标机器 DLL 不在标准目录）

## 环境要求

- .NET 8.0 SDK
- J-Link DLL: `C:\iar\ewarm-9.60.4\arm\bin\JLink_x64.dll`
- Windows 10/11

## 构建

```bash
dotnet build
dotnet run
```

## 源项目参考

- 源项目：`D:\C#_text\烧录程序上位机`
- 移植映射：`docs/PORTING_MAP.md`
- 源项目踩坑笔记：`docs\pitfalls.md`
