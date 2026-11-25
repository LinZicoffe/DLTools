# IP纯净度检测功能更新日志

## 当前目标

把 ip-purity-test（出口 IP 纯净度与 ChatGPT / Claude 连通性检测）原生移植进 DL小工具，成为第七个页面「IP 纯净度」：点一次按钮，约 10 秒完成 6 项检查并逐项落位，给出评分、等级、一句话结论、AI 直连状态、出口一致性与 UDP 出口。

评分采用 2026-11-25 用户确认的「建议规则 + 分流时分别评估 AI 服务出口」：

- 分数只评价出口 IP 本身（网络身份、隐私标记）；AI 可用性、出口一致性、UDP 泄漏是路由状况，作为状态单列，不计入分数。
- AI 服务看到的出口与当前出口不同（分流）时，单独查询并评估那个出口的类型、标记与纯净度。
- 网页版规则（v1）原样移植，作为扣分引擎与差分测试基准；未知项不按安全计分、隐私标记重叠递减、机房扣分随情报准确度递减这三条保持不变。

## 开发约束

1. 最终实现是原生 WPF XAML（`Pages/IpPurityPage.xaml` + `ViewModels/IpPurityViewModel.cs`），不引入 WebView2，不运行 HTML。`原型/DL小工具-IP纯净度检测.html` 只是评审稿与界面规格。
2. 不改现有六个页面的行为。导航只新增一项（「IP 纯净度」排在「IP 扫描」之后），`MainWindow.xaml.cs` 的胶囊序号随之顺延。
3. 后台任务登记到 `BackgroundTaskHost`（新增 `BackgroundOperationKind.IpPurityCheck`），关窗走既有确认链；取消后等待在途请求退出。
4. 网络请求只发往固定端点白名单：协议、主机、端口、路径与查询串精确匹配（唯一的可变段是 IPinfo 情报地址里的 IP，必须是校验过的 IP 字面量）；仅 http/https、不跟随重定向、不携带 Cookie、不伪装浏览器 UA、不保存结果。桌面程序没有 WebRTC，改用 STUN/UDP 探测；只有境外 STUN 返回与出口**同协议**且不同的地址才算泄漏。
5. v1 评分与上游 `score.js` 逐项一致（标签与依据文案逐字相同），以差分测试为准；v2 只改变「哪些项计入分数」与结论文案。差分基准的来源与做法见「其他」第 5 条。
6. 基线 main / 7e6e453，分支 `feature/ip-purity-check`。开发阶段不合并、不推送；提交与推送由用户本人在 PowerShell 执行（见「其他」第 3 条）。
7. 保留命名空间与源目录 `烧录上位机程序`；不触碰 `%APPDATA%/JLinkRTTViewer/` 下任何既有配置。
8. 后台结果一律经 Dispatcher 投递到 UI 线程后再更新绑定属性；取消或关窗之后迟到的结果不得写入界面。
9. 整次检测有 30 秒总时限：到时仍未完成的请求按「请求超时」收尾，用已有结果出结论。取消必须能真正中断在途的 HTTP、DNS 与 UDP 等待（关窗确认后对非烧录任务是无上限等待的）。

## 验收标准

1. `dotnet build "DL小工具.sln" -c Release` 0 警告 0 错误；`dotnet test` 全量通过（既有集成测试仍按原开关跳过）。
2. 评分差分测试通过：C# 的 v1 移植在固定随机用例上与上游 `score.js` 的输出逐字段一致。
3. 本机（TUN + fake-ip + 系统代理）实网检测跑通：能拿到出口 IP 与情报、两个 AI 服务的判定、UDP 出口与一致性；结论与实际网络状况相符。
4. 检测中「取消」与「关窗」两条路径都验证过：取消后回到检测前的状态，关窗确认后进程能正常退出。
5. 真实窗口 1280×800 与最小 1000×700 两种尺寸下，未检测 / 检测中 / 检测完成 / 取消 四种状态截图无错位、无截断。
6. 导航变成 7 项，选中每一项时胶囊都落在该项上；原有六个页面的代码与行为没有变化。

## 待办列表

| ID | 待办描述 | 预计代码位置 | 状态 |
|----|----------|--------------|------|
| P01 | 设计评审稿与本日志入库 | 原型/DL小工具-IP纯净度检测.html；IP纯净度检测功能更新日志.md | 已完成 |
| P02 | 数据模型、端点白名单与静态数据（服务表、ASN、关键词、地区代码） | Models/IpPurity/IpPurityModels.cs；Services/IpPurity/IpPurityEndpoints.cs；Services/IpPurity/IpPurityData.cs；tests/DLTools.Tests/IpPurityEndpointTests.cs、IpPurityDataTests.cs | 已完成 |
| P03 | HTTP 传输：系统代理、诚实 UA、不跟随重定向、超时与错误归一化、端点守卫 | Services/IpPurity/IpPurityTransport.cs；tests/DLTools.Tests/IpPurityTransportTests.cs | 已完成 |
| P04 | 检测逻辑：出口链、情报链、服务判定、出口汇总（移植 checks.js / api.js） | Services/IpPurity/IpPurityChecks.cs；tests/DLTools.Tests/IpPurityChecksTests.cs | 已完成 |
| P05 | STUN/UDP 出口探测（替代 WebRTC） | Services/IpPurity/StunProbe.cs；tests/DLTools.Tests/StunProbeTests.cs | 已完成 |
| P06 | 评分：v1 原样移植 + v2 建议规则 + 单出口纯净度；对上游 score.js 的差分测试 | Services/IpPurity/IpPurityScoring.cs；tests/DLTools.Tests/IpPurityScoringTests.cs；tests/DLTools.Tests/Fixtures/IpPurity/ | 已完成 |
| P07 | 编排器：6 个阶段、增量快照、30 秒总时限、取消与迟到结果、分流出口分别评估 | Services/IpPurity/IpPurityRunner.cs；tests/DLTools.Tests/IpPurityRunnerTests.cs | 已完成 |
| P08 | ViewModel：状态机、全局任务槽、复制与打开链接；新增任务类型 `IpPurityCheck` | ViewModels/IpPurityViewModel.cs；Services/BackgroundTaskHost.cs；tests/DLTools.Tests/IpPurityViewModelTests.cs | 已完成 |
| P09 | 页面 XAML、页面内样式与评分环；导航第 7 项（含胶囊序号顺延与关窗提示）；真实窗口截图 | Pages/IpPurityPage.*；Controls/IpPurityRing.cs；MainWindow.xaml；MainWindow.xaml.cs；ViewModels/MainWindowViewModel.cs；docs/images/ip-purity-*.png | 已完成 |
| P10 | 实网验证、README 说明、验收与评审记录 | tests/DLTools.Tests/IpPurityIntegrationTests.cs；README.md；docs/验收记录-IP纯净度检测.md；docs/评审记录-IP纯净度检测.md | 已完成 |
| R1 | 关窗放弃之后仍把完成快照送进界面，断言代际号会丢掉它 | tests/DLTools.Tests/IpPurityViewModelTests.cs | 已完成 |
| R2 | 把 v1 差分夹具的上游提交、种子和生成脚本写进仓库 | tests/DLTools.Tests/Fixtures/IpPurity/generate-score-v1.js；upstream-score.js；IP纯净度检测功能更新日志.md | 已完成 |

<!-- ID 用 ASCII 字母或数字；最终审查的返工待办用 R 前缀（R1、R2…）；
     状态只用：未开始 / 进行中 / 待审查 / 阻塞 / 已完成 -->

## 已知风险

1. **端点域名的解析结果不校验内网/保留地址。** 本机处于 TUN + fake-ip，域名会解析到 198.18.0.0/15；若在解析后拒绝保留地址，功能在常见代理环境下会全部失败。安全边界改由「固定端点白名单（整条 URL 精确匹配）+ 不跟随重定向」承担。
2. **ip-api.com 只支持 HTTP**（HTTPS 为付费功能），响应在明文链路上可能被篡改。它只在主情报源失败时作为回退，置信度为 medium，且要求返回的地址与被查询的出口一致才采用。
3. **STUN/UDP 探测不等同于浏览器的 WebRTC。** 它只说明 UDP 流量从哪个地址出去；仅设置了系统代理、未开 TUN 或 UDP 转发的环境下，会显示为泄漏。没有任何 STUN 服务器响应时结论是「无法判断」，不是「未泄漏」。
4. **第三方接口可能变化。** IPinfo 的 widget/demo 与 ip-api.com 都不是带服务等级的正式接口，可能限流或改结构；某一来源失败时会回退到下一来源并在页面上说明。
5. **地区支持列表是上游 2026-11-24 的快照**（`regionsChecked`），不会自动更新。

## 其他

1. 范围外、暂不实现：地区列表自动更新；检测结果保存与历史；深色主题；v1/v2 规则在界面上切换（界面固定使用建议规则，v1 只用于扣分引擎与测试）。
2. **审查流程变更（用户 2026-11-25 指示）**：不再逐步独立审查，P02–P10 每步只做本地验证（编译 + 针对性测试）后直接记「已完成」；全部编写完成后统一检查一次（Release 构建、全量测试、实网与真实窗口验证、一轮独立审查）。因此待办列表各步的「已完成」不含独立审查结论，以最后的统一检查为准。
3. 提交方式：ZCode 内对 `烧录上位机程序` 仓库的任何 `git commit` / `git push` 都会被 Mimosa 钩子拦截，按既定约定由用户本人在 PowerShell 执行。开发期间每步只做本地验证，全部步骤完成后统一给出按待办分提交的命令块。
4. 仓库里原有的未跟踪文件全部留在原处，排除于本任务与审查之外：`.omc/`、`.zcode/`、`.zcodeignore`、`CLAUDE.md`、`DLTools_icon.ico`、`DLTools_icon.svg`、`DLTools_icon_H_四色芯片.svg`、`DLTools_icon_H_四色芯片_1.ico`、`UIPreview/`、`dltools-ui-redesign.html`、`分析高速下载项目_2026-11-21_10-41.md`、`打包教程.md`。
5. v1 差分基准：上游 `assets/js/score.js` 快照为提交 `96166cf448de3e185805d2e370951e5660996085`（2026-11-24，提交说明「在 README 中写明线上地址，方便直接打开检测。」），副本在 `tests/DLTools.Tests/Fixtures/IpPurity/upstream-score.js`。副本与上游内容逐字相同，行尾是 CRLF，所以文件哈希与 GitHub 上的 LF 原文不同。生成脚本 `generate-score-v1.js` 和这份副本都是出处，不参与测试编译；测试只复制 `score-v1.json`。脚本用 `mulberry32(20261125)` 产生 6 个命名场景和 240 个随机组合，共 246 条。在仓库根目录重跑：`node "tests/DLTools.Tests/Fixtures/IpPurity/generate-score-v1.js"`。2026-11-25 重跑结果与现有夹具字节相同（SHA256 `E878F9ECCAAD1B71B7AC097ECFB12A02C3B7B2DB0A5C508FDB00AE3B6ECB979C`）。
6. 统一审查里记下、本轮不改的建议：检测中截图没有把 `IsBusy` 设为 true，按钮仍是空闲态；`OpenSite` 没有捕获无法打开浏览器的异常；30 秒总时限缺少「已有出口、其余请求超时后仍出完成结论」的用例；进行中的快照可能乱序上屏，完成态在全部任务结束后才发布。

---

## 更新记录

### 2026-11-25

*V2*：

- 开发完成：三名最终审查均通过。R1 让关窗测试在放弃之后真正送出完成快照；R2 补上 v1 夹具的上游提交、种子和生成脚本
- 文档更新：P09 去掉未改动的 `Resources/UiStyles.xaml`；差分副本注明为 CRLF

*V1*：

- 新增功能：完成 P02–P10。服务层、页面、导航第 7 项、关窗放弃迟到结果、评分差分、实网检测与窗口截图均已落地
- 修复问题：关窗确认只取消任务令牌时，检测仍可能把完成快照写上界面；确认退出前先放弃未上屏结果，并在评分前检查用户取消
- 文档更新：README 增加 IP 纯净度说明；验收与评审记录见 docs/
