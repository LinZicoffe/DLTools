# IP 纯净度验收记录

日期：2026-11-25。基线 main `7e6e453`，功能分支 `feature/ip-purity-check`。尚未合并。

## 实现与接入

- 第七个页面「IP 纯净度」，排在「IP 扫描」之后。现有局域网 ICMP 扫描未改。
- 原生 WPF：`Pages/IpPurityPage.xaml`、`ViewModels/IpPurityViewModel.cs`。不使用 WebView。
- 分数只评价出口 IP 的网络身份和隐私标记。AI 连通性、出口一致性、UDP 泄漏单独显示。AI 服务看到另一个出口时，单独查询并评分。
- 请求只发往固定端点，不跟随重定向，User-Agent 为 `DLTools/1.0`。跟随系统代理。不把 DNS 解析到保留地址当作失败（TUN fake-ip 会解析到 `198.18.0.0/15`）。
- UDP 用 STUN 代替 WebRTC。只有境外、公网、与出口同协议且地址不同的结果算泄漏。
- 登记 `BackgroundOperationKind.IpPurityCheck`，与烧录、打包、授权、IP 扫描、C盘清理互斥。关窗确认后放弃尚未上屏的结果，并等待在途请求结束，不使用烧录的 15 秒上限。

## 自动验证

| 检查 | 结果 |
|---|---|
| Release 构建 | 随全量测试编译，0 警告、0 错误 |
| 排除实网后的全量测试 | 600 通过、0 失败、44 跳过，共 644 项，约 1 分 14 秒。过滤条件 `FullyQualifiedName!~IpPurityIntegration` |
| v1 评分差分 | `IpPurityScoringTests` 用 `score-v1.json`（6 个命名场景加 240 个随机组合）对总分、等级、说明和每一项扣分逐字段比对。夹具由上游 `score.js` 提交 `96166cf` 的副本 `upstream-score.js` 经 `generate-score-v1.js`（种子 `mulberry32(20261125)`）生成 |
| 取消与超时 | 用户取消不发布完成评分；200 毫秒总时限把未完成项收成失败并标记超时 |
| 关窗迟到结果 | `Start_RendersTheSnapshotAndCancelReturnsToTheIdleState` 在 `AbandonPendingResults` 之后先放出完成快照（100 分、`203.0.113.7`），再取消。界面仍停在检测中的空分数，不上屏。关窗确认在 `RequestCancellation` 之前调用放弃 |
| 实网 | `DLTOOLS_IPPURITY_INTEGRATION=1` 下 1 次通过，约 4 秒。证据在测试输出目录 `TestResults/ip-purity-live.json`，不入库 |

实网当次（地址不写入本文）：Cloudflare 看到的出口被标为机房；ChatGPT 与 Claude 都走这个出口并给出风险判定；境外 STUN 映射到另一个地址，记为 UDP 泄漏；一台国内 STUN 超时。v2 分数 65，等级「一般」。默认测试不跑这项。

## 窗口

`docs/images/` 下有 8 张，由 `IpPurityScreenshotTests` 在 `DLTOOLS_IPPURITY_SHOTS=1` 时用真实窗口渲染：

| 状态 | 1280×800 | 1000×700 |
|---|---|---|
| 未检测 | ip-purity-idle-1280.png | ip-purity-idle-1000.png |
| 检测中 | ip-purity-running-1280.png | ip-purity-running-1000.png |
| 检测完成 | ip-purity-done-1280.png | ip-purity-done-1000.png |
| 取消 | ip-purity-cancel-1280.png | ip-purity-cancel-1000.png |

完成态和检测中使用文档地址 `203.0.113.7`、`198.51.100.9`，不是本机真实地址。检测中不提前显示分流出口。取消图是把界面设回空状态后拍摄，不是鼠标点「取消」的录像。1000×700 下标题与按钮不重叠，长内容在页面内滚动。

## 尚未现场确认

- 没有用鼠标点真正的关窗确认框。代码路径是：确认后调用 `AbandonIpPurity()`，再取消任务并无超时等待。按钮取消和放弃迟到快照有单元测试。
- 导航胶囊在「IP 纯净度」上的落位见截图。其余六项的胶囊没有在本轮逐张重拍。
- 本机 IPv6 探测没有单独断言成功。
- 页面审计脚本未跑。对比度按设计稿手算，正文与状态色不低于 4.5:1。导航选中项原有对比度不在本次范围。
