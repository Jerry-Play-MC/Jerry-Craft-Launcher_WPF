using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    internal class InstallFabric
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

        private const int FIRST_LAUNCH_TIMEOUT_MS = 15 * 60 * 1000;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly object _consoleLock = new object();
        private static int _lastProgressTick = 0;

        private const string SERVER_MAX_MEMORY = "4G";
        private const string SERVER_MIN_MEMORY = "4G";

        private const string JVM_ARGS =
            "-Djava.net.preferIPv4Stack=true " +
            "-Dsun.net.spi.nameservice.nameservers=223.5.5.5,119.29.29.29 " +
            "-Dsun.net.inetaddr.ttl=0 " +
            "-Dsun.net.client.defaultConnectTimeout=3000 " +
            "-Dsun.net.client.defaultReadTimeout=3000";

        // ===================== 统一异常日志 =====================
        private static void LogEx(string context, Exception ex, LogLevel level = LogLevel.Error)
        {
            recordLog(level, context + " -> " + (ex == null ? "null" : ex.ToString()));
        }

        // ===================== 客户端 =====================
        public static void InstallClient(string version, string minecraftDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            recordLog(LogLevel.Info, $"[Fabric] 开始安装客户端 Fabric {version}");

            string loaderVersion = ResolveLoaderVersion(version, loaderParam);
            if (string.IsNullOrEmpty(loaderVersion))
                throw new Exception("无法获取 Fabric Loader 版本");
            recordLog(LogLevel.Info, $"[Fabric] 使用 Loader 版本: {loaderVersion}");

            string versionId = $"fabric-loader-{loaderVersion}-{version}";

            // 1. 获取 profile JSON
            string profileUrl = $"{FABRIC_META}/v2/versions/loader/" +
                                $"{Uri.EscapeDataString(version)}/" +
                                $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
            recordLog(LogLevel.Info, $"[Fabric] 获取 profile: {profileUrl}");
            string profileJson = DownloadString(profileUrl);

            // 2. ★ 合并父版本 JSON（传入 versionId，强制锁定 id/jar）
            string mergedJson = MergeParentJson(profileJson, minecraftDir, versionId);

            // 3. 保存版本 JSON
            string versionsDir = Path.Combine(minecraftDir, "versions");
            string versionDir = Path.Combine(versionsDir, versionId);
            Directory.CreateDirectory(versionDir);
            string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
            try
            {
                File.WriteAllText(versionJsonPath, mergedJson, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new Exception($"[Fabric] 写入版本 JSON 失败: {versionJsonPath}", ex);
            }
            recordLog(LogLevel.Info, $"[Fabric] 版本 JSON 已保存: {versionJsonPath}");

            // 4. ★ 复制原版 client.jar 到 Fabric 版本目录（失败会抛异常）
            CopyClientJar(versionsDir, version, versionDir, versionId);

            // 5. 解析（已合并的）JSON 收集 libraries
            var root = ParseJson(mergedJson);
            if (root == null || !root.ContainsKey("libraries"))
                throw new Exception("profile JSON 缺少 libraries");

            string librariesDir = Path.Combine(minecraftDir, "libraries");
            var tasks = CollectLibraryTasks(root, librariesDir, "client");
            recordLog(LogLevel.Info, $"[Fabric] Libraries 任务: {tasks.Count}");

            if (tasks.Count > 0)
                ParallelDownload(tasks, MAX_CONCURRENCY);

            recordLog(LogLevel.Info, $"[Fabric] 客户端 Fabric {version} 安装完成");
        }

        // ===================== 服务端 =====================
        public static void InstallServer(string version, string serverDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(serverDir))
                throw new ArgumentException("服务端路径不能为空", "serverDir");

            recordLog(LogLevel.Info, $"[Fabric] 开始安装服务端 Fabric {version}");

            string serverJarPath = Path.Combine(serverDir, "server.jar");
            if (!File.Exists(serverJarPath) || new FileInfo(serverJarPath).Length == 0)
                recordLog(LogLevel.Warn, $"[Fabric] 警告: 未找到原版 server.jar，请先运行 Vanilla.InstallServer");

            string loaderVersion = ResolveLoaderVersion(version, loaderParam);
            if (string.IsNullOrEmpty(loaderVersion))
                throw new Exception("无法获取 Fabric Loader 版本");
            recordLog(LogLevel.Info, $"[Fabric] 使用 Loader 版本: {loaderVersion}");

            Directory.CreateDirectory(serverDir);
            string librariesDir = Path.Combine(serverDir, "libraries");

            string profileUrl = $"{FABRIC_META}/v2/versions/loader/" +
                                $"{Uri.EscapeDataString(version)}/" +
                                $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
            recordLog(LogLevel.Info, $"[Fabric] 获取 profile: {profileUrl}");
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

            recordLog(LogLevel.Info, $"[Fabric] 共 {tasks.Count} 个下载任务");

            if (tasks.Count > 0)
                ParallelDownload(tasks, MAX_CONCURRENCY);

            GenerateStartBat(serverDir);

            recordLog(LogLevel.Info, $"[Fabric] 首次启动验证（Fabric 会自行下载缺失库并 remap 原版 jar，可能需要几分钟）...");
            VerifyFirstLaunch(serverDir);

            recordLog(LogLevel.Info, $"[Fabric] 服务端 Fabric {version} 安装完成");
            recordLog(LogLevel.Info, $"[Fabric] 启动脚本已生成: start.bat");
            recordLog(LogLevel.Info, $"[Fabric] 首次启动会提示接受 EULA，请按提示操作");
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
                sb.AppendLine($"java {JVM_ARGS} -Xms{SERVER_MIN_MEMORY} -Xmx{SERVER_MAX_MEMORY} -jar fabric-server-launch.jar nogui");
                sb.AppendLine("pause");

                File.WriteAllText(batPath, sb.ToString(), Encoding.Default);
                recordLog(LogLevel.Info, $"[Fabric] start.bat 已生成: {batPath}");
            }
            catch (Exception ex)
            {
                // start.bat 生成失败不致命，但必须完整记录堆栈
                LogEx($"[Fabric] 生成 start.bat 失败(不致命): {serverDir}", ex);
            }
        }

        // ===================== 首次启动验证 =====================
        private static void VerifyFirstLaunch(string serverDir)
        {
            string javaPath = FindJavaForServer();
            if (string.IsNullOrEmpty(javaPath))
                throw new Exception("未找到 Java 运行时，请安装 Java 17+ 并设置 JAVA_HOME");

            recordLog(LogLevel.Info, $"[Fabric] 使用 Java: {javaPath}");

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

                    string trimmed = line.Trim();
                    if (trimmed.Contains("下载") || trimmed.Contains("Unpacking") ||
                        trimmed.Contains("Loading Minecraft") || trimmed.Contains("Starting") ||
                        trimmed.Contains("ERROR") || trimmed.Contains("WARN") ||
                        trimmed.Contains("eula") || trimmed.Contains("EULA") ||
                        trimmed.Contains("Done"))
                    {
                        recordLog(LogLevel.Info, $"[服务器] {trimmed}");
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

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    throw new Exception($"[Fabric] 启动 java 进程失败: {javaPath}", ex);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(FIRST_LAUNCH_TIMEOUT_MS);
                if (exited) process.WaitForExit();

                if (!exited)
                {
                    recordLog(LogLevel.Info, $"[Fabric] 服务器已启动，发送 stop 命令...");
                    try
                    {
                        process.StandardInput.WriteLine("stop");
                        process.StandardInput.Flush();
                    }
                    catch (Exception ex)
                    {
                        // 进程可能已经退出，忽略但记录
                        LogEx($"[Fabric] 向服务器发送 stop 失败（可能已退出）", ex, LogLevel.Warn);
                    }

                    if (!process.WaitForExit(30000))
                    {
                        recordLog(LogLevel.Error, $"[Fabric] 服务器未正常退出，强制结束");
                        try { process.Kill(); }
                        catch (Exception ex) { LogEx($"[Fabric] Kill 服务器进程失败", ex); }
                        process.WaitForExit(5000);
                    }
                }

                string fullOutput;
                lock (outLock) fullOutput = outputBuilder.ToString();

                if (eulaDetected)
                {
                    recordLog(LogLevel.Info, $"[Fabric] ✅ 检测到 EULA 提示，Fabric 库已下载完成，安装验证通过");
                    return;
                }

                if (startedDetected)
                {
                    recordLog(LogLevel.Info, $"[Fabric] ✅ 服务器已成功启动，安装验证通过");
                    return;
                }

                string eulaPath = Path.Combine(serverDir, "eula.txt");
                string propsPath = Path.Combine(serverDir, "server.properties");
                if (File.Exists(eulaPath) || File.Exists(propsPath))
                {
                    recordLog(LogLevel.Info, $"[Fabric] ✅ 检测到配置文件已生成，安装验证通过");
                    return;
                }

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
                try
                {
                    foreach (string sub in Directory.GetDirectories(root))
                    {
                        string exe = Path.Combine(sub, "bin", "java.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch (Exception ex)
                {
                    LogEx($"[Fabric] 枚举 Java 目录失败: {root}", ex, LogLevel.Warn);
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
                LogEx($"[Fabric] 获取最新 Loader 版本失败: {gameVersion}", ex);
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
            catch (Exception ex)
            {
                // 兜底返回默认版本，需要记录
                LogEx($"[Fabric] 获取 Installer 版本失败，使用默认 1.1.2", ex, LogLevel.Warn);
            }
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
            var failures = new List<string>();

            recordLog(LogLevel.Info, $"[Fabric] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");
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
                            }
                            LogEx($"[Fabric] 下载失败: {task.Name}", ex);
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
                if (failed > 0 && failures.Count > 0)
                {
                    Console.WriteLine($"[Fabric] 失败清单（前 {Math.Min(failures.Count, 20)} 条）:");
                    int n = Math.Min(failures.Count, 20);
                    for (int i = 0; i < n; i++)
                        Console.WriteLine($"  - {failures[i]}");
                }
            }

            // ★ 有失败就抛异常，且带失败清单
            if (failed > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"[Fabric] 有 {failed}/{total} 个文件下载失败");
                int n = Math.Min(failures.Count, 20);
                if (n > 0)
                {
                    sb.Append("\n失败清单（前 ").Append(n).Append(" 条）:");
                    for (int i = 0; i < n; i++)
                        sb.Append("\n  - ").Append(failures[i]);
                }
                throw new Exception(sb.ToString());
            }
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
            catch (Exception ex)
            {
                LogEx($"[Fabric] DNS 解析失败: {uri.Host}", ex, LogLevel.Warn);
            }

            if (ipv4List.Count == 0)
            {
                recordLog(LogLevel.Warn, $"[Fabric] 未解析到 IPv4，退回原 URL: {url}");
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

        // ===================== jar 校验 =====================
        private static bool IsJarPath(string path)
        {
            return path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateJarFile(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                if (fs.Length < 4)
                    throw new Exception("文件太小，不是有效 zip/jar");

                byte[] header = new byte[4];
                int n = fs.Read(header, 0, 4);
                if (n < 4 || header[0] != 0x50 || header[1] != 0x4B)
                    throw new Exception("不是有效的 zip/jar 文件（可能是 HTML 错误页）");

                fs.Seek(0, SeekOrigin.Begin);
                try
                {
                    using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        int count = 0;
                        foreach (var entry in archive.Entries)
                        {
                            count++;
                            if (count > 100000) break;
                        }
                        if (count == 0)
                            throw new Exception("zip/jar 内无任何文件（可能是半截下载）");
                    }
                }
                catch (InvalidDataException ex)
                {
                    throw new Exception($"zip/jar 文件损坏: {ex.Message}", ex);
                }
            }
        }

        // ===================== 底层下载（.tmp + jar 校验） =====================
        private static bool DownloadFile(string url, string dest, bool showProgress)
        {
            bool isJar = IsJarPath(dest);

            // ---- 已存在检查 ----
            if (File.Exists(dest))
            {
                try
                {
                    if (new FileInfo(dest).Length > 0)
                    {
                        if (!isJar) return true;
                        try
                        {
                            ValidateJarFile(dest);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            LogEx($"[Fabric] 已存在文件校验失败，删除重下: {dest}", ex, LogLevel.Warn);
                            try { File.Delete(dest); }
                            catch (Exception delEx) { LogEx($"[Fabric] 删除损坏文件失败: {dest}", delEx); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogEx($"[Fabric] 检查文件失败: {dest}", ex, LogLevel.Warn);
                }
            }

            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { throw new Exception($"[Fabric] 创建目录失败: {dir}", ex); }
            }

            string tmpPath = dest + ".tmp";
            string[] urls = BuildUrlCandidates(url);
            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                foreach (string u in urls)
                {
                    try
                    {
                        SingleDownload(u, tmpPath, showProgress);

                        if (isJar)
                        {
                            try { ValidateJarFile(tmpPath); }
                            catch (Exception ex)
                            {
                                try { File.Delete(tmpPath); }
                                catch (Exception delEx) { LogEx($"[Fabric] 删除损坏 .tmp 失败: {tmpPath}", delEx, LogLevel.Warn); }
                                throw new Exception($"[Fabric] jar 校验失败: {tmpPath}", ex);
                            }
                        }

                        // 原子替换
                        if (File.Exists(dest))
                        {
                            try { File.Delete(dest); }
                            catch (Exception ex) { LogEx($"[Fabric] 删除旧目标失败: {dest}", ex, LogLevel.Warn); }
                        }
                        File.Move(tmpPath, dest);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                        catch (Exception delEx) { LogEx($"[Fabric] 清理 .tmp 失败: {tmpPath}", delEx, LogLevel.Warn); }
                        LogEx($"[Fabric] 下载失败({u})", ex);
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
                        recordLog(LogLevel.Info, $"[Fabric] 请求: {u}");
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
                        LogEx($"[Fabric] 请求失败({u})", ex);
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
            catch (Exception ex)
            {
                LogEx("[Fabric] JSON 解析失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 复制原版 client.jar 到 Fabric 版本目录。
        /// ★ 失败必须抛异常：否则版本目录里没有 jar，启动器会报"找不到核心"。
        /// </summary>
        private static void CopyClientJar(string versionsDir, string originalVersion,
                                           string versionDir, string versionId)
        {
            string originalClientJar = Path.Combine(versionsDir, originalVersion,
                                                     originalVersion + ".jar");
            string fabricVersionJar = Path.Combine(versionDir, versionId + ".jar");

            if (!File.Exists(originalClientJar) || new FileInfo(originalClientJar).Length == 0)
            {
                // ★ 强制失败：原版 jar 缺失意味着 Vanilla 安装未完成或目录被清
                throw new Exception(
                    $"[Fabric] 原版 client.jar 缺失: {originalClientJar}\n" +
                    "请先执行 Vanilla.InstallClient 完成原版安装。");
            }

            try
            {
                long srcLen = new FileInfo(originalClientJar).Length;
                if (File.Exists(fabricVersionJar) &&
                    new FileInfo(fabricVersionJar).Length == srcLen)
                {
                    recordLog(LogLevel.Info, $"[Fabric] 版本核心已存在: {fabricVersionJar}");
                    return;
                }

                File.Copy(originalClientJar, fabricVersionJar, true);
                recordLog(LogLevel.Info, $"[Fabric] 已复制原版核心到: {fabricVersionJar}");
            }
            catch (Exception ex)
            {
                // ★ 强制失败：核心 jar 复制不出来，安装不算完成
                throw new Exception(
                    $"[Fabric] 复制原版核心失败: {originalClientJar} -> {fabricVersionJar}", ex);
            }
        }

        /// <summary>
        /// 合并父版本 JSON，并强制把 id / jar 锁定为 versionId。
        /// ★ 这是修复"文件都下完了但启动器找不到版本"的核心改动。
        /// </summary>
        private static string MergeParentJson(string fabricJson, string minecraftDir, string versionId)
        {
            var fabricRoot = ParseJson(fabricJson);
            if (fabricRoot == null)
            {
                LogEx("[Fabric] profile JSON 解析失败", null, LogLevel.Error);
                throw new Exception("[Fabric] profile JSON 解析失败，无法继续安装");
            }

            // ★ 无论是否有 inheritsFrom，先强制改 id
            fabricRoot["id"] = versionId;

            // 无 inheritsFrom → 只改 id
            if (!fabricRoot.ContainsKey("inheritsFrom"))
            {
                recordLog(LogLevel.Info, $"[Fabric] profile JSON 无 inheritsFrom，仅改写 id={versionId}");
                try { return new JavaScriptSerializer().Serialize(fabricRoot); }
                catch (Exception ex)
                {
                    LogEx("[Fabric] 序列化 profile JSON 失败", ex);
                    throw;
                }
            }

            string parentId = fabricRoot["inheritsFrom"].ToString();
            string parentPath = Path.Combine(minecraftDir, "versions", parentId, parentId + ".json");

            if (!File.Exists(parentPath))
            {
                LogEx($"[Fabric] 未找到父版本 JSON，无法合并", null, LogLevel.Error);
                throw new Exception(
                    $"[Fabric] 找不到父版本 JSON: {parentPath}\n" +
                    "请先安装原版 Minecraft。");
            }

            Dictionary<string, object> parentRoot;
            try
            {
                parentRoot = ParseJson(File.ReadAllText(parentPath));
            }
            catch (Exception ex)
            {
                throw new Exception($"[Fabric] 读取父版本 JSON 失败: {parentPath}", ex);
            }
            if (parentRoot == null)
                throw new Exception($"[Fabric] 父版本 JSON 解析失败: {parentPath}");

            string fabricId = fabricRoot.ContainsKey("id") ? fabricRoot["id"].ToString() : versionId;
            recordLog(LogLevel.Info, $"[Fabric] 合并父版本 {parentId} 到 {fabricId}");

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

            // 3. ★ 强制锁定 id 和 jar（关键修复）
            merged["id"] = versionId;
            merged["jar"] = versionId;

            // 4. 合并 libraries（Fabric 优先，父版本去重补充）
            merged["libraries"] = MergeLibraries(fabricRoot, parentRoot);

            // 5. 合并 arguments（父 + Fabric）
            var mergedArgs = MergeArguments(fabricRoot, parentRoot);
            if (mergedArgs != null)
                merged["arguments"] = mergedArgs;

            // 6. 序列化
            try
            {
                string result = new JavaScriptSerializer().Serialize(merged);
                recordLog(LogLevel.Info, $"[Fabric] 合并完成，JSON 长度: {result.Length}");
                return result;
            }
            catch (Exception ex)
            {
                LogEx("[Fabric] 序列化合并结果失败", ex);
                throw;
            }
        }

        private static List<object> MergeLibraries(Dictionary<string, object> fabricRoot,
                                                    Dictionary<string, object> parentRoot)
        {
            var result = new List<object>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            var jvmList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("jvm"))
            {
                var l = parentArgs["jvm"] as ArrayList;
                if (l != null)
                {
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