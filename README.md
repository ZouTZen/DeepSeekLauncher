# DeepSeek Launcher

DeepSeek Harness 的 Windows 桌面启动器。双击即可在单个 WebView2 窗口里使用 DeepSeek Harness,顶部导航栏一键切换 **Harness / 控制台 / 聊天 / GitHub**,无需命令行、无需手动开浏览器。窗口关闭时自动结束后台服务进程。

## 项目内容

| 目录 / 文件 | 说明 |
|---|---|
| `src/` | 启动器源码(C#,WinForms + WebView2) |
| `build.ps1` | 编译脚本,用 Windows 自带 .NET Framework 4.8,无需安装 SDK |
| `installer/` | Inno Setup 安装包脚本(`installer.iss` + 依赖安装脚本) |
| `dist/` | 免安装版产物(exe + 3 个 DLL,编译生成) |
| `vendor/dsh-balance-plugin/` | 内置余额监控插件(MIT) |

## 特性

- **双击即用**:自动定位 `node.exe` 与 dsh CLI 入口,无需命令行、无需手动开浏览器
- **单实例**:重复双击聚焦已开窗口,而不是报"端口被占"
- **顶部导航**:Harness / Platform / Chat / GitHub 四页同窗切换
- **高 DPI 清晰渲染**:`PerMonitorV2` manifest + 代码兜底,125%/150% 缩放下不再模糊
- **缩放跟随系统**:页面默认 100% 跟随系统 DPI;换显示器后右键 Harness 按钮「更新缩放率」重新匹配
- **崩溃自愈**:node 服务挂在 kill-on-close Job Object 下,启动器被强杀也不留孤儿进程
- **可观测**:服务 stdout/stderr 写入 `%LOCALAPPDATA%\DeepSeekHarnessDesktop\launcher.log`
- **去本机化**:通过环境变量 + 自动发现适配 npx / npm 全局 / git clone 三种 harness 部署,不含硬编码绝对路径

## 使用方式

### 方式一:直接使用 dist(免安装,面向开发者)

适合**已经部署了 harness** 的人。

1. 下载 Release 里的 dist 压缩包(或自行 `build.ps1` 编译),解压得到 4 个文件:
   - `DshDesktop.exe`
   - `Microsoft.Web.WebView2.Core.dll`
   - `Microsoft.Web.WebView2.WinForms.dll`
   - `WebView2Loader.dll`
2. 双击 `DshDesktop.exe`
3. 首次打开按 Web GUI 提示配置 API Key

前提:

- 64 位 Windows
- 已安装 node.js(启动器自动定位)
- 已通过 **npx / npm 全局 / git clone** 任一方式安装 harness(启动器自动定位)

`dist\` 放任意位置均可,启动器会自动查找 harness 的 CLI 入口。

### 方式二:setup 安装(面向普通用户)

适合**从零开始、想一键装好所有依赖**的人。

1. 下载 `DeepSeekHarness-Setup.exe`
2. 双击安装,安装器会:
   - 第一步询问是否已装 harness(npx / npm 全局 / clone / 否)
   - 选「否」时自动 `npm install -g @deepseek-ai/dsh`
   - 自动检测/安装 Node.js LTS 与 WebView2 Runtime(联网)
3. 装完桌面出现「DeepSeek Harness Launcher」快捷方式,双击启动

> 安装需要联网 + UAC 提权;卸载在「控制面板 → 程序和功能」,会连同 npm 全局 harness 一起卸载。

## Release 产物

### v1.0.0

| 产物 | 说明 |
|---|---|
| `DeepSeekHarness-Setup.exe` | 安装包(推荐:自动装依赖 + 建快捷方式 + 卸载器) |
| `DeepSeekLauncher-dist.zip` | 免安装版(exe + 3 DLL),手动解压使用 |

> 从 GitHub Releases 页面下载;`DeepSeekHarness-Setup.exe` 未签名,首次运行可能触发 SmartScreen 提示。

## 顶部导航栏

| 按钮 | 目标 | 说明 |
|---|---|---|
| **Harness** | `http://127.0.0.1:<port>` | 本地 DSH WebUI(编码 Agent) |
| **Platform** | `platform.deepseek.com` | 余额、用量、充值、API Key 管理 |
| **Chat** | `chat.deepseek.com` | 官方网页聊天(登录态由网站原生保持) |
| **GitHub** | `github.com` | GitHub 主页 |

四个页面共享同一份 WebView2 用户数据目录(`%LOCALAPPDATA%\DeepSeekHarnessDesktop\WebView2`),登录态持久保留;页面内 `target=_blank` 链接也在当前窗口打开,不弹浏览器。

## 构建

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

出安装包(需先装 Inno Setup 6):

```powershell
# 准备 payload(dist 4 文件 + WebView2 bootstrapper),然后用 ISCC 编译
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\installer.iss
# 产物:installer\DeepSeekHarness-Setup.exe
```

运行中重编译(旧 exe 会锁文件):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -OutputName DshDesktop.new.exe
# 关闭窗口后:
powershell -ExecutionPolicy Bypass -File promote-new.ps1
```

## 配置(环境变量)

| 变量 | 作用 | 默认 |
|---|---|---|
| `DSH_PROFILE` | 要 boot 的 profile | `web` |
| `DSH_NODE` | node.exe 完整路径 | 注册表 → PATH → 常见位置 |
| `DSH_CLI` | dsh 入口 `bin.js` 完整路径 | 自动定位(见下) |
| `DSH_PORT` | 服务端口 | `3080` |
| `DSH_HOME` | 用户数据目录(会话、设置、凭据) | 由 dsh 默认决定(通常 `~/.dsh`) |

CLI 自动定位顺序:`DSH_CLI` 环境变量 → 保存过的路径 → exe 旁便携布局 → 向上搜 `apps/cli/lib/bin.js` → npm/pnpm 全局 → npx 缓存 → 盘符根 + 用户目录兜底搜索 → 都找不到则弹框让用户手动指定。

## 目录结构

```
launcher/
├── src/                          # 启动器源码
│   ├── DshDesktopLauncher.cs
│   ├── DshDesktop.manifest
│   └── assets/deepseek-whale.ico
├── build.ps1                     # 编译脚本
├── promote-new.ps1               # 运行中升级脚本
├── installer/                    # 安装包工程
│   ├── installer.iss             # Inno Setup 脚本
│   ├── ChineseSimplified.isl     # 中文语言文件
│   └── payload/setup-deps.ps1    # 依赖安装脚本(node/harness/WebView2)
├── vendor/dsh-balance-plugin/    # 内置余额监控插件(MIT)
├── dist/                         # 免安装版产物(编译生成,git 忽略)
├── lib/                          # WebView2 SDK(git 忽略)
├── README.md
└── LICENSE
```

## 已知限制

- exe 未签名,首次运行可能触发 SmartScreen/杀软提示。
- 端口默认 3080,被占用时启动器报错并提示设置 `DSH_PORT`,不静默换端口。
- WebView2 用户数据保存在 `%LOCALAPPDATA%\DeepSeekHarnessDesktop\WebView2`,删除可重置浏览器侧状态。

## License

[MIT](LICENSE)
