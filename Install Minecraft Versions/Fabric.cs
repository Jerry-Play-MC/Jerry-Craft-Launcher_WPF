using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Install_Minecraft_Versions
{
    internal class Fabric
    {
        // ===================== Fabric 官方源 =====================
        private const string FABRIC_META = "https://meta.fabricmc.net";
        private const string FABRIC_MAVEN = "https://maven.fabricmc.net";

        // ===================== BMCLAPI 镜像 =====================
        private const string BMCL_BASE = "https://bmclapi2.bangbang93.com";

        // ===================== 下载参数 =====================
        private const int MAX_CONCURRENCY = 24;
        private const int RETRY_COUNT = 3;
        private const int BUFFER_SIZE = 256 * 1024;
        private const int PROGRESS_REFRESH_MS = 100;
        private const int HTTP_TIMEOUT_MS = 15000;

        // 首次启动验证超时（Fabric 首次要 remap 原版 jar，给 15 分钟）
        private const int FIRST_LAUNCH_TIMEOUT_MS = 15 * 60 * 1000;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly object _consoleLock = new object();
        private static int _lastProgressTick = 0;

        // ===================== 服务端启动参数（可调） =====================
        private const string SERVER_MAX_MEMORY = "4G";
        private const string SERVER_MIN_MEMORY = "4G";

        // 统一 JVM 参数（start.bat 和首次启动验证共用）
        private const string JVM_ARGS =
            "-Djava.net.preferIPv4Stack=true " +
            "-Dsun.net.spi.nameservice.nameservers=223.5.5.5,119.29.29.29 " +
            "-Dsun.net.inetaddr.ttl=0 " +
            "-Dsun.net.client.defaultConnectTimeout=3000 " +
            "-Dsun.net.client.defaultReadTimeout=3000";

        // ===================== 客户端 =====================
        // ===================== 客户端 =====================
        public static void InstallClient(string version, string minecraftDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            Log($"[Fabric] 开始安装客户端 Fabric {version}");

            string loaderVersion = ResolveLoaderVersion(version, loaderParam);
            if (string.IsNullOrEmpty(loaderVersion))
                throw new Exception("无法获取 Fabric Loader 版本");
            Log($"[Fabric] 使用 Loader 版本: {loaderVersion}");

            string versionId = $"fabric-loader-{loaderVersion}-{version}";

            // 1. 获取 profile JSON
            string profileUrl = $"{FABRIC_META}/v2/versions/loader/" +
                                $"{Uri.EscapeDataString(version)}/" +
                                $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
            Log($"[Fabric] 获取 profile: {profileUrl}");
            string profileJson = DownloadString(profileUrl);

            // 2. ★ 合并父版本 JSON（去 inheritsFrom，生成独立版本）
            string mergedJson = MergeParentJson(profileJson, minecraftDir);

            // 3. 保存版本 JSON
            string versionsDir = Path.Combine(minecraftDir, "versions");
            string versionDir = Path.Combine(versionsDir, versionId);
            Directory.CreateDirectory(versionDir);
            string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(versionJsonPath, mergedJson, Encoding.UTF8);
            Log($"[Fabric] 版本 JSON 已保存: {versionJsonPath}");

            // 4. ★ 复制原版 client.jar 到 Fabric 版本目录
            CopyClientJar(versionsDir, version, versionDir, versionId);

            // 5. 解析（已合并的）JSON 收集 libraries
            var root = ParseJson(mergedJson);
            if (root == null || !root.ContainsKey("libraries"))
                throw new Exception("profile JSON 缺少 libraries");

            string librariesDir = Path.Combine(minecraftDir, "libraries");
            var tasks = CollectLibraryTasks(root, librariesDir, "client");
            Log($"[Fabric] Libraries 任务: {tasks.Count}");

            if (tasks.Count > 0)
                ParallelDownload(tasks, MAX_CONCURRENCY);

            Log($"[Fabric] 客户端 Fabric {version} 安装完成");
        }

        // ===================== 服务端 =====================
        public static void InstallServer(string version, string serverDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(serverDir))
                throw new ArgumentException("服务端路径不能为空", "serverDir");

            Log($"[Fabric] 开始安装服务端 Fabric {version}");

            string serverJarPath = Path.Combine(serverDir, "server.jar");
            if (!File.Exists(serverJarPath) || new FileInfo(serverJarPath).Length == 0)
                Log("[Fabric] 警告: 未找到原版 server.jar，请先运行 Vanilla.InstallServer");

            string loaderVersion = ResolveLoaderVersion(version, loaderParam);
            if (string.IsNullOrEmpty(loaderVersion))
                throw new Exception("无法获取 Fabric Loader 版本");
            Log($"[Fabric] 使用 Loader 版本: {loaderVersion}");

            Directory.CreateDirectory(serverDir);
            string librariesDir = Path.Combine(serverDir, "libraries");

            string profileUrl = $"{FABRIC_META}/v2/versions/loader/" +
                                $"{Uri.EscapeDataString(version)}/" +
                                $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
            Log($"[Fabric] 获取 profile: {profileUrl}");
            string profileJson = DownloadString(profileUrl);

            var root = ParseJson(profileJson);
            if (root == null || !root.ContainsKey("libraries"))
                throw new Exception("profile JSON 缺少 libraries");

            var tasks = CollectLibraryTasks(root, librariesDir, "server");

            string installerVersion = GetLatestInstallerVersion();
            string launchJarUrl = $"{FABRIC_META}/v2/versions/loader/" +
                                  $"{Uri.EscapeDataString(version)}/" +
                                  $"{Uri.EscapeDataString(loaderVersion)}/" +
                                  $"{Uri.EscapeDataString(installerVersion)}/server/jar";
            tasks.Add(new DownloadTask
            {
                Url = launchJarUrl,
                Dest = Path.Combine(serverDir, "fabric-server-launch.jar"),
                Name = "fabric-server-launch.jar",
                Type = "server",
                ShowProgress = true
            });

            Log($"[Fabric] 共 {tasks.Count} 个下载任务");

            if (tasks.Count > 0)
                ParallelDownload(tasks, MAX_CONCURRENCY);

            // ★ 生成 start.bat（纯启动参数）
            GenerateStartBat(serverDir);

            // ★ 首次启动验证：让 Fabric 自己下载缺失库、remap 原版 jar，
            //   直到出现 eula 相关日志（说明一切准备就绪），视为安装成功。
            Log("[Fabric] 首次启动验证（Fabric 会自行下载缺失库并 remap 原版 jar，可能需要几分钟）...");
            VerifyFirstLaunch(serverDir);

            Log($"[Fabric] 服务端 Fabric {version} 安装完成");
            Log("[Fabric] 启动脚本已生成: start.bat");
            Log("[Fabric] 首次启动会提示接受 EULA，请按提示操作");
        }

        // ===================== 生成 start.bat（纯启动参数） =====================
        private static void GenerateStartBat(string serverDir)
        {
            try
            {
                string batPath = Path.Combine(serverDir, "start.bat");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("cd /d \"%~dp0\"");
                sb.AppendLine($"java {JVM_ARGS} -Xms{SERVER_MIN_MEMORY} -Xmx{SERVER_MAX_MEMORY} -jar fabric-server-launch.jar nogui");
                sb.AppendLine("pause");

                // 用 GBK 写（中文 Windows 默认），避免乱码
                File.WriteAllText(batPath, sb.ToString(), Encoding.Default);
                Log($"[Fabric] start.bat 已生成: {batPath}");
            }
            catch (Exception ex)
            {
                Log($"[Fabric] 生成 start.bat 失败(不致命): {ex.Message}");
            }
        }

        // ===================== 首次启动验证 =====================
        /// <summary>
        /// 首次启动 Fabric 服务端：
        /// 1. Fabric Launcher 会检查缺失库，联网下载
        /// 2. 从原版 server.jar 解压原版依赖
        /// 3. 把原版 jar remap 成 server-intermediary.jar
        /// 4. 最后才检查 eula.txt
        /// 出现 eula 相关日志，说明前面所有步骤都成功，安装验证通过。
        /// 若 eula.txt 已 eula=true，服务器会继续启动，进程不退出——
        /// 这种情况下 eula.txt / server.properties 存在也视为验证通过。
        /// </summary>
        private static void VerifyFirstLaunch(string serverDir)
        {
            string javaPath = FindJavaForServer();
            if (string.IsNullOrEmpty(javaPath))
                throw new Exception("未找到 Java 运行时，请安装 Java 17+ 并设置 JAVA_HOME");

            Log($"[Fabric] 使用 Java: {javaPath}");

            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = $"{JVM_ARGS} -Xms{SERVER_MIN_MEMORY} -Xmx{SERVER_MAX_MEMORY} " +
                            "-jar fabric-server-launch.jar nogui",
                WorkingDirectory = serverDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            var outputBuilder = new StringBuilder();
            object outLock = new object();
            bool eulaDetected = false;
            bool startedDetected = false;

            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    string line = e.Data;
                    lock (outLock) outputBuilder.AppendLine(line);

                    // 只打印关键行
                    string trimmed = line.Trim();
                    if (trimmed.Contains("下载") || trimmed.Contains("Unpacking") ||
                        trimmed.Contains("Loading Minecraft") || trimmed.Contains("Starting") ||
                        trimmed.Contains("ERROR") || trimmed.Contains("WARN") ||
                        trimmed.Contains("eula") || trimmed.Contains("EULA") ||
                        trimmed.Contains("Done"))
                    {
                        Log("[服务器] " + trimmed);
                    }

                    // 检测 eula 相关日志 → 说明前面都成功了
                    if (trimmed.Contains("You need to agree to the EULA") ||
                        trimmed.Contains("Failed to load eula.txt"))
                    {
                        eulaDetected = true;
                    }

                    // 检测服务器真正启动（eula 已接受时的场景）
                    if (trimmed.Contains("Done (") && trimmed.Contains("For help"))
                    {
                        startedDetected = true;
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    lock (outLock) outputBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 等待进程退出 / 或超时
                bool exited = process.WaitForExit(FIRST_LAUNCH_TIMEOUT_MS);
                if (exited) process.WaitForExit();

                if (!exited)
                {
                    // 进程一直没退出 → 说明服务器真的起来了（eula 已接受）
                    Log("[Fabric] 服务器已启动，发送 stop 命令...");
                    try
                    {
                        process.StandardInput.WriteLine("stop");
                        process.StandardInput.Flush();
                    }
                    catch { }

                    // 再等 30 秒让它正常退出
                    if (!process.WaitForExit(30000))
                    {
                        Log("[Fabric] 服务器未正常退出，强制结束");
                        try { process.Kill(); } catch { }
                        process.WaitForExit(5000);
                    }
                }

                string fullOutput;
                lock (outLock) fullOutput = outputBuilder.ToString();

                // 判断验证是否成功
                if (eulaDetected)
                {
                    Log("[Fabric] ✅ 检测到 EULA 提示，Fabric 库已下载完成，安装验证通过");
                    return;
                }

                if (startedDetected)
                {
                    Log("[Fabric] ✅ 服务器已成功启动，安装验证通过");
                    return;
                }

                // 兜底：看 eula.txt / server.properties 是否生成
                string eulaPath = Path.Combine(serverDir, "eula.txt");
                string propsPath = Path.Combine(serverDir, "server.properties");
                if (File.Exists(eulaPath) || File.Exists(propsPath))
                {
                    Log("[Fabric] ✅ 检测到配置文件已生成，安装验证通过");
                    return;
                }

                // 失败
                throw new Exception(
                    "Fabric 首次启动验证失败，未检测到 EULA 提示或服务器启动。\n" +
                    "输出摘要:\n" + Truncate(fullOutput, 2000));
            }
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "\n... (已截断)";
        }

        // ===================== 查找 Java =====================
        private static string FindJavaForServer()
        {
            // 1. JAVA_HOME
            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                javaHome = javaHome.Trim().Trim('"');
                string exe = Path.Combine(javaHome, "bin", "java.exe");
                if (File.Exists(exe)) return exe;
            }

            // 2. PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string dir in pathEnv.Split(';'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string d = dir.Trim().Trim('"');
                    if (string.IsNullOrEmpty(d)) continue;
                    string exe = Path.Combine(d, "java.exe");
                    if (File.Exists(exe)) return exe;
                }
            }

            // 3. 常见目录
            string[] roots = new string[]
            {
                @"C:\Program Files\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\Microsoft\jdk",
                @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files\Zulu",
                @"C:\Program Files\BellSoft",
                @"D:\Program\Java JDK",
                @"D:\Program\Java",
                @"D:\Java",
                @"E:\Program\Java JDK",
                @"E:\Program\Java",
                @"E:\Java",
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string sub in Directory.GetDirectories(root))
                {
                    string exe = Path.Combine(sub, "bin", "java.exe");
                    if (File.Exists(exe)) return exe;
                }
            }

            return "java";   // 兜底：依赖 PATH
        }

        // ===================== 版本解析 =====================
        private static string ResolveLoaderVersion(string gameVersion, string loaderParam)
        {
            if (!string.IsNullOrEmpty(loaderParam))
            {
                string first = loaderParam.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(first) &&
                    !first.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                    first != "不选择")
                    return first;
            }
            return GetLatestLoaderVersion(gameVersion);
        }

        private static string GetLatestLoaderVersion(string gameVersion)
        {
            try
            {
                string url = $"{FABRIC_META}/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}";
                string json = DownloadString(url);
                if (string.IsNullOrEmpty(json)) return null;

                var arr = new JavaScriptSerializer().Deserialize<ArrayList>(json);
                if (arr == null || arr.Count == 0) return null;

                var first = arr[0] as Dictionary<string, object>;
                if (first != null && first.ContainsKey("loader"))
                {
                    var loader = first["loader"] as Dictionary<string, object>;
                    if (loader != null && loader.ContainsKey("version"))
                        return loader["version"].ToString();
                }
            }
            catch (Exception ex)
            {
                Log($"[Fabric] 获取最新 Loader 版本失败: {ex.Message}");
            }
            return null;
        }

        private static string GetLatestInstallerVersion()
        {
            try
            {
                string json = DownloadString($"{FABRIC_META}/v2/versions/installer");
                if (string.IsNullOrEmpty(json)) return "1.1.2";

                var arr = new JavaScriptSerializer().Deserialize<ArrayList>(json);
                if (arr != null && arr.Count > 0)
                {
                    var first = arr[0] as Dictionary<string, object>;
                    if (first != null && first.ContainsKey("version"))
                        return first["version"].ToString();
                }
            }
            catch { }
            return "1.1.2";
        }

        // ===================== libraries 收集 =====================
        private static List<DownloadTask> CollectLibraryTasks(
            Dictionary<string, object> root, string librariesDir, string type)
        {
            var tasks = new List<DownloadTask>();
            var libraries = root["libraries"] as ArrayList;
            if (libraries == null) return tasks;

            foreach (var libObj in libraries)
            {
                var lib = libObj as Dictionary<string, object>;
                if (lib == null) continue;

                string name = lib.ContainsKey("name") ? lib["name"].ToString() : null;
                if (string.IsNullOrEmpty(name)) continue;

                string baseUrl = lib.ContainsKey("url") ? lib["url"].ToString() : FABRIC_MAVEN;
                if (string.IsNullOrEmpty(baseUrl)) baseUrl = FABRIC_MAVEN;
                if (!baseUrl.EndsWith("/")) baseUrl += "/";

                string relPath = MavenNameToPath(name);
                if (string.IsNullOrEmpty(relPath)) continue;

                tasks.Add(new DownloadTask
                {
                    Url = baseUrl + relPath,
                    Dest = Path.Combine(librariesDir, relPath),
                    Name = relPath,
                    Type = type,
                    ShowProgress = false
                });
            }

            return tasks;
        }

        private static string MavenNameToPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var parts = name.Split(':');
            if (parts.Length < 3) return null;

            string group = parts[0].Replace('.', '/');
            string artifact = parts[1];
            string version = parts[2];
            string classifier = parts.Length >= 4 ? parts[3] : null;
            string ext = ".jar";

            if (!string.IsNullOrEmpty(classifier) && classifier.Contains("@"))
            {
                int at = classifier.IndexOf('@');
                ext = "." + classifier.Substring(at + 1);
                classifier = classifier.Substring(0, at);
            }

            string fileName = $"{artifact}-{version}";
            if (!string.IsNullOrEmpty(classifier)) fileName += "-" + classifier;
            fileName += ext;

            return $"{group}/{artifact}/{version}/{fileName}";
        }

        // ===================== 并行下载 =====================
        private static void ParallelDownload(List<DownloadTask> tasks, int maxConcurrency)
        {
            int failed = 0, skipped = 0;
            int completed = 0;
            int total = tasks.Count;

            int nextIndex = 0;
            object queueLock = new object();
            object statLock = new object();

            Log($"[Fabric] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");
            Interlocked.Exchange(ref _lastProgressTick, 0);

            lock (_consoleLock)
            {
                PrintProgressLine(skipped, failed, completed, total);
            }

            var threads = new List<Thread>();
            for (int i = 0; i < maxConcurrency; i++)
            {
                var worker = new Thread(() =>
                {
                    while (true)
                    {
                        DownloadTask task;
                        lock (queueLock)
                        {
                            if (nextIndex >= tasks.Count) return;
                            task = tasks[nextIndex++];
                        }

                        try
                        {
                            bool wasSkipped = DownloadFile(task.Url, task.Dest, task.ShowProgress);
                            lock (statLock)
                            {
                                if (wasSkipped) skipped++;
                                completed++;
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (statLock) { failed++; completed++; }
                            lock (_consoleLock)
                            {
                                Console.Write("\r" + new string(' ', 140) + "\r");
                                Console.WriteLine($"[Fabric] 下载失败: {task.Name} -> {ex.Message}");
                            }
                        }

                        int now = Environment.TickCount;
                        int last = Volatile.Read(ref _lastProgressTick);
                        bool isLast = false;
                        lock (statLock) isLast = (completed == total);

                        if (isLast || (now - last) >= PROGRESS_REFRESH_MS)
                        {
                            Volatile.Write(ref _lastProgressTick, now);
                            lock (_consoleLock)
                            {
                                PrintProgressLine(skipped, failed, completed, total);
                            }
                        }
                    }
                });
                worker.IsBackground = true;
                worker.Name = "FabricDownloader-" + i;
                worker.Priority = ThreadPriority.Normal;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.WriteLine($"[Fabric] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");
            }

            // 有失败就抛异常，避免"假成功"
            if (failed > 0)
                throw new Exception($"[Fabric] 有 {failed}/{total} 个库下载失败");
        }

        private static void PrintProgressLine(int skipped, int failed, int completed, int total)
        {
            double pct = total == 0 ? 100 : (completed * 100.0 / total);
            Console.Write($"\r[Fabric] 跳过 {skipped} | 失败 {failed} | " +
                          $"总进度 {pct:F1}% ({completed}/{total})  ");
            try { Console.Out.Flush(); } catch { }
        }

        // ===================== URL 候选 =====================
        private static string[] BuildUrlCandidates(string url)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(url)) list.Add(url);

            if (url.StartsWith(FABRIC_META, StringComparison.OrdinalIgnoreCase))
                list.Add(BMCL_BASE + "/fabric-meta" + url.Substring(FABRIC_META.Length));
            else if (url.StartsWith(FABRIC_MAVEN, StringComparison.OrdinalIgnoreCase))
                list.Add(BMCL_BASE + "/maven" + url.Substring(FABRIC_MAVEN.Length));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var u in list)
                if (!string.IsNullOrEmpty(u) && seen.Add(u)) result.Add(u);
            return result.ToArray();
        }

        // ===================== 请求构造 =====================
        private static HttpWebResponse SendRequestIPv4First(string url, int timeoutMs, int readWriteTimeoutMs)
        {
            Uri uri = new Uri(url);

            if (IPAddress.TryParse(uri.Host, out _))
                return SendRaw(url, null, timeoutMs, readWriteTimeoutMs);

            var ipv4List = new List<IPAddress>();
            try
            {
                foreach (var a in Dns.GetHostAddresses(uri.Host))
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                        ipv4List.Add(a);
                }
            }
            catch { }

            if (ipv4List.Count == 0)
            {
                Log($"[Fabric] 未解析到 IPv4，退回原 URL: {url}");
                return SendRaw(url, null, timeoutMs, readWriteTimeoutMs);
            }

            Exception lastEx = null;
            foreach (var ip in ipv4List)
            {
                try
                {
                    string ipUrl = $"{uri.Scheme}://{ip}{uri.PathAndQuery}";
                    return SendRaw(ipUrl, uri.Host, timeoutMs, readWriteTimeoutMs);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }

            throw new Exception(
                $"所有 IPv4 都失败（{ipv4List.Count} 个）: {lastEx?.Message}", lastEx);
        }

        private static HttpWebResponse SendRaw(string url, string hostHeader,
                                               int timeoutMs, int readWriteTimeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.AllowAutoRedirect = true;
            req.KeepAlive = true;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = readWriteTimeoutMs;
            req.AutomaticDecompression = DecompressionMethods.None;

            if (!string.IsNullOrEmpty(hostHeader))
                req.Host = hostHeader;

            return (HttpWebResponse)req.GetResponse();
        }

        // ===================== 底层下载 =====================
        private static bool DownloadFile(string url, string dest, bool showProgress)
        {
            if (File.Exists(dest))
            {
                try
                {
                    if (new FileInfo(dest).Length > 0)
                        return true;
                }
                catch { }
            }

            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string[] urls = BuildUrlCandidates(url);
            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                foreach (string u in urls)
                {
                    try
                    {
                        SingleDownload(u, dest, showProgress);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        Log($"[Fabric] 下载失败({u}): {ex.Message}");
                    }
                }
                if (retry < RETRY_COUNT - 1)
                    Thread.Sleep(500 * (retry + 1));
            }

            throw new Exception($"下载 {dest} 失败: {lastEx?.Message}", lastEx);
        }

        private static void SingleDownload(string url, string dest, bool showProgress)
        {
            using (var resp = SendRequestIPv4First(url, HTTP_TIMEOUT_MS, 60000))
            using (var netStream = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                           FileShare.None, BUFFER_SIZE))
            {
                long total = resp.ContentLength;
                long received = 0;
                byte[] buffer = new byte[BUFFER_SIZE];
                int read;

                bool displayProgress = showProgress && total > 1024 * 1024;

                while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fs.Write(buffer, 0, read);
                    received += read;

                    if (displayProgress)
                    {
                        int pct = total > 0 ? (int)(received * 100 / total) : 0;
                        lock (_consoleLock)
                        {
                            Console.Write($"\r[下载中] {Path.GetFileName(dest)} - {pct}% " +
                                          $"({FormatSize(received)}/{FormatSize(total)})   ");
                            try { Console.Out.Flush(); } catch { }
                        }
                    }
                }

                if (displayProgress)
                {
                    lock (_consoleLock)
                    {
                        Console.Write("\r" + new string(' ', 120) + "\r");
                        try { Console.Out.Flush(); } catch { }
                    }
                }

                if (total > 0 && received != total)
                    throw new Exception($"下载不完整: {received}/{total}");
            }
        }

        private static string DownloadString(string url)
        {
            string[] urls = BuildUrlCandidates(url);
            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                foreach (string u in urls)
                {
                    try
                    {
                        Log($"[Fabric] 请求: {u}");
                        using (var resp = SendRequestIPv4First(u, HTTP_TIMEOUT_MS, HTTP_TIMEOUT_MS))
                        using (var stream = resp.GetResponseStream())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            return reader.ReadToEnd();
                        }
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        Log($"[Fabric] 请求失败({u}): {ex.Message}");
                    }
                }
                if (retry < RETRY_COUNT - 1)
                    Thread.Sleep(500 * (retry + 1));
            }
            throw new Exception($"下载 {url} 失败: {lastEx?.Message}", lastEx);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }

        private static Dictionary<string, object> ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(json);
            }
            catch { return null; }
        }

        private static void Log(string msg)
        {
            lock (_consoleLock)
            {
                Console.Write("\r" + new string(' ', 140) + "\r");
                Console.WriteLine(msg);
            }
        }

        /// <summary>
        /// 把原版 client.jar 复制到 Fabric 版本目录。
        /// Fabric profile 本身不生成 jar，靠 inheritsFrom 从原版目录拿；
        /// 合并后变成独立版本，需要同名 jar 才能被启动器识别。
        /// </summary>
        private static void CopyClientJar(string versionsDir, string originalVersion,
                                           string versionDir, string versionId)
        {
            string originalClientJar = Path.Combine(versionsDir, originalVersion,
                                                     originalVersion + ".jar");
            string fabricVersionJar = Path.Combine(versionDir, versionId + ".jar");

            if (!File.Exists(originalClientJar) || new FileInfo(originalClientJar).Length == 0)
            {
                Log($"[Fabric] 警告: 未找到原版 client.jar({originalClientJar})，请先运行 Vanilla.InstallClient");
                return;
            }

            try
            {
                long srcLen = new FileInfo(originalClientJar).Length;
                if (File.Exists(fabricVersionJar) &&
                    new FileInfo(fabricVersionJar).Length == srcLen)
                {
                    Log($"[Fabric] 版本核心已存在: {fabricVersionJar}");
                    return;
                }

                File.Copy(originalClientJar, fabricVersionJar, true);
                Log($"[Fabric] 已复制原版核心到: {fabricVersionJar}");
            }
            catch (Exception ex)
            {
                Log($"[Fabric] 复制原版核心失败(不致命): {ex.Message}");
            }
        }

        /// <summary>
        /// 把 profile JSON 里 inheritsFrom 指向的父版本内容合并到当前 JSON，
        /// 并删除 inheritsFrom，使 Fabric 版本成为一个独立的版本。
        /// </summary>
        private static string MergeParentJson(string fabricJson, string minecraftDir)
        {
            var fabricRoot = ParseJson(fabricJson);
            if (fabricRoot == null)
            {
                Log("[Fabric] profile JSON 解析失败，跳过合并");
                return fabricJson;
            }

            // 没有 inheritsFrom → 不需要合并
            if (!fabricRoot.ContainsKey("inheritsFrom"))
            {
                Log("[Fabric] profile JSON 无 inheritsFrom，无需合并");
                return fabricJson;
            }

            string parentId = fabricRoot["inheritsFrom"].ToString();
            string parentPath = Path.Combine(minecraftDir, "versions", parentId, parentId + ".json");

            if (!File.Exists(parentPath))
            {
                Log($"[Fabric] 警告: 未找到父版本 JSON({parentPath})，跳过合并");
                return fabricJson;
            }

            var parentRoot = ParseJson(File.ReadAllText(parentPath));
            if (parentRoot == null)
            {
                Log("[Fabric] 父版本 JSON 解析失败，跳过合并");
                return fabricJson;
            }

            // ★ 三元表达式先算出来，避免在内插里写
            string fabricId = fabricRoot.ContainsKey("id") ? fabricRoot["id"].ToString() : "?";
            Log($"[Fabric] 合并父版本 {parentId} 到 {fabricId}");

            // ---- 开始合并 ----
            var merged = new Dictionary<string, object>();

            // 1. 复制父版本所有字段
            foreach (var kv in parentRoot)
                merged[kv.Key] = kv.Value;

            // 2. 用 Fabric 的字段覆盖（libraries / inheritsFrom 除外）
            foreach (var kv in fabricRoot)
            {
                if (kv.Key == "inheritsFrom") continue;
                if (kv.Key == "libraries") continue;
                merged[kv.Key] = kv.Value;
            }

            // 3. 合并 libraries（Fabric 优先，父版本去重补充）
            merged["libraries"] = MergeLibraries(fabricRoot, parentRoot);

            // 4. 合并 arguments（父 + Fabric）
            var mergedArgs = MergeArguments(fabricRoot, parentRoot);
            if (mergedArgs != null)
                merged["arguments"] = mergedArgs;

            // 5. 序列化
            try
            {
                string result = new JavaScriptSerializer().Serialize(merged);
                Log($"[Fabric] 合并完成，JSON 长度: {result.Length}");
                return result;
            }
            catch (Exception ex)
            {
                Log($"[Fabric] 序列化合并结果失败: {ex.Message}，使用原 JSON");
                return fabricJson;
            }
        }

        /// <summary>
        /// 合并 libraries：Fabric 优先，父版本按 name 去重补充。
        /// </summary>
        private static List<object> MergeLibraries(Dictionary<string, object> fabricRoot,
                                                    Dictionary<string, object> parentRoot)
        {
            var result = new List<object>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Fabric 库优先
            if (fabricRoot.ContainsKey("libraries"))
            {
                var libs = fabricRoot["libraries"] as ArrayList;
                if (libs != null)
                {
                    foreach (var lib in libs)
                    {
                        var dict = lib as Dictionary<string, object>;
                        if (dict == null || !dict.ContainsKey("name")) continue;
                        string name = dict["name"].ToString();
                        if (seenNames.Add(name))
                            result.Add(lib);
                    }
                }
            }

            // 父版本库补充
            if (parentRoot.ContainsKey("libraries"))
            {
                var libs = parentRoot["libraries"] as ArrayList;
                if (libs != null)
                {
                    foreach (var lib in libs)
                    {
                        var dict = lib as Dictionary<string, object>;
                        if (dict == null || !dict.ContainsKey("name")) continue;
                        string name = dict["name"].ToString();
                        if (seenNames.Add(name))
                            result.Add(lib);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 合并 arguments：jvm 和 game 都按"父 + Fabric"顺序拼接。
        /// </summary>
        private static Dictionary<string, object> MergeArguments(
            Dictionary<string, object> fabricRoot,
            Dictionary<string, object> parentRoot)
        {
            var parentArgs = parentRoot.ContainsKey("arguments")
                ? parentRoot["arguments"] as Dictionary<string, object> : null;
            var fabricArgs = fabricRoot.ContainsKey("arguments")
                ? fabricRoot["arguments"] as Dictionary<string, object> : null;

            if (parentArgs == null && fabricArgs == null)
                return null;

            var mergedArgs = new Dictionary<string, object>();

            // jvm 参数
            var jvmList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("jvm"))
            {
                var l = parentArgs["jvm"] as ArrayList;
                if (l != null)
                {
                    // ★ 用 foreach 替代 AddRange，绕过 IEnumerable<object> 转换问题
                    foreach (var item in l) jvmList.Add(item);
                }
            }
            if (fabricArgs != null && fabricArgs.ContainsKey("jvm"))
            {
                var l = fabricArgs["jvm"] as ArrayList;
                if (l != null)
                {
                    foreach (var item in l) jvmList.Add(item);
                }
            }
            if (jvmList.Count > 0)
                mergedArgs["jvm"] = jvmList;

            // game 参数
            var gameList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("game"))
            {
                var l = parentArgs["game"] as ArrayList;
                if (l != null)
                {
                    foreach (var item in l) gameList.Add(item);
                }
            }
            if (fabricArgs != null && fabricArgs.ContainsKey("game"))
            {
                var l = fabricArgs["game"] as ArrayList;
                if (l != null)
                {
                    foreach (var item in l) gameList.Add(item);
                }
            }
            if (gameList.Count > 0)
                mergedArgs["game"] = gameList;

            return mergedArgs;
        }

        // ===================== 内部类 =====================
        private class DownloadTask
        {
            public string Url { get; set; }
            public string Dest { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public bool ShowProgress { get; set; }
        }
    }
}