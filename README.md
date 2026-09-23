# Codex Tray Status

一个轻量的 Windows Codex 用量监控工具。它把两行状态直接放在系统通知区左侧，无需悬停：

~~~text
5D · 剩余 94%
今日 1.8M · 估算 $0.42
~~~

点击状态文字直接打开本地网页，**概览、统计、设置全部在浏览器中使用**，不再弹出原生详情小窗口。右键托盘菜单可直达各页面。

- 概览：额度、重置时间、今日 Token、费用分项与七天趋势。
- 公开重置公告：显示最近一次公开重置、明确排期的公告和近期记录；数据来自 [Codex Resets](https://codex-resets.com)，与个人账号额度分开展示。
- 统计：今天 / 近 7 天 / 近 30 天及自定义日期，支持模型筛选；分别统计未缓存输入、缓存输入、输出及对应费用，逐日展开模型明细，可导出 CSV。
- 设置：刷新间隔、剩余/已用口径、开机启动、USD/CNY 币种与参考汇率。设置保存在本机，币种同步到任务栏。
- 网页仅监听 `127.0.0.1`，使用随机私有路径，页面资源全部内置于 EXE，无 CDN 或 Node.js 运行依赖。关闭浏览器不会退出托盘；退出托盘后网页停止更新。

## 安装

推荐从 [GitHub Releases](https://github.com/L-aros/CodexTrayStatus/releases) 下载 `CodexTrayStatus-Setup.exe`。本地构建时，安装包位于 `artifacts/installer/CodexTrayStatus-Setup.exe`。

- 按当前用户安装到 %LOCALAPPDATA%/Programs/CodexTrayStatus
- 不需要管理员权限
- 提供开始菜单入口和标准卸载程序
- 安装时可选开机启动
- 需要 Windows 10/11 与 .NET Framework 4.8

发布包通过 [Sigstore Cosign](https://docs.sigstore.dev/cosign/signing/signing_with_blobs/) 无密钥签名，安装包和独立 EXE 各附带一个 `.sigstore.json` 文件。下载发布页中的安装包及同名签名包后，可验证来源和文件完整性：

~~~powershell
cosign verify-blob .\CodexTrayStatus-Setup.exe `
  --bundle .\CodexTrayStatus-Setup.exe.sigstore.json `
  --certificate-identity 'https://github.com/L-aros/CodexTrayStatus/.github/workflows/release.yml@refs/tags/v0.4.5' `
  --certificate-oidc-issuer 'https://token.actions.githubusercontent.com'
~~~

其他版本请将命令中的 `v0.4.5` 改为对应发布标签。Sigstore 文件签名不是 Windows Authenticode 签名，因此 SmartScreen 仍可能显示“未知发布者”。

## 数据与隐私

- 额度：读取 %USERPROFILE%/.codex/auth.json（或 CODEX_HOME/auth.json）中的现有 OAuth 凭据，仅请求 https://chatgpt.com/backend-api/wham/usage。
- 公开重置公告：本地程序向 `https://codex-resets.com/api/v1/status` 和 `/api/v1/resets?limit=5` 发起不带 Codex 凭据的只读请求，成功结果缓存 15 分钟。公开消息不代表个人账号额度已重置；计划中的公告在确认执行前始终标记为“尚未确认执行”。
- 回退：官方请求暂时不可用时，从本地 Codex session JSONL 中读取最近一次额度快照。
- 每日用量：流式扫描 sessions 与 archived_sessions，按本地日期汇总最近 30 天；缓存未变化文件，不再受原先 100 个会话读取上限影响。没有日志记录的日期显示 0，日志已删除或未保存在本机的用量无法还原。
- 花费：按日志中的模型和内置标准费率估算；它不是账单，长上下文、Fast 模式及特殊计费可能产生差异。
- 总 Token = 未缓存输入 + 缓存输入 + 输出；输出已包含推理用量。优先对累计用量求增量，避免重复累计快照被再次计数。按模型保留费用精度，只在显示时舍入；未知费率模型显示待定价，不套用其他模型价格。
- 人民币使用手动参考汇率换算，初始参考值为 1 USD = 7 CNY，可在设置中修改；不是实时外汇报价。
- 程序不会修改 .codex 数据，不会记录或上传 access token。

## 为什么不用 Electron

旧原型的业务代码很小，但 Electron/Chromium 让便携包达到约 81 MB、解压目录约 326 MB，并产生多进程常驻开销。当前 Windows 版改用 .NET Framework WinForms + Win32：

- 单进程常驻
- 原生任务栏坐标和 DPI 处理
- 主程序与安装包均为百 KB 量级
- 无 Node.js、Chromium 或额外 NuGet 运行依赖

旧 Electron 源码仍保留作迁移参考，但发布物不再包含它。

## 构建与验证

本机需要 .NET Framework 4.8 开发组件和 NSIS。构建脚本会查找标准安装位置，或 `artifacts/tools/nsis/makensis.exe`。

~~~powershell
./scripts/test-native.ps1
./scripts/build-native.ps1 -Version 0.4.5
# 未安装 NSIS 时只构建独立程序
./scripts/build-native.ps1 -SkipInstaller
~~~

输出：

~~~text
artifacts/native/CodexTrayStatus.exe
artifacts/installer/CodexTrayStatus-Setup.exe
~~~

## 当前边界与 macOS 计划

第一阶段优先支持带通知区的 Windows 主横向任务栏；自动隐藏时状态层会随任务栏一起隐藏。竖向任务栏和副屏独立任务栏后续再做专门布局。

macOS 不会捆绑 Windows/.NET 版本。计划用 Swift/AppKit 做原生菜单栏 companion，并复用相同的数据口径与面板信息结构，以保持小体积和低常驻开销。

## 参考

功能与视觉方向参考了 MIT 项目 [libing93920/CodexStatus](https://github.com/libing93920/CodexStatus)。本项目是独立的原生 Windows 实现，重点是任务栏常驻、低内存和小安装包。

本次面板参考 [CodexBar](https://github.com/steipete/CodexBar) 的额度概览与 [ccusage](https://github.com/ccusage/ccusage) 的按日 Token / 估算费用汇总，采用独立 WinForms 绘制实现。
