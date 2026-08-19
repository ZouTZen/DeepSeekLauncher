# DeepSeek Launcher

DeepSeek Harness 的 Windows 桌面启动器。双击即可在单个无边框应用窗口里使用 DeepSeek Harness,左侧边栏一键切换 **Harness / Platform / Chat / GitHub / 设置**,无需命令行、无需手动开浏览器。窗口关闭时自动结束后台服务进程。

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
- **左侧边栏五页同窗切换**:Harness / Platform / Chat / GitHub / 设置
- **页面状态保留**:四个网页各自独立 WebView2 实例,切换只显示/隐藏、不刷新——GitHub 进到子页面后切走再切回,仍在原地
- **右键刷新**:右键侧边栏按钮刷新对应页面
- **原生应用外观**:无系统边框 + 自绘标题栏(可拖动、双击最大化),标题栏「☰」按钮折叠/展开侧边栏;窗口四边/四角保留 6px 热区,可拖拽调整大小,最大化铺满工作区
- **自定义背景图片**:设置页可选亮色/暗色两张背景图,左上角对齐、不拉伸变形,拉伸窗口只改变显露范围;路径持久化,重启自动恢复
- **主题黑/白/跟随系统**:边框(标题栏 + 侧边栏)、按钮、WebView 页面首选配色整体跟随;设背景后按系统深浅自动选用亮/暗背景图
- **设置独立页**:屏幕缩放(8 档)、主题、背景图管理
- **外部链接安全跳转**:除 launcher 自身端口外,其余 http(s) 链接(含 127.0.0.1 其他端口)一律在系统浏览器打开,不被页面劫持
- **高 DPI 清晰渲染**:`PerMonitorV2` manifest + 代码兜底,125%/150% 缩放下不再模糊
- **崩溃自愈**:node 服务挂在 kill-on-close Job Object 下,启动器被强杀也不留孤儿进程
- **可观测**:服务 stdout/stderr 写入 `%LOCALAPPDATA%\DeepSeekHarnessDesktop\launcher.log`
- **去本机化**:通过环境变量 + 自动发现适配 npx / npm 全局 / git clone 三种 harness 部署,不含硬编码绝对路径

## 使用方式

## 使用方式

### setup 安装(面向普通用户)

适合**从零开始、想一键装好所有依赖**的人。

1. 下载 `DeepSeekHarness-Setup.exe`
2. 双击安装,安装器会:
   - 第一步询问是否已装 harness(npx / npm 全局 / clone / 否)
   - 选「否」时自动 `npm install -g @deepseek-ai/dsh`
   - 自动检测/安装 Node.js LTS 与 WebView2 Runtime(联网)
3. 装完桌面出现「DeepSeek Harness Launcher」快捷方式,双击启动

> 安装需要联网 + UAC 提权;卸载在「控制面板 → 程序和功能」,会连同 npm 全局 harness 一起卸载。

> 开发者如需免安装运行,可用 `build.ps1` 自行编译到 `dist\`,双击 `dist\DshDesktop.exe` 即可(需已部署 harness)。

## Release 产物

### v1.1.0

| 产物 | 说明 |
|---|---|
| `DeepSeekHarness-Setup.exe` | 安装包(自动装依赖 + 建快捷方式 + 卸载器) |

> 从 GitHub Releases 页面下载;`DeepSeekHarness-Setup.exe` 未签名,首次运行可能触发 SmartScreen 提示。

## 左侧边栏

| 按钮 | 目标 | 说明 |
|---|---|---|
| **Harness** | `http://127.0.0.1:<port>` | 本地 DSH WebUI(编码 Agent) |
| **Platform** | `platform.deepseek.com` | 余额、用量、充值、API Key 管理 |
| **Chat** | `chat.deepseek.com` | 官方网页聊天(登录态由网站原生保持) |
| **GitHub** | `github.com` | GitHub(子页面浏览状态保留) |
| **设置** | 本地设置页 | 屏幕缩放 / 主题 / 背景图片 |

四个网页共享同一份 WebView2 用户数据目录(`%LOCALAPPDATA%\DeepSeekHarnessDesktop\WebView2`),登录态持久保留;页面内 `target=_blank` 链接也在当前窗口打开,不弹浏览器。

## 背景图片说明

- 在「设置」页选择**亮色背景图**与**暗色背景图**(支持 png/jpg/jpeg/bmp/gif)
- 背景图覆盖标题栏、侧边栏与按钮(框架层),左上角对齐、不拉伸;窗口缩放只改变显露范围
- 建议导入与屏幕分辨率一致的图片以获得最佳效果
- 只设一张图时,主题选项不可选;设了两张时可通过黑/白主题切换选图;系统深浅色会自动选用对应图
- 背景图路径保存在 `%LOCALAPPDATA%\DeepSeekHarnessDesktop\bg-light.txt` / `bg-dark.txt`,重启自动恢复

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
- 背景图只覆盖应用框架层(标题栏/侧边栏/按钮);网页内容区(harness 与外部站点)保持网页自身背景,不透明透出壁纸。

## License

[MIT](LICENSE)
