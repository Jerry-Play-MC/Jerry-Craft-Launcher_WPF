using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    internal class InstallVanilla
    {
        // ===================== 官方源 =====================
        private const string OFFICIAL_MANIFEST = "https://piston-meta.mojang.com/mc/game/version_manifest.json";
        private const string OFFICIAL_RESOURCES = "https://resources.download.minecraft.net";

        // ===================== 下载参数 =====================
        private const int MAX_CONCURRENCY = 24;
        private const int RETRY_COUNT = 3;
        private const int BUFFER_SIZE = 256 * 1024;
        private const long SMALL_FILE_SHA1_SKIP = 64 * 1024;
        private const int PROGRESS_REFRESH_MS = 100;
        private const int MAX_FAILURE_PRINT = 20;
        private const int TMP_MAX_AGE_HOURS = 24;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public static string ServerMaxMemory { get; set; } = "2G";
        public static string ServerMinMemory { get; set; } = "1G";

        private static readonly object _consoleLock = new object();
        private static readonly Encoding UTF8NoBom = new UTF8Encoding(false);
        private static int _lastProgressTick = 0;
        private static Exception _lastJsonError;

        private static IWebProxy _cachedProxy;
        private static bool _proxyInitialized;
        private static readonly object _proxyLock = new object();

        private static void LogEx(string context, Exception ex, LogLevel level = LogLevel.Error)
        {
            recordLog(level, context + " -> " + (ex == null ? "null" : ex.ToString()));
        }

        // ===================== 主入口（客户端） =====================
        public static void InstallClient(string version, string minecraftDir, string localManifestDir,
            TaskGroup group = null, CancellationToken ct = default(CancellationToken), InstallStages stages = null)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            // ★ fallback：老调用没传 stages 但有 group
            if (stages == null && group != null)
            {
                stages = new InstallStages
                {
                    Manifest = group.AddStage("Fetch version manifest", 1),
                    VersionJson = group.AddStage("Download version JSON", 2),
                    ClientJar = group.AddStage("Download client.jar", 20),
                    AssetIndex = group.AddStage("Download asset index", 3),
                    LogConfig = group.AddStage("Download log config", 1),
                    Libraries = group.AddStage("Download libraries", 50),
                    Assets = group.AddStage("Download assets", 100),
                    VirtualAssets = group.AddStage("Build virtual assets", 5),
                };
            }

            ct.ThrowIfCancellationRequested();
            recordLog(LogLevel.Info, $"[Vanilla] 开始安装 Minecraft {version} -> {minecraftDir}");

            CleanupTempFiles(minecraftDir);

            // ═══ 阶段 1：Fetch manifest ═══
            if (stages?.Manifest != null)
                group?.UpdateStage(stages.Manifest, 0, "Fetching");
            string manifestText = LoadManifestText(localManifestDir);
            string versionJsonUrl = FindVersionJsonUrl(manifestText, version);
            if (string.IsNullOrEmpty(versionJsonUrl))
                throw new Exception($"version_manifest.json 中未找到版本 {version}");
            group?.CompleteStage(stages?.Manifest);
            recordLog(LogLevel.Info, $"[Vanilla] 找到版本条目: {versionJsonUrl}");

            string versionsDir = System.IO.Path.Combine(minecraftDir, "versions");
            string versionDir = System.IO.Path.Combine(versionsDir, version);
            string librariesDir = System.IO.Path.Combine(minecraftDir, "libraries");
            string assetsDir = System.IO.Path.Combine(minecraftDir, "assets");
            Directory.CreateDirectory(versionDir);
            Directory.CreateDirectory(librariesDir);
            Directory.CreateDirectory(assetsDir);

            // ═══ 阶段 2：Download version JSON ═══
            if (stages?.VersionJson != null)
                group?.UpdateStage(stages.VersionJson, 0, "Downloading");
            string versionJsonPath = System.IO.Path.Combine(versionDir, version + ".json");
            string versionJsonSha1 = ExtractSha1FromUrl(versionJsonUrl);
            DownloadFile(versionJsonUrl, versionJsonPath, versionJsonSha1, "version.json",
                         showProgress: true, strictMode: true, out _, ct);
            group?.CompleteStage(stages?.VersionJson);
            recordLog(LogLevel.Info, $"[Vanilla] 已保存版本 JSON: {versionJsonPath}");

            var versionRoot = ParseJsonObject(ReadAllTextShared(versionJsonPath));
            if (versionRoot == null)
                throw new Exception($"版本 JSON 解析失败: {versionJsonPath} ({_lastJsonError?.Message})");
            versionRoot["id"] = version;

            // ═══ 阶段 3：Download client.jar ═══
            if (stages?.ClientJar != null)
                group?.UpdateStage(stages.ClientJar, 0, "Downloading");
            DownloadClientJar(versionRoot, versionDir, version, ct);
            group?.CompleteStage(stages?.ClientJar);

            // ═══ 阶段 4：Download asset index ═══
            if (stages?.AssetIndex != null)
                group?.UpdateStage(stages.AssetIndex, 0, "Downloading");
            string assetIndexId = DownloadAssetIndex(versionRoot, assetsDir, ct);
            group?.CompleteStage(stages?.AssetIndex);

            // ═══ 阶段 5：Download log config（失败不致命） ═══
            if (stages?.LogConfig != null)
                group?.UpdateStage(stages.LogConfig, 0, "Downloading");
            DownloadLoggingConfig(versionRoot, assetsDir, ct);
            group?.CompleteStage(stages?.LogConfig);

            // ═══ 收集任务 ═══
            var allTasks = new List<DownloadTask>();
            CollectLibraryTasks(versionRoot, librariesDir, allTasks);
            if (!string.IsNullOrEmpty(assetIndexId))
                CollectAssetTasks(assetsDir, assetIndexId, allTasks);
            allTasks = DeduplicateTasks(allTasks);

            // ═══ 按类型拆成两个独立队列 ═══
            var libTasks = new List<DownloadTask>();
            var assetTasks = new List<DownloadTask>();
            foreach (var t in allTasks)
            {
                if (t.Type == "libraries") libTasks.Add(t);
                else if (t.Type == "asset") assetTasks.Add(t);
            }
            recordLog(LogLevel.Info, $"[Vanilla] Libraries={libTasks.Count}, Assets={assetTasks.Count}");

            // ═══ 阶段 6-7：并行下载 libraries + assets ═══
            if (group != null)
            {
                if (stages?.Libraries != null)
                    group?.UpdateStage(stages.Libraries, 0, "Starting");
                if (stages?.Assets != null)
                    group?.UpdateStage(stages.Assets, 0, "Starting");

                // 两池同时跑
                Exception libEx = null, assetEx = null;
                var libThread = new Thread(() =>
                {
                    try { DownloadPhase(libTasks, group, stages?.Libraries, ct, "libraries"); }
                    catch (Exception ex) { libEx = ex; }
                });
                var assetThread = new Thread(() =>
                {
                    try { DownloadPhase(assetTasks, group, stages?.Assets, ct, "assets"); }
                    catch (Exception ex) { assetEx = ex; }
                });
                libThread.IsBackground = true;
                assetThread.IsBackground = true;
                libThread.Start();
                assetThread.Start();
                libThread.Join();
                assetThread.Join();

                if (libEx != null) throw libEx;
                if (assetEx != null) throw assetEx;
            }
            else
            {
                // 无 group 时退化：合并成一个并行池（老行为）
                if (allTasks.Count > 0)
                    ParallelDownload(allTasks, MAX_CONCURRENCY, ct);
            }

            // ═══ 阶段 8：Build virtual assets ═══
            if (!string.IsNullOrEmpty(assetIndexId))
            {
                if (stages?.VirtualAssets != null)
                    group?.UpdateStage(stages.VirtualAssets, 0, "Building");
                BuildVirtualAssets(assetsDir, assetIndexId, ct);
                group?.CompleteStage(stages?.VirtualAssets);
            }

            recordLog(LogLevel.Info, $"[Vanilla] Minecraft {version} 安装完成");
        }

        // ===================== 主入口（服务端） =====================
        public static void InstallServer(
            string version, string serverDir, string localManifestDir,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(serverDir))
                throw new ArgumentException("服务端路径不能为空", "serverDir");

            ct.ThrowIfCancellationRequested();
            recordLog(LogLevel.Info, $"[Vanilla] 开始安装 Minecraft 服务端 {version} -> {serverDir}");

            CleanupTempFiles(serverDir);

            string manifestText = LoadManifestText(localManifestDir);
            string versionJsonUrl = FindVersionJsonUrl(manifestText, version);
            if (string.IsNullOrEmpty(versionJsonUrl))
                throw new Exception($"version_manifest.json 中未找到版本 {version}");

            string tempJsonPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"mc_server_{version}_{Guid.NewGuid():N}.json");
            try
            {
                DownloadFile(versionJsonUrl, tempJsonPath, ExtractSha1FromUrl(versionJsonUrl),
                             "version.json", showProgress: false, strictMode: true, out _, ct);

                var versionRoot = ParseJsonObject(ReadAllTextShared(tempJsonPath));
                if (versionRoot == null)
                    throw new Exception($"版本 JSON 解析失败 ({_lastJsonError?.Message})");
                if (!versionRoot.ContainsKey("downloads"))
                    throw new Exception("版本 JSON 缺少 downloads 字段");

                var downloads = versionRoot["downloads"] as Dictionary<string, object>;
                if (downloads == null || !downloads.ContainsKey("server"))
                    throw new Exception($"版本 {version} 没有服务端下载信息");

                var server = downloads["server"] as Dictionary<string, object>;
                string serverUrl = server["url"].ToString();
                string serverSha1 = server.ContainsKey("sha1") ? server["sha1"].ToString() : null;

                Directory.CreateDirectory(serverDir);
                string serverJarPath = System.IO.Path.Combine(serverDir, "server.jar");

                DownloadFile(serverUrl, serverJarPath, serverSha1, "server.jar",
                             showProgress: true, strictMode: true, out _, ct);
                recordLog(LogLevel.Info, $"[Vanilla] 服务端核心已下载: {serverJarPath}");
            }
            finally
            {
                try { if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath); }
                catch (Exception ex) { LogEx($"[Vanilla] 清理临时 JSON 失败(不致命): {tempJsonPath}", ex, LogLevel.Warn); }
            }

            recordLog(LogLevel.Info, $"[Vanilla] Minecraft 服务端 {version} 下载完成");
        }

        // ===================== ★ 单类型并行下载阶段 =====================
        private static void DownloadPhase(
            List<DownloadTask> tasks,
            TaskGroup group, SubTaskItem stage,
            CancellationToken ct, string typeName)
        {
            ct.ThrowIfCancellationRequested();

            if (tasks.Count == 0)
            {
                if (stage != null)
                {
                    stage.Progress = 1;
                    stage.State = TaskState.Skipped;
                    stage.StatusText = "0 items";
                }
                group?.CompleteStage(stage);
                return;
            }

            if (stage != null) stage.State = TaskState.Running;

            int total = tasks.Count;
            int done = 0, failed = 0, skipped = 0;
            var failures = new List<string>();
            object statLock = new object();
            int nextIndex = 0;
            object queueLock = new object();
            int workerCount = Math.Min(MAX_CONCURRENCY, tasks.Count);

            recordLog(LogLevel.Info, $"[Vanilla] {typeName} 开始下载 {total} 个文件");

            var threads = new List<Thread>();
            for (int i = 0; i < workerCount; i++)
            {
                var worker = new Thread(() =>
                {
                    while (true)
                    {
                        if (ct.IsCancellationRequested) return;

                        DownloadTask task;
                        lock (queueLock)
                        {
                            if (nextIndex >= tasks.Count) return;
                            task = tasks[nextIndex++];
                        }

                        try
                        {
                            bool wasSkipped;
                            DownloadFile(task.Url, task.Dest, task.Sha1, task.Name,
                                         showProgress: false, strictMode: false, out wasSkipped, ct);

                            int n;
                            lock (statLock)
                            {
                                if (wasSkipped) skipped++;
                                n = ++done;
                            }

                            // 每 4 个或最后一个刷新
                            if (stage != null && (n % 4 == 0 || n == total))
                                group?.UpdateStage(stage, (double)n / total, $"{n}/{total}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lock (statLock)
                            {
                                failed++;
                                if (failures.Count < 200)
                                    failures.Add($"{task.Name} <- {ex.Message}");
                            }
                            LogEx($"[Vanilla] 下载失败: {task.Name} ({typeName})", ex);
                        }
                    }
                });
                worker.IsBackground = true;
                worker.Name = "Vanilla-" + typeName + "-" + i;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            ct.ThrowIfCancellationRequested();

            if (failed > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"[{typeName}] 有 {failed}/{total} 个文件下载失败");
                int n = Math.Min(failures.Count, MAX_FAILURE_PRINT);
                if (n > 0)
                {
                    sb.Append("\n失败清单（前 ").Append(n).Append(" 条）:");
                    for (int i = 0; i < n; i++)
                        sb.Append("\n  - ").Append(failures[i]);
                }
                group?.FailStage(stage, $"{failed} failed");
                throw new Exception(sb.ToString());
            }

            group?.CompleteStage(stage);
            recordLog(LogLevel.Info, $"[Vanilla] {typeName} 下载完成 {total}/{total} (skipped {skipped})");
        }

        // ===================== 老并行（无 group 时用，InstallServer 走这条） =====================
        private static void ParallelDownload(List<DownloadTask> tasks, int maxConcurrency,
                                               CancellationToken ct = default(CancellationToken))
        {
            int totalLib = 0, totalAsset = 0;
            foreach (var t in tasks)
            {
                if (t.Type == "libraries") totalLib++;
                else if (t.Type == "asset") totalAsset++;
            }

            int doneLib = 0, doneAsset = 0;
            int failed = 0, skipped = 0;
            int completed = 0;
            int total = tasks.Count;

            int nextIndex = 0;
            object queueLock = new object();
            object statLock = new object();
            var failures = new List<string>();

            recordLog(LogLevel.Info, $"[Vanilla] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");

            var threads = new List<Thread>();
            for (int i = 0; i < maxConcurrency; i++)
            {
                var worker = new Thread(() =>
                {
                    while (true)
                    {
                        if (ct.IsCancellationRequested) return;
                        DownloadTask task;
                        lock (queueLock)
                        {
                            if (nextIndex >= tasks.Count) return;
                            task = tasks[nextIndex++];
                        }

                        try
                        {
                            bool wasSkipped;
                            DownloadFile(task.Url, task.Dest, task.Sha1, task.Name,
                                         showProgress: false, strictMode: false, out wasSkipped, ct);

                            lock (statLock)
                            {
                                if (task.Type == "libraries") doneLib++;
                                else if (task.Type == "asset") doneAsset++;
                                if (wasSkipped) skipped++;
                                completed++;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lock (statLock)
                            {
                                failed++;
                                completed++;
                                if (failures.Count < 200)
                                    failures.Add($"{task.Name} <- {ex.Message}");
                            }
                            LogEx($"[Vanilla] 下载失败: {task.Name} ({task.Type})", ex);
                        }
                    }
                });
                worker.IsBackground = true;
                worker.Name = "Downloader-" + i;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();
            ct.ThrowIfCancellationRequested();

            if (failed > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"[Vanilla] 有 {failed}/{total} 个文件下载失败");
                int n = Math.Min(failures.Count, MAX_FAILURE_PRINT);
                if (n > 0)
                {
                    sb.Append("\n失败清单（前 ").Append(n).Append(" 条）:");
                    for (int i = 0; i < n; i++)
                        sb.Append("\n  - ").Append(failures[i]);
                }
                throw new Exception(sb.ToString());
            }
        }

        // ===================== 代理（带缓存） =====================
        private static IWebProxy GetProxy()
        {
            lock (_proxyLock)
            {
                if (!_proxyInitialized)
                {
                    try { _cachedProxy = BuildProxyFromEnv(); }
                    catch (Exception ex) { LogEx("[Vanilla] 构建代理失败，降级直连", ex, LogLevel.Warn); _cachedProxy = null; }
                    _proxyInitialized = true;
                }
                return _cachedProxy;
            }
        }

        public static void RefreshProxy()
        {
            lock (_proxyLock) { _proxyInitialized = false; }
        }

        private static IWebProxy BuildProxyFromEnv()
        {
            string p = Environment.GetEnvironmentVariable("HTTPS_PROXY");
            if (string.IsNullOrEmpty(p)) p = Environment.GetEnvironmentVariable("https_proxy");
            if (string.IsNullOrEmpty(p)) p = Environment.GetEnvironmentVariable("HTTP_PROXY");
            if (string.IsNullOrEmpty(p)) p = Environment.GetEnvironmentVariable("http_proxy");
            if (string.IsNullOrEmpty(p)) return null;

            try
            {
                string url = p.Trim().Trim('"');
                if (!url.Contains("://")) url = "http://" + url;
                var proxy = new WebProxy(url);
                try
                {
                    var uri = new Uri(url);
                    if (!string.IsNullOrEmpty(uri.UserInfo))
                    {
                        var parts = uri.UserInfo.Split(':');
                        string user = Uri.UnescapeDataString(parts[0]);
                        string pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
                        proxy.Credentials = new NetworkCredential(user, pass);
                    }
                }
                catch (Exception ex) { LogEx($"[Vanilla] 解析代理凭据失败: {url}", ex, LogLevel.Warn); }
                return proxy;
            }
            catch (Exception ex)
            {
                LogEx($"[Vanilla] 解析代理环境变量失败: {p}", ex, LogLevel.Warn);
                return null;
            }
        }

        // ===================== .tmp 清理 =====================
        private static void CleanupTempFiles(string rootDir)
        {
            try
            {
                if (!Directory.Exists(rootDir)) return;
                DateTime cutoff = DateTime.UtcNow.AddHours(-TMP_MAX_AGE_HOURS);
                int count = 0;
                string[] subDirs = { "versions", "libraries", "assets" };
                foreach (string sub in subDirs)
                {
                    string dir = System.IO.Path.Combine(rootDir, sub);
                    if (!Directory.Exists(dir)) continue;
                    count += CleanupTempInDir(dir, cutoff, true);
                }
                count += CleanupTempInDir(rootDir, cutoff, false);
                if (count > 0)
                    recordLog(LogLevel.Info, $"[Vanilla] 清理陈旧 .tmp: {count} 个");
            }
            catch (Exception ex) { LogEx($"[Vanilla] 清理 .tmp 失败(不致命)", ex, LogLevel.Warn); }
        }

        private static int CleanupTempInDir(string dir, DateTime cutoff, bool recursive)
        {
            int count = 0;
            try
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.GetFiles(dir, "*.tmp", option))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff) { File.Delete(f); count++; }
                    }
                    catch (Exception ex) { LogEx($"[Vanilla] 删除陈旧 .tmp 失败: {f}", ex, LogLevel.Warn); }
                }
            }
            catch (Exception ex) { LogEx($"[Vanilla] 枚举 .tmp 目录失败: {dir}", ex, LogLevel.Warn); }
            return count;
        }

        // ===================== Manifest =====================
        private static string LoadManifestText(string localManifestDir)
        {
            if (!string.IsNullOrEmpty(localManifestDir) && localManifestDir != "none")
            {
                string localPath = System.IO.Path.Combine(localManifestDir, "version_manifest.json");
                if (File.Exists(localPath))
                {
                    recordLog(LogLevel.Info, $"[Vanilla] 使用本地 manifest: {localPath}");
                    return ReadAllTextShared(localPath);
                }
            }
            return DownloadString(OFFICIAL_MANIFEST);
        }

        private static string FindVersionJsonUrl(string manifestText, string version)
        {
            var manifest = ParseJsonObject(manifestText);
            if (manifest == null || !manifest.ContainsKey("versions")) return null;
            var versions = manifest["versions"] as ArrayList;
            if (versions == null) return null;
            foreach (var v in versions)
            {
                var entry = v as Dictionary<string, object>;
                if (entry == null) continue;
                if (entry.ContainsKey("id") && entry["id"].ToString() == version)
                    return entry.ContainsKey("url") ? entry["url"].ToString() : null;
            }
            return null;
        }

        // ===================== client.jar =====================
        private static void DownloadClientJar(
            Dictionary<string, object> versionRoot, string versionDir, string version,
            CancellationToken ct)
        {
            if (!versionRoot.ContainsKey("downloads")) return;
            var downloads = versionRoot["downloads"] as Dictionary<string, object>;
            if (downloads == null || !downloads.ContainsKey("client")) return;

            var client = downloads["client"] as Dictionary<string, object>;
            string sha1 = client.ContainsKey("sha1") ? client["sha1"].ToString() : null;
            string url = client.ContainsKey("url") ? client["url"].ToString() : null;
            string dest = System.IO.Path.Combine(versionDir, version + ".jar");

            DownloadFile(url, dest, sha1, version + ".jar",
                         showProgress: true, strictMode: true, out _, ct);
        }

        // ===================== assetIndex =====================
        private static string DownloadAssetIndex(
            Dictionary<string, object> versionRoot, string assetsDir,
            CancellationToken ct)
        {
            if (!versionRoot.ContainsKey("assetIndex")) return null;
            var idx = versionRoot["assetIndex"] as Dictionary<string, object>;
            if (idx == null || !idx.ContainsKey("id")) return null;

            string id = idx["id"].ToString();
            string sha1 = idx.ContainsKey("sha1") ? idx["sha1"].ToString() : null;
            string url = idx.ContainsKey("url") ? idx["url"].ToString() : null;
            string dest = System.IO.Path.Combine(assetsDir, "indexes", id + ".json");

            DownloadFile(url, dest, sha1, id + ".json",
                         showProgress: true, strictMode: true, out _, ct);
            return id;
        }

        // ===================== logging 配置 =====================
        private static void DownloadLoggingConfig(
            Dictionary<string, object> versionRoot, string assetsDir,
            CancellationToken ct)
        {
            if (!versionRoot.ContainsKey("logging")) return;
            var logging = versionRoot["logging"] as Dictionary<string, object>;
            if (logging == null || !logging.ContainsKey("client")) return;

            var client = logging["client"] as Dictionary<string, object>;
            if (client == null || !client.ContainsKey("file")) return;

            var file = client["file"] as Dictionary<string, object>;
            if (file == null || !file.ContainsKey("id") || !file.ContainsKey("url")) return;

            string id = file["id"].ToString();
            string url = file["url"].ToString();
            string sha1 = file.ContainsKey("sha1") ? file["sha1"].ToString() : null;
            string dest = System.IO.Path.Combine(assetsDir, "log_configs", id);

            try
            {
                DownloadFile(url, dest, sha1, id, showProgress: false, strictMode: true, out _, ct);
            }
            catch (Exception ex)
            {
                LogEx($"[Vanilla] 日志配置下载失败(不致命): {id}", ex, LogLevel.Warn);
            }
        }

        // ===================== libraries 收集 =====================
        private static void CollectLibraryTasks(
            Dictionary<string, object> versionRoot, string librariesDir, List<DownloadTask> tasks)
        {
            if (!versionRoot.ContainsKey("libraries")) return;
            var libraries = versionRoot["libraries"] as ArrayList;
            if (libraries == null) return;

            foreach (var libObj in libraries)
            {
                var lib = libObj as Dictionary<string, object>;
                if (lib == null) continue;
                if (!IsLibraryAllowedOnWindows(lib)) continue;

                if (lib.ContainsKey("downloads"))
                {
                    var downloads = lib["downloads"] as Dictionary<string, object>;
                    if (downloads != null)
                    {
                        if (downloads.ContainsKey("artifact"))
                        {
                            var artifact = downloads["artifact"] as Dictionary<string, object>;
                            if (artifact != null && artifact.ContainsKey("path") &&
                                artifact.ContainsKey("url"))
                            {
                                tasks.Add(new DownloadTask
                                {
                                    Url = artifact["url"].ToString(),
                                    Dest = System.IO.Path.Combine(librariesDir, artifact["path"].ToString()),
                                    Sha1 = artifact.ContainsKey("sha1") ? artifact["sha1"].ToString() : null,
                                    Name = artifact["path"].ToString(),
                                    Type = "libraries"
                                });
                            }
                        }

                        if (downloads.ContainsKey("classifiers"))
                        {
                            var classifiers = downloads["classifiers"] as Dictionary<string, object>;
                            if (classifiers != null && classifiers.ContainsKey("natives-windows"))
                            {
                                var native = classifiers["natives-windows"] as Dictionary<string, object>;
                                if (native != null && native.ContainsKey("path") &&
                                    native.ContainsKey("url"))
                                {
                                    tasks.Add(new DownloadTask
                                    {
                                        Url = native["url"].ToString(),
                                        Dest = System.IO.Path.Combine(librariesDir, native["path"].ToString()),
                                        Sha1 = native.ContainsKey("sha1") ? native["sha1"].ToString() : null,
                                        Name = native["path"].ToString(),
                                        Type = "libraries"
                                    });
                                }
                            }
                        }
                        continue;
                    }
                }

                if (lib.ContainsKey("name") && lib.ContainsKey("url"))
                {
                    string name = lib["name"].ToString();
                    string baseUrl = lib["url"].ToString();
                    string relPath = MavenNameToPath(name, null);
                    if (!string.IsNullOrEmpty(relPath))
                    {
                        if (!baseUrl.EndsWith("/")) baseUrl += "/";
                        tasks.Add(new DownloadTask
                        {
                            Url = baseUrl + relPath,
                            Dest = System.IO.Path.Combine(librariesDir, relPath),
                            Sha1 = null,
                            Name = relPath,
                            Type = "libraries"
                        });
                    }
                }
            }
        }

        private static string MavenNameToPath(string name, string forcedExtension)
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
            if (!string.IsNullOrEmpty(forcedExtension)) ext = forcedExtension;

            string fileName = $"{artifact}-{version}";
            if (!string.IsNullOrEmpty(classifier)) fileName += "-" + classifier;
            fileName += ext;

            return $"{group}/{artifact}/{version}/{fileName}";
        }

        private static bool IsLibraryAllowedOnWindows(Dictionary<string, object> lib)
        {
            if (!lib.ContainsKey("rules")) return true;
            var rules = lib["rules"] as ArrayList;
            if (rules == null || rules.Count == 0) return true;

            bool? state = null;
            bool is64 = Environment.Is64BitProcess;

            foreach (var ruleObj in rules)
            {
                var rule = ruleObj as Dictionary<string, object>;
                if (rule == null) continue;
                bool matches = true;

                if (rule.ContainsKey("os"))
                {
                    var os = rule["os"] as Dictionary<string, object>;
                    if (os != null)
                    {
                        if (os.ContainsKey("name"))
                        {
                            if (os["name"].ToString() != "windows") matches = false;
                        }
                        if (matches && os.ContainsKey("arch"))
                        {
                            string arch = os["arch"].ToString().ToLowerInvariant();
                            if (arch == "x86" && is64) matches = false;
                            else if (arch == "x86_64" && !is64) matches = false;
                            else if (arch == "arm64") matches = false;
                        }
                    }
                }

                if (matches && rule.ContainsKey("features"))
                {
                    var feats = rule["features"] as Dictionary<string, object>;
                    if (feats != null)
                    {
                        foreach (var f in feats)
                        {
                            bool expected;
                            try { expected = Convert.ToBoolean(f.Value); }
                            catch { expected = false; }
                            if (expected) { matches = false; break; }
                        }
                    }
                }

                if (matches && rule.ContainsKey("action"))
                    state = (rule["action"].ToString() == "allow");
            }
            return state ?? true;
        }

        // ===================== assets 收集 =====================
        private static void CollectAssetTasks(
            string assetsDir, string assetIndexId, List<DownloadTask> tasks)
        {
            string idxPath = System.IO.Path.Combine(assetsDir, "indexes", assetIndexId + ".json");
            if (!File.Exists(idxPath))
            {
                recordLog(LogLevel.Warn, $"[Vanilla] 未找到资源索引: {idxPath}");
                return;
            }

            var idxRoot = ParseJsonObject(ReadAllTextShared(idxPath));
            if (idxRoot == null || !idxRoot.ContainsKey("objects")) return;

            var objects = idxRoot["objects"] as Dictionary<string, object>;
            if (objects == null) return;

            string objectsDir = System.IO.Path.Combine(assetsDir, "objects");
            Directory.CreateDirectory(objectsDir);

            foreach (var kv in objects)
            {
                var obj = kv.Value as Dictionary<string, object>;
                if (obj == null || !obj.ContainsKey("hash")) continue;

                string hash = obj["hash"].ToString();
                if (hash.Length < 2) continue;
                string sub = hash.Substring(0, 2);
                string dest = System.IO.Path.Combine(objectsDir, sub, hash);
                string url = $"{OFFICIAL_RESOURCES}/{sub}/{hash}";

                tasks.Add(new DownloadTask
                {
                    Url = url,
                    Dest = dest,
                    Sha1 = hash,
                    Name = kv.Key,
                    Type = "asset"
                });
            }
        }

        private static void BuildVirtualAssets(string assetsDir, string assetIndexId,
                                                 CancellationToken ct = default(CancellationToken))
        {
            string idxPath = System.IO.Path.Combine(assetsDir, "indexes", assetIndexId + ".json");
            if (!File.Exists(idxPath)) return;

            var idxRoot = ParseJsonObject(ReadAllTextShared(idxPath));
            if (idxRoot == null) return;

            bool isVirtual = false;
            bool isMapToResources = false;
            if (idxRoot.ContainsKey("virtual"))
            {
                try { isVirtual = Convert.ToBoolean(idxRoot["virtual"]); } catch { }
            }
            if (idxRoot.ContainsKey("map_to_resources"))
            {
                try { isMapToResources = Convert.ToBoolean(idxRoot["map_to_resources"]); } catch { }
            }

            if (!isVirtual && !isMapToResources) return;

            var objects = idxRoot["objects"] as Dictionary<string, object>;
            if (objects == null) return;

            string targetDir = isVirtual
                ? System.IO.Path.Combine(assetsDir, "virtual", assetIndexId)
                : System.IO.Path.Combine(assetsDir, "minecraft");

            Directory.CreateDirectory(targetDir);
            recordLog(LogLevel.Info, $"[Vanilla] 生成 assets 映射到: {targetDir}");

            string objectsDir = System.IO.Path.Combine(assetsDir, "objects");
            int copied = 0;
            foreach (var kv in objects)
            {
                ct.ThrowIfCancellationRequested();
                string origPath = kv.Key;
                var obj = kv.Value as Dictionary<string, object>;
                if (obj == null || !obj.ContainsKey("hash")) continue;

                string hash = obj["hash"].ToString();
                if (hash.Length < 2) continue;
                string src = System.IO.Path.Combine(objectsDir, hash.Substring(0, 2), hash);
                if (!File.Exists(src)) continue;

                string dst = System.IO.Path.Combine(targetDir,
                    origPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                string dstDir = System.IO.Path.GetDirectoryName(dst);
                if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);

                try { File.Copy(src, dst, true); copied++; } catch { }
            }
            recordLog(LogLevel.Info, $"[Vanilla] assets 映射完成: {copied} 个文件");
        }

        // ===================== 任务去重 =====================
        private static List<DownloadTask> DeduplicateTasks(List<DownloadTask> tasks)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byDest = new Dictionary<string, DownloadTask>(StringComparer.OrdinalIgnoreCase);
            var result = new List<DownloadTask>(tasks.Count);

            foreach (var t in tasks)
            {
                if (string.IsNullOrEmpty(t.Dest)) continue;
                string key;
                try { key = System.IO.Path.GetFullPath(t.Dest); }
                catch { key = t.Dest; }

                if (seen.Add(key)) { byDest[key] = t; result.Add(t); }
            }
            return result;
        }

        // ===================== 底层下载（加 ct + DownloadGate） =====================
        private static void DownloadFile(
            string url, string dest, string sha1, string displayName,
            bool showProgress, bool strictMode, out bool outSkipped,
            CancellationToken ct = default(CancellationToken))
        {
            outSkipped = false;
            string tmpPath = dest + ".tmp";

            if (File.Exists(dest))
            {
                if (strictMode)
                {
                    if (string.IsNullOrEmpty(sha1))
                    {
                        try
                        {
                            if (new FileInfo(dest).Length > 0) { outSkipped = true; return; }
                        }
                        catch (Exception ex) { LogEx($"[Vanilla] 读取已有文件大小失败: {dest}", ex, LogLevel.Warn); }
                    }
                    else
                    {
                        try
                        {
                            if (ComputeSha1(dest).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                            { outSkipped = true; return; }
                            try { File.Delete(dest); } catch { }
                            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        }
                        catch (Exception ex) { LogEx($"[Vanilla] 计算已有文件 SHA1 失败: {dest}", ex, LogLevel.Warn); }
                    }
                }
                else
                {
                    try
                    {
                        if (new FileInfo(dest).Length > 0) { outSkipped = true; return; }
                    }
                    catch (Exception ex) { LogEx($"[Vanilla] 读取已有文件大小失败: {dest}", ex, LogLevel.Warn); }
                }
            }

            if (string.IsNullOrEmpty(url))
                throw new Exception($"下载 {displayName} 失败：URL 为空");

            Exception lastEx = null;
            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    SingleStreamDownload(url, tmpPath, showProgress, ct);

                    if (!string.IsNullOrEmpty(sha1))
                    {
                        long fileSize = 0;
                        try { fileSize = new FileInfo(tmpPath).Length; } catch { }

                        bool shouldVerify = strictMode || fileSize > SMALL_FILE_SHA1_SKIP;
                        if (shouldVerify)
                        {
                            string actual = ComputeSha1(tmpPath);
                            if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Delete(tmpPath); } catch { }
                                throw new Exception($"SHA1 校验失败: 期望 {sha1}, 实际 {actual}");
                            }
                        }
                    }

                    MoveOrReplace(tmpPath, dest);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastEx = ex;
                    LogEx($"[Vanilla] 下载 {displayName} 失败（第 {retry + 1}/{RETRY_COUNT} 次）", ex, LogLevel.Warn);
                    if (retry < RETRY_COUNT - 1)
                    {
                        if (ct.WaitHandle.WaitOne(500 * (retry + 1)))
                            ct.ThrowIfCancellationRequested();
                    }
                }
            }

            throw new Exception(
                $"下载 {displayName} 失败（重试 {RETRY_COUNT} 次）: {lastEx?.Message}", lastEx);
        }

        private static void MoveOrReplace(string src, string dst)
        {
            if (File.Exists(dst))
            {
                try { File.Replace(src, dst, null); return; }
                catch (PlatformNotSupportedException) { }
                catch (IOException) { }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { File.Delete(dst); break; }
                    catch (IOException ex)
                    {
                        if (attempt == 2)
                            throw new Exception($"[Vanilla] 删除目标文件失败（可能被占用）: {dst}", ex);
                        Thread.Sleep(200 * (attempt + 1));
                    }
                }
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { File.Move(src, dst); return; }
                catch (IOException ex)
                {
                    if (attempt == 2)
                        throw new Exception($"[Vanilla] 移动临时文件失败（可能被占用）: {src} -> {dst}", ex);
                    Thread.Sleep(200 * (attempt + 1));
                }
            }
        }

        // ===================== 单流下载（带 ct + DownloadGate） =====================
        private static void SingleStreamDownload(string url, string dest, bool showProgress,
                                                   CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();

            // ★ 全局下载许可（防止多任务并发时总并发过载）
            DownloadGate.Global.Wait(ct);
            try
            {
                SingleStreamDownloadCore(url, dest, showProgress, ct);
            }
            finally
            {
                DownloadGate.Global.Release();
            }
        }

        private static void SingleStreamDownloadCore(string url, string dest, bool showProgress,
                                                       CancellationToken ct)
        {
            string dir = System.IO.Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { throw new Exception($"[Vanilla] 创建下载目录失败: {dir}", ex); }
            }

            long existingLength = 0;
            if (File.Exists(dest))
            {
                try { existingLength = new FileInfo(dest).Length; } catch { }
            }

            HttpWebResponse resp = null;
            bool isPartial = false;

            try
            {
                resp = SendDownloadRequest(url, existingLength);
                isPartial = resp.StatusCode == HttpStatusCode.PartialContent;
            }
            catch (WebException ex) when (IsRangeNotSatisfiable(ex))
            {
                if (ex.Response != null) { try { ex.Response.Close(); } catch { } }
                if (existingLength > 0)
                {
                    try { File.Delete(dest); } catch { }
                    existingLength = 0;
                }
                resp = SendDownloadRequest(url, 0);
                isPartial = false;
            }

            using (resp)
            using (var netStream = resp.GetResponseStream())
            {
                if (existingLength > 0 && !isPartial) existingLength = 0;

                long totalBytes;
                if (existingLength > 0 && isPartial)
                {
                    totalBytes = ParseContentRangeTotal(resp.Headers["Content-Range"]);
                    if (totalBytes <= 0) totalBytes = existingLength + resp.ContentLength;
                }
                else totalBytes = resp.ContentLength;

                if (existingLength > 0 && totalBytes > 0 && existingLength == totalBytes) return;

                FileMode mode = existingLength > 0 ? FileMode.Append : FileMode.Create;

                using (var fs = new FileStream(dest, mode, FileAccess.Write,
                                               FileShare.None, BUFFER_SIZE))
                {
                    long received = existingLength;
                    byte[] buffer = new byte[BUFFER_SIZE];
                    int read;
                    bool displayProgress = showProgress && totalBytes > 1024 * 1024;

                    while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        fs.Write(buffer, 0, read);
                        received += read;

                        if (displayProgress)
                        {
                            int pct = totalBytes > 0 ? (int)(received * 100 / totalBytes) : 0;
                            lock (_consoleLock)
                            {
                                Console.Write($"\r[下载中] {System.IO.Path.GetFileName(dest)} - {pct}% " +
                                              $"({FormatSize(received)}/{FormatSize(totalBytes)})   ");
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

                    if (totalBytes > 0 && received != totalBytes)
                        throw new Exception($"下载不完整: {received} 字节，期望 {totalBytes} 字节");
                }
            }
        }

        private static HttpWebResponse SendDownloadRequest(string url, long existingLength)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.Proxy = GetProxy();
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            req.KeepAlive = true;
            req.AutomaticDecompression = DecompressionMethods.None;
            if (existingLength > 0) req.AddRange(existingLength);
            return (HttpWebResponse)req.GetResponse();
        }

        private static bool IsRangeNotSatisfiable(WebException ex)
        {
            return ex.Response is HttpWebResponse r &&
                   r.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable;
        }

        private static long ParseContentRangeTotal(string contentRange)
        {
            if (string.IsNullOrEmpty(contentRange)) return -1;
            int slash = contentRange.LastIndexOf('/');
            if (slash < 0) return -1;
            long total;
            if (long.TryParse(contentRange.Substring(slash + 1).Trim(), out total))
                return total;
            return -1;
        }

        // ===================== 读取辅助 =====================
        private static string ReadAllTextShared(string path, int maxRetry = 10)
        {
            for (int i = 0; i < maxRetry; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (IOException) when (i < maxRetry - 1) { Thread.Sleep(100 * (i + 1)); }
                catch (Exception) when (i < maxRetry - 1) { Thread.Sleep(100 * (i + 1)); }
            }
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static string DownloadString(string url)
        {
            Exception lastEx = null;
            for (int i = 0; i < RETRY_COUNT; i++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = UA_STRING;
                    req.Proxy = GetProxy();
                    req.Timeout = 30000;
                    req.AllowAutoRedirect = true;
                    req.KeepAlive = true;

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (i < RETRY_COUNT - 1) Thread.Sleep(500 * (i + 1));
                }
            }
            throw new Exception($"下载 {url} 失败（重试 {RETRY_COUNT} 次）: {lastEx?.Message}", lastEx);
        }

        private static string ComputeSha1(string filePath)
        {
            using (var fs = File.OpenRead(filePath))
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ExtractSha1FromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var m = Regex.Match(url, @"/(?:packages|objects)/([a-f0-9]{40})/",
                                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        private static Dictionary<string, object> ParseJsonObject(string json)
        {
            _lastJsonError = null;
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception ex)
            {
                _lastJsonError = ex;
                Debug.WriteLine($"[Vanilla] JSON 解析失败: {ex.Message}");
                return null;
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }

        private class DownloadTask
        {
            public string Url { get; set; }
            public string Dest { get; set; }
            public string Sha1 { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
        }
    }
}