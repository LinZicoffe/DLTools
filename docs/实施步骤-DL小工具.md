# DL小工具实施步骤

## 1. 项目目标

将单页 J-Link 烧录程序变为“DL小工具”：左侧固件烧录/项目打包导航，右侧缓存页面；本地导入 C# WinForms/WPF 工程，说明框架、.NET 与 CPU，生成指定目录、指定名称的 Inno Setup EXE 安装包。

## 2. 实施范围

包含传统及 SDK 格式 .NET Framework、现代 .NET；文件夹/.sln/.csproj 导入；多个入口或 TFM 手选；现代 .NET x86/x64、Framework x86/x64/AnyCPU；默认独立部署可切换；通用模板及已有脚本；额外资源映射；任务、取消、文件恢复、配置记忆、测试。

不包含 ZIP/云端上传、其他技术栈、ARM64 打包、MSI、自动安装工具或目标机运行时、自动推送/合并。已有脚本的宏与 Source 不自动解释，用户指定构建输入目录；只支持单 EXE 安装包。

## 3. 当前现状

基线 main 为 .NET 8 x64 WPF 单工程。启动缺 J-Link DLL 即退出；关窗仅检查当前 FlashPage；FlashFinished 早于后台 Task 真正结束。原配置及日志位于 AppData/JLinkRTTViewer，必须保持兼容。

## 4. 技术方案

保留源码目录/命名空间；sln/csproj/EXE/标题改 DL小工具，显式保留 RootNamespace。左导航约180px、右Frame缓存Page，默认烧录，1280x800、最小1000x700、页内滚动。烧录开始才取得可重试的 loader。全局一次一个重任务，按Task真正终态释放，关窗查看宿主。

项目识别采用 MSBuild17.8+ 属性及引用评估，传统工程结合引用/ProjectTypeGuids；sln ActiveCfg 映射、构建平台与CPU分离。现代 dotnet publish 指定配置/TFM/RID/selfcontained/UseAppHost；Framework 正常引用布局 Restore+Rebuild，不统一覆盖OutDir：备份清空入口正常TargetDir，构建后复制至独占staging。预检SDK、VS MSBuild、targeting pack、ISCC6和中文语言文件，允许指定工具路径。

内部模型 ProjectDescriptor/PackagingOptions/ExtraResourceMapping/PackagingResult；识别、工具发现、执行器、文件事务、模板、profile及编排服务分离。Windows Job Object原子归组子进程，结构化参数且不经shell，异步双流日志，取消等全部后代结束再还原文件。

模板整目录收集，保留native/语言/XML/JSON，只排调试PDB/vshost；额外文件/目录映射安装根下相对路径，拒绝越界、链接与碰撞。默认当前用户安装、开始菜单/可选桌面/安装后启动、标准卸载，不递归删用户数据。应用名/版本/图标可改，版本缺省1.0.0，包名默认应用名_v版本_Setup.exe但不改目标EXE。每项目稳定AppId保存在原AppData下独立profile，可高级指定。

已有iss先构建再投放用户指定输入目录（现代建议脚本旁Publish，Framework正常TargetDir）。非空目录先展示确认再备份；与Framework输出相同只一事务。保留原脚本位置/宏/图标/外部授权/AppId，禁用通用模板信息与资源控件，ISCC /O和/F覆盖包输出及名称。

成功点是最终包提交成功：编译到临时目录，再复制校验到用户输出目录临时文件并同卷原子提交；同名覆盖确认。任何失败/取消恢复原目录、旧包不变；恢复失败保留备份/日志并阻止同目录重试。拒绝盘根、源码根及祖先、含源码目录、链接与危险嵌套。成功留新输出及可定位备份。日志不输出授权内容。

## 5. 影响范围

UI、启动/关闭、烧录初始化、后台任务、配置和构建工具集成发生变化；J-Link编程/强制校验/复位语义保持。原AppData不迁移。无数据库、PLC、采集通讯更改。新添xUnit和Windows进程控制，缺工具/驱动只影响对应功能。

## 6. 实施步骤

| ID | 目标/修改位置 | 验证方式与完成标准 |
|---|---|---|
| P00 | 实施方案、评审记录、功能日志/分支 | 范围对应批准方案，未跟踪文件保留，独立审查通过 |
| P01 | 外壳命名、导航、J-Link延迟、任务宿主 | Release构建；缺DLL仍启动，缓存页和任务终态互斥/关窗不依赖当前页 |
| P02 | 模型、Job执行器、工具发现、xUnit基础 | 参数/进程错误/取消及父退子存活测试，真实工具检测 |
| P03 | 项目发现、MSBuild评估与配置映射 | SDK/Framework WPF/Forms、条件导入/多项目TFM/CPU及sln映射样本 |
| P04 | 路径/目录事务、安装模板、profiles | 资源越界/链接/冲突、稳定ID、名称转义、备份恢复及原子替换测试 |
| P05 | 现代/Framework构建、两模式编排 | 真实构建+ISCC，失败取消恢复、旧包保护、附属输出拒绝 |
| P06 | 打包页面、选择/说明/日志/配置交互 | 中文路径UI操作，模式禁用/目录确认/重复启动/导航关窗验证 |
| P07 | 回归、README/AGENTS、最终审查 | 自动测试和实际安装记录，三个新Reviewer全项目审查，现场未验收项独立列出 |

每步只做当前目标，验证和独立审查通过后，代码与日志定向提交。新增必要返工另列待办。最终停在 codex/dl-tools-packaging，不自动push或merge。

## 7. 验证方案

xUnit覆盖SDK/传统Framework WPF+Forms、net48/8/9、条件与导入、多入口TFM/解决方案映射/外部引用/AnyCPU；缺工具、还原/构建/ISCC错误、中文空格/非法名称、资源越界/碰撞；取消树/后代存活、目录恢复失败、最终提交失败及旧包保护。使用真实ISCC验证安装/启动/稳定AppId升级/卸载保留用户数据。教程UDP脚本验证固定ID及外部授权缺失失败，禁止输出密钥。

UI检查无DLL可打包、缓存输入日志、切页关窗、完成事件已发但Task未终态时互斥、100%/150%缩放。烧录硬件与干净目标机runtime验收分别记录，不能用build替代。

## 8. 风险与约束

原未跟踪 .zcodeignore、AGENTS.md、打包教程.md 保留，不盲目纳入功能提交；AGENTS最终按批准范围更新。外部原项目源码/iss不改；构建输出替换须用户界面确认。任务取消与恢复必须等待Job进程退出。恢复失败不能掩盖成一般失败。安装/硬件环境若缺失，保留明确未验收记录。

## 9. 完成标准

用户能完成导入→理解识别→指定输出/包名→生成可安装运行包。所有开发步骤与必要自动验证通过，三路最终审查无阻塞。构建、自动测试、安装实测、烧录硬件实测分别记录；未做的现场验收不宣称通过。
