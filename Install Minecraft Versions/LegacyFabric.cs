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
    internal class LegacyFabric
    {
        // ===================== Legacy Fabric 官方源 =====================
        private const string LEGACY_META = "https://meta.legacyfabric.net";
        private const string LEGACY_MAVEN = "https://maven.legacyfabric.net";

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

        // ===================== 客户端 =====================
        public static void InstallClient(string version, string minecraftDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            Log($"[LegacyFabric] 开始安装客户端 Legacy Fabric {version}");

            string loaderVersion = ResolveLoaderVersion(version, loaderParam);
            if (string.IsNullOrEmpty(loaderVersion))
                throw new Exception("无法获取 Legacy Fabric Loader 版本");
            Log($"[LegacyFabric] 使用 Loader 版本: {loaderVersion}");

            string versionId = $"legacyfabric-loader-{loaderVersion}-{version}";

            // 1. 获取 profile JSON
            string profileUrl = $"{LEGACY_META}/v2/versions/loader/" +
                                $"{Uri.EscapeDataString(version)}/" +
                                $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
            Log($"[LegacyFabric] 获取 profile: {profileUrl}");
            string profileJson = DownloadString(profileUrl);

            // 2. 合并父版本 JSON（去 inheritsFrom，生成独立版本）
            string mergedJson = MergeParentJson(profileJson, minecraftDir, versionId);

            // 3. 保存版本 JSON
            string versionsDir = Path.Combine(minecraftDir, "versions");
            string versionDir = Path.Combine(versionsDir, versionId);
            Directory.CreateDirectory(versionDir);
            string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(versionJsonPath, mergedJson, Encoding.UTF8);
            Log($"[LegacyFabric] 版本 JSON 已保存: {versionJsonPath}");

            // 4. 复制原版 client.jar
            CopyClientJar(versionsDir, version, versionDir, versionId);

            // 5. 解析（已合并的）JSON 收集 libraries
            var root = ParseJson(mergedJson);
            if (root == null || !root.ContainsKey("libraries"))
                throw new Exception("profile JSON 缺少 libraries");

            string librariesDir = Path.Combine(minecraftDir, "libraries");
            var tasks = CollectLibraryTasks(root, librariesDir, "client");
            Log($"[LegacyFabric] Libraries 任务: {tasks.Count}");

            if (tasks.Count > 0)
                ParallelDownload(tasks, MAX_CONCURRENCY);

            Log($"[LegacyFabric] 客户端 Legacy Fabric {version} 安装完成");
        }

        // ===================== 服务端：不支持 =====================
        public static void InstallServer(string version, string serverDir, string loaderParam)
        {
            throw new NotSupportedException(
                "Legacy Fabric 服务端需要通过官方安装器手动部署，本启动器暂不支持。\n" +
                "如需服务端请改用 Forge / Fabric（现代版）。");
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
                string url = $"{LEGACY_META}/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}";
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
                Log($"[LegacyFabric] 获取最新 Loader 版本失败: {ex.Message}");
            }
            return null;
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

                // ============================================================
                // 情况 A：库带 downloads 字段
                // ============================================================
                if (lib.ContainsKey("downloads"))
                {
                    var downloads = lib["downloads"] as Dictionary<string, object>;
                    if (downloads == null) continue;

                    bool hasClassifiers = downloads.ContainsKey("classifiers");

                    bool isNativesOnly = hasClassifiers
                                      || lib.ContainsKey("natives")
                                      || name.IndexOf("-platform",
                                             StringComparison.OrdinalIgnoreCase) >= 0;

                    // A1. 主 artifact
                    if (downloads.ContainsKey("artifact"))
                    {
                        var artifact = downloads["artifact"] as Dictionary<string, object>;
                        if (artifact != null &&
                            artifact.ContainsKey("url") && artifact.ContainsKey("path"))
                        {
                            string relPath = artifact["path"].ToString();
                            string url = artifact["url"].ToString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                tasks.Add(new DownloadTask
                                {
                                    Url = url,
                                    Dest = Path.Combine(librariesDir, relPath),
                                    Name = relPath,
                                    Type = type,
                                    ShowProgress = false,
                                    SkipIfNotFound = isNativesOnly
                                });
                            }
                        }
                    }

                    // A2. natives-windows
                    if (downloads.ContainsKey("classifiers"))
                    {
                        var classifiers = downloads["classifiers"] as Dictionary<string, object>;
                        if (classifiers != null && classifiers.ContainsKey("natives-windows"))
                        {
                            var nat = classifiers["natives-windows"] as Dictionary<string, object>;
                            if (nat != null &&
                                nat.ContainsKey("url") && nat.ContainsKey("path"))
                            {
                                string relPath = nat["path"].ToString();
                                string url = nat["url"].ToString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    tasks.Add(new DownloadTask
                                    {
                                        Url = url,
                                        Dest = Path.Combine(librariesDir, relPath),
                                        Name = relPath,
                                        Type = type,
                                        ShowProgress = false,
                                        SkipIfNotFound = false
                                    });
                                }
                            }
                        }
                    }

                    continue;
                }

                // ============================================================
                // 情况 B：无 downloads，按 name + url 拼路径
                // ============================================================
                string baseUrl = lib.ContainsKey("url") ? lib["url"].ToString() : LEGACY_MAVEN;
                if (string.IsNullOrEmpty(baseUrl)) baseUrl = LEGACY_MAVEN;
                if (!baseUrl.EndsWith("/")) baseUrl += "/";

                string rel = MavenNameToPath(name);
                if (string.IsNullOrEmpty(rel)) continue;

                // ★ 情况 B 也判断 natives-only
                bool isNativesOnlyB = lib.ContainsKey("natives")
                                   || name.IndexOf("-platform",
                                          StringComparison.OrdinalIgnoreCase) >= 0;

                tasks.Add(new DownloadTask
                {
                    Url = baseUrl + rel,
                    Dest = Path.Combine(librariesDir, rel),
                    Name = rel,
                    Type = type,
                    ShowProgress = false,
                    SkipIfNotFound = isNativesOnlyB   // ★
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

            Log($"[LegacyFabric] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");
            Interlocked.Exchange(ref _lastProgressTick, 0);

            lock (_consoleLock)
                PrintProgressLine(skipped, failed, completed, total);

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
                            bool wasSkipped = DownloadFile(task);   // ★ 传整个 task
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
                                Console.WriteLine($"[LegacyFabric] 下载失败: {task.Name} -> {ex.Message}");
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
                                PrintProgressLine(skipped, failed, completed, total);
                        }
                    }
                });
                worker.IsBackground = true;
                worker.Name = "LegacyFabricDownloader-" + i;
                worker.Priority = ThreadPriority.Normal;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.WriteLine($"[LegacyFabric] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");
            }

            if (failed > 0)
                throw new Exception($"[LegacyFabric] 有 {failed}/{total} 个库下载失败");
        }

        private static void PrintProgressLine(int skipped, int failed, int completed, int total)
        {
            double pct = total == 0 ? 100 : (completed * 100.0 / total);
            Console.Write($"\r[LegacyFabric] 跳过 {skipped} | 失败 {failed} | " +
                          $"总进度 {pct:F1}% ({completed}/{total})  ");
            try { Console.Out.Flush(); } catch { }
        }

        // ===================== 底层下载 =====================
        private static bool DownloadFile(DownloadTask task)
        {
            string url = task.Url;
            string dest = task.Dest;

            if (File.Exists(dest))
            {
                try
                {
                    if (new FileInfo(dest).Length > 0) return true;
                }
                catch { }
            }

            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = dest + ".tmp";
            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                try
                {
                    SingleDownload(url, tmpPath, task.ShowProgress);
                    if (File.Exists(dest)) { try { File.Delete(dest); } catch { } }
                    File.Move(tmpPath, dest);
                    return false;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

                    string msg = ex.Message ?? "";

                    // ★ 404 + SkipIfNotFound → 视为"已跳过"，不重试、不报错
                    if (task.SkipIfNotFound &&
                        msg.IndexOf("404", StringComparison.Ordinal) >= 0)
                    {
                        Log($"[LegacyFabric] 404 跳过（natives-only 库）: {task.Name}");
                        return true;
                    }

                    Log($"[LegacyFabric] 下载失败({url}): {ex.Message}");
                    if (retry < RETRY_COUNT - 1) Thread.Sleep(500 * (retry + 1));
                }
            }

            throw new Exception($"下载 {dest} 失败: {lastEx?.Message}", lastEx);
        }

        private static void SingleDownload(string url, string dest, bool showProgress)
        {
            using (var resp = SendRequest(url, HTTP_TIMEOUT_MS, 60000))
            using (var netStream = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                           FileShare.None, BUFFER_SIZE))
            {
                long total = resp.ContentLength;
                long received = 0;
                byte[] buffer = new byte[BUFFER_SIZE];
                int read;

                while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fs.Write(buffer, 0, read);
                    received += read;
                }

                if (total > 0 && received != total)
                    throw new Exception($"下载不完整: {received}/{total}");
            }
        }

        private static string DownloadString(string url)
        {
            Exception lastEx = null;
            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                try
                {
                    using (var resp = SendRequest(url, HTTP_TIMEOUT_MS, HTTP_TIMEOUT_MS))
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (retry < RETRY_COUNT - 1) Thread.Sleep(500 * (retry + 1));
                }
            }
            throw new Exception($"下载 {url} 失败: {lastEx?.Message}", lastEx);
        }

        private static HttpWebResponse SendRequest(string url, int timeoutMs, int readWriteTimeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = readWriteTimeoutMs;
            req.AutomaticDecompression = DecompressionMethods.None;
            return (HttpWebResponse)req.GetResponse();
        }

        // ===================== 复制原版核心 =====================
        private static void CopyClientJar(string versionsDir, string originalVersion,
                                           string versionDir, string versionId)
        {
            string originalClientJar = Path.Combine(versionsDir, originalVersion,
                                                     originalVersion + ".jar");
            string fabricVersionJar = Path.Combine(versionDir, versionId + ".jar");

            if (!File.Exists(originalClientJar) || new FileInfo(originalClientJar).Length == 0)
            {
                Log($"[LegacyFabric] 警告: 未找到原版 client.jar({originalClientJar})，请先运行 Vanilla.InstallClient");
                return;
            }

            try
            {
                long srcLen = new FileInfo(originalClientJar).Length;
                if (File.Exists(fabricVersionJar) &&
                    new FileInfo(fabricVersionJar).Length == srcLen)
                {
                    Log($"[LegacyFabric] 版本核心已存在: {fabricVersionJar}");
                    return;
                }

                File.Copy(originalClientJar, fabricVersionJar, true);
                Log($"[LegacyFabric] 已复制原版核心到: {fabricVersionJar}");
            }
            catch (Exception ex)
            {
                Log($"[LegacyFabric] 复制原版核心失败(不致命): {ex.Message}");
            }
        }

        // ===================== 合并父版本 JSON =====================
        private static string MergeParentJson(string fabricJson, string minecraftDir, string versionId)
        {
            var fabricRoot = ParseJson(fabricJson);
            if (fabricRoot == null)
            {
                Log("[LegacyFabric] profile JSON 解析失败，跳过合并");
                return fabricJson;
            }

            // ★ 无论是否有 inheritsFrom，都强制把 id 改成我们的 versionId
            fabricRoot["id"] = versionId;

            if (!fabricRoot.ContainsKey("inheritsFrom"))
            {
                Log("[LegacyFabric] profile JSON 无 inheritsFrom，仅改写 id");
                return new JavaScriptSerializer().Serialize(fabricRoot);
            }

            string parentId = fabricRoot["inheritsFrom"].ToString();
            string parentPath = Path.Combine(minecraftDir, "versions", parentId, parentId + ".json");

            if (!File.Exists(parentPath))
            {
                Log($"[LegacyFabric] 警告: 未找到父版本 JSON({parentPath})，跳过合并");
                return new JavaScriptSerializer().Serialize(fabricRoot);
            }

            var parentRoot = ParseJson(File.ReadAllText(parentPath));
            if (parentRoot == null)
            {
                Log("[LegacyFabric] 父版本 JSON 解析失败，跳过合并");
                return new JavaScriptSerializer().Serialize(fabricRoot);
            }

            Log($"[LegacyFabric] 合并父版本 {parentId} 到 {versionId}");

            var merged = new Dictionary<string, object>();

            foreach (var kv in parentRoot)
                merged[kv.Key] = kv.Value;

            foreach (var kv in fabricRoot)
            {
                if (kv.Key == "inheritsFrom") continue;
                if (kv.Key == "libraries") continue;
                merged[kv.Key] = kv.Value;
            }

            // ★ 显式保证 id 和 jar
            merged["id"] = versionId;
            merged["jar"] = versionId;

            merged["libraries"] = MergeLibraries(fabricRoot, parentRoot);

            var mergedArgs = MergeArguments(fabricRoot, parentRoot);
            if (mergedArgs != null)
                merged["arguments"] = mergedArgs;

            try
            {
                string result = new JavaScriptSerializer().Serialize(merged);
                Log($"[LegacyFabric] 合并完成，JSON 长度: {result.Length}");
                return result;
            }
            catch (Exception ex)
            {
                Log($"[LegacyFabric] 序列化合并结果失败: {ex.Message}，使用原 JSON");
                return fabricJson;
            }
        }

        private static List<object> MergeLibraries(Dictionary<string, object> fabricRoot,
                                                    Dictionary<string, object> parentRoot)
        {
            var result = new List<object>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (fabricRoot.ContainsKey("libraries"))
            {
                var libs = fabricRoot["libraries"] as IList;
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
                var libs = parentRoot["libraries"] as IList;
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
                var l = parentArgs["jvm"] as IList;
                if (l != null) foreach (var item in l) jvmList.Add(item);
            }
            if (fabricArgs != null && fabricArgs.ContainsKey("jvm"))
            {
                var l = fabricArgs["jvm"] as IList;
                if (l != null) foreach (var item in l) jvmList.Add(item);
            }
            if (jvmList.Count > 0)
                mergedArgs["jvm"] = jvmList;

            var gameList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("game"))
            {
                var l = parentArgs["game"] as IList;
                if (l != null) foreach (var item in l) gameList.Add(item);
            }
            if (fabricArgs != null && fabricArgs.ContainsKey("game"))
            {
                var l = fabricArgs["game"] as IList;
                if (l != null) foreach (var item in l) gameList.Add(item);
            }
            if (gameList.Count > 0)
                mergedArgs["game"] = gameList;

            return mergedArgs.Count > 0 ? mergedArgs : null;
        }

        // ===================== 工具 =====================
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

        // ===================== 内部类 =====================
        private class DownloadTask
        {
            public string Url { get; set; }
            public string Dest { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public bool ShowProgress { get; set; }

            /// <summary>
            /// 若为 true，遇到 404 时视为"已跳过"而非失败。
            /// 用于 natives-only 库（同时有 artifact 和 classifiers，
            /// 但主 jar 在 Maven 上不存在）。
            /// </summary>
            public bool SkipIfNotFound { get; set; }
        }
    }
}