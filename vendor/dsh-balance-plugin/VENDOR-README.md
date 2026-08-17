# dsh-balance-plugin(本地 vendor 版)

DeepSeek 余额监控与用量统计插件,接入 DSH Web GUI。

- **来源**:[Francis-Xavier-code/dsh-balance-plugin](https://github.com/Francis-Xavier-code/dsh-balance-plugin)(MIT),版本 1.0.0
- **为何 vendor**:本机无法直连 GitHub(git clone / raw 超时),源码通过 jsdelivr CDN 获取并落盘于此;后续若要更新,重新从 CDN 拉取 `lib/index.js` 与 `lib/client.js` 覆盖即可。
- **安装方式**:`dsh plugin --profile web add link:D:/deepseek-harness/launcher/vendor/dsh-balance-plugin`,已写入 `~/.dsh/profiles/web/` 的 `dependencies` 与 `dsh.profile.bundles`。

## 本地修改(相对上游)

1. **余额查询改用 Node 原生 `fetch`**(上游用 `shell` + `curl`,在 Windows pwsh 下因 `curl` 别名与单引号传参而失败,报 `SEC_E_NO_CREDENTIALS`)。`fetchAccount()` 现直接 `fetch(BALANCE_URL, { headers: { Authorization: ... } })`,跨平台稳定,且 key 不再出现在命令行。
2. 其余逻辑(用量统计、插件清单、RPC 路由、UI 组件)与上游一致。

## 功能

- 💰 余额监控:自动读取 `DEEPSEEK_API_KEY`,查官方 `GET /user/balance`,CNY/USD 双余额池,低余额告警(默认阈值 ¥10/$2)
- 📊 用量统计:轮次/步数/token/缓存命中率、GitHub 风格用量日历、趋势图、模型消耗、最近 50 条明细
- 🔗 官方入口:充值 `platform.deepseek.com/top_up`、用量 `platform.deepseek.com/usage`
- 🧩 三方插件清单
- 🤖 模型工具 `query_api_quota`(问"还剩多少余额")

入口在 Web GUI 输入框右侧三个图标(💰 钱包 / 📊 用量 / 🧩 插件)。
