using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Launch_Minecraft
{
    public enum ServerEulaStatus
    {
        NoEulaFile,   // eula.txt 不存在 → 直接启动，让服务端自己生成
        Accepted,     // eula.txt 存在且 eula=true → 直接启动
        NeedAccept    // eula.txt 存在且 eula=false → 需要弹窗
    }

    /// <summary>
    /// 服务端启动器：
    /// · 加载器识别完全基于目录特征，不读任何 *.json，避免误读服务端生成文件
    /// · Java 需求优先走 Mojang 版本清单，失败则本地读 jar / 扫 class
    /// · eula 处理：只在「已经存在 eula=false」或「服务端秒退报 eula 未同意」时动手
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

        /// <summary>
        /// 写 eula=true。文件不存在则创建；存在则原地替换 eula 行。
        /// 只在「用户已同意」时才调用。
        /// </summary>
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
        /// 启动服务端。
        /// 返回：0 = 已启动，-1 = 秒退且疑似 eula 未同意，其它 = 服务端退出码
        /// </summary>
        public static int StartServer(string serverDir, string javaBaseDir = null,
                                      Action<string> onOutput = null)
        {
            var psi = BuildStartInfo(serverDir, javaBaseDir);

            // onOutput 为空时，默认写到 Console，并把日志落盘到 launcher-server.log
            string logPath = Path.Combine(serverDir, "launcher-server.log");
            Action<string> sink = onOutput ?? Console.WriteLine;
            Action<string> write = line =>
            {
                try { File.AppendAllText(logPath, line + Environment.NewLine); }
                catch { }
                sink(line);
            };

            var proc = new Process { StartInfo = psi };
            bool eulaDetected = false;

            DataReceivedEventHandler onData = (s, e) =>
            {
                if (e.Data == null) return;
                write(e.Data);
                if (e.Data.IndexOf("agree to the EULA",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    eulaDetected = true;
            };

            proc.OutputDataReceived += onData;
            proc.ErrorDataReceived += onData;
            proc.EnableRaisingEvents = true;

            Console.WriteLine($"[Server] 启动: {Path.GetFileName(psi.FileName)} {psi.Arguments}");

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (_running) _running.Add(proc);

            proc.Exited += (s, e) =>
            {
                lock (_running) _running.Remove(proc);
                Console.WriteLine($"[Server] 服务端已退出，退出码 {proc.ExitCode}");
            };

            // 8 秒内秒退 → 视为启动失败
            if (proc.WaitForExit(8000))
            {
                lock (_running) _running.Remove(proc);
                if (eulaDetected) return -1;
                return proc.ExitCode;
            }

            Console.WriteLine($"[Server] 服务端已启动，PID = {proc.Id}");
            Console.WriteLine($"[Server] 日志文件：{logPath}");
            return 0;
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

            // 4) 通用 server.jar（原版 / 老版本 Forge / 部分整合包）
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

            // 把用户指定的 Java 注入 PATH/JAVA_HOME，脚本里的 "java" 会优先用它
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
            // 优先走 Mojang 清单识别 Java 需求
            int required = GetRequiredJavaFromManifest(jarPath);

            // 清单拿不到 → 回退到本地 jar 检测
            if (required <= 0)
                required = DetectRequiredJavaFromJar(jarPath);

            // 还是拿不到 → 兜底 17
            if (required <= 0)
                required = 17;

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

        // 进程内缓存：版本号 → Java 主版本
        private static readonly Dictionary<string, int> _manifestJavaCache =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 从 Mojang 版本清单获取该 jar 对应的 Java 主版本。
        /// 拿不到返回 0。
        /// </summary>
        private static int GetRequiredJavaFromManifest(string jarPath)
        {
            // 1) 先拿到 jar 里声明的版本号（version.json 的 id 字段）
            string versionId = ReadVersionIdFromJar(jarPath);
            if (string.IsNullOrEmpty(versionId))
            {
                Console.WriteLine("[Server] 无法从 jar 读取版本号，跳过 Mojang 清单");
                return 0;
            }

            // 2) 查缓存
            lock (_manifestJavaCache)
            {
                if (_manifestJavaCache.ContainsKey(versionId))
                    return _manifestJavaCache[versionId];
            }

            try
            {
                Console.WriteLine($"[Server] 查询 Mojang 清单：{versionId}");

                // 3) 下载 version_manifest.json
                string manifestJson = DownloadString(MOJANG_MANIFEST_URL);
                if (string.IsNullOrEmpty(manifestJson))
                {
                    Console.WriteLine("[Server] 下载版本清单失败");
                    return 0;
                }

                // 4) 在清单里找该版本，拿到它的 url
                string versionUrl = FindVersionUrlInManifest(manifestJson, versionId);
                if (string.IsNullOrEmpty(versionUrl))
                {
                    Console.WriteLine($"[Server] 清单中未找到版本 {versionId}");
                    return 0;
                }

                // 5) 下载版本详情 JSON
                string versionJson = DownloadString(versionUrl);
                if (string.IsNullOrEmpty(versionJson))
                {
                    Console.WriteLine("[Server] 下载版本 JSON 失败");
                    return 0;
                }

                // 6) 读 javaVersion.majorVersion
                int required = ParseJavaMajorVersion(versionJson);
                if (required > 0)
                {
                    lock (_manifestJavaCache)
                        _manifestJavaCache[versionId] = required;

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

        /// <summary>从 jar 里读取 version.json 的 id 字段</summary>
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

        /// <summary>在 version_manifest.json 里找到对应版本的 url 字段</summary>
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
                    {
                        return dict.ContainsKey("url")
                            ? Convert.ToString(dict["url"])
                            : null;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>从版本 JSON 里读 javaVersion.majorVersion</summary>
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

        /// <summary>
        /// 从 server.jar 本地推断 Java 主版本：
        ///   1) 先看 version.json 的 javaVersion.majorVersion
        ///   2) 扫所有 class 的 major version 换算
        ///   3) 都拿不到返回 0
        /// </summary>
        private static int DetectRequiredJavaFromJar(string jarPath)
        {
            try
            {
                using (var fs = File.OpenRead(jarPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    // ---------- 1) version.json 的 javaVersion ----------
                    var vj = zip.GetEntry("version.json");
                    if (vj != null)
                    {
                        using (var sr = new StreamReader(vj.Open()))
                        {
                            string json = sr.ReadToEnd();

                            // 优先匹配 javaVersion 对象里的 majorVersion
                            var m = Regex.Match(json,
                                @"""javaVersion""\s*:\s*\{[^}]*""majorVersion""\s*:\s*(\d+)");
                            if (m.Success)
                            {
                                int v = int.Parse(m.Groups[1].Value);
                                if (v > 0) return v;
                            }

                            // 退而求其次：任意位置的 "majorVersion": N
                            m = Regex.Match(json, @"""majorVersion""\s*:\s*(\d+)");
                            if (m.Success)
                            {
                                int v = int.Parse(m.Groups[1].Value);
                                if (v > 0) return v;
                            }
                        }
                    }

                    // ---------- 2) 扫 class 文件的 major version ----------
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

                    // class major → Java 主版本（>= Java 8 时 major - 44 = Java 版本）
                    if (maxClassMajor >= 52)
                        return maxClassMajor - 44;
                }
            }
            catch { }
            return 0;
        }
    }
}