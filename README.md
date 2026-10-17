# DL小工具

C# / .NET 8 / WPF 的常用工具集合。左侧选择“固件烧录”“项目打包”或“离线授权”，右侧保留对应页面状态。默认进入烧录页；窗口默认1280×800，最小1000×700，表单可滚动。

## 离线授权

此页面是我方内部六位授权码生成器，现场和生成端均无需联网，客户不用收发授权文件。当前版本不对烧录、打包功能施加授权限制。

1. 打开“离线授权”，输入客户／设备名称并新建档案。每个档案具有独立设备标识和随机密钥，请及时备份。
2. 选择正确档案，输入现场提供的六位申请码，例如 `000012`，点击“生成授权码”。前导零会保留。
3. 点击“复制授权码”，将六位结果告知客户。更换档案或申请码后旧结果立即清空；切换页面保留输入和结果。
4. 用“创建加密备份”保存我方 `.dlauth` 档案备份，使用至少16字符的独立强密码。换电脑后用“从备份恢复”合并档案；同设备不同密钥拒绝覆盖，恢复确认期间发生其他修改则要求重新预览。损坏库恢复前保留原文件副本。备份仅供我方保管，客户不需要操作。

本地密钥由 Windows 当前用户 DPAPI 保护；跨电脑恢复必须使用加密备份，直接复制本地 `profiles.dat` 不保证可解密。备份使用 AES-256-GCM 和 PBKDF2-HMAC-SHA256，忘记密码无法恢复，不应把备份与密码存放在一起。

**客户验证端尚需接入。** 客户软件必须由我方预置同一设备标识和密钥，并实现当前申请、错误限次及授权状态保存；`OfflineAuthorizationService.Verify` 只完成计算比较，不包含这些状态保护。不能仅复制六位码就完成首次密钥配对。纯 Windows 实现不承诺抵抗专业逆向、密钥提取或整库回滚，重装后也不保证自动恢复授权。

计算协议、公开测试向量及后续接入约定见[设计文档](docs/设计-六位离线授权.md)，本次验证见[离线授权验收记录](docs/验收记录-六位离线授权.md)。

## 固件烧录

- 支持`.elf` / `.axf`（ELF32 LOAD段）、`.hex`（Intel HEX）和`.bin`（需填写起始地址）。
- 配置目标设备、SWD/JTAG、速度和J-Link序列号；支持整片/扇区擦除及完成后的复位动作。
- 烧录后强制按4096字节分块读回比对，保持HEX校验和、EOF和稀疏地址校验。
- 开始烧录时才加载J-Link；缺少驱动不影响打开程序和项目打包。

流程：Connect → 可选整片擦除 → 按段halt/BeginDownload/WriteMem/EndDownload → 强制Verify → 可选Reset → Close。取消保留原来的段/校验块边界；关窗确认停止后最多等待15秒。

## 项目打包

1. 导入本地文件夹、`.sln`或C# `.csproj`，点击“识别”。多个入口/目标框架时手动选择候选。
2. 查看WPF/WinForms、.NET版本、构建配置、项目平台、CPU、入口EXE和识别依据。直接项目修改配置/平台后需重新评估；解决方案采用候选对应的ActiveCfg映射。
3. 指定输出目录、安装包文件名、部署架构及模式。默认独立部署，可改为依赖目标机Desktop Runtime；Framework沿用项目CPU并显示运行时要求。
4. 开始打包，查看实际阶段和实时日志；支持取消，成功后可打开输出目录或完整日志。

安装包文件名、安装后的应用名称和目标EXE独立处理。更改包名不会重命名程序入口，也不会改变项目的稳定AppId。

### 支持范围与工具

| 项目/操作 | 要求 |
|---|---|
| 工具自身运行 | Windows 10/11 x64，.NET 8 Desktop Runtime |
| 工具自身开发 | .NET 8或兼容更新SDK |
| 现代.NET WPF/WinForms | 对应SDK，MSBuild17.8+；发布win-x86/win-x64，遵守项目CPU/RID约束 |
| 传统/SDK格式Framework WPF/WinForms | Visual Studio MSBuild17.8+及对应Developer Pack；支持x86/x64/AnyCPU |
| 安装包编译 | Inno Setup6+；自动模板还需Languages/ChineseSimplified.isl |
| 固件烧录 | SEGGER驱动与JLink_x64.dll |

可在“构建工具与路径设置”中自动定位、手动指定及检查dotnet、MSBuild和ISCC。ARM64、其他技术栈、ZIP导入和MSI不在首版范围内。

### 自动生成模板

读取项目名称、版本及图标，允许修改，缺少版本默认1.0.0。默认包名为`<应用名>_v<版本>_Setup.exe`。支持额外文件/目录映射到安装目录下的相对位置；目录目标`.`表示合并安装根，文件目标须包含文件名。越界、链接和重名冲突会拒绝。

默认当前用户安装，提供开始菜单、可选桌面快捷方式、卸载和安装后启动。稳定AppId按完整项目路径保存；升级时保持该值。模板不添加递归删除用户数据的卸载规则。

### 使用已有脚本

选择原`.iss`及其实际读取的构建输入目录；现代项目通常为脚本旁`Publish`，Framework可用正常TargetDir。工具先构建，再准备输入并编译原脚本；原脚本的AppId、授权、图标、安装及卸载规则继续生效。通用应用信息和额外资源控件在此模式下禁用。

输入路径必须与脚本Source/宏匹配，工具不推断自定义宏。脚本须支持ISPP有效规则检查；builtin预处理器和无法导出规则的脚本会明确拒绝。首版只输出独立单EXE安装包，拒绝分卷、external、下载及安装包旁附属文件依赖。编译器参数控制输出目录和包名；不会改写原脚本。工具不主动读取授权文件内容写入日志；私密预处理文本会删除且不入日志。

### 任务与文件恢复

全局一次执行一个烧录、项目或授权档案任务，切页不会释放运行资格；打包关窗会等进程退出和目录恢复，授权档案任务会等安全保存结束，均不使用烧录的15秒上限。退出问题与打包覆盖问题按顺序显示。

现代.NET发布到本次任务的独占目录；Framework先确认备份入口正常输出目录，再还原/重新构建并整目录收集。保留运行时、原生库、语言资源、XML/JSON，只过滤调试产物。已有脚本的非空输入目录同样需要确认备份。成功后保留旧目录备份；失败或取消恢复旧输入和旧安装包，部分输出另存保留。

安装包先在目标卷暂存并校验，再原子提交；覆盖同名包需要确认。恢复异常会显示备份及记录位置，阻止相关父子目录重试。应先按记录恢复文件，再处理相应恢复记录，避免绕过保护。保留的任务产物和备份需按需人工归档。

## 构建与测试

在仓库根目录使用PowerShell：

```powershell
dotnet restore "DL小工具.sln"
dotnet build "DL小工具.sln" -c Release
dotnet run --project "烧录上位机程序/DL小工具.csproj"
dotnet test "tests/DLTools.Tests/DLTools.Tests.csproj" -c Release
```

普通测试不执行实际安装，项目识别用例也会调用实际SDK、VS MSBuild17.8+并检查Framework4.8开发包。完整发布/安装集成检查需本机SDK8/9、Framework4.8开发包、VS MSBuild和Inno：

```powershell
$env:DLTOOLS_INTEGRATION = '1'
$env:DLTOOLS_INSTALL_INTEGRATION = '1' # 允许测试独立安装、升级和卸载临时验证应用
dotnet test "tests/DLTools.Tests/DLTools.Tests.csproj" -c Release
```

安装测试只使用独占临时目录和新AppId，不使用真实业务应用标识。安装、GUI及硬件边界见[验收记录](docs/验收记录-DL小工具.md)。

## 目录与本地配置

```text
DL小工具.sln
烧录上位机程序/
  DL小工具.csproj          # net8.0-windows / WPF / x64
  Pages/                  # 烧录页、打包页、离线授权页
  ViewModels/             # 页面状态与命令
  Models/Packaging/       # 识别结果与打包选项
  Models/Authorization/   # 设备档案展示数据（不含密钥）
  Services/Packaging/     # 构建、Job进程、事务、Inno、profiles
  Services/JLink/         # 驱动、探测器、烧录工作器
  Services/Authorization/ # 六位计算、DPAPI档案、加密备份恢复
  Resources/              # 共享样式及既有翻译资源
tests/                    # xUnit、子进程与安装测试
docs/                     # 方案、审查和验收记录
```

保留原命名空间和`%APPDATA%/JLinkRTTViewer/`根目录。烧录设置仍为`user_prefs.json`，原日志位于`logs/`；打包配置为`packaging/profiles.json`，任务日志/产物在`packaging/runs/<任务ID>/`，恢复记录在`packaging/recovery/`。目录备份位于相同卷的`.dltools-backups/`，具体位置由结果和日志列出。

授权档案独立保存在`authorization/profiles.dat`；该文件整体由当前用户DPAPI加密。不要手工编辑或删除密钥库，不要将其或加密备份提交到仓库。

J-Link DLL可放在EXE旁，也支持SEGGER安装目录、IAR捆绑目录和PATH定位。

## 验证记录与开发进度

改版与打包功能已完成并入主分支。最终收尾复核通过：Release构建零警告零错误，双开关全量测试119/119通过、零失败零跳过（含真实发布、Inno编译及安装/升级/卸载生命周期）；2026-10-15用户按上文流程对实际.NET8 WPF工程完成打包实测并确认符合要求。真机烧录回归、干净目标机运行时安装及150%缩放显示仍待现场验收。

改版步骤、提交与审查结果见[功能日志](DL小工具功能更新日志.md)、[方案](docs/实施步骤-DL小工具.md)及[评审记录](docs/评审记录-DL小工具.md)。构建/自动测试、真实安装、窗口检查和硬件验收分开记录，未完成的现场项不由编译替代。

历史烧录版本于2026-09-29使用J-Link + STM32H750VB、SWD4000kHz验证烧录、强制校验和复位；此前还完成HEX解析35项、配置跨进程读写及翻译资源检查，详见`烧录链修复功能更新日志.md`。历史结果不替代本次硬件回归。

源程序移植映射见`docs/PORTING_MAP.md`，DLL导出表见`docs/JLink_DLL_exports.txt`；返回值语义参考[pylink-square](https://github.com/square/pylink)。
