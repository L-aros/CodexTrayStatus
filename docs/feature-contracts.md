# Feature 01: 数据新鲜度共享契约

实现范围：C# 5 / .NET Framework 4.8 原生程序及本地网页；不含提醒、额度历史、预测或持久化缓存。

## 时间与来源

所有 `long` 时间均为 UTC Unix 毫秒；可空时间的 null 表示未知。`AppSnapshot.RefreshedAt` 保持整轮结束时间的旧契约，不能用于新鲜度判断。`QuotaResult.RefreshedAt` 是在线接收时间或可信本地事件时间；未知为 0。

`AppSnapshot.RefreshState`：`AttemptId`（本轮 UUID）、`AttemptedAt`、`CompletedAt`、`IsRefreshing`。托盘接受刷新时立即发布开始状态；重复请求由现有互斥合并。服务单独调用也返回完整轮次。只有原生刷新协调器发布的 AttemptId 代表 UI 请求生命周期。

`AppSnapshot.QuotaState` 是权威额度状态；发布完成时 `QuotaResult.State` 与其为同一对象，后者仅用于 FetchQuotaAsync 的返回值兼容。不要修改发布状态。合并保留值时复制 QuotaResult 与 DataState，不改写上一轮状态。

`AppSnapshot.UsageState` 描述完整的 Today / Daily 数据集合，两者整体接受或保留。

两路均使用 `DataState`：

| 字段 | 语义 |
|---|---|
| Source | official / local / none；保留值仍保留原来源 |
| Delivery | updated / fallback / retained / unavailable |
| Scope | account / unattributed_local_quota / local_logs；日志是本机集合，不宣称属于当前账号 |
| AccountKey | 已确认额度观测所属账号的不可逆键；null 为未知。本地 JSONL 额度无可信账号归属，因此始终 null |
| ObservedAt | 官方响应接收时间、本地额度事件时间、完整日志扫描完成时间 |
| TimeBasis | response_received / event_timestamp / scan_completed / unknown |
| LastSuccessAt | 最近接受该路结果的完成时间；重新读取本地快照不改变 ObservedAt |
| LastOfficialSuccessAt | 最近同账号在线获取成功时间；回退不推进 |
| LastAttemptAt | 本路最近尝试时间 |
| LastAttemptOutcome | success / partial / failed；在线失败但回退成功为 partial |
| ErrorCode | official_timeout / official_failed / quota_failed / usage_scan_failed / refresh_failed / unknown_metadata；成功清除 |
| ObservationId | 官方每次成功响应的 UUID；本地由事件时间和窗口内容摘要构成；未知时不可采样 |
| IsNewObservation | 本轮接受新额度观测。重复本地观测、保留值为 false；日志目前始终 false |
| IsComplete | 该数据集合完整处理；日志枚举或读取失败不发布部分总量 |
| CoverageStartDate / CoverageEndDate | 本地 yyyy-MM-dd；日志实际扫描的日期范围 |

状态的 IsNewObservation 不是消费一次后自动清除的消息。定时重发 API 不产生新事件，下游必须用 ObservationId 去重；不可仅按收到一次 JSON 就写入历史。它也不表示可信：未知账号的首次本地观测依然不可自动使用。

## 账号与窗口身份

`QuotaService.AccountIdentity(accountId)`：SHA-256(`CodexTrayStatus/account/v1:` + 实际请求的 account_id)，返回 `sha256:` 加小写十六进制摘要。空账号返回 null；不从 token 推断身份、不输出原 account_id 或 token。此键只用于隔离，不是访问授权凭证。

`AppSnapshot.RequestedAccountKey` 是本轮官方请求实际使用的账号摘要，供合并隔离；它不是本地回退数据的归属，不将它复制进本地 AccountKey。网页不输出 RequestedAccountKey。账号改变或身份不可读时不保留以前已确认账号的额度。本地无归属值可继续作为明确标注的参考显示，不能写入任意账号历史。

`RateLimitWindow` 保留 Id、Label、UsedPercent、RemainingPercent、ResetsAt，新增：

- `WindowKey`：稳定池标识与响应窗口名组成的带长度前缀键。主池为 account，命名附加池为 pool:<名称>；附加池没有名称时 null。不同池即使 Label 相同也保留。
- `WindowSeconds`：来源实际提供的窗口时长，未知为 null。不从 Label 猜测。
- `ResetsAt`：来源给出的绝对重置时间，或响应/事件时间加来源相对重置秒数。它是当前观测的周期结束边界；不会因当前时间跨界而移到下一周期。

额度历史推荐身份为 `(AccountKey, WindowKey, ResetsAt)`，每条样本再保存 ObservationId、ObservedAt、WindowSeconds。不得仅靠 Label。没有稳定键或周期结束边界时禁用自动判断。需要周期开始时可由结束减时长获得名义边界，但不得宣称是服务器另外报告的真实开始时间。

## 有效性

`DataFreshness.EvaluateQuota(window, state, nowMilliseconds)` 与 `EvaluateUsage(state, hasValue, nowMilliseconds)` 为无 I/O 纯判断。`MaxAgeMilliseconds=900000`（15 分钟），未来容差 60 秒；到边界即过期。

返回 `ValidityResult`：HasValue、IsFresh、IsUsable、IsNewObservation、ExpiresAt、ReasonCodes。

- IsFresh：有值且时间可信、未过期；非法额度百分比为 false。它不等同于身份可自动使用。
- IsUsable：新鲜、完整且满足身份/窗口要求；日志还需覆盖当前本地日期。未知账号或窗口可以时间新鲜但 IsUsable=false。
- 额度 ExpiresAt 是观测时间+15分钟与 ResetsAt 中较早者。日志是成功完整扫描时间+15分钟。
- ReasonCodes：no_value、unknown_time、invalid_time、expired、incomplete、invalid_value、unknown_account、unknown_window、date_not_covered。
- 提醒/预测至少检查 IsUsable；要求在线时另检查 Source=official，需要新样本时另检查 IsNewObservation 并对 ObservationId 去重。
- 数据可以被保留显示，但不可把过期值推算成 100% 或重置后 0%。未知百分比不等于零。

## 合并与兼容

`DataFreshness.Merge(previous,current)` 接受两路结果，失败保留对应的完整数据与观测时间，更新尝试状态。可信旧值比本地候选更新时保留旧值；账号隔离优先于保留值。官方恢复后完整替换窗口集合，不拼接旧窗口。

`Synchronize(snapshot)` 令 Quota.State 与 QuotaState 一致，并在已知日志覆盖日期过期时清除 Today；Daily 保留用于历史查看。原生面板、任务栏及 Web API 另外防止将旧日期 Today 标为今日。

旧快照无元数据时允许兼容显示，未知时间不可自动使用；无新增注册表设置、无数据库迁移。日志目录不存在代表空集合；目录不可访问、枚举中断、实际需要重读的文件失败则整路失败，保留上次完整统计。未变化文件可以复用解析缓存。

本地额度候选维持最多 12 个文件的轻量预算，在预算内按事件时间选最新；不能保证找到全目录绝对最新观测。缺少事件时间时文件修改时间仅兼容解析相对重置字段，ObservedAt=null / TimeBasis=unknown。

## 本地网页 API

`api/usage` 原有 Quota 数组、Today、Daily、Error、RefreshedAt 等字段保留，兼容新增 RefreshState、QuotaState、UsageState、QuotaValidity（与 Quota 数组逐项对应）、UsageValidity、EvaluatedAt。

原生每 5 秒重新评估并发布；网页每 5 秒读取、每秒更新状态和重置倒计时。网页断连显示连接中断，利用 ExpiresAt 老化显示。后续原生消费者应调用 Evaluate，不能长期缓存序列化布尔值。保持现有本地 API 私密路径及同源限制，不暴露原始日志、凭据或异常堆栈。

## 验证入口

`scripts/test-native.ps1`：既有额度解析、DataFreshnessSmoke 合成集成测试、WinForms 面板回归与 Web API 回归。新鲜度测试只使用临时合成文件与假 HttpMessageHandler，不读取真实账号或访问额度服务。

若机器已有 Node，入口还运行 WebFreshnessSmoke.js，使用实际网页状态函数验证回退、过期、断连、恢复和失败文案；未安装 Node 时明确跳过，不增加程序运行依赖。本次验证环境已运行通过。该测试不代替完整浏览器布局测试。

`scripts/build-native.ps1 -SkipInstaller -NativeOutputDirectory freshness-verification` 构建独立程序，不运行安装或常驻应用。

# Feature 02: 低额度与恢复提醒

实现使用现有 DataState / RateLimitWindow，不新增另一套额度观测 DTO，不改变 Feature 01 时间或来源含义。生产入口仅在 TrayApplicationContext.RefreshAsync 收到本次结果后调用 ReminderCoordinator.Process；ApplySnapshot、5 秒状态发布、网页轮询及设置保存不会再次消费观测。提醒内部失败不会使额度刷新失败。

## 接受观测与周期

提醒要求 Source=official、Delivery=updated、Scope=account、TimeBasis=response_received、IsNewObservation=true、非空 ObservationId，并逐窗口调用 DataFreshness.EvaluateQuota 得到 IsUsable=true / IsNewObservation=true。另外检查 RemainingPercent 为有限的 0..100，与 UsedPercent 互补；重置时间在可表示日期范围内。本地回退、保留值、未知元数据、缺失账号/窗口/周期、过期数据均不能触发或推进提醒状态。额度有效但日志统计失败时仍可提醒。

身份沿用 AccountKey / WindowKey；不使用 Label 或 RequestedAccountKey。每个账号/窗口保存一个当前 CycleEnd 锚点、最近 ObservationId / ObservedAt 和消费状态。相同 ID、ObservedAt 不递增时忽略。周期比较使用固定锚点与 60 秒容差：±60 秒属于同周期，不随每次漂移移动锚点；更早周期忽略；向后移动超过 60 秒且本次 ObservedAt 已到原 CycleEnd 才确认新周期。原周期结束前出现大幅重置变更属于不明确数据，暂不消费，等待边界后的在线确认。这是提醒内部保守归并规则；额度历史仍保存来源原始 ResetsAt，不改写历史样本的周期边界。

## 状态机与通知

- 默认启用总开关、20/10/0 三档及恢复提醒。每档可独立关闭；不开放任意百分比。
- 首次有效低值也触发。25→8 只发最严重且已启用的 10% 档，并消耗所有已达到的档位；之后到 0 再提醒。0.4% 不算耗尽。判断使用原值，不使用显示舍入。
- 同周期每档至多一次；数值回升不重新武装。不同额度池即便同名也独立。通知正文带窗口 Id 区分额度池，不含账号原始信息。
- 已确认 ≤20% 后达到 ≥22% 才恢复，固定 2 个百分点缓冲；0→5 不算退出低额度。每个低额度周期最多一次恢复，可在后续周期的新观测中确认。新周期仍低时继续处于低额度状态。到期本身没有事件。
- 总开关、档位关闭、恢复关闭、定时免打扰、临时暂停均继续消费状态，结束或开启后不补发。状态损坏等阻断错误则暂停整个提醒处理，必须显式重建。
- 同轮多个窗口合并一次系统 NotifyIcon 通知，点击打开概览。系统通知文本过长时截断并提示点击查看。没有新增声音或常驻进程。
- 先原子持久化消费状态，再调用 IReminderNotificationSink；调用失败不重试。至多一次发送尝试，不承诺系统送达。提交后调用前崩溃可能漏发，重启不会重复。

ReminderEngine.Evaluate(snapshot, preferences, previousState, now) 为无 I/O 纯状态转换，返回新 State、Events、Changed，不修改输入状态。ReminderCoordinator 负责提交及调用注入的通知接收器；生产由 UI 线程串行调用。时间参数 DateTimeOffset 的日期/小时用于本地免打扰，Unix 毫秒用于观测与暂停；生产传 DateTimeOffset.Now，测试使用固定时间。

## 共享设置兼容契约（后续功能必须保持）

WebPreferences 兼容新增可空 Reminders。GET api/preferences 返回完整已解析设置；POST 中缺少或 null 的 Reminders 表示保留当前提醒设置，其内部缺少或 null 的成员也表示保留，false / 0 是明确赋值。ReminderPreferences.Merge(current, patch) 在 UI 线程用最新设置合并后校验。服务端可提前校验，但不得把提前读取的完整对象当作最终并发合并结果。

| Reminders 字段 | 类型 | 默认/范围 |
|---|---|---|
| Version | int? | 1；其他版本拒绝 |
| Enabled | bool? | true |
| Threshold20 / Threshold10 / Threshold0 | bool? | true |
| RecoveryEnabled | bool? | true |
| QuietHoursEnabled | bool? | false |
| QuietStartMinutes / QuietEndMinutes | int? | 1380 / 480；0..1439，起止不能相同 |
| SnoozeUntilUtcMs | long? | 0 表示取消；非负 Unix 毫秒，最大 253402300799999 |

定时免打扰使用系统本地时间、起点包含终点不包含、允许跨午夜。临时暂停保存绝对 UTC 截止；网页和托盘支持一小时、明天 08:00、取消临时暂停。取消临时暂停不改变总开关或定时免打扰。网页“保持当前暂停时间”不提交 SnoozeUntilUtcMs；币种快捷操作不提交 Reminders，避免覆盖其他入口最新提醒设置。网页未保存编辑不被轮询覆盖。

设置保存至原注册表路径 HKCU\\Software\\CodexTrayStatus 的单个字符串值 RemindersV1（完整 JSON），旧值缺失采用默认。原有字段位置不迁移。读取损坏、未知版本或写入失败展示原因，失败保存保留前一有效设置；读取错误期间暂停自动提醒，重新保存设置恢复。所有后续设置页面必须保留未涉及的嵌套设置，不能因新增 bool 的默认 false 覆盖既有偏好。

## 去重存储与失败状态

%LOCALAPPDATA%\\CodexTrayStatus\\reminders-state.json 保存 Version=1、BaselineOnly、Windows；每个账号/窗口仅一条状态，不保存额度采样历史，不依赖日用量历史。Windows 字段为 AccountKey、WindowKey、CycleEnd、ObservedAt、ObservationId、Remaining、ConsumedThresholds（20/10/0 位掩码 1/2/4）、PendingRecoveryCycle、RecoveryConsumed。账号仅摘要；不存凭据或日志正文。

同目录临时文件写入并 Flush(true)，首次 Move、后续 File.Replace 并保留 .bak。坏主文件不会自动回滚备份，避免重放备份后已消费事件；存在备份但主文件丢失同样暂停。校验必需字段、版本、值及重复身份。最多 4 MiB，达到上限暂停而非静默淘汰活跃去重状态；目前保留每个曾观测账号/窗口的最后状态，无每日采样增长。

只读 API 的 ReminderStatus（位于 api/usage 顶层）包含 StorageAvailable、Suppression、SnoozeUntilUtcMs、ErrorCode；不暴露去重明细。Suppression 为 null / disabled / snoozed / quiet_hours / settings_error；ErrorCode 为 state_read_failed / state_write_failed / state_evaluation_failed / notification_failed / settings_read_failed / settings_write_failed。与其他状态一样，每 5 秒重新发布，网页断连明确不再确认运行状态。

POST api/reminders/rebuild，空对象请求，沿用私密路径及同源限制，成功返回 {}，失败 400。网页仅在 StorageAvailable=false 时展示重建按钮和影响说明。重建提交空状态并标记 BaselineOnly=true；每个新遇到的账号/窗口首次有效观测只建立基线，之后更低的新档位及恢复正常处理。该标记持久化，防止重启后对尚未重新见到的账号补报。

## Feature 02 验证与构建

- scripts/test-native.ps1 新增 ReminderServiceSmoke.exe（固定时间、合成快照、假通知接收器及临时目录），覆盖跨阈值、0.4%、首次低值、重启、池/账号隔离、周期漂移/到期、恢复缓冲、未知/过期/重复拒绝、免打扰、开关、原子文件、损坏/缺字段、重建基线、写失败、通知失败及提交后崩溃。
- WebDashboardSmoke 扩展旧请求/嵌套补丁兼容、非法时间、状态序列化、重建及跨域拒绝；原有额度、新鲜度、WinForms、网页显示测试保留。
- node scripts/run-web-tests.cjs scripts/test-web.cjs scripts/test-web-reminders.cjs：实际无头 Edge，合成服务，覆盖已有页面及提醒设置、暂停、错误/重建显示、移动端。不会调用生产通知或读取真实账号。
- scripts/build-native.ps1 -SkipInstaller -NativeOutputDirectory reminders-verification：独立构建，不安装、不启动生产常驻应用。

限制：系统通知是否最终显示及点击交互需在用户桌面人工验收，本轮没有发真实测试通知；提前发生的手动重置因周期不明确可能延后或不提醒。新鲜度规则为前置依赖，不依赖额度历史、预测、长期统计或费率维护。

# Feature 03: 任务栏显示定制

实现仍为 C# 5 / .NET Framework 4.8 分层 Win32 窗口，不新增运行依赖，不扩展多显示器。生产网页仍由 StatisticsServer 提供静态资源并在浏览器打开；不扩展旧 DashboardForm 或 Electron 源码。

## 设置与兼容

WebPreferences 新增可空 TaskbarDisplay；缺少或 null 保留当前值，嵌套成员缺少/null 同样保留，false 明确关闭。TaskbarDisplayPreferences.Merge 是纯合并/校验；不修改输入。完整默认值：

| 字段 | 类型 | 默认 / 范围 |
|---|---|---|
| Version | int? | 1；其他版本拒绝 |
| SelectionMode | string | default / selected / minimum；默认 default |
| SelectedWindowKey | string | 默认空字符串；selected 必须非空白；最多 512 字符、不含控制字符 |
| ShowCost | bool? | true |
| ShowCountdown | bool? | true |

直接使用 Feature 01 的 WindowKey，精确区分额度池，不从 Label 或 Id 构造身份。缺失窗口键仍保留设置，窗口恢复后自动显示；同键出现多个窗口视为歧义，不猜测。没有稳定键的窗口不出现在指定列表。

保存位置为 HKCU\Software\CodexTrayStatus 的单个字符串 TaskbarDisplayV1（完整 JSON）。TaskbarSettingsStore 注入读取/写入委托；只有写入成功才替换 Current。缺失值加载默认；损坏或未来版本加载默认并通过 WebPreferences.TaskbarSettingsError 提示，读取不覆盖原值，显式重新保存恢复。

POST api/preferences 现在允许旧基础字段的部分补丁：RefreshInterval / ShowRemaining / AutoStart / Currency / ExchangeRate 缺失保留，显式 null 拒绝；原完整请求仍兼容。WebPreferencePatch.Parse 保存字段存在性；服务端仅提前校验，生产 ApplyWebPreferences 在 UI 线程使用最新值再次合并。TaskbarDisplay / Reminders 保持各自可空补丁契约。任务栏布尔字段要求 JSON boolean，不接受字符串。

网页只提交相对表单初始值发生变化的字段，未修改的提醒、币种、暂停时间均不提交。币种快捷入口只提交 Currency。轮询不覆盖未保存草稿；旧客户端未携带 TaskbarDisplay 不会重置新增默认值。提醒设置错误时，不更改任何控件直接再次保存仍可重写提醒设置恢复。

生产持久化先于内存/画面更新。只写发生变化的旧基础字段，嵌套设置仅在请求携带时写入；TaskbarDisplayV1 是最后一次持久化操作。失败时内存/画面不变，其他已写字段尽力回滚；回滚失败明确提示部分旧设置无法恢复，不声称跨注册表值事务。成功持久化后不把位图或后续状态发布异常误报为保存失败，维护定时器重试显示。

WebPreferences.Revision 为本次进程内发布设置的递增序号，仅用于网页忽略过时响应，不是持久化版本或乐观锁，不跨进程比较。不同字段并发修改由 UI 线程补丁合并，同一字段按处理顺序最后一次生效。以后功能若绕过此入口保存设置，仍应发布完整设置。

## 选择、展示与新鲜度

TaskbarPresenter.Select / Build 无 I/O，时间参数为 UTC Unix 毫秒；复用 DataFreshness.EvaluateQuota / EvaluateUsage。TaskbarPresentation 不承载历史或提醒状态。

- default：保留优先首个 Label=5h、否则首个非空窗口；正常在线数据仍为“2H · 剩余 72%”和“今日 Token · 费用”两行。
- selected：精确匹配 WindowKey；消失/歧义显示“所选窗口暂无数据”、--，不会切换其他窗口。
- minimum：只比较新鲜、完整、两种百分比有限且在 0..100 范围并互补的数据；直接比较未取整的 RemainingPercent，不随 ShowRemaining 改变。并列优先 5h，其余按 WindowKey、Id 的 ordinal 顺序。无候选显示额度未知/--。这是展示选择，不是提醒或预测自动决策，时间新鲜的本地参考数据可参与，但仍明确标注参考。
- 默认/指定模式允许保留数值展示；未知/非法百分比显示 --，过期或未知时间显示“旧值”并使用中性灰。新鲜但缺少自动使用条件或来源非 official 显示“参考”。不会到期后推算 100%。
- 非默认模式保留窗口标签再追加倒计时。倒计时沿用 D/H/M，正数小于一分钟显示 1M，已到期显示待更新，未知/越界时间退回窗口标签。关闭后显示窗口标签。
- ShowCost=false 保留今日 Token；开启沿用全局币种汇率，两位小数。含待定价 Token 时费用加 ≥；乘法溢出显示费用过大。今日日期不匹配显示今日暂无统计；陈旧统计标旧值。
- 任务栏、托盘菜单额度行、托盘图标使用同一选择。图标未知/陈旧中性灰；颜色阈值仍是剩余 15/40，不改变提醒的 20/10/0 策略。

## 预览与更新

GET api/taskbar-preview 返回已应用设置的 TaskbarPresentation；POST 同路径接受 WebPreferencePatch 作为只读草稿预览，既不持久化也不触发额度刷新。沿用私密路径、同源 POST 与 4096 字节请求限制。返回字段：WindowKey、Prefix、CompactPrefix、Percent、Secondary、CompactSecondary、Status、Detail、Accent、EvaluatedAt、Revision。Status=fresh/reference/stale/unknown/missing；Accent=green/amber/red/neutral。Detail 为可读的新鲜度/选择原因。

网页预览调用原生 Presenter，不复制选择规则。草稿明确标未保存；保存响应后立即重新取已应用预览。请求序号丢弃已失效的预览响应，Revision 防止旧设置 GET 覆盖新保存。设置页每 1.5 秒更新文字预览，沿用全页 5 秒设置/数据同步，重新聚焦立即读取；其他已开页面最多一个既有轮询周期同步。断连/预览失败保留旧文字但显示预览暂不可用，不伪称已同步。预览只展示文本，物理布局以原生测量为准。

TaskbarOverlay 继续复用 1500ms 维护计时器；TaskbarPresenter.VisualKey 不含当前时间/修订号，格式化文本和颜色/状态不变时保持位图缓存，不重复提交。窗口布局变化或 WM_DPICHANGED 重绘。任务栏全屏/自动隐藏、连续三次布局失联隐藏及 z-order 恢复行为保留。

## 物理布局与验证

TaskbarLayout.Create 根据实际任务栏物理高度选择字体，在同一 Graphics/StringFormat 下测量与绘制。布局顺序：完整两行 → 精简前缀和仅 Token 第二行 → 额度单行 → 最小百分比（仍保留旧值/参考标记）；最小行也容不下才隐藏，托盘入口保留。整体宽高及文本边界均检查，不截断金额或百分比。空间不足时清除位图就绪标记，防止下一维护周期误展示旧宽位图。

- scripts/test-native.ps1 新增 TaskbarDisplaySmoke：合成快照、固定时刻、可注入设置读写失败，验证默认/指定/最低/并列/空值/过期/消失恢复/同键歧义、币种、未知费用、跨日、倒计时、旧请求、false/null、非法设置、重启加载及写失败；不读取或修改生产注册表。
- 离屏矩阵物理高度 24/32/40/48/60/72/84/96/120、宽度 8/25/48/80/120/240/800，涵盖小任务栏及常见 100% 至 250% 对应高度。断言文本矩形和实际字形 alpha 不接触图像边界，输出 artifacts/tests/taskbar PNG；它不等同于真实系统 DPI 切换验收。
- WebDashboardSmoke 覆盖任务栏补丁和已应用/草稿预览；node scripts/run-web-tests.cjs scripts/test-web.cjs scripts/test-web-reminders.cjs scripts/test-web-taskbar.cjs 在真实无头 Edge 中验证原页面和提醒回归、草稿/已应用预览、选择持久化、窗口消失、未知、切换开关、保留其他入口变更、失败草稿、旧响应拒绝和移动布局。全部使用合成服务。
- scripts/build-native.ps1 -SkipInstaller -NativeOutputDirectory taskbar-verification 输出独立程序；不安装、不启动生产常驻应用。

真实桌面限制：实际 DPI 热切换、Explorer 重建、全屏/自动隐藏、任务栏叠放与长期无闪烁仍需人工验收；本轮没有操作生产任务栏。可单独使用既有 scripts/test-taskbar-stability.ps1 辅助已安装环境验收，不纳入默认离线测试。

# Feature 04: 额度历史记录与图表

`QuotaHistoryStore` 独立保存确认的在线额度观测，不与 Token 日统计互相换算。记录写入 `%LOCALAPPDATA%\\CodexTrayStatus\\quota-history.jsonl`，只含账号摘要、稳定窗口键、周期结束、观测时间、观测编号、已用/剩余百分比和 `official` 来源；不写入凭据、聊天正文或项目路径。

只有 `DataFreshness.EvaluateQuota` 判定为有效的 `official`、`updated`、新观测才会写入。其去重身份为账号、窗口、周期结束和观测编号，因此刷新重试、本地回退、旧值保留和同一观测的重复发布都不会生成样本。每次写入使用临时文件加 `File.Replace`，保留至多 90 天、50,000 点和 16 MiB；损坏 JSONL 的单行被跳过，超限或读写失败使历史查询明确不可用，不影响实时额度。

`GET api/quota-history?range=24h|7d|30d|90d` 沿用私有 loopback 路径，只按当前快照的内部账号摘要过滤。响应不会序列化 `AccountKey`。无效范围返回 400；网页显示 24 小时、7 天、30 天、90 天切换和已用百分比曲线，周期结束改变以虚线标记，缺测不补线。当前额度窗口标签用于展示，历史内部键不向用户暴露。

预测功能只能消费该存储的有效点，并且必须按账号、窗口及周期边界隔离；它不得从网页响应重建账号维度。长期 Token 统计、费率和排行不依赖本功能。

验证：`QuotaHistorySmoke` 覆盖重启加载、重复观测、账号隔离、无效/本地/过期数据、损坏尾行和非法范围。`WebDashboardSmoke` 验证 API 范围与账号字段脱敏；`scripts/test-web-history.cjs` 在合成服务和无头 Edge 验证图表、范围切换及窄屏。完整 `scripts/test-native.ps1`、4 个网页脚本和 `scripts/build-native.ps1 -SkipInstaller -NativeOutputDirectory quota-history-verification` 均已通过。
