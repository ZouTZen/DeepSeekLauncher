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
        private WebView2 webView;
        private RoundedButton harnessButton;
        private RoundedButton consoleButton;
        private RoundedButton chatButton;
        private RoundedButton githubButton;
        private ContextMenuStrip harnessMenu;

        // The URL the user last picked, so a click before WebView2 finishes
        // initializing still lands on the right page.
        private string pendingUrl;

        // True while a navigation was triggered by a top nav button (Platform /
        // Chat / GitHub are intended external sites); page-internal link clicks
        // leave this false and get their external targets routed to the browser.
        private bool navButtonNavigation;

        // DeepSeek brand blue for the selected nav button fill.
        private static readonly System.Drawing.Color ActiveColor = System.Drawing.Color.FromArgb(0x6B, 0x87, 0xD9);

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
            FormClosing += OnFormClosing;

            pendingUrl = HarnessUrl();

            // WinForms docks controls in reverse z-order: the last control
            // added to Controls claims space first. Add the fill-docked
            // WebView2 BEFORE the top-docked nav bar, so the bar claims its
            // strip first and the web view fills only what remains — the
            // previous order filled the web view over the whole client area
            // and let the bar overlap the page's top strip.
            webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(webView);
            BuildNavBar();
            BuildHarnessMenu();
            Load += OnLoad;
        }

        private string HarnessUrl()
        {
            return "http://127.0.0.1:" + port;
        }

        private const string ConsoleUrl = "https://platform.deepseek.com/";
        private const string ChatUrl = "https://chat.deepseek.com/";
        private const string GitHubUrl = "https://github.com/";

        private void BuildNavBar()
        {
            Panel bar = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 8, 8, 8) };

            harnessButton = MakeNavButton("Harness", HarnessUrl());
            consoleButton = MakeNavButton("Platform", ConsoleUrl);
            chatButton = MakeNavButton("Chat", ChatUrl);
            githubButton = MakeNavButton("GitHub", GitHubUrl);

            // Equal width, equal 12px gap, vertically centered on the bar.
            int x = 8;
            harnessButton.Location = new System.Drawing.Point(x, 8);
            x += harnessButton.Width + 12;
            consoleButton.Location = new System.Drawing.Point(x, 8);
            x += consoleButton.Width + 12;
            chatButton.Location = new System.Drawing.Point(x, 8);
            x += chatButton.Width + 12;
            githubButton.Location = new System.Drawing.Point(x, 8);

            bar.Controls.Add(harnessButton);
            bar.Controls.Add(consoleButton);
            bar.Controls.Add(chatButton);
            bar.Controls.Add(githubButton);
            Controls.Add(bar);
        }

        private RoundedButton MakeNavButton(string label, string url)
        {
            RoundedButton b = new RoundedButton
            {
                Text = label,
                Size = new System.Drawing.Size(96, 32),
                Cursor = Cursors.Hand,
                Tag = url,
                Font = System.Drawing.SystemFonts.CaptionFont,
            };
            b.Click += OnNavClick;
            return b;
        }

        private void OnNavClick(object sender, EventArgs e)
        {
            Button b = (Button)sender;
            string url = (string)b.Tag;
            navButtonNavigation = true;
            NavigateTo(url);
        }

        private void NavigateTo(string url)
        {
            pendingUrl = url;
            HighlightNav(url);
            try
            {
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.Navigate(url);
                }
            }
            catch
            {
                // CoreWebView2 not ready yet; OnLoad navigates to pendingUrl once
                // initialization completes.
            }
        }

        private void HighlightNav(string url)
        {
            ApplyNavState(harnessButton, url == HarnessUrl());
            ApplyNavState(consoleButton, url == ConsoleUrl);
            ApplyNavState(chatButton, url == ChatUrl);
            ApplyNavState(githubButton, url == GitHubUrl);
        }

        // Selected: blue fill + white text. Unselected: white fill + black text.
        private static void ApplyNavState(RoundedButton button, bool selected)
        {
            button.Selected = selected;
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
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
                await webView.EnsureCoreWebView2Async(env);

                // Keep every target=_blank / window.open inside this one window:
                // the harness, the DeepSeek console, and chat all stay in the
                // same WebView2 (and share its cookies/localStorage, so console
                // and chat logins persist) instead of opening a browser.
                webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                webView.CoreWebView2.NavigationStarting += OnNavigationStarting;

                HighlightNav(pendingUrl);
                webView.CoreWebView2.Navigate(pendingUrl);
            }
            catch (Exception ex)
            {
                Launcher.Log("WebView2 init failed: " + ex);
                Launcher.ShowError("WebView2 初始化失败", ex.Message);
                Close();
            }
        }

        // Right-click menu on the Harness button: a single "更新缩放率" action
        // that re-matches the page to the current display's system scaling. The
        // default is already 100% (ZoomFactor 1.0), which on a DPI-aware WebView2
        // renders at the monitor's scale. After moving the window to another
        // monitor or changing Windows scaling, this resets ZoomFactor to 1.0 so
        // the page follows the new system scale.
        private void BuildHarnessMenu()
        {
            harnessMenu = new ContextMenuStrip();
            ToolStripMenuItem update = new ToolStripMenuItem("更新缩放率(匹配系统)");
            update.Click += OnUpdateZoomClick;
            harnessMenu.Items.Add(update);
            harnessButton.ContextMenuStrip = harnessMenu;
        }

        private void OnUpdateZoomClick(object sender, EventArgs e)
        {
            double scale = DisplayScale();
            try
            {
                if (webView.CoreWebView2 != null)
                {
                    webView.ZoomFactor = 1.0; // 100% = 跟随系统 DPI
                }
            }
            catch
            {
                // CoreWebView2 not ready yet; ignore.
            }
            Launcher.Log("zoom updated: display scale " + scale.ToString("0.##") + ", ZoomFactor = 1.0 (follow system)");
        }

        // Current display scale of this window: 96 → 1.0, 120 → 1.25, 144 → 1.5.
        private double DisplayScale()
        {
            try
            {
                uint dpi = GetDpiForWindow(Handle);
                if (dpi >= 96) return dpi / 96.0;
            }
            catch
            {
                // Pre-Windows-10-1607 hosts lack GetDpiForWindow.
            }
            return 1.0;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            // External pop-ups open in the system browser; internal ones stay in
            // this window (the harness opens its own UI in pop-ups).
            if (IsExternalUrl(e.Uri))
            {
                try { Process.Start(e.Uri); } catch { }
                return;
            }
            try
            {
                webView.CoreWebView2.Navigate(e.Uri);
                pendingUrl = e.Uri;
            }
            catch
            {
                // Best effort; the link simply does not open if navigation fails.
            }
        }

        // Intercept page-internal navigation: a link inside a Harness answer
        // must not hijack this tab-less, back-less shell. External http(s) URLs
        // open in the system browser; the Harness's own 127.0.0.1 URLs still
        // navigate in place.
        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (navButtonNavigation)
            {
                navButtonNavigation = false;
                return;
            }
            if (IsExternalUrl(e.Uri))
            {
                e.Cancel = true;
                try { Process.Start(e.Uri); } catch { }
            }
        }

        private bool IsExternalUrl(string uri)
        {
            if (uri.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)) return false;
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

        /// <summary>
        /// A nav button drawn as a rounded rectangle: white fill + black text by
        /// default, blue fill + white text when selected, always with a thin
        /// black border and centered text. Owner-drawn because the stock
        /// WinForms Button cannot produce rounded corners.
        /// </summary>
        private sealed class RoundedButton : Button
        {
            private const int CornerRadius = 8;
            private bool selected;

            public RoundedButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);
            }

            /// <summary>Selected state: blue fill + white text when true.</summary>
            public bool Selected
            {
                get { return selected; }
                set
                {
                    if (selected == value) return;
                    selected = value;
                    Invalidate();
                }
            }

            // Clip the control to its rounded outline: the four corners are not
            // part of the control at all, so no rectangular frame can ever show
            // there regardless of what the stock Button would paint.
            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                using (GraphicsPath path = RoundedRect(new System.Drawing.Rectangle(0, 0, Width, Height), CornerRadius))
                {
                    Region = new System.Drawing.Region(path);
                }
            }

            // The rounded Region owns the shape; paint nothing here, leaving the
            // corners (outside the Region) transparent to the nav bar behind.
            protected override void OnPaintBackground(PaintEventArgs e)
            {
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                System.Drawing.Color fill = selected ? ActiveColor : System.Drawing.Color.White;
                System.Drawing.Color text = selected ? System.Drawing.Color.White : System.Drawing.Color.Black;

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                // Inset by one pixel so the 1px border is not clipped at the edge.
                using (GraphicsPath path = RoundedRect(new System.Drawing.Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
                {
                    using (System.Drawing.SolidBrush brush = new System.Drawing.SolidBrush(fill))
                    {
                        e.Graphics.FillPath(brush, path);
                    }
                    using (System.Drawing.Pen pen = new System.Drawing.Pen(System.Drawing.Color.Black, 1f))
                    {
                        e.Graphics.DrawPath(pen, path);
                    }
                }
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    Font,
                    ClientRectangle,
                    text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            /// <summary>Build a rounded-rectangle path for the given bounds.</summary>
            private static GraphicsPath RoundedRect(System.Drawing.Rectangle rect, int radius)
            {
                int d = radius * 2;
                GraphicsPath path = new GraphicsPath();
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
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
