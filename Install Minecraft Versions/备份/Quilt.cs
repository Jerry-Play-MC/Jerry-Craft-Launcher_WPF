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
    internal class Quilt
    {
        // ===================== Quilt 官方源 =====================
        private const string QUILT_META = "https://meta.quiltmc.org/v3";
        private const string QUILT_MAVEN = "https://maven.quiltmc.org/repository/release/";

        // Fabric Maven（Quilt 也依赖部分 Fabric 库）
        private const string FABRIC_MAVEN = "https://maven.fabricmc.net/";

        // ===================== BMCLAPI 镜像 =====================
        private const string BMCL_BASE = "https://bmclapi2.bangbang93.com";

        // ===================== 下载参数 =====================
        private const int MAX_CONCURRENCY = 24;
        private const int RETRY_COUNT = 3;
        private const int BUFFER_SIZE = 256 * 1024;
        private const int PROGRESS_REFRESH_MS = 100;
        private const int HTTP_TIMEOUT_MS = 15000;

        private const int FIRST_LAUNCH_TIMEOUT_MS = 15 * 60 * 1000;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly object _consoleLock = new object();
        private static int _lastProgressTick = 0;

        // ===================== 服务端启动参数 =====================
        private const string SERVER_MAX_MEMORY = "4G";
        private const string SERVER_MIN_MEMORY = "4G";

        private const string JVM_ARGS =
            "-Djava.net.preferIPv4Stack=true " +
            "-Dsun.net.spi.nameservice.nameservers=223.5.5.5,119.29.29.29 " +
            "-Dsun.net.inetaddr.ttl=0 " +
            "-Dsun.net.client.defaultConnectTimeout=3000 " +
            "-Dsun.net.client.defaultReadTimeout=3000";

        // ===================== 客户端 =====================
        public static void InstallClient(string version, string minecraftDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            Log($"[Quilt] 开始安装客户端 Quilt {version}");

            string loaderVersion = ResolveLoaderVersion(version, loaderParam);
            if (string.IsNullOrEmpty(loaderVersion))
                throw new Exception("无法获取 Quilt Loader 版本");
            Log($"[Quilt] 使用 Loader 版本: {loaderVersion}");

            string versionId = $"quilt-loader-{loaderVersion}-{version}";

            string profileUrl = $"{QUILT_META}/versions/loader/" +
                                $"{Uri.EscapeDataString(version)}/" +
                                $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
            Log($"[Quilt] 获取 profile: {profileUrl}");
            string profileJson = DownloadString(profileUrl);

            string mergedJson = MergeParentJson(profileJson, minecraftDir);

            string versionsDir = Path.Combine(minecraftDir, "versions");
            string versionDir = Path.Combine(versionsDir, versionId);
            Directory.CreateDirectory(versionDir);
            string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(versionJsonPath, mergedJson, Encoding.UTF8);
            Log($"[Quilt] 版本 JSON 已保存: {versionJsonPath}");

            CopyClientJar(versionsDir, version, versionDir, versionId);

            var root = ParseJson(mergedJson);
            if (root == null || !root.ContainsKey("libraries"))
                throw new Exception("profile JSON 缺少 libraries");

            string librariesDir = Path.Combine(minecraftDir, "libraries");
            var tasks = CollectLibraryTasks(root, librariesDir, "client");
            Log($"[Quilt] Libraries 任务: {tasks.Count}");

            if (tasks.Count > 0)
                ParallelDownload(tasks, MAX_CONCURRENCY);

            VerifyQuiltLoaderJar(librariesDir, loaderVersion);

            Log($"[Quilt] 客户端 Quilt {version} 安装完成");
        }

        // ===================== 服务端 =====================
        public static void InstallServer(string version, string serverDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(serverDir))
                throw new ArgumentException("服务端路径不能为空", "serverDir");

            Log($"[Quilt] 开始安装服务端 Quilt {version}");

            string serverJarPath = Path.Combine(serverDir, "server.jar");
            if (!File.Exists(serverJarPath) || new FileInfo(serverJarPath).Length == 0)
                Log("[Quilt] 警告: 未找到原版 server.jar，请先运行 Vanilla.InstallServer");

            string loaderVersion = ResolveLoaderVersion(version, loaderParam);
            if (string.IsNullOrEmpty(loaderVersion))
                throw new Exception("无法获取 Quilt Loader 版本");
            Log($"[Quilt] 使用 Loader 版本: {loaderVersion}");

            Directory.CreateDirectory(serverDir);
            string librariesDir = Path.Combine(serverDir, "libraries");

            string profileUrl = $"{QUILT_META}/versions/loader/" +
                                $"{Uri.EscapeDataString(version)}/" +
                                $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
            Log($"[Quilt] 获取 profile: {profileUrl}");
            string profileJson = DownloadString(profileUrl);

            var root = ParseJson(profileJson);
            if (root == null || !root.ContainsKey("libraries"))
                throw new Exception("profile JSON 缺少 libraries");

            var tasks = CollectLibraryTasks(root, librariesDir, "server");

            string installerVersion = GetLatestInstallerVersion();
            string launchJarUrl = $"{QUILT_META}/versions/loader/" +
                                  $"{Uri.EscapeDataString(version)}/" +
                                  $"{Uri.EscapeDataString(loaderVersion)}/" +
                                  $"{Uri.EscapeDataString(installerVersion)}/server/jar";
            tasks.Add(new DownloadTask
            {
                Url = launchJarUrl,
                Dest = Path.Combine(serverDir, "quilt-server-launch.jar"),
                Name = "quilt-server-launch.jar",
                Type = "server",
                ShowProgress = true
            });

            Log($"[Quilt] 共 {tasks.Count} 个下载任务");

            if (tasks.Count > 0)
                ParallelDownload(tasks, MAX_CONCURRENCY);

            GenerateStartBat(serverDir);

            Log("[Quilt] 首次启动验证（Quilt 会自行下载缺失库并 remap 原版 jar，可能需要几分钟）...");
            VerifyFirstLaunch(serverDir);

            Log($"[Quilt] 服务端 Quilt {version} 安装完成");
            Log("[Quilt] 启动脚本已生成: start.bat");
            Log("[Quilt] 首次启动会提示接受 EULA，请按提示操作");
        }

        // ===================== 验证 quilt-loader jar =====================
        private static void VerifyQuiltLoaderJar(string librariesDir, string loaderVersion)
        {
            string jarPath = Path.Combine(librariesDir,
                "org", "quiltmc", "quilt-loader", loaderVersion,
                $"quilt-loader-{loaderVersion}.jar");

            if (File.Exists(jarPath) && new FileInfo(jarPath).Length > 0)
            {
                Log($"[Quilt] ✅ quilt-loader jar 已就绪: {jarPath}");
                return;
            }

            throw new Exception(
                $"Quilt 安装失败：缺少关键库 quilt-loader-{loaderVersion}.jar\n" +
                $"期望路径: {jarPath}\n" +
                $"请检查网络，或手动下载：\n" +
                $"  https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-loader/{loaderVersion}/quilt-loader-{loaderVersion}.jar");
        }

        // ===================== 生成 start.bat =====================
        private static void GenerateStartBat(string serverDir)
        {
            try
            {
                string batPath = Path.Combine(serverDir, "start.bat");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("cd /d \"%~dp0\"");
                sb.AppendLine($"java {JVM_ARGS} -Xms{SERVER_MIN_MEMORY} -Xmx{SERVER_MAX_MEMORY} -jar quilt-server-launch.jar nogui");
                sb.AppendLine("pause");

                File.WriteAllText(batPath, sb.ToString(), Encoding.Default);
                Log($"[Quilt] start.bat 已生成: {batPath}");
            }
            catch (Exception ex)
            {
                Log($"[Quilt] 生成 start.bat 失败(不致命): {ex.Message}");
            }
        }

        // ===================== 首次启动验证 =====================
        private static void VerifyFirstLaunch(string serverDir)
        {
            string javaPath = FindJavaForServer();
            if (string.IsNullOrEmpty(javaPath))
                throw new Exception("未找到 Java 运行时，请安装 Java 17+ 并设置 JAVA_HOME");

            Log($"[Quilt] 使用 Java: {javaPath}");

            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = $"{JVM_ARGS} -Xms{SERVER_MIN_MEMORY} -Xmx{SERVER_MAX_MEMORY} " +
                            "-jar quilt-server-launch.jar nogui",
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

                    string trimmed = line.Trim();
                    if (trimmed.Contains("下载") || trimmed.Contains("Unpacking") ||
                        trimmed.Contains("Loading Minecraft") || trimmed.Contains("Starting") ||
                        trimmed.Contains("ERROR") || trimmed.Contains("WARN") ||
                        trimmed.Contains("eula") || trimmed.Contains("EULA") ||
                        trimmed.Contains("Done"))
                    {
                        Log("[服务器] " + trimmed);
                    }

                    if (trimmed.Contains("You need to agree to the EULA") ||
                        trimmed.Contains("Failed to load eula.txt"))
                    {
                        eulaDetected = true;
                    }

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

                bool exited = process.WaitForExit(FIRST_LAUNCH_TIMEOUT_MS);
                if (exited) process.WaitForExit();

                if (!exited)
                {
                    Log("[Quilt] 服务器已启动，发送 stop 命令...");
                    try
                    {
                        process.StandardInput.WriteLine("stop");
                        process.StandardInput.Flush();
                    }
                    catch { }

                    if (!process.WaitForExit(30000))
                    {
                        Log("[Quilt] 服务器未正常退出，强制结束");
                        try { process.Kill(); } catch { }
                        process.WaitForExit(5000);
                    }
                }

                string fullOutput;
                lock (outLock) fullOutput = outputBuilder.ToString();

                if (eulaDetected)
                {
                    Log("[Quilt] ✅ 检测到 EULA 提示，Quilt 库已下载完成，安装验证通过");
                    return;
                }

                if (startedDetected)
                {
                    Log("[Quilt] ✅ 服务器已成功启动，安装验证通过");
                    return;
                }

                string eulaPath = Path.Combine(serverDir, "eula.txt");
                string propsPath = Path.Combine(serverDir, "server.properties");
                if (File.Exists(eulaPath) || File.Exists(propsPath))
                {
                    Log("[Quilt] ✅ 检测到配置文件已生成，安装验证通过");
                    return;
                }

                throw new Exception(
                    "Quilt 首次启动验证失败，未检测到 EULA 提示或服务器启动。\n" +
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
            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                javaHome = javaHome.Trim().Trim('"');
                string exe = Path.Combine(javaHome, "bin", "java.exe");
                if (File.Exists(exe)) return exe;
            }

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

            return "java";
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
                string url = $"{QUILT_META}/versions/loader/{Uri.EscapeDataString(gameVersion)}";
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
                Log($"[Quilt] 获取最新 Loader 版本失败: {ex.Message}");
            }
            return null;
        }

        private static string GetLatestInstallerVersion()
        {
            try
            {
                string json = DownloadString($"{QUILT_META}/versions/installer");
                if (string.IsNullOrEmpty(json)) return "0.9.2";

                var arr = new JavaScriptSerializer().Deserialize<ArrayList>(json);
                if (arr != null && arr.Count > 0)
                {
                    var first = arr[0] as Dictionary<string, object>;
                    if (first != null && first.ContainsKey("version"))
                        return first["version"].ToString();
                }
            }
            catch { }
            return "0.9.2";
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

                string relPath = MavenNameToPath(name);
                if (string.IsNullOrEmpty(relPath)) continue;

                string url = null;

                // 优先：downloads.artifact.url
                if (lib.ContainsKey("downloads"))
                {
                    var downloads = lib["downloads"] as Dictionary<string, object>;
                    if (downloads != null && downloads.ContainsKey("artifact"))
                    {
                        var artifact = downloads["artifact"] as Dictionary<string, object>;
                        if (artifact != null && artifact.ContainsKey("url"))
                            url = artifact["url"].ToString();
                    }
                }

                // 其次：url 字段
                if (string.IsNullOrEmpty(url) && lib.ContainsKey("url"))
                {
                    string baseUrl = lib["url"].ToString();
                    if (!string.IsNullOrEmpty(baseUrl))
                    {
                        if (!baseUrl.EndsWith("/")) baseUrl += "/";
                        url = baseUrl + relPath;
                    }
                }

                // 兜底：按 group 猜
                if (string.IsNullOrEmpty(url))
                {
                    url = GuessMavenUrl(name) + relPath;
                    Log($"[Quilt] 库无 url 字段，猜测源: {url}");
                }

                tasks.Add(new DownloadTask
                {
                    Url = url,
                    Dest = Path.Combine(librariesDir, relPath),
                    Name = relPath,
                    Type = type,
                    ShowProgress = false
                });
            }

            return tasks;
        }

        private static string GuessMavenUrl(string name)
        {
            if (string.IsNullOrEmpty(name)) return QUILT_MAVEN;

            string group = name.Split(':')[0];

            if (group.StartsWith("org.ow2.asm") ||
                group.StartsWith("net.fabricmc") ||
                group.StartsWith("org.spongepowered"))
            {
                return FABRIC_MAVEN;
            }

            return QUILT_MAVEN;
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
            var failures = new List<string>();

            Log($"[Quilt] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");
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
                            lock (statLock)
                            {
                                failed++;
                                completed++;
                                if (failures.Count < 50)
                                    failures.Add($"{task.Name} <- {ex.Message}");
                            }
                            lock (_consoleLock)
                            {
                                Console.Write("\r" + new string(' ', 140) + "\r");
                                Console.WriteLine($"[Quilt] 下载失败: {task.Name} -> {ex.Message}");
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
                worker.Name = "QuiltDownloader-" + i;
                worker.Priority = ThreadPriority.Normal;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.WriteLine($"[Quilt] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");

                if (failed > 0)
                {
                    Console.WriteLine($"[Quilt] 失败清单（前 {Math.Min(failures.Count, 20)} 条）:");
                    int n = Math.Min(failures.Count, 20);
                    for (int i = 0; i < n; i++)
                        Console.WriteLine($"  - {failures[i]}");
                }
            }

            if (failed > 0)
                throw new Exception($"[Quilt] 有 {failed}/{total} 个库下载失败");
        }

        private static void PrintProgressLine(int skipped, int failed, int completed, int total)
        {
            double pct = total == 0 ? 100 : (completed * 100.0 / total);
            Console.Write($"\r[Quilt] 跳过 {skipped} | 失败 {failed} | " +
                          $"总进度 {pct:F1}% ({completed}/{total})  ");
            try { Console.Out.Flush(); } catch { }
        }

        // ===================== URL 候选 =====================
        private static string[] BuildUrlCandidates(string url)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(url)) list.Add(url);

            if (url.StartsWith(QUILT_META, StringComparison.OrdinalIgnoreCase))
                list.Add(BMCL_BASE + "/quilt-meta" + url.Substring(QUILT_META.Length));
            else if (url.StartsWith(QUILT_MAVEN, StringComparison.OrdinalIgnoreCase))
                list.Add(BMCL_BASE + "/maven" + url.Substring(QUILT_MAVEN.Length));
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
                Log($"[Quilt] 未解析到 IPv4，退回原 URL: {url}");
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
            req.KeepAlive = false;    // ★ 关闭连接复用，避免僵尸连接卡顿
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = readWriteTimeoutMs;
            req.AutomaticDecompression = DecompressionMethods.None;

            if (!string.IsNullOrEmpty(hostHeader))
                req.Host = hostHeader;

            return (HttpWebResponse)req.GetResponse();
        }

        // ===================== 底层下载（.tmp 原子替换） =====================
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

            string tmpPath = dest + ".tmp";
            string[] urls = BuildUrlCandidates(url);
            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                foreach (string u in urls)
                {
                    try
                    {
                        // 下载到 .tmp
                        SingleDownload(u, tmpPath, showProgress);

                        // 成功后原子替换
                        if (File.Exists(dest))
                        {
                            try { File.Delete(dest); } catch { }
                        }
                        File.Move(tmpPath, dest);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        // ★ 失败时清理 .tmp，避免残破文件污染
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        Log($"[Quilt] 下载失败({u}): {ex.Message}");
                    }
                }
                if (retry < RETRY_COUNT - 1)
                    Thread.Sleep(500 * (retry + 1));
            }

            throw new Exception($"下载 {dest} 失败: {lastEx?.Message}", lastEx);
        }

        private static void SingleDownload(string url, string dest, bool showProgress)
        {
            // ★ 读写超时也设为 15 秒（原 60 秒）
            using (var resp = SendRequestIPv4First(url, HTTP_TIMEOUT_MS, HTTP_TIMEOUT_MS))
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
                        Log($"[Quilt] 请求: {u}");
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
                        Log($"[Quilt] 请求失败({u}): {ex.Message}");
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

        // ===================== 复制原版 jar =====================
        private static void CopyClientJar(string versionsDir, string originalVersion,
                                           string versionDir, string versionId)
        {
            string originalClientJar = Path.Combine(versionsDir, originalVersion,
                                                     originalVersion + ".jar");
            string quiltVersionJar = Path.Combine(versionDir, versionId + ".jar");

            if (!File.Exists(originalClientJar) || new FileInfo(originalClientJar).Length == 0)
            {
                Log($"[Quilt] 警告: 未找到原版 client.jar({originalClientJar})，请先运行 Vanilla.InstallClient");
                return;
            }

            try
            {
                long srcLen = new FileInfo(originalClientJar).Length;
                if (File.Exists(quiltVersionJar) &&
                    new FileInfo(quiltVersionJar).Length == srcLen)
                {
                    Log($"[Quilt] 版本核心已存在: {quiltVersionJar}");
                    return;
                }

                File.Copy(originalClientJar, quiltVersionJar, true);
                Log($"[Quilt] 已复制原版核心到: {quiltVersionJar}");
            }
            catch (Exception ex)
            {
                Log($"[Quilt] 复制原版核心失败(不致命): {ex.Message}");
            }
        }

        // ===================== 合并父版本 JSON =====================
        private static string MergeParentJson(string quiltJson, string minecraftDir)
        {
            var quiltRoot = ParseJson(quiltJson);
            if (quiltRoot == null)
            {
                Log("[Quilt] profile JSON 解析失败，跳过合并");
                return quiltJson;
            }

            if (!quiltRoot.ContainsKey("inheritsFrom"))
            {
                Log("[Quilt] profile JSON 无 inheritsFrom，无需合并");
                return quiltJson;
            }

            string parentId = quiltRoot["inheritsFrom"].ToString();
            string parentPath = Path.Combine(minecraftDir, "versions", parentId, parentId + ".json");

            if (!File.Exists(parentPath))
            {
                Log($"[Quilt] 警告: 未找到父版本 JSON({parentPath})，跳过合并");
                return quiltJson;
            }

            var parentRoot = ParseJson(File.ReadAllText(parentPath));
            if (parentRoot == null)
            {
                Log("[Quilt] 父版本 JSON 解析失败，跳过合并");
                return quiltJson;
            }

            string quiltId = quiltRoot.ContainsKey("id") ? quiltRoot["id"].ToString() : "?";
            Log($"[Quilt] 合并父版本 {parentId} 到 {quiltId}");

            var merged = new Dictionary<string, object>();

            foreach (var kv in parentRoot)
                merged[kv.Key] = kv.Value;

            foreach (var kv in quiltRoot)
            {
                if (kv.Key == "inheritsFrom") continue;
                if (kv.Key == "libraries") continue;
                merged[kv.Key] = kv.Value;
            }

            merged["libraries"] = MergeLibraries(quiltRoot, parentRoot);

            var mergedArgs = MergeArguments(quiltRoot, parentRoot);
            if (mergedArgs != null)
                merged["arguments"] = mergedArgs;

            try
            {
                string result = new JavaScriptSerializer().Serialize(merged);
                Log($"[Quilt] 合并完成，JSON 长度: {result.Length}");
                return result;
            }
            catch (Exception ex)
            {
                Log($"[Quilt] 序列化合并结果失败: {ex.Message}，使用原 JSON");
                return quiltJson;
            }
        }

        private static List<object> MergeLibraries(Dictionary<string, object> quiltRoot,
                                                    Dictionary<string, object> parentRoot)
        {
            var result = new List<object>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (quiltRoot.ContainsKey("libraries"))
            {
                var libs = quiltRoot["libraries"] as ArrayList;
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

        private static Dictionary<string, object> MergeArguments(
            Dictionary<string, object> quiltRoot,
            Dictionary<string, object> parentRoot)
        {
            var parentArgs = parentRoot.ContainsKey("arguments")
                ? parentRoot["arguments"] as Dictionary<string, object> : null;
            var quiltArgs = quiltRoot.ContainsKey("arguments")
                ? quiltRoot["arguments"] as Dictionary<string, object> : null;

            if (parentArgs == null && quiltArgs == null)
                return null;

            var mergedArgs = new Dictionary<string, object>();

            var jvmList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("jvm"))
            {
                var l = parentArgs["jvm"] as ArrayList;
                if (l != null)
                    foreach (var item in l) jvmList.Add(item);
            }
            if (quiltArgs != null && quiltArgs.ContainsKey("jvm"))
            {
                var l = quiltArgs["jvm"] as ArrayList;
                if (l != null)
                    foreach (var item in l) jvmList.Add(item);
            }
            if (jvmList.Count > 0)
                mergedArgs["jvm"] = jvmList;

            var gameList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("game"))
            {
                var l = parentArgs["game"] as ArrayList;
                if (l != null)
                    foreach (var item in l) gameList.Add(item);
            }
            if (quiltArgs != null && quiltArgs.ContainsKey("game"))
            {
                var l = quiltArgs["game"] as ArrayList;
                if (l != null)
                    foreach (var item in l) gameList.Add(item);
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