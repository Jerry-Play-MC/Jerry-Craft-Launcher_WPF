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

            // ★ 不用 using：句柄必须活得比本方法长，Exited 事件可能在几分钟后才触发
            var doneHandle = new ManualResetEvent(false);
            var exitHandle = new ManualResetEvent(false);

            // 用 ref-like 包装保证多线程可见性
            var state = new ServerRunState();

            DataReceivedEventHandler onData = (s, e) =>
            {
                if (e.Data == null) return;

                // 落盘
                try { File.AppendAllText(logPath, e.Data + Environment.NewLine); }
                catch { }

                // ★ 控制台实时输出（之前漏了这句，导致日志被吃）
                try { Console.WriteLine(e.Data); } catch { }

                // eula 未同意
                if (!state.EulaDetected &&
                    e.Data.IndexOf("agree to the EULA",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    state.EulaDetected = true;
                }

                // 服务端启动完成：Done (X.XXXs)! For help, type "help"
                if (!state.DoneDetected && IsDoneLine(e.Data))
                {
                    state.DoneDetected = true;
                    try { doneHandle.Set(); }
                    catch (ObjectDisposedException) { }
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

                // 只有已经进入 Running 后才需要通知 UI 服务端停了
                if (state.DoneDetected)
                {
                    Report(onProgress, ServerStartPhase.Stopped,
                        $"已停止（退出码 {code}）", code);
                }

                try { exitHandle.Set(); }
                catch (ObjectDisposedException) { }

                // 后台线程不再需要这两个句柄，此处释放
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
                try { doneHandle.Close(); } catch { }
                try { exitHandle.Close(); } catch { }
                return ServerStartResult.Failed;
            }

            lock (_running) _running.Add(proc);
            Console.WriteLine($"[Server] 已启动 PID = {proc.Id}，日志：{logPath}");

            // 等待 Done 出现或进程退出，最长 180 秒
            int idx;
            try
            {
                idx = WaitHandle.WaitAny(new[] { doneHandle, exitHandle }, 180000);
            }
            catch (ObjectDisposedException)
            {
                // 极罕见：Exited 先到，句柄已释放
                idx = 1;
            }

            // 进程已退出
            if (idx == 1 || proc.HasExited)
            {
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

            // Done 出现（或超时），视为已进入运行状态
            Report(onProgress, ServerStartPhase.Running, "正在运行...");
            return ServerStartResult.Success;
        }

        /// <summary>进程内共享状态（跨事件线程安全）</summary>
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

        /// <summary>判断某行是否为服务端启动完成的 Done 行</summary>
        /// <summary>判断某行是否为服务端启动完成的 Done 行</summary>
        private static bool IsDoneLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;

            // 标准：Done (X.XXXs)! For help, type "help"
            int idx = line.IndexOf("Done (", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 &&
                line.IndexOf("s)!", idx + 6, StringComparison.OrdinalIgnoreCase) > 0)
                return true;

            // 兜底：任意位置出现 "Done!" 也算
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
                return BuildBatStartInfo(serverDir, bat, javaBaseDir);

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

        private static ProcessStartInfo BuildBatStartInfo(string workDir, string batPath,
                                                          string javaBaseDir)
        {
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

            string bin = ResolveJavaBinDir(javaBaseDir);
            if (!string.IsNullOrEmpty(bin))
            {
                string oldPath = psi.EnvironmentVariables.ContainsKey("PATH")
                    ? psi.EnvironmentVariables["PATH"]
                    : (Environment.GetEnvironmentVariable("PATH") ?? "");
                psi.EnvironmentVariables["PATH"] = bin + ";" + oldPath;

                string home = Path.GetDirectoryName(bin);
                if (!string.IsNullOrEmpty(home))
                    psi.EnvironmentVariables["JAVA_HOME"] = home;
            }

            return psi;
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

        private static string ResolveJavaBinDir(string javaBaseDir)
        {
            if (string.IsNullOrEmpty(javaBaseDir) || !Directory.Exists(javaBaseDir))
                return null;

            if (File.Exists(Path.Combine(javaBaseDir, "bin", "java.exe")))
                return Path.Combine(javaBaseDir, "bin");

            try
            {
                foreach (var sub in Directory.GetDirectories(javaBaseDir))
                    if (File.Exists(Path.Combine(sub, "bin", "java.exe")))
                        return Path.Combine(sub, "bin");
            }
            catch { }
            return null;
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

        private static int GetRequiredJavaFromManifest(string jarPath)
        {
            string versionId = ReadVersionIdFromJar(jarPath);
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