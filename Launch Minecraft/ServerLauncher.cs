using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Launch_Minecraft
{
    public enum ServerEulaStatus
    {
        NoEulaFile,   // eula.txt 不存在 → 直接启动，让服务端自己生成
        Accepted,     // eula.txt 存在且 eula=true → 直接启动
        NeedAccept    // eula.txt 存在且 eula=false → 需要弹窗
    }

    public enum ServerStartPhase
    {
        Detecting,   // 获取启动信息（识别 Java 版本、组装启动命令）
        Starting,    // 启动服务器（脚本/参数已传，等待 Done）
        Running,     // 正在运行（Done 已出现）
        Stopped,     // 已停止（进程退出）
        Failed       // 启动失败
    }

    public enum ServerStartResult
    {
        Success,     // 已进入 Running 状态
        NeedEula,    // 秒退且报告 eula 未同意
        Failed       // 其它失败
    }

    public class ServerStartProgress
    {
        public ServerStartPhase Phase;
        public string Message;
        public int ExitCode;
    }

    /// <summary>
    /// 服务端启动器：
    /// · 加载器识别基于目录特征，不读任何 *.json（避免误读 banned-ips.json 等）
    /// · Java 需求优先走 Mojang 版本清单，失败则本地读 jar / 扫 class
    /// · Forge / NeoForge 的 run.bat 会按 MC 版本查出所需 Java，注入 PATH/JAVA_HOME
    /// · 通过 onProgress 回调上报 Detecting / Starting / Running / Stopped / Failed
    /// </summary>
    public static class ServerLauncher
    {
        // ============================================================
        //                       EULA 检测
        // ============================================================

        public static ServerEulaStatus CheckEula(string serverDir)
        {
            string eulaPath = Path.Combine(serverDir, "eula.txt");
            if (!File.Exists(eulaPath)) return ServerEulaStatus.NoEulaFile;

            try
            {
                string content = File.ReadAllText(eulaPath);
                var m = Regex.Match(content, @"^\s*eula\s*=\s*(true|false)\s*$",
                                    RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (!m.Success) return ServerEulaStatus.NoEulaFile;

                return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    ? ServerEulaStatus.Accepted
                    : ServerEulaStatus.NeedAccept;
            }
            catch
            {
                return ServerEulaStatus.NoEulaFile;
            }
        }

        public static void WriteEulaTrue(string serverDir)
        {
            string eulaPath = Path.Combine(serverDir, "eula.txt");
            var sb = new StringBuilder();
            bool found = false;

            if (File.Exists(eulaPath))
            {
                foreach (var raw in File.ReadAllLines(eulaPath))
                {
                    if (Regex.IsMatch(raw, @"^\s*eula\s*=", RegexOptions.IgnoreCase))
                    {
                        sb.AppendLine("eula=true");
                        found = true;
                    }
                    else
                    {
                        sb.AppendLine(raw);
                    }
                }
            }

            if (!found) sb.AppendLine("eula=true");
            File.WriteAllText(eulaPath, sb.ToString(), new UTF8Encoding(false));
        }

        // ============================================================
        //                       启动服务端
        // ============================================================

        // 保存活跃进程，防止 GC 掉造成事件断流
        private static readonly List<Process> _running = new List<Process>();

        /// <summary>
        /// 启动服务端。通过 onProgress 上报阶段；服务端进入 Running 后本方法返回，
        /// 后台进程继续运行，停止时再通过 onProgress 上报 Stopped。
        /// </summary>
        public static ServerStartResult StartServer(string serverDir, string javaBaseDir,
                                                    Action<ServerStartProgress> onProgress)
        {
            // ---------- 1) Detecting：识别启动入口 ----------
            Report(onProgress, ServerStartPhase.Detecting, "获取启动信息...");

            ProcessStartInfo psi;
            try
            {
                psi = BuildStartInfo(serverDir, javaBaseDir);
            }
            catch (Exception ex)
            {
                Report(onProgress, ServerStartPhase.Failed, "启动失败：" + ex.Message);
                return ServerStartResult.Failed;
            }

            // ---------- 2) Starting：启动进程 ----------
            string logPath = Path.Combine(serverDir, "launcher-server.log");
            var proc = new Process { StartInfo = psi };

            // ★ 三个信号：EULA / Done / Exited
            var eulaHandle = new ManualResetEvent(false);
            var doneHandle = new ManualResetEvent(false);
            var exitHandle = new ManualResetEvent(false);

            var state = new ServerRunState();

            DataReceivedEventHandler onData = (s, e) =>
            {
                if (e.Data == null) return;

                try { File.AppendAllText(logPath, e.Data + Environment.NewLine); }
                catch { }

                try { Console.WriteLine(e.Data); } catch { }

                // ★ 检测到 EULA 提示 → 立刻置信号，主线程会 Kill 进程并返回 NeedEula
                if (!state.EulaDetected &&
                    e.Data.IndexOf("agree to the EULA",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    state.EulaDetected = true;
                    try { eulaHandle.Set(); } catch (ObjectDisposedException) { }
                }

                if (!state.DoneDetected && IsDoneLine(e.Data))
                {
                    state.DoneDetected = true;
                    try { doneHandle.Set(); } catch (ObjectDisposedException) { }
                }
            };

            proc.OutputDataReceived += onData;
            proc.ErrorDataReceived += onData;
            proc.EnableRaisingEvents = true;

            proc.Exited += (s, e) =>
            {
                int code = 0;
                try { code = proc.ExitCode; } catch { }
                state.ExitCode = code;

                lock (_running) _running.Remove(proc);

                if (state.DoneDetected)
                {
                    Report(onProgress, ServerStartPhase.Stopped,
                        $"已停止（退出码 {code}）", code);
                }

                try { exitHandle.Set(); } catch (ObjectDisposedException) { }

                try { eulaHandle.Close(); } catch { }
                try { doneHandle.Close(); } catch { }
                try { exitHandle.Close(); } catch { }
                try { proc.Dispose(); } catch { }
            };

            Report(onProgress, ServerStartPhase.Starting, "启动服务器...");

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Report(onProgress, ServerStartPhase.Failed, "启动失败：" + ex.Message);
                try { eulaHandle.Close(); } catch { }
                try { doneHandle.Close(); } catch { }
                try { exitHandle.Close(); } catch { }
                return ServerStartResult.Failed;
            }

            lock (_running) _running.Add(proc);
            Console.WriteLine($"[Server] 已启动 PID = {proc.Id}，日志：{logPath}");

            // ★ 等待三个信号之一
            //   0 = EULA      → 主动 Kill 并返回 NeedEula
            //   1 = Done      → 正常进入 Running
            //   2 = Exited    → 进程退出（可能秒退或用户关服）
            int idx;
            try
            {
                idx = WaitHandle.WaitAny(
                    new WaitHandle[] { eulaHandle, doneHandle, exitHandle }, 180000);
            }
            catch (ObjectDisposedException)
            {
                idx = 2;
            }

            // ---------- 情况 1：检测到 EULA ----------
            if (idx == 0)
            {
                Console.WriteLine("[Server] 检测到 EULA 未同意，强制终止进程树");
                KillProcessTree(proc);
                try { proc.WaitForExit(5000); } catch { }

                Report(onProgress, ServerStartPhase.Failed,
                    "需要同意 EULA", state.ExitCode);
                return ServerStartResult.NeedEula;
            }

            // ---------- 情况 2：进程已退出 ----------
            if (idx == 2 || SafeHasExited(proc))
            {
                // 进程退出了，但期间也看到了 EULA 提示 → 视为 NeedEula
                if (state.EulaDetected)
                {
                    Report(onProgress, ServerStartPhase.Failed,
                        "需要同意 EULA", state.ExitCode);
                    return ServerStartResult.NeedEula;
                }

                Report(onProgress, ServerStartPhase.Stopped,
                    $"已停止（退出码 {state.ExitCode}）", state.ExitCode);
                return ServerStartResult.Failed;
            }

            // ---------- 情况 3：Done（或超时视为已运行）----------
            Report(onProgress, ServerStartPhase.Running, "正在运行...");
            return ServerStartResult.Success;
        }

        /// <summary>安全判断进程是否已退出（避免 Exited 里 Dispose 后抛异常）</summary>
        private static bool SafeHasExited(Process proc)
        {
            if (proc == null) return true;
            try { return proc.HasExited; }
            catch { return true; }
        }

        /// <summary>
        /// 强制结束进程及其子进程。
        /// run.bat 是 cmd.exe，java.exe 是它的子进程，单独 Kill cmd 会留下孤儿 java，
        /// 所以用 taskkill /F /T 把整棵进程树一起干掉。
        /// </summary>
        private static void KillProcessTree(Process proc)
        {
            if (proc == null) return;
            try
            {
                int pid = proc.Id;
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/F /T /PID " + pid,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    try { p.WaitForExit(3000); } catch { }
                }
            }
            catch { }
        }

        private class ServerRunState
        {
            public volatile bool EulaDetected;
            public volatile bool DoneDetected;
            public volatile int ExitCode;
        }

        private static void Report(Action<ServerStartProgress> onProgress,
                                   ServerStartPhase phase, string message,
                                   int exitCode = 0)
        {
            Console.WriteLine($"[Server] {message}");
            if (onProgress == null) return;
            try
            {
                onProgress(new ServerStartProgress
                {
                    Phase = phase,
                    Message = message,
                    ExitCode = exitCode
                });
            }
            catch { }
        }

        private static bool IsDoneLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;

            int idx = line.IndexOf("Done (", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 &&
                line.IndexOf("s)!", idx + 6, StringComparison.OrdinalIgnoreCase) > 0)
                return true;

            if (line.IndexOf("Done!", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        // ============================================================
        //                       识别 & 组装
        // ============================================================

        private static ProcessStartInfo BuildStartInfo(string serverDir, string javaBaseDir)
        {
            // 1) Forge / NeoForge 1.17+ —— 官方安装器生成的 run.bat
            string bat = Path.Combine(serverDir, "run.bat");
            if (File.Exists(bat) && IsForgeOrNeoForgeRunBat(bat))
                return BuildForgeLikeStartInfo(serverDir, bat, javaBaseDir);

            // 2) Fabric 服务端
            string fabricJar = Path.Combine(serverDir, "fabric-server-launch.jar");
            if (File.Exists(fabricJar))
                return BuildJavaStartInfo(serverDir, fabricJar, javaBaseDir);

            // 3) Quilt 服务端
            string quiltJar = Path.Combine(serverDir, "quilt-server-launch.jar");
            if (File.Exists(quiltJar))
                return BuildJavaStartInfo(serverDir, quiltJar, javaBaseDir);

            // 4) 通用 server.jar
            string serverJar = Path.Combine(serverDir, "server.jar");
            if (File.Exists(serverJar))
                return BuildJavaStartInfo(serverDir, serverJar, javaBaseDir);

            throw new FileNotFoundException(
                "在服务端目录下未找到任何启动入口：\n" +
                "  · run.bat（Forge/NeoForge 1.17+）\n" +
                "  · fabric-server-launch.jar\n" +
                "  · quilt-server-launch.jar\n" +
                "  · server.jar");
        }

        /// <summary>
        /// Forge / NeoForge run.bat 启动：
        /// · 从 libraries/ 的 maven 目录推断出 MC 版本
        /// · 按 MC 版本查 Mojang 清单得到所需 Java 主版本
        /// · 用 JavaLocator 精确挑出 java.exe，注入 PATH / JAVA_HOME
        /// 这样 run.bat 里的裸 `java` 一定命中该版本，不再受系统 PATH 影响。
        /// </summary>
        internal static ProcessStartInfo BuildForgeLikeStartInfo(
            string workDir, string batPath, string javaBaseDir)
        {
            // ---------- 1) 按 MC 版本查该用哪个 Java ----------
            int required = 0;
            string mcVersion = DetectMcVersionFromForgeLibs(workDir);
            if (!string.IsNullOrEmpty(mcVersion))
            {
                Console.WriteLine($"[Server] Forge/NeoForge 服务端对应 MC 版本：{mcVersion}");
                required = GetRequiredJavaByVersionId(mcVersion);
            }

            if (required <= 0)
            {
                // 兜底：离线 / 清单查不到
                required = 17;
                Console.WriteLine($"[Server] 未能从清单确定 Java 版本，回退到 Java {required}");
            }

            string java;
            try
            {
                java = JavaLocator.Find(required, javaBaseDir);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"未找到 Java {required}。请安装后重试，或在设置中指定 Java 基准目录。\n" +
                    ex.Message, ex);
            }

            string javaBin = Path.GetDirectoryName(java);
            string javaHome = string.IsNullOrEmpty(javaBin)
                ? null : Path.GetDirectoryName(javaBin);

            Console.WriteLine($"[Server] run.bat 将使用 Java {required}：{java}");

            // ---------- 2) 构造 cmd 并注入环境变量 ----------
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + batPath + "\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };

            if (!string.IsNullOrEmpty(javaBin))
            {
                string oldPath = psi.EnvironmentVariables.ContainsKey("PATH")
                    ? psi.EnvironmentVariables["PATH"]
                    : (Environment.GetEnvironmentVariable("PATH") ?? "");
                psi.EnvironmentVariables["PATH"] = javaBin + ";" + oldPath;
            }
            if (!string.IsNullOrEmpty(javaHome))
                psi.EnvironmentVariables["JAVA_HOME"] = javaHome;

            return psi;
        }

        // ============================================================
        //     从 Forge / NeoForge 的 maven 目录名推断 MC 版本
        // ============================================================

        internal static string DetectMcVersionFromForgeLibs(string serverDir)
        {
            string libDir = Path.Combine(serverDir, "libraries");
            if (!Directory.Exists(libDir)) return null;

            // Forge / 早期 NeoForge: libraries/net/minecraftforge/forge/<mc>-<forge>/
            //   "1.20.1-47.2.0" → "1.20.1"
            //   "26.3-66.0.9"   → "26.3"
            string forgeDir = Path.Combine(libDir, "net", "minecraftforge", "forge");
            if (Directory.Exists(forgeDir))
            {
                string best = null;
                foreach (var d in SafeGetDirectories(forgeDir))
                {
                    string name = Path.GetFileName(d);
                    int dash = name.IndexOf('-');
                    if (dash <= 0) continue;

                    string mc = name.Substring(0, dash);
                    if (string.IsNullOrEmpty(mc)) continue;
                    if (mc.IndexOf('.') < 0) continue;    // 至少形如 X.Y

                    // 多个目录时取字典序最大的（一般只有一个）
                    if (best == null || string.Compare(mc, best, StringComparison.Ordinal) > 0)
                        best = mc;
                }
                if (best != null) return best;
            }

            // 新版 NeoForge: libraries/net/neoforged/neoforge/<ver>/
            string neoDir = Path.Combine(libDir, "net", "neoforged", "neoforge");
            if (Directory.Exists(neoDir))
            {
                foreach (var d in SafeGetDirectories(neoDir))
                {
                    string name = Path.GetFileName(d);
                    string mc = MapNeoForgeVersionToMc(name);
                    if (!string.IsNullOrEmpty(mc)) return mc;
                }
            }

            return null;
        }

        private static string[] SafeGetDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return new string[0]; }
        }

        /// <summary>NeoForge 版号 → Minecraft 版号</summary>
        internal static string MapNeoForgeVersionToMc(string neoVer)
        {
            if (string.IsNullOrEmpty(neoVer)) return null;

            // 早期 NeoForge：1.20.1-47.1.x（继承 Forge 版号风格）
            if (neoVer.StartsWith("1."))
            {
                int dash = neoVer.IndexOf('-');
                if (dash > 0) return neoVer.Substring(0, dash);
            }

            // 新版 NeoForge：<MC.minor>.<MC.patch>.<build>
            //   20.2.88    → 1.20.2
            //   20.4.237   → 1.20.4
            //   20.6.119   → 1.20.6
            //   21.0.167   → 1.21
            //   21.1.72    → 1.21.1
            //   21.4.123   → 1.21.4
            var parts = neoVer.Split('.');
            if (parts.Length < 2) return null;

            int major, minor;
            if (!int.TryParse(parts[0], out major)) return null;
            if (!int.TryParse(parts[1], out minor)) return null;

            if (major == 20) return "1.20." + minor;
            if (major == 21) return minor == 0 ? "1.21" : "1.21." + minor;
            if (major == 22) return minor == 0 ? "1.22" : "1.22." + minor;

            return null;
        }

        private static ProcessStartInfo BuildJavaStartInfo(string workDir, string jarPath,
                                                           string javaBaseDir)
        {
            int required = GetRequiredJavaFromManifest(jarPath);
            if (required <= 0) required = DetectRequiredJavaFromJar(jarPath);
            if (required <= 0) required = 17;

            Console.WriteLine($"[Server] {Path.GetFileName(jarPath)} 要求 Java {required}");

            string java;
            try { java = JavaLocator.Find(required, javaBaseDir); }
            catch { java = JavaLocator.Find(required + 1, javaBaseDir); }

            return new ProcessStartInfo
            {
                FileName = java,
                Arguments = "-Xmx2G -jar \"" + jarPath + "\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };
        }

        private static bool IsForgeOrNeoForgeRunBat(string batPath)
        {
            if (string.IsNullOrEmpty(batPath) || !File.Exists(batPath)) return false;
            try
            {
                string content = File.ReadAllText(batPath);
                bool a = content.IndexOf("user_jvm_args.txt",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                bool b =
                    content.IndexOf("minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    content.IndexOf("neoforged", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    content.IndexOf("forge-", StringComparison.OrdinalIgnoreCase) >= 0;
                return a && b;
            }
            catch { return false; }
        }

        // ============================================================
        //        通过 Mojang 版本清单识别 Java 需求
        // ============================================================

        private const string MOJANG_MANIFEST_URL =
            "https://piston-meta.mojang.com/mc/game/version_manifest.json";

        private static readonly Dictionary<string, int> _manifestJavaCache =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按版本 ID 直接查 Java 主版本（Forge / NeoForge 分支用）</summary>
        internal static int GetRequiredJavaByVersionId(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) return 0;

            lock (_manifestJavaCache)
            {
                if (_manifestJavaCache.ContainsKey(versionId))
                    return _manifestJavaCache[versionId];
            }

            try
            {
                Console.WriteLine($"[Server] 查询 Mojang 清单：{versionId}");
                string manifestJson = DownloadString(MOJANG_MANIFEST_URL);
                if (string.IsNullOrEmpty(manifestJson)) return 0;

                string versionUrl = FindVersionUrlInManifest(manifestJson, versionId);
                if (string.IsNullOrEmpty(versionUrl)) return 0;

                string versionJson = DownloadString(versionUrl);
                if (string.IsNullOrEmpty(versionJson)) return 0;

                int required = ParseJavaMajorVersion(versionJson);
                if (required > 0)
                {
                    lock (_manifestJavaCache) _manifestJavaCache[versionId] = required;
                    Console.WriteLine($"[Server] Mojang 清单声明需要 Java {required}");
                }
                return required;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] 查询 Mojang 清单出错：{ex.Message}");
                return 0;
            }
        }

        private static int GetRequiredJavaFromManifest(string jarPath)
        {
            string versionId = ReadVersionIdFromJar(jarPath);
            return GetRequiredJavaByVersionId(versionId);
        }

        private static string ReadVersionIdFromJar(string jarPath)
        {
            try
            {
                using (var fs = File.OpenRead(jarPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var vj = zip.GetEntry("version.json");
                    if (vj == null) return null;

                    using (var sr = new StreamReader(vj.Open()))
                    {
                        string json = sr.ReadToEnd();
                        var m = Regex.Match(json, @"""id""\s*:\s*""([^""]+)""");
                        return m.Success ? m.Groups[1].Value : null;
                    }
                }
            }
            catch { return null; }
        }

        private static string FindVersionUrlInManifest(string manifestJson, string versionId)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(manifestJson);
                if (root == null || !root.ContainsKey("versions")) return null;

                var versions = root["versions"] as ArrayList;
                if (versions == null) return null;

                foreach (var item in versions)
                {
                    var dict = item as Dictionary<string, object>;
                    if (dict == null) continue;

                    string id = dict.ContainsKey("id") ? Convert.ToString(dict["id"]) : null;
                    if (string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase))
                        return dict.ContainsKey("url") ? Convert.ToString(dict["url"]) : null;
                }
            }
            catch { }
            return null;
        }

        private static int ParseJavaMajorVersion(string versionJson)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(versionJson);
                if (root == null || !root.ContainsKey("javaVersion")) return 0;

                var jv = root["javaVersion"] as Dictionary<string, object>;
                if (jv == null || !jv.ContainsKey("majorVersion")) return 0;

                int v;
                if (int.TryParse(Convert.ToString(jv["majorVersion"]), out v) && v > 0)
                    return v;
            }
            catch { }
            return 0;
        }

        private static string DownloadString(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "JerryCraftLauncher/1.0";
                req.Timeout = 15000;
                req.AllowAutoRedirect = true;

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] 网络请求失败：{url} - {ex.Message}");
                return null;
            }
        }

        // ============================================================
        //        本地 jar 检测（Mojang 清单失败时的回退）
        // ============================================================

        private static int DetectRequiredJavaFromJar(string jarPath)
        {
            try
            {
                using (var fs = File.OpenRead(jarPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var vj = zip.GetEntry("version.json");
                    if (vj != null)
                    {
                        using (var sr = new StreamReader(vj.Open()))
                        {
                            string json = sr.ReadToEnd();

                            var m = Regex.Match(json,
                                @"""javaVersion""\s*:\s*\{[^}]*""majorVersion""\s*:\s*(\d+)");
                            if (m.Success)
                            {
                                int v = int.Parse(m.Groups[1].Value);
                                if (v > 0) return v;
                            }

                            m = Regex.Match(json, @"""majorVersion""\s*:\s*(\d+)");
                            if (m.Success)
                            {
                                int v = int.Parse(m.Groups[1].Value);
                                if (v > 0) return v;
                            }
                        }
                    }

                    int maxClassMajor = 0;
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Length < 8) continue;
                        if (!entry.FullName.EndsWith(".class",
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (entry.FullName.StartsWith("META-INF",
                                StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            using (var s = entry.Open())
                            {
                                byte[] h = new byte[8];
                                int read = s.Read(h, 0, 8);
                                if (read < 8) continue;
                                if (h[0] != 0xCA || h[1] != 0xFE ||
                                    h[2] != 0xBA || h[3] != 0xBE) continue;

                                int major = (h[6] << 8) | h[7];
                                if (major > maxClassMajor) maxClassMajor = major;
                            }
                        }
                        catch { }
                    }

                    if (maxClassMajor >= 52)
                        return maxClassMajor - 44;
                }
            }
            catch { }
            return 0;
        }
    }
}