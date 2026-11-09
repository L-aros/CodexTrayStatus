# 在线汇率与模型费率（0.4.8）

## 汇率

`ExchangeRateService` 通过 [Frankfurter 的 ECB provider](https://api.frankfurter.dev/v2/providers/ecb/rate/USD/CNY) 获取 USD/CNY，每小时检查。主接口失败或数据无效时，直接读取 [ECB 官方 XML](https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml)，使用同日 EUR/CNY ÷ EUR/USD，四舍五入到四位小数。数据为 ECB 每日参考价；页面区分来源日期 `AsOf` 与读取时间 `FetchedAt`，不会称为逐笔行情。

失败后五分钟重试，保留上次有效值。缓存 `%LOCALAPPDATA%/CodexTrayStatus/exchange-rate.json`；没有在线值时暂用保存的手动参考值，新安装默认 7。刷新失败、超过检查周期或来源日期超过七天时，显示缓存状态。

`WebPreferences.AutoExchangeRate` 控制生效模式；注册表同名值缺失时默认启用。`ExchangeRate` 返回当前生效值，注册表 `ExchangeRate` 仍只保存手动参考值，在线更新不改写它。切换手动模式时，网页明确提交当前可见数值，避免后台更新造成保存值与草稿不符。汇率变化同步网页、任务栏和 CSV 换算。

## 模型费率

`ModelRadarPricingService` 为兼容宿主保留原类型名，实际读取 [Models.dev](https://models.dev/api.json) 的 `openai.models`，使用 `cost.input`、`cost.cache_read`、`cost.output`，单位 USD / 百万 Token。选用直接 OpenAI provider，不使用代理或其他厂商报价。定价可对照 [OpenAI 官方资料](https://developers.openai.com/api/docs/pricing)。原 ModelRadar 响应出现缓存价与输出价错位，已替换。

每六小时检查，失败后一小时重试；缓存 `%LOCALAPPDATA%/CodexTrayStatus/models-dev-prices.json`，版本 2，不读取旧源缓存。上游无全局价格生效日，`EffectiveDate` 保持 null，页面只展示真实读取时间。输入/缓存/输出缺项保持 nullable，由内置价格补齐；不能补齐的模型保持待定价，异常记录不发布。

费率优先级：手动覆盖 → 有效远程价格 → 内置价格。价格变化后清理会话费用缓存、重新汇总已读日志；Token 数量不变。历史费用按当前费率估算，不表示实际账单或历史成交价。

## API 与刷新

`GET api/usage` 的 `ExchangeRate`、`Pricing` 字段包含可用性、缓存状态、来源和读取时间。`POST api/rates/refresh` 排队强制更新两类数据，忽略常规刷新间隔；复用既有私有 loopback 路径与同源请求检查。已有请求进行时复用服务任务，宿主保留后续强制刷新请求。

两个数据源均使用独立匿名客户端，不携带 Codex 凭据、日志或聊天内容。设置页保留未保存草稿，自动模式不提交旧汇率覆盖后台新值。

## 验证入口

- `scripts/test-native.ps1`：汇率 41 项、模型服务 55 项、费率目录 24 项，以及既有原生/API/网页显示回归。
- `node scripts/run-web-tests.cjs scripts/test-web-rates.cjs`：真实无头 Edge 验证模式保存/重载、手动草稿保护、并发币种更新及实际刷新请求；Playwright 可通过 `PLAYWRIGHT_MODULE` 指定。
- `tests-native/LiveRatesProbe.cs`：单独编译运行的可选联网验证，不纳入默认离线测试，不读取用户账号。
