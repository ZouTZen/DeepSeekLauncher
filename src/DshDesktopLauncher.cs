// DshDesktopLauncher.cs
// Double-click desktop launcher for the DeepSeek Harness web UI.
// Responsibilities:
//   1. Enforce a single instance (a second launch focuses the open window).
//   2. Locate node.exe and the dsh CLI entry (bin.js) via DSH_NODE / DSH_CLI
//      environment variables, the registry, PATH, or walking up from the exe.
//   3. Start `node <bin.js> --profile web --host 127.0.0.1 --port <port>` with
//      no console window, on the fixed port (3080 by default), streaming the
//      child's stdout/stderr to a launcher log.
//   4. Assign the server to a kill-on-close Job Object so the whole node tree
//      is reaped even if this launcher crashes or is killed.
//   5. Poll the port until the web server answers.
//   6. Open a WebView2 window loading that local URL (embedded UI, identical
//      to the browser web UI).
//   7. On window close, terminate the server process tree (taskkill /T /F).
//
// Build (x64, .NET Framework 4.8):
//   csc.exe /nologo /target:winexe /platform:x64 /optimize+ /out:DshDesktop.exe ^
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
//     /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll ^
//     DshDesktopLauncher.cs
// Deploy next to the exe: Microsoft.Web.WebView2.Core.dll,
// Microsoft.Web.WebView2.WinForms.dll, WebView2Loader.dll (win-x64 native).

using System;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshDesktop
{
    internal static class Launcher
    {
        internal const string AppTitle = "DeepSeek";
        private const int DefaultStartPort = 3080;

        // Logging (see Log). Written to %LOCALAPPDATA%\DeepSeekHarnessDesktop\launcher.log.
        private static readonly object LogLock = new object();
        private static string logPath;

        // The port the dsh server actually reported binding to, parsed from its
        // stdout ("dsh web: http://127.0.0.1:PORT"). Used to detect the case
        // where the server silently moves to a different port than requested.
        private static int detectedPort = -1;

        // Handle to the Job Object that owns the node process tree. Kept alive
        // for the whole launcher lifetime so the OS reaps node even if this
        // process is killed; KILL_ON_JOB_CLOSE makes that automatic.
        private static IntPtr serverJob = IntPtr.Zero;

        // Held for the whole process lifetime so single-instance stays effective.
        private static Mutex singleInstanceMutex;

        [STAThread]
        private static int Main(string[] args)
        {
            // Single-instance: a second launch focuses the existing window
            // instead of failing on the already-bound port.
            bool createdNew;
            singleInstanceMutex = new Mutex(true, "DshDesktopLauncher.SingleInstance", out createdNew);
            if (!createdNew)
            {
                BringExistingToFront();
                return 0;
            }

            // DPI awareness belt-and-suspenders: the embedded manifest declares
            // PerMonitorV2, and this call backs it up for hosts that ignore the
            // manifest. Without it, WebView2 renders at 96 DPI and Windows
            // stretches the bitmap on >100% displays, blurring all UI text.
            TrySetPerMonitorV2DpiAwareness();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                string nodePath = ResolveNode();
                string dshCli = ResolveDshCli();
                string profile = ResolveProfile();
                int port = ResolvePort();
                string webViewData = WebViewDataDir();

                Process server = StartServer(nodePath, dshCli, profile, port);
                if (server == null || server.HasExited)
                {
                    ShowError("服务启动失败", "无法启动 dsh web 服务。\n\n" + DescribeNode(nodePath) + "\n" + DescribeCli(dshCli));
                    return 2;
                }

                string diag;
                if (!WaitForReady(port, server, out diag))
                {
                    KillTree(server.Id);
                    ShowError("服务未就绪", diag);
                    return 3;
                }

                Application.Run(new MainForm(port, server.Id, webViewData));
                return 0;
            }
            catch (Exception ex)
            {
                Log("fatal: " + ex);
                ShowError("启动失败", ex.Message);
                return 1;
            }
        }

        // ── resolution ────────────────────────────────────────────────────────

        private static string ResolveNode()
        {
            string fromEnv = Environment.GetEnvironmentVariable("DSH_NODE");
            if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

            // 1. Registry install path (works across drives/machines).
            string regDir = ReadRegistry("HKLM\\SOFTWARE\\Node.js", "InstallPath")
                         ?? ReadRegistry("HKCU\\SOFTWARE\\Node.js", "InstallPath");
            if (!string.IsNullOrEmpty(regDir))
            {
                string candidate = Path.Combine(regDir, "node.exe");
                if (File.Exists(candidate)) return candidate;
            }

            // 2. PATH.
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string raw in pathVar.Split(';'))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                string candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate)) return candidate;
            }

            // 3. Common install locations, derived from environment variables
            //    (no hard-coded drive letters).
            string[] known = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
            };
            foreach (string candidate in known)
            {
                if (File.Exists(candidate)) return candidate;
            }

            throw new FileNotFoundException(
                "找不到 node.exe。请安装 Node.js,或设置环境变量 DSH_NODE 指向 node.exe 的完整路径。");
        }

        private static string ResolveDshCli()
        {
            string fromEnv = Environment.GetEnvironmentVariable("DSH_CLI");
            if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

            // A path the user saved via the "please point me to bin.js" prompt.
            string saved = ReadSavedCliPath();
            if (saved != null) return saved;

            string exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";

            // 1. Portable layout: app\apps\cli\lib\bin.js beside the exe.
            string portable = Path.Combine(exeDir, "app", "apps", "cli", "lib", "bin.js");
            if (File.Exists(portable)) return portable;

            // 2. Walk up from the exe directory looking for a checkout root that
            //    contains apps\cli\lib\bin.js. Handles any depth of nesting and
            //    any drive, so no hard-coded absolute path is needed.
            string cursor = exeDir;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(cursor); i++)
            {
                string candidate = Path.Combine(cursor, "apps", "cli", "lib", "bin.js");
                if (File.Exists(candidate)) return candidate;
                DirectoryInfo parent = Directory.GetParent(cursor);
                if (parent == null) break;
                cursor = parent.FullName;
            }

            // 3. Globally-installed `dsh` (npm/pnpm install -g @deepseek-ai/dsh).
            string globalCli = ResolveGlobalDshCli();
            if (globalCli != null) return globalCli;

            // 3b. npx-installed `dsh` (npx @deepseek-ai/dsh): cached under the
            //     npx cache, which neither -g nor a checkout search reaches.
            string npxCli = ResolveNpxDshCli();
            if (npxCli != null) return npxCli;

            // 4. Bounded filesystem fallback: find a git-cloned checkout anywhere
            //    reachable without a full-disk scan.
            string searched = SearchFilesystemForCli();
            if (searched != null) return searched;

            // 5. Nothing found: ask the user to point at bin.js, then remember it.
            string prompted = PromptForCliPath();
            if (prompted != null) return prompted;

            throw new FileNotFoundException(
                "找不到 dsh CLI 入口(bin.js)。请把启动器放进 harness 仓库目录,或设置环境变量 DSH_CLI 指向 bin.js。");
        }

        // 用户手动指定 CLI 路径的持久化(首次引导后不再询问)。
        private static string ConfigCliPathFile()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarnessDesktop");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "cli-path.txt");
        }

        private static string ReadSavedCliPath()
        {
            try
            {
                string f = ConfigCliPathFile();
                if (File.Exists(f))
                {
                    string p = File.ReadAllText(f).Trim();
                    if (p.Length > 0 && File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        private static void SaveCliPath(string path)
        {
            try { File.WriteAllText(ConfigCliPathFile(), path); }
            catch { }
        }

        private static string PromptForCliPath()
        {
            try
            {
                using (CliPromptForm form = new CliPromptForm())
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        string p = form.CliPath.Trim();
                        if (p.Length > 0 && File.Exists(p))
                        {
                            SaveCliPath(p);
                            return p;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // Locate a globally-installed `dsh` (npm install -g @deepseek-ai/dsh).
        // npm/pnpm put a `dsh.cmd`/`dsh` shim in a directory on PATH; the real
        // entry sits at <that dir>\node_modules\@deepseek-ai\dsh\lib\bin.js.
        private static string ResolveGlobalDshCli()
        {
            // npm default global prefix (fast path, no PATH walk needed).
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string npmDefault = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (File.Exists(npmDefault)) return npmDefault;

            // Any npm/pnpm/yarn global bin directory on PATH: the shim and the
            // package live side by side under <bindir>\node_modules\@deepseek-ai\dsh.
            foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                bool hasShim = File.Exists(Path.Combine(dir, "dsh.cmd")) || File.Exists(Path.Combine(dir, "dsh"));
                if (!hasShim) continue;
                string bin = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                if (File.Exists(bin)) return bin;
            }
            return null;
        }

        // npx-installed `dsh` (npx @deepseek-ai/dsh web): npx caches the package
        // under %LOCALAPPDATA%\npm-cache\_npx\<hash>\node_modules\@deepseek-ai\dsh.
        // Each invocation may have its own hash dir, so scan them all.
        private static string ResolveNpxDshCli()
        {
            string cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "npm-cache", "_npx");
            if (!Directory.Exists(cache)) return null;
            foreach (string dir in SafeGetDirectories(cache))
            {
                string bin = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                if (File.Exists(bin)) return bin;
            }
            return null;
        }

        // Bounded filesystem fallback for a git-cloned checkout whose directory
        // we could not reach by walking up from the exe. Avoids a full-disk scan:
        // it checks drive-root children and the user profile to a bounded depth,
        // skipping heavyweight subtrees, which covers the common clone locations
        // in a second or two.
        private static string SearchFilesystemForCli()
        {
            System.Collections.Generic.HashSet<string> seen =
                new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Direct children of every drive root: X:\<folder>\apps\cli\lib\bin.js.
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType == DriveType.CDRom) continue;
                    foreach (string sub in SafeGetDirectories(drive.RootDirectory.FullName))
                    {
                        string candidate = Path.Combine(sub, "apps", "cli", "lib", "bin.js");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch
            {
                // Drive enumeration is best-effort; fall through to the profile walk.
            }

            // 2. User profile to a bounded depth (skip heavyweight subtrees).
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return SearchDirForBinJs(home, 3, seen);
        }

        private static string SearchDirForBinJs(string dir, int depth, System.Collections.Generic.HashSet<string> seen)
        {
            if (depth < 0 || string.IsNullOrEmpty(dir) || !seen.Add(dir)) return null;
            string candidate = Path.Combine(dir, "apps", "cli", "lib", "bin.js");
            if (File.Exists(candidate)) return candidate;
            if (depth == 0) return null;
            foreach (string sub in SafeGetDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name == "node_modules" || name == ".git" || name == "AppData" || name == "Application Data" || name == "Library") continue;
                string found = SearchDirForBinJs(sub, depth - 1, seen);
                if (found != null) return found;
            }
            return null;
        }

        private static string[] SafeGetDirectories(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return new string[0]; }
        }

        // The DSH profile to boot (default "web"). A DSH_PROFILE environment
        // variable overrides it, so the launcher is not bound to this machine's
        // web profile. The value is kept to a single argv token (no spaces or
        // path separators), falling back to "web" on anything invalid.
        private static string ResolveProfile()
        {
            string fromEnv = Environment.GetEnvironmentVariable("DSH_PROFILE");
            string profile = string.IsNullOrWhiteSpace(fromEnv) ? "web" : fromEnv.Trim();
            if (profile.Length == 0 || profile.IndexOfAny(new[] { ' ', '"', '\'', '/', '\\' }) >= 0)
            {
                Log("DSH_PROFILE invalid, falling back to 'web': " + (fromEnv ?? "(null)"));
                return "web";
            }
            return profile;
        }

        private static string WebViewDataDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(local, "DeepSeekHarnessDesktop", "WebView2");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. Fails harmlessly when the
        // manifest already set awareness (the call then returns an error, which
        // is fine — the process is already aware).
        private static void TrySetPerMonitorV2DpiAwareness()
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch (EntryPointNotFoundException)
            {
                // Pre-Windows-10 hosts lack the API; the manifest is inert there
                // too, and blurring only appears on >100% scaling anyway.
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        private static string DescribeNode(string nodePath)
        {
            return "node: " + nodePath;
        }

        private static string DescribeCli(string cli)
        {
            return "dsh CLI: " + cli;
        }

        // ── port ──────────────────────────────────────────────────────────────

        // The fixed UI port (3080 by default). A DSH_PORT environment variable
        // overrides it. If the chosen port is already in use the launcher fails
        // loudly instead of silently moving to a different port, so the address
        // the user expects always matches the address actually served.
        private static int ResolvePort()
        {
            string fromEnv = Environment.GetEnvironmentVariable("DSH_PORT");
            int port;
            if (!string.IsNullOrEmpty(fromEnv) && int.TryParse(fromEnv, out port) && port > 0 && port < 65536)
            {
                return EnsureFree(port, fromEnv);
            }
            return EnsureFree(DefaultStartPort, DefaultStartPort.ToString());
        }

        private static int EnsureFree(int port, string label)
        {
            try
            {
                TcpListener probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                throw new IOException(
                    "端口 " + port + " 已被占用(" + label + ")。\n" +
                    "请先关闭占用该端口的程序,或设置环境变量 DSH_PORT 改用其他端口(如 3081)。");
            }
        }

        // ── server process ────────────────────────────────────────────────────

        private static Process StartServer(string nodePath, string dshCli, string profile, int port)
        {
            string args = "\"" + dshCli + "\" --profile " + profile + " --host 127.0.0.1 --port " + port;
            ProcessStartInfo psi = new ProcessStartInfo(nodePath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(dshCli) ?? "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                Process server = Process.Start(psi);

                // Stream node's output to the launcher log asynchronously
                // (Begin*ReadLine avoids the deadlock that occurs when the
                // child's stdio buffer fills). Also parse the bound port so we
                // can report accurately when the server silently moves ports.
                server.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    Log("[node] " + e.Data);
                    Match m = Regex.Match(e.Data, @"127\.0\.0\.1:(\d+)");
                    if (m.Success) { int p; if (int.TryParse(m.Groups[1].Value, out p)) detectedPort = p; }
                };
                server.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Log("[node err] " + e.Data);
                };
                server.BeginOutputReadLine();
                server.BeginErrorReadLine();

                Log("started node: " + nodePath + " " + args + " (pid " + server.Id + ")");

                // Put the server in a kill-on-close Job Object so the whole
                // process tree is reaped even if this launcher crashes or is
                // killed. Failure falls back to taskkill on window close.
                IntPtr job = CreateKillOnCloseJob();
                if (job != IntPtr.Zero)
                {
                    bool assigned = false;
                    try { assigned = AssignProcessToJobObject(job, server.Handle); }
                    catch { assigned = false; }
                    if (assigned)
                    {
                        serverJob = job; // keep the handle alive
                        Log("assigned node pid " + server.Id + " to kill-on-close job object");
                    }
                    else
                    {
                        CloseHandle(job);
                        Log("AssignProcessToJobObject failed, falling back to taskkill");
                    }
                }

                return server;
            }
            catch (Exception ex)
            {
                Log("failed to start node: " + ex);
                ShowError("无法启动服务", "启动 node 失败:\n" + ex.Message + "\n\n" + DescribeNode(nodePath));
                return null;
            }
        }

        private static bool WaitForReady(int port, Process server, out string diag)
        {
            diag = "";
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (server.HasExited)
                {
                    diag = "dsh 服务进程已退出(exit code " + server.ExitCode + ")。\n日志: " + LogPath();
                    return false;
                }
                if (HttpPing(port)) return true;
                Thread.Sleep(300);
            }
            if (HttpPing(port)) return true;

            if (detectedPort > 0 && detectedPort != port)
            {
                diag = "dsh 服务实际绑定到了端口 " + detectedPort + " 而不是 " + port +
                       "。\n可能端口 " + port + " 被其他程序占用。请关闭占用程序,或设置 DSH_PORT。";
            }
            else
            {
                diag = "等待 http://127.0.0.1:" + port + " 就绪超时(60 秒)。\n日志: " + LogPath();
            }
            return false;
        }

        private static bool HttpPing(int port)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/");
                req.Timeout = 2000;
                req.Method = "GET";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return resp.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static void KillTree(int pid)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                }
            }
            catch
            {
                // Best effort; the process may already be gone.
            }
        }

        internal static void ShowError(string title, string message)
        {
            Log("error [" + title + "]: " + message);
            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ── logging ────────────────────────────────────────────────────────────

        internal static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    File.AppendAllText(LogPath(),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch
            {
                // Best effort; logging must never take down the launcher.
            }
        }

        internal static string LogPath()
        {
            if (logPath == null)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarnessDesktop");
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "launcher.log");
            }
            return logPath;
        }

        // ── registry ───────────────────────────────────────────────────────────

        private static string ReadRegistry(string keyPath, string valueName)
        {
            try
            {
                string[] parts = keyPath.Split(new[] { '\\' }, 2);
                if (parts.Length != 2) return null;
                Microsoft.Win32.RegistryKey root = parts[0] == "HKLM"
                    ? Microsoft.Win32.Registry.LocalMachine
                    : Microsoft.Win32.Registry.CurrentUser;
                using (Microsoft.Win32.RegistryKey key = root.OpenSubKey(parts[1]))
                {
                    object val = key == null ? null : key.GetValue(valueName);
                    return val as string;
                }
            }
            catch
            {
                return null;
            }
        }

        // ── single instance ────────────────────────────────────────────────────

        private static void BringExistingToFront()
        {
            IntPtr hwnd = FindWindow(null, AppTitle);
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ── job object (reap node tree if the launcher dies) ───────────────────

        private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            JOBOBJECTINFOCLASS JobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private enum JOBOBJECTINFOCLASS
        {
            JobObjectExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
                return job;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly int port;
        private readonly int serverPid;
        private readonly string webViewData;
        private Panel webHost;
        private WebView2[] views;
        private bool[] viewLoaded;
        private string[] pageUrls;
        private int activeView;
        private WebView2 webView;
        private Button harnessButton;
        private Button consoleButton;
        private Button chatButton;
        private Button githubButton;
        private Button settingsButton;
        private Panel titleBar;
        private Panel sidebar;
        private Panel settingsPage;
        private ComboBox themeBox;
        private System.Drawing.Image backgroundLight;
        private System.Drawing.Image backgroundDark;

        // The page index currently highlighted in the sidebar.
        private int currentIndex = 0;

        public MainForm(int port, int serverPid, string webViewData)
        {
            this.port = port;
            this.serverPid = serverPid;
            this.webViewData = webViewData;
            Text = Launcher.AppTitle;
            Icon = LoadAppIcon();
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1280;
            Height = 860;
            // 原生应用外观:去掉系统标题栏,由自绘标题栏承担拖动/窗口控制。
            FormBorderStyle = FormBorderStyle.None;
            FormClosing += OnFormClosing;

            // 载入上次保存的背景图;有背景时设 WebView2 透明环境变量
            // (在 WebView 环境创建前设置,避免透明背景的白底闪烁)。
            LoadBackground();

            pageUrls = new string[] { HarnessUrl(), ConsoleUrl, ChatUrl, GitHubUrl };
            views = new WebView2[4];
            viewLoaded = new bool[4];

            // WinForms docks controls in reverse z-order: the last control
            // added claims space first. The web host (containing four
            // independent WebViews) fills first, then the sidebar and title bar
            // claim their strips, and the settings page overlays the web host.
            webHost = new Panel { Dock = DockStyle.Fill };
            Controls.Add(webHost);
            for (int i = 0; i < views.Length; i++)
            {
                views[i] = new WebView2 { Dock = DockStyle.Fill, Visible = false };
                webHost.Controls.Add(views[i]);
            }
            webView = views[0];

            // Dock 顺序 = 后 add 先占位:设置页(Fill)在 webHost 之后、边栏之前
            // add,使其覆盖 webHost 却不遮住标题栏与侧边栏。
            BuildSettingsPage();
            BuildSidebar();
            BuildTitleBar();
            Load += OnLoad;
        }

        private string HarnessUrl()
        {
            return "http://127.0.0.1:" + port;
        }

        // ── 背景图持久化 ─────────────────────────────────────────────────
        // 亮/暗背景图路径存到 %LocalAppData%\DeepSeekHarnessDesktop\,下次启动恢复。

        private static string BackgroundPathFile(bool light)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarnessDesktop");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, light ? "bg-light.txt" : "bg-dark.txt");
        }

        private static string ReadBackgroundPath(bool light)
        {
            try
            {
                string f = BackgroundPathFile(light);
                if (File.Exists(f))
                {
                    string p = File.ReadAllText(f).Trim();
                    if (p.Length > 0) return p;
                }
            }
            catch { }
            return null;
        }

        private static void SaveBackgroundPath(bool light, string path)
        {
            try { File.WriteAllText(BackgroundPathFile(light), path); }
            catch { }
        }

        private static void ClearBackgroundPath(bool light)
        {
            try { File.Delete(BackgroundPathFile(light)); }
            catch { }
        }

        private void LoadBackground()
        {
            try
            {
                string light = ReadBackgroundPath(true);
                string dark = ReadBackgroundPath(false);
                if (light != null && File.Exists(light)) backgroundLight = System.Drawing.Image.FromFile(light);
                if (dark != null && File.Exists(dark)) backgroundDark = System.Drawing.Image.FromFile(dark);
            }
            catch { }
        }

        private const string ConsoleUrl = "https://platform.deepseek.com/";
        private const string ChatUrl = "https://chat.deepseek.com/";
        private const string GitHubUrl = "https://github.com/";

        // ── 应用边框(顶部标题栏 + 左侧边栏) ────────────────────────────
        // 无系统边框后,顶部标题栏承担应用标题与最小化/最大化/关闭(可拖动、
        // 双击切换最大化);左侧边栏承担页面切换(Harness/Platform/Chat/GitHub)
        // 与底部设置。边框颜色跟随系统深浅色,也可在设置里手动指定。

        private const int TitleBarHeight = 40;
        private const int SidebarWidth = 140;
        private const int SideButtonHeight = 46;

        private enum ColorMode { FollowSystem, Light, Dark }
        private ColorMode colorMode = ColorMode.FollowSystem;

        private static bool SystemUsesLightTheme()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v != 0;
                }
            }
            catch { }
            return true;
        }

        private bool IsDarkMode()
        {
            return colorMode == ColorMode.Dark
                || (colorMode == ColorMode.FollowSystem && !SystemUsesLightTheme());
        }

        private void BuildTitleBar()
        {
            titleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = TitleBarHeight,
            };
            titleBar.MouseDown += OnTitleBarMouseDown;
            titleBar.MouseDoubleClick += OnTitleBarDoubleClick;
            titleBar.Resize += (s, e) => LayoutTitleBar();

            // 折叠按钮:左键收起/展开左侧边栏
            Button collapse = MakeWindowButton("\u2630", OnCollapseClick);
            collapse.Name = "collapse";
            collapse.Location = new System.Drawing.Point(8, 5);
            collapse.Width = 34;
            titleBar.Controls.Add(collapse);

            // 应用标题(与标题栏一样可拖动/双击)
            Label title = new Label
            {
                Text = Launcher.AppTitle,
                AutoSize = true,
                BackColor = System.Drawing.Color.Transparent,
                Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(46, 11),
            };
            title.MouseDown += OnTitleBarMouseDown;
            title.MouseDoubleClick += OnTitleBarDoubleClick;
            titleBar.Controls.Add(title);

            // 窗口控制按钮(最右侧,由 LayoutTitleBar 定位)
            Button close = MakeWindowButton("\u2715", OnCloseClick);
            close.Name = "winClose";
            close.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(0xC4, 0x2B, 0x1C);
            Button max = MakeWindowButton("\u25A1", OnMaximizeClick);
            max.Name = "winMax";
            Button min = MakeWindowButton("\u2014", OnMinimizeClick);
            min.Name = "winMin";
            titleBar.Controls.Add(min);
            titleBar.Controls.Add(max);
            titleBar.Controls.Add(close);

            Controls.Add(titleBar);
        }

        private void BuildSidebar()
        {
            sidebar = new Panel { Dock = DockStyle.Left, Width = SidebarWidth };

            settingsButton = MakeSettingsButton();
            settingsButton.Dock = DockStyle.Bottom;

            harnessButton = MakeSideButton("Harness", 0);
            consoleButton = MakeSideButton("Platform", 1);
            chatButton = MakeSideButton("Chat", 2);
            githubButton = MakeSideButton("GitHub", 3);

            // Dock 顺序:后加入的先占位,故先放设置(底部),再依次放导航(顶部往下)。
            sidebar.Controls.Add(settingsButton);
            sidebar.Controls.Add(githubButton);
            sidebar.Controls.Add(chatButton);
            sidebar.Controls.Add(consoleButton);
            sidebar.Controls.Add(harnessButton);

            Controls.Add(sidebar);
        }

        // 侧边栏导航按钮:全宽填充、无圆角、无边框。未选白底黑字,选中蓝底白字。
        // 左键:切换到对应页面(多实例,保留上次浏览位置);右键:刷新该页。
        private Button MakeSideButton(string label, int index)
        {
            Button b = new Button
            {
                Text = label,
                Dock = DockStyle.Top,
                Height = SideButtonHeight,
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.Color.White,
                ForeColor = System.Drawing.Color.Black,
                Font = new System.Drawing.Font("Segoe UI", 11f, System.Drawing.FontStyle.Bold),
                Cursor = Cursors.Hand,
                Tag = index,
                Margin = new Padding(0),
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(0xE8, 0xE8, 0xE8);
            b.Click += OnNavClick;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem refresh = new ToolStripMenuItem("刷新");
            refresh.Click += (s, e) => RefreshPage(index);
            menu.Items.Add(refresh);
            b.ContextMenuStrip = menu;
            return b;
        }

        private Button MakeSettingsButton()
        {
            Button b = new Button
            {
                Text = "设置",
                Dock = DockStyle.Top,
                Height = SideButtonHeight,
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.Color.White,
                ForeColor = System.Drawing.Color.Black,
                Font = new System.Drawing.Font("Segoe UI", 11f, System.Drawing.FontStyle.Bold),
                Cursor = Cursors.Hand,
                Tag = 4,
                Margin = new Padding(0),
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(0xE8, 0xE8, 0xE8);
            b.Click += OnNavClick;
            return b;
        }

        // ── 设置页(第 5 个页面,与四个跳转页同级) ─────────────────────
        // 填满 webHost,含主题(黑/白/跟随系统)与屏幕缩放,无关闭按钮
        // (通过切到其他页离开)。
        private void BuildSettingsPage()
        {
            settingsPage = new Panel { Dock = DockStyle.Fill, Visible = false };

            Label heading = new Label
            {
                Text = "设置",
                Font = new System.Drawing.Font("Segoe UI", 20f, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.Black,
                BackColor = System.Drawing.Color.Transparent,
                Location = new System.Drawing.Point(40, 36),
                AutoSize = true,
            };

            Label themeLabel = new Label
            {
                Text = "主题",
                Font = new System.Drawing.Font("Segoe UI", 11f),
                ForeColor = System.Drawing.Color.Black,
                BackColor = System.Drawing.Color.Transparent,
                Location = new System.Drawing.Point(40, 110),
                AutoSize = true,
            };
            themeBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(140, 106),
                Width = 200,
                Font = new System.Drawing.Font("Segoe UI", 11f),
            };
            themeBox.Items.AddRange(new object[] { "黑", "白", "跟随系统" });
            themeBox.SelectedIndex = 2;
            themeBox.SelectedIndexChanged += (s, e) =>
            {
                if (themeBox.SelectedIndex == 0) colorMode = ColorMode.Dark;
                else if (themeBox.SelectedIndex == 1) colorMode = ColorMode.Light;
                else colorMode = ColorMode.FollowSystem;
                ApplyColorScheme();
            };

            Label zoomLabel = new Label
            {
                Text = "屏幕缩放",
                Font = new System.Drawing.Font("Segoe UI", 11f),
                ForeColor = System.Drawing.Color.Black,
                BackColor = System.Drawing.Color.Transparent,
                Location = new System.Drawing.Point(40, 160),
                AutoSize = true,
            };
            ComboBox zoomBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(140, 156),
                Width = 200,
                Font = new System.Drawing.Font("Segoe UI", 11f),
            };
            double[] factors = { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 };
            string[] zoomLabels = { "80%", "90%", "100%(跟随系统)", "110%", "125%", "150%", "175%", "200%" };
            zoomBox.Items.AddRange(zoomLabels);
            zoomBox.SelectedIndex = 2;
            zoomBox.SelectedIndexChanged += (s, e) =>
            {
                if (zoomBox.SelectedIndex >= 0 && zoomBox.SelectedIndex < factors.Length)
                    ApplyZoom(factors[zoomBox.SelectedIndex]);
            };

            // 背景图片:亮色图/暗色图各一张;设置后主题选项失效,系统深浅色决定用哪张。
            Label bgLabel = new Label
            {
                Text = "背景图片(设置后主题失效,跟随系统深浅色选用亮/暗图)",
                Font = new System.Drawing.Font("Segoe UI", 11f),
                ForeColor = System.Drawing.Color.Black,
                BackColor = System.Drawing.Color.Transparent,
                Location = new System.Drawing.Point(40, 210),
                AutoSize = true,
            };
            Button lightBgButton = new Button
            {
                Text = "亮色背景图",
                Location = new System.Drawing.Point(40, 246),
                Size = new System.Drawing.Size(150, 34),
                FlatStyle = FlatStyle.Flat,
                Font = new System.Drawing.Font("Segoe UI", 11f),
            };
            lightBgButton.FlatAppearance.BorderSize = 1;
            lightBgButton.Click += (s, e) => ChooseBackground(true);
            Button darkBgButton = new Button
            {
                Text = "暗色背景图",
                Location = new System.Drawing.Point(200, 246),
                Size = new System.Drawing.Size(150, 34),
                FlatStyle = FlatStyle.Flat,
                Font = new System.Drawing.Font("Segoe UI", 11f),
            };
            darkBgButton.FlatAppearance.BorderSize = 1;
            darkBgButton.Click += (s, e) => ChooseBackground(false);
            Button clearBgButton = new Button
            {
                Text = "清除背景",
                Location = new System.Drawing.Point(360, 246),
                Size = new System.Drawing.Size(130, 34),
                FlatStyle = FlatStyle.Flat,
                Font = new System.Drawing.Font("Segoe UI", 11f),
            };
            clearBgButton.FlatAppearance.BorderSize = 1;
            clearBgButton.Click += (s, e) => ClearBackground();

            settingsPage.Controls.Add(heading);
            settingsPage.Controls.Add(themeLabel);
            settingsPage.Controls.Add(themeBox);
            settingsPage.Controls.Add(zoomLabel);
            settingsPage.Controls.Add(zoomBox);
            settingsPage.Controls.Add(bgLabel);
            settingsPage.Controls.Add(lightBgButton);
            settingsPage.Controls.Add(darkBgButton);
            settingsPage.Controls.Add(clearBgButton);

            webHost.Controls.Add(settingsPage);
        }

        private void ChooseBackground(bool light)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";
                dlg.Title = light ? "选择亮色背景图" : "选择暗色背景图";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    System.Drawing.Image img = System.Drawing.Image.FromFile(dlg.FileName);
                    if (light)
                    {
                        if (backgroundLight != null) backgroundLight.Dispose();
                        backgroundLight = img;
                    }
                    else
                    {
                        if (backgroundDark != null) backgroundDark.Dispose();
                        backgroundDark = img;
                    }
                    SaveBackgroundPath(light, dlg.FileName);
                    ApplyColorScheme();
                }
                catch (Exception ex)
                {
                    Launcher.ShowError("无法加载图片", ex.Message);
                }
            }
        }

        private void ClearBackground()
        {
            if (backgroundLight != null) { backgroundLight.Dispose(); backgroundLight = null; }
            if (backgroundDark != null) { backgroundDark.Dispose(); backgroundDark = null; }
            ClearBackgroundPath(true);
            ClearBackgroundPath(false);
            ApplyColorScheme();
        }

        private void ApplyZoom(double factor)
        {
            foreach (WebView2 v in views)
            {
                try { if (v.CoreWebView2 != null) v.ZoomFactor = factor; } catch { }
            }
        }

        private bool HasBackground()
        {
            return backgroundLight != null || backgroundDark != null;
        }

        private System.Drawing.Image CurrentBackground()
        {
            bool dark = IsDarkMode();
            return dark ? (backgroundDark ?? backgroundLight) : (backgroundLight ?? backgroundDark);
        }

        // 边框(标题栏 + 侧边栏)配色跟随当前模式;有壁纸时框架透明透出壁纸。
        private void ApplyChromeColors()
        {
            bool dark = IsDarkMode();
            System.Drawing.Color chromeBg = dark
                ? System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E)
                : System.Drawing.Color.FromArgb(0xF3, 0xF3, 0xF3);
            System.Drawing.Color chromeText = dark
                ? System.Drawing.Color.White
                : System.Drawing.Color.Black;

            bool hasBg = HasBackground();
            System.Drawing.Image bg = hasBg ? CurrentBackground() : null;

            // 窗体背景:有壁纸时铺壁纸(左上角固定、不拉伸,窗口缩放只是裁剪/显露更多)
            if (bg != null)
            {
                BackgroundImage = bg;
                BackgroundImageLayout = ImageLayout.None;
            }
            else
            {
                BackgroundImage = null;
            }

            // 框架背景:有壁纸时透明(透出壁纸),否则纯色
            System.Drawing.Color frameBg = bg != null ? System.Drawing.Color.Transparent : chromeBg;
            // 框架文字:有壁纸时统一白色(通用),否则跟随主题
            System.Drawing.Color frameText = bg != null ? System.Drawing.Color.White : chromeText;

            if (titleBar != null) titleBar.BackColor = frameBg;
            if (sidebar != null) sidebar.BackColor = frameBg;
            if (webHost != null) webHost.BackColor = frameBg;
            if (settingsPage != null)
            {
                settingsPage.BackColor = frameBg;
                foreach (Control c in settingsPage.Controls)
                {
                    if (c is Label) c.ForeColor = frameText;
                    if (c is Button)
                    {
                        Button btn = (Button)c;
                        if (bg != null)
                        {
                            btn.BackColor = System.Drawing.Color.Transparent;
                            btn.ForeColor = System.Drawing.Color.White;
                            btn.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF);
                        }
                        else if (dark)
                        {
                            btn.BackColor = System.Drawing.Color.FromArgb(0x2A, 0x2A, 0x2A);
                            btn.ForeColor = System.Drawing.Color.White;
                            btn.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(0x30, 0x36, 0x3D);
                        }
                        else
                        {
                            btn.BackColor = System.Drawing.Color.White;
                            btn.ForeColor = System.Drawing.Color.Black;
                            btn.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(0xD0, 0xD7, 0xDE);
                        }
                    }
                }
            }
            if (titleBar != null)
            {
                foreach (Control c in titleBar.Controls)
                {
                    if (c is Label) c.ForeColor = frameText;
                    if (c is Button && c.Name != null
                        && (c.Name.StartsWith("win", StringComparison.Ordinal) || c.Name == "collapse"))
                        c.ForeColor = frameText;
                }
            }

            // 恰好只设置一张背景图时,主题不可选(切换黑白无意义);无背景或两张时可选。
            bool singleBackground = (backgroundLight != null) != (backgroundDark != null);
            if (themeBox != null) themeBox.Enabled = !singleBackground;

            HighlightNav(currentIndex);
        }

        // 边框颜色 + 所有 WebView 页面首选颜色方案一起切换(页面跟随深色/浅色)。
        // 有壁纸时 WebView 背景透明,透出壁纸。
        private void ApplyColorScheme()
        {
            ApplyChromeColors();
            CoreWebView2PreferredColorScheme scheme;
            if (colorMode == ColorMode.Dark) scheme = CoreWebView2PreferredColorScheme.Dark;
            else if (colorMode == ColorMode.Light) scheme = CoreWebView2PreferredColorScheme.Light;
            else scheme = CoreWebView2PreferredColorScheme.Auto;
            foreach (WebView2 v in views)
            {
                try
                {
                    if (v.CoreWebView2 != null)
                    {
                        // WebView 一律不透明(harness 内容区不透明,只框架透壁纸),切换不闪烁
                        v.DefaultBackgroundColor = IsDarkMode()
                            ? System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E)
                            : System.Drawing.Color.White;
                        v.CoreWebView2.Profile.PreferredColorScheme = scheme;
                    }
                }
                catch { }
            }
        }

        // 窗口控制按钮右对齐:跟随标题栏宽度变化重新定位。
        private void LayoutTitleBar()
        {
            if (titleBar == null) return;
            foreach (Control c in titleBar.Controls)
            {
                if (c is Button && c.Name != null && c.Name.StartsWith("win", StringComparison.Ordinal))
                {
                    int index = c.Name == "winClose" ? 1 : c.Name == "winMax" ? 2 : 3;
                    c.Location = new System.Drawing.Point(titleBar.ClientSize.Width - 46 * index, 6);
                }
            }
        }

        private void OnTitleBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (WindowState == FormWindowState.Maximized) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        private void OnTitleBarDoubleClick(object sender, EventArgs e)
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        private Button MakeWindowButton(string glyph, EventHandler onClick)
        {
            Button b = new Button
            {
                Text = glyph,
                Size = new System.Drawing.Size(46, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.Color.Transparent,
                ForeColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Segoe UI Symbol", 10f),
                Cursor = Cursors.Default,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(70, 255, 255, 255);
            b.Click += onClick;
            return b;
        }

        private void OnMinimizeClick(object sender, EventArgs e) { WindowState = FormWindowState.Minimized; }
        private void OnMaximizeClick(object sender, EventArgs e)
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }
        private void OnCloseClick(object sender, EventArgs e) { Close(); }
        private void OnCollapseClick(object sender, EventArgs e)
        {
            if (sidebar != null) sidebar.Visible = !sidebar.Visible;
        }

        // 无边框窗口的 8px 边缘热区交给系统做 resize,保住调整窗口大小的能力。
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) // WM_NCHITTEST
            {
                base.WndProc(ref m);
                if ((int)m.Result == 0x01) // HTCLIENT
                {
                    System.Drawing.Point p = PointToClient(Cursor.Position);
                    int w = ClientSize.Width;
                    int h = ClientSize.Height;
                    const int grip = 8;
                    bool l = p.X < grip, r = p.X >= w - grip;
                    bool t = p.Y < grip, b = p.Y >= h - grip;
                    if (t && l) m.Result = (IntPtr)13;      // HTTOPLEFT
                    else if (t && r) m.Result = (IntPtr)14; // HTTOPRIGHT
                    else if (b && l) m.Result = (IntPtr)16; // HTBOTTOMLEFT
                    else if (b && r) m.Result = (IntPtr)17; // HTBOTTOMRIGHT
                    else if (t) m.Result = (IntPtr)12;      // HTTOP
                    else if (b) m.Result = (IntPtr)15;      // HTBOTTOM
                    else if (l) m.Result = (IntPtr)10;      // HTLEFT
                    else if (r) m.Result = (IntPtr)11;      // HTRIGHT
                }
                return;
            }
            base.WndProc(ref m);
        }

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private void OnNavClick(object sender, EventArgs e)
        {
            int index = (int)((Button)sender).Tag;
            SwitchPage(index);
        }

        // 切换到指定页面:首次访问才加载,之后切换只是显示/隐藏,保留上次
        // 浏览位置(尤其 GitHub 子页面多,切换不再刷新丢位置)。
        // 页面索引:0=Harness 1=Platform 2=Chat 3=GitHub 4=设置。
        private void SwitchPage(int index)
        {
            if (index < 0 || index > 4 || index == activeView) return;
            if (index < 4 && !viewLoaded[index])
            {
                viewLoaded[index] = true;
                try { views[index].CoreWebView2.Navigate(pageUrls[index]); } catch { }
            }

            if (index < 4)
            {
                // 切到 view:先显示新 view(覆盖旧),再隐藏旧,避免空白帧闪烁
                views[index].Visible = true;
                views[index].BringToFront();
                if (activeView < 4) views[activeView].Visible = false;
                else settingsPage.Visible = false;
                webView = views[index];
            }
            else
            {
                // 切到设置页(纯 GDI):必须先隐藏 view(WebView 是 native 窗口会盖住 GDI)
                if (activeView < 4) views[activeView].Visible = false;
                settingsPage.Visible = true;
                settingsPage.BringToFront();
            }
            activeView = index;
            HighlightNav(index);
        }

        private void RefreshPage(int index)
        {
            if (index < 0 || index >= views.Length) return;
            try
            {
                if (views[index].CoreWebView2 != null)
                {
                    views[index].CoreWebView2.Reload();
                }
            }
            catch { }
        }

        private void HighlightNav(int index)
        {
            currentIndex = index;
            ApplyNavState(harnessButton, index == 0);
            ApplyNavState(consoleButton, index == 1);
            ApplyNavState(chatButton, index == 2);
            ApplyNavState(githubButton, index == 3);
            ApplyNavState(settingsButton, index == 4);
        }

        // 参考 GitHub 亮暗主题:
        //   亮色:未选白底黑字 + 浅灰边框;暗色:未选黑底白字 + 深灰边框;
        //   选中(两主题一致):绿底白字 + 绿边框。
        //   有壁纸时:未选透明(透出壁纸)+ 白字 + 半透明边框;选中仍绿底白字。
        private void ApplyNavState(Button button, bool selected)
        {
            if (button == null) return;
            bool dark = IsDarkMode();
            bool hasBg = HasBackground();
            System.Drawing.Color green = System.Drawing.Color.FromArgb(0x2D, 0xA4, 0x4E);
            if (selected)
            {
                button.BackColor = green;
                button.ForeColor = System.Drawing.Color.White;
                button.FlatAppearance.BorderColor = green;
            }
            else if (hasBg)
            {
                button.BackColor = System.Drawing.Color.Transparent;
                button.ForeColor = System.Drawing.Color.White;
                button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF);
            }
            else if (dark)
            {
                button.BackColor = System.Drawing.Color.FromArgb(0x0D, 0x11, 0x17);
                button.ForeColor = System.Drawing.Color.White;
                button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(0x30, 0x36, 0x3D);
            }
            else
            {
                button.BackColor = System.Drawing.Color.White;
                button.ForeColor = System.Drawing.Color.Black;
                button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(0xD0, 0xD7, 0xDE);
            }
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                // 无边框窗口最大化时只占工作区(不盖住任务栏)。
                MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;

                // Detect a missing WebView2 Runtime up front and give the user a
                // concrete install link instead of a generic failure.
                string availableVersion = null;
                try { availableVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch { availableVersion = null; }
                if (string.IsNullOrEmpty(availableVersion))
                {
                    Launcher.ShowError("缺少 WebView2 Runtime",
                        "未检测到 Microsoft Edge WebView2 运行时,无法显示页面。\n\n" +
                        "请从以下地址下载并安装后重试:\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703");
                    Close();
                    return;
                }

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, webViewData, null);
                for (int i = 0; i < views.Length; i++)
                {
                    await views[i].EnsureCoreWebView2Async(env);
                    views[i].CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                    views[i].CoreWebView2.NavigationStarting += OnNavigationStarting;
                }

                // 只加载默认页 Harness;其余页首次切换时才加载。
                activeView = 0;
                webView = views[0];
                viewLoaded[0] = true;
                views[0].Visible = true;
                HighlightNav(0);
                ApplyColorScheme();
                views[0].CoreWebView2.Navigate(pageUrls[0]);
            }
            catch (Exception ex)
            {
                Launcher.Log("WebView2 init failed: " + ex);
                Launcher.ShowError("WebView2 初始化失败", ex.Message);
                Close();
            }
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            // External pop-ups open in the system browser; internal ones stay in
            // the active view (the harness opens its own UI in pop-ups).
            if (IsExternalUrl(e.Uri))
            {
                try { Process.Start(e.Uri); } catch { }
                return;
            }
            try
            {
                views[activeView].CoreWebView2.Navigate(e.Uri);
            }
            catch
            {
                // Best effort; the link simply does not open if navigation fails.
            }
        }

        // Harness 视图保持"应用界面"干净:其内部点外部链接交给系统浏览器。
        // Platform/Chat/GitHub 视图自由导航,保留这些站点的浏览状态(子页面切换不刷新)。
        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (sender == views[0].CoreWebView2)
            {
                if (IsExternalUrl(e.Uri))
                {
                    e.Cancel = true;
                    try { Process.Start(e.Uri); } catch { }
                }
            }
        }

        private bool IsExternalUrl(string uri)
        {
            // Only the launcher's own UI origin may navigate in place. The port
            // suffix is anchored with the following slash so that 127.0.0.1 on
            // any other port (e.g. 3082) is still treated as external.
            string origin = "http://127.0.0.1:" + port + "/";
            if (uri.Equals("http://127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase)) return false;
            return uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            Launcher.KillTree(serverPid);
        }

        // Loads the DeepSeek whale icon embedded as a managed resource
        // (build.ps1 embeds src\assets\deepseek-whale.ico under the name
        // DshDesktop.deepseek-whale.ico). Setting Form.Icon covers both the
        // title-bar icon and the taskbar icon.
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (System.IO.Stream s = asm.GetManifestResourceStream("DshDesktop.deepseek-whale.ico"))
                {
                    if (s != null) return new System.Drawing.Icon(s);
                }
            }
            catch
            {
                // Fall through to the default icon rather than failing startup.
            }
            return null;
        }

    }

    // 找不到 harness 时弹出的"请指定 bin.js 路径"对话框。
    internal sealed class CliPromptForm : Form
    {
        private TextBox pathBox;

        public string CliPath
        {
            get { return pathBox.Text; }
        }

        public CliPromptForm()
        {
            Text = "指定 DeepSeek Harness 位置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new System.Drawing.Size(600, 200);

            Label hint = new Label
            {
                AutoSize = false,
                Location = new System.Drawing.Point(16, 12),
                Size = new System.Drawing.Size(568, 66),
                Text = "没有自动找到 DeepSeek Harness(CLI 入口 bin.js)。\n\n请在下方指定 bin.js 的完整路径,通常在 harness 仓库的 apps\\cli\\lib\\bin.js:",
            };

            pathBox = new TextBox { Location = new System.Drawing.Point(16, 90), Width = 480 };

            Button browse = new Button { Text = "浏览...", Location = new System.Drawing.Point(504, 88), Width = 80 };
            browse.Click += (s, e) =>
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Filter = "bin.js|bin.js|所有文件|*.*";
                    dlg.Title = "选择 bin.js";
                    if (dlg.ShowDialog() == DialogResult.OK) pathBox.Text = dlg.FileName;
                }
            };

            Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new System.Drawing.Point(390, 150), Width = 90 };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new System.Drawing.Point(494, 150), Width = 90 };
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(hint);
            Controls.Add(pathBox);
            Controls.Add(browse);
            Controls.Add(ok);
            Controls.Add(cancel);
        }
    }
}
