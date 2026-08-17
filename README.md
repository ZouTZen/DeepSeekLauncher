# DeepSeek Harness 桌面启动器

一个免安装的 Windows 桌面壳:双击 `dist\DshDesktop.exe` 自动启动本地 DSH Web 服务,并用 WebView2 弹出一个带顶部导航栏的窗口,在 **Harness / 控制台 / 纯聊天** 三个页面间切换——全部在同一个窗口内完成,不另开浏览器、不敲命令行。窗口关闭时自动结束后台服务进程。

## 特性

- **双击即用**:自动定位 `node.exe` 和 dsh CLI 入口,无需命令行、无需手动开浏览器
- **单实例**:重复双击会聚焦已打开的窗口,而不是报"端口被占"
- **顶部导航**:Harness(本地 DSH)/ Platform(官方控制台)/ Chat(官方聊天)三页同窗切换
- **高 DPI 清晰渲染**:`PerMonitorV2` manifest + 启动时代码兜底,125%/150% 缩放下不再模糊
- **缩放跟随系统 + 手动更新**:页面默认 100% 跟随系统 DPI(125% 显示 125%、80% 显示 80%);换显示器/改系统缩放后,右键 Harness 按钮点"更新缩放率"即可重新匹配
- **崩溃自愈**:node 服务挂在 kill-on-close Job Object 下,启动器即使被强杀/崩溃,系统也会自动回收整棵 node 进程树,不留孤儿进程
- **可观测**:服务 stdout/stderr 与启动过程写入 `%LOCALAPPDATA%\DeepSeekHarnessDesktop\launcher.log`,失败时可定位原因
- **去本机化**:通过环境变量 + 自动发现适配任意 harness 部署,不含硬编码的绝对路径

## 顶部导航栏

| 按钮 | 目标 | 说明 |
|---|---|---|
| **Harness** | `http://127.0.0.1:<port>` | 本地 DSH WebUI(编码 Agent) |
| **Platform** | `platform.deepseek.com` | 余额、用量、充值、API Key 管理 |
| **Chat** | `chat.deepseek.com` | 官方网页聊天(登录态、历史记录由网站原生保持) |
| **GitHub** | `github.com` | GitHub 主页 |

三个页面共享同一个 WebView2 用户数据目录(`%LOCALAPPDATA%\DeepSeekHarnessDesktop\WebView2`),所以控制台和纯聊天登录一次后登录态持久保留。页面内的 `target=_blank`/`window.open` 链接也会被接管到当前窗口内打开,不弹浏览器。

## 快速开始

```powershell
# 编译(需 Windows 自带 .NET Framework 4.8,无需安装 SDK)
powershell -ExecutionPolicy Bypass -File build.ps1

# 运行
dist\DshDesktop.exe
```

编译产物在 `dist\`:

- `DshDesktop.exe` — 64 位启动器
- `Microsoft.Web.WebView2.Core.dll` / `Microsoft.Web.WebView2.WinForms.dll` — WebView2 .NET 封装
- `WebView2Loader.dll` — win-x64 原生加载器(必须与 exe 同目录)

## 行为

1. **单实例**:通过命名 Mutex 保证只有一个实例;再次启动会聚焦已有窗口后退出。
2. **定位 node.exe**:`DSH_NODE` 环境变量 → 注册表 `HKLM/HKCU\SOFTWARE\Node.js` → `PATH` → 常见安装位置。
3. **定位 dsh CLI 入口**:`DSH_CLI` 环境变量 → 从 exe 目录向上搜索 `apps/cli/lib/bin.js`(最多 6 级) → exe 旁 `app\apps\cli\lib\bin.js` 便携布局。
4. **端口固定**:默认 3080;被占用时明确报错(不静默跳端口)。可用 `DSH_PORT` 覆盖。
5. **静默启动**:`node <bin.js> --profile <profile> --host 127.0.0.1 --port <port>`,无命令行窗口,stdout/stderr 重定向到日志。
6. **等就绪**:轮询本地端口 HTTP 200(最长 60 秒);若检测到服务跳到了别的端口,会在错误提示里说明实际端口。
7. **显示窗口**:顶部导航栏 + WebView2 页面,默认加载 Harness;窗口标题 `DeepSeek`,标题栏/任务栏图标为 DeepSeek 小鲸鱼。页面缩放默认 100% 跟随系统 DPI;右键 Harness 按钮点"更新缩放率(匹配系统)"可重新检测并匹配当前系统缩放。
8. **关闭清理**:窗口关闭时 `taskkill /T /F` 终止服务进程树;Job Object 兜底处理启动器被强杀的场景。

## 配置(环境变量)

| 变量 | 作用 | 默认 |
|---|---|---|
| `DSH_PROFILE` | 要 boot 的 profile | `web` |
| `DSH_NODE` | node.exe 完整路径 | 注册表 → PATH → 常见位置 |
| `DSH_CLI` | dsh 入口 `apps/cli/lib/bin.js` 完整路径 | 从 exe 向上搜 → 便携 `app/` 布局 |
| `DSH_PORT` | 服务端口 | `3080` |
| `DSH_HOME` | 用户数据目录(会话、设置、凭据) | 由 dsh 默认决定(通常 `~/.dsh`) |

设置方式(永久):

```powershell
[Environment]::SetEnvironmentVariable('DSH_CLI', 'D:\your-harness\apps\cli\lib\bin.js', 'User')
[Environment]::SetEnvironmentVariable('DSH_PROFILE', 'web', 'User')
```

## 部署到其他 harness 环境

启动器不含任何硬编码的绝对路径,会自动定位 CLI,所以 `dist\`(exe + 3 个 DLL)**放在任意位置双击即可**,无需关心目录结构。CLI 查找顺序:

1. `DSH_CLI` 环境变量 → 2. exe 旁 `app\apps\cli\lib\bin.js` 便携布局 → 3. 从 exe 向上搜 `apps\cli\lib\bin.js`(仓库内)→ 4. npm/pnpm 全局 → 5. **盘符根 + 用户目录兜底搜索**。

- **git clone 的仓库**:dist 放哪都行——放进仓库内走"向上搜索"秒中,放外面走"兜底搜索"也能自动找到 `apps\cli\lib\bin.js`。
- **npm 全局安装** (`npm install -g @deepseek-ai/dsh`):自动定位 `%APPDATA%\npm\node_modules\@deepseek-ai\dsh\lib\bin.js`。
- **实在找不到时**:设用户级 `DSH_CLI` 指向 `bin.js` 即可。
- 前提:目标机器需为 64 位 Windows、已安装 node.js(`DSH_NODE` 或 PATH/注册表可定位)。

## 升级(运行中重编译)

运行中的 exe 会锁住 `dist\DshDesktop.exe`,此时直接 `build.ps1` 会失败。改为:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -OutputName DshDesktop.new.exe
# 关闭窗口后:
powershell -ExecutionPolicy Bypass -File promote-new.ps1   # 用 new 覆盖正式 exe(并备份旧版)
```

## 已接入插件

Web GUI 已接入 [dsh-balance-plugin](vendor/dsh-balance-plugin/VENDOR-README.md)(本地 vendor 版,余额查询已改为 Node `fetch` 以适配 Windows):

- 输入框右侧三个图标:💰 余额监控 · 📊 用量统计 · 🧩 三方插件
- 余额自动读取 `DEEPSEEK_API_KEY`,查询官方 `GET /user/balance`(CNY/USD 双池,低余额告警)
- 一键跳转官方充值 `platform.deepseek.com/top_up` 与用量页 `platform.deepseek.com/usage`
- 模型工具 `query_api_quota`:直接问"还剩多少余额"

## 已知限制

- exe 未签名,首次运行可能触发 SmartScreen/杀软提示(自编译应用的正常现象)。
- 端口默认 3080,被占用时启动器报错并提示设置 `DSH_PORT`;不会悄悄换端口。
- WebView2 用户数据(登录态、localStorage)保存在 `%LOCALAPPDATA%\DeepSeekHarnessDesktop\WebView2`,删除该目录可重置浏览器侧状态。
