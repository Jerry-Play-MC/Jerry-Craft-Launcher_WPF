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
using System.Web.Script.Serialization;

namespace Install_Minecraft_Versions
{
    internal class Vanilla
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
        // ★ .tmp 保留时长：只有超过这个时长的陈旧 .tmp 才会被清理
        //   24 小时以内的 .tmp 保留，供跨会话断点续传
        private const int TMP_MAX_AGE_HOURS = 24;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // ===================== 可配置项 =====================
        // 全局静态属性，仅应在单线程调用 InstallServer 前设置。
        /// <summary>服务端最大堆内存（-Xmx），默认 2G</summary>
        public static string ServerMaxMemory { get; set; } = "2G";
        /// <summary>服务端初始堆内存（-Xms），默认 1G</summary>
        public static string ServerMinMemory { get; set; } = "1G";

        // ===================== 状态 =====================
        private static readonly object _consoleLock = new object();
        private static readonly Encoding UTF8NoBom = new UTF8Encoding(false);
        private static int _lastProgressTick = 0;
        // 单线程安装场景使用；多线程并发 ParseJsonObject 会互相覆盖
        private static Exception _lastJsonError;

        // 代理缓存
        private static IWebProxy _cachedProxy;
        private static bool _proxyInitialized;
        private static readonly object _proxyLock = new object();

        // ===================== 主入口（客户端） =====================
        public static void InstallClient(string version, string minecraftDir, string localManifestDir)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            Log($"[Vanilla] 开始安装 Minecraft {version} -> {minecraftDir}");

            CleanupTempFiles(minecraftDir);

            string manifestText = LoadManifestText(localManifestDir);
            string versionJsonUrl = FindVersionJsonUrl(manifestText, version);
            if (string.IsNullOrEmpty(versionJsonUrl))
                throw new Exception($"version_manifest.json 中未找到版本 {version}");
            Log($"[Vanilla] 找到版本条目: {versionJsonUrl}");

            string versionsDir = System.IO.Path.Combine(minecraftDir, "versions");
            string versionDir = System.IO.Path.Combine(versionsDir, version);
            string librariesDir = System.IO.Path.Combine(minecraftDir, "libraries");
            string assetsDir = System.IO.Path.Combine(minecraftDir, "assets");
            Directory.CreateDirectory(versionDir);
            Directory.CreateDirectory(librariesDir);
            Directory.CreateDirectory(assetsDir);

            string versionJsonPath = System.IO.Path.Combine(versionDir, version + ".json");
            string versionJsonSha1 = ExtractSha1FromUrl(versionJsonUrl);
            DownloadFile(versionJsonUrl, versionJsonPath, versionJsonSha1, "version.json",
                         showProgress: true, strictMode: true, out _);
            Log($"[Vanilla] 已保存版本 JSON: {versionJsonPath}");

            var versionRoot = ParseJsonObject(ReadAllTextShared(versionJsonPath));
            if (versionRoot == null)
                throw new Exception($"版本 JSON 解析失败: {versionJsonPath} ({_lastJsonError?.Message})");
            versionRoot["id"] = version;

            DownloadClientJar(versionRoot, versionDir, version);
            string assetIndexId = DownloadAssetIndex(versionRoot, assetsDir);
            DownloadLoggingConfig(versionRoot, assetsDir);

            var tasks = new List<DownloadTask>();
            CollectLibraryTasks(versionRoot, librariesDir, tasks);
            if (!string.IsNullOrEmpty(assetIndexId))
                CollectAssetTasks(assetsDir, assetIndexId, tasks);

            tasks = DeduplicateTasks(tasks);
            Log($"[Vanilla] 共收集 {tasks.Count} 个下载任务");

            if (tasks.Count > 0)
                ParallelDownload(tasks, MAX_CONCURRENCY);

            if (!string.IsNullOrEmpty(assetIndexId))
                BuildVirtualAssets(assetsDir, assetIndexId);

            Log($"[Vanilla] Minecraft {version} 安装完成");
        }

        // ===================== 主入口（服务端） =====================
        public static void InstallServer(string version, string serverDir, string localManifestDir)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(serverDir))
                throw new ArgumentException("服务端路径不能为空", "serverDir");

            Log($"[Vanilla] 开始安装 Minecraft 服务端 {version} -> {serverDir}");

            CleanupTempFiles(serverDir);

            string manifestText = LoadManifestText(localManifestDir);
            string versionJsonUrl = FindVersionJsonUrl(manifestText, version);
            if (string.IsNullOrEmpty(versionJsonUrl))
                throw new Exception($"version_manifest.json 中未找到版本 {version}");
            Log($"[Vanilla] 找到版本条目: {versionJsonUrl}");

            string tempJsonPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"mc_server_{version}_{Guid.NewGuid():N}.json");
            try
            {
                DownloadFile(versionJsonUrl, tempJsonPath, ExtractSha1FromUrl(versionJsonUrl),
                             "version.json", showProgress: false, strictMode: true, out _);

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

                Log($"[Vanilla] 下载服务端核心: {serverUrl}");
                DownloadFile(serverUrl, serverJarPath, serverSha1, "server.jar",
                             showProgress: true, strictMode: true, out _);
                Log($"[Vanilla] 服务端核心已下载: {serverJarPath}");
            }
            finally
            {
                try { if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath); } catch { }
            }

            Log($"[Vanilla] Minecraft 服务端 {version} 下载完成");
            Log("[Vanilla] 提示：首次启动时服务器会生成 eula.txt，需由用户接受 EULA");
        }

        // ===================== 代理（带缓存） =====================
        private static IWebProxy GetProxy()
        {
            lock (_proxyLock)
            {
                if (!_proxyInitialized)
                {
                    _cachedProxy = BuildProxyFromEnv();
                    _proxyInitialized = true;
                }
                return _cachedProxy;
            }
        }

        /// <summary>外部修改了环境变量后调用此方法，下次会重新读取</summary>
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
                catch { }

                return proxy;
            }
            catch { return null; }
        }

        // ===================== .tmp 清理（只清 24 小时前的陈旧文件） =====================
        /// <summary>
        /// 清理 24 小时前残留的 .tmp。
        /// 24 小时以内的 .tmp 保留，供跨会话断点续传。
        /// 只扫 versions / libraries / assets 和根目录一级，避免大资产库上慢。
        /// </summary>
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
                    Log($"[Vanilla] 清理 {TMP_MAX_AGE_HOURS} 小时前的残留 .tmp: {count} 个");
            }
            catch { }
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
                        if (File.GetLastWriteTimeUtc(f) < cutoff)
                        {
                            File.Delete(f);
                            count++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
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
                    Log($"[Vanilla] 使用本地 manifest: {localPath}");
                    return ReadAllTextShared(localPath);
                }
                Log($"[Vanilla] 本地 manifest 不存在({localPath})，改从网络获取");
            }

            Log($"[Vanilla] 获取官方 manifest: {OFFICIAL_MANIFEST}");
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
            Dictionary<string, object> versionRoot, string versionDir, string version)
        {
            if (!versionRoot.ContainsKey("downloads")) return;
            var downloads = versionRoot["downloads"] as Dictionary<string, object>;
            if (downloads == null || !downloads.ContainsKey("client")) return;

            var client = downloads["client"] as Dictionary<string, object>;
            string sha1 = client.ContainsKey("sha1") ? client["sha1"].ToString() : null;
            string url = client.ContainsKey("url") ? client["url"].ToString() : null;
            string dest = System.IO.Path.Combine(versionDir, version + ".jar");

            DownloadFile(url, dest, sha1, version + ".jar",
                         showProgress: true, strictMode: true, out _);
            Log($"[Vanilla] 客户端核心已下载: {dest}");
        }

        // ===================== assetIndex =====================
        private static string DownloadAssetIndex(
            Dictionary<string, object> versionRoot, string assetsDir)
        {
            if (!versionRoot.ContainsKey("assetIndex")) return null;
            var idx = versionRoot["assetIndex"] as Dictionary<string, object>;
            if (idx == null || !idx.ContainsKey("id")) return null;

            string id = idx["id"].ToString();
            string sha1 = idx.ContainsKey("sha1") ? idx["sha1"].ToString() : null;
            string url = idx.ContainsKey("url") ? idx["url"].ToString() : null;
            string dest = System.IO.Path.Combine(assetsDir, "indexes", id + ".json");

            DownloadFile(url, dest, sha1, id + ".json",
                         showProgress: true, strictMode: true, out _);
            Log($"[Vanilla] 资源索引已下载: {id}");
            return id;
        }

        // ===================== logging 配置 =====================
        private static void DownloadLoggingConfig(
            Dictionary<string, object> versionRoot, string assetsDir)
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
                DownloadFile(url, dest, sha1, id, showProgress: false, strictMode: true, out _);
                Log($"[Vanilla] 日志配置已下载: {id}");
            }
            catch (Exception ex)
            {
                Log($"[Vanilla] 日志配置下载失败(不致命): {ex.Message}");
            }
        }

        // ===================== libraries 收集 =====================
        private static void CollectLibraryTasks(
            Dictionary<string, object> versionRoot, string librariesDir, List<DownloadTask> tasks)
        {
            if (!versionRoot.ContainsKey("libraries")) return;
            var libraries = versionRoot["libraries"] as ArrayList;
            if (libraries == null) return;

            int added = 0;
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
                                string relPath = artifact["path"].ToString();
                                string sha1 = artifact.ContainsKey("sha1") ? artifact["sha1"].ToString() : null;
                                string url = artifact["url"].ToString();

                                tasks.Add(new DownloadTask
                                {
                                    Url = url,
                                    Dest = System.IO.Path.Combine(librariesDir, relPath),
                                    Sha1 = sha1,
                                    Name = relPath,
                                    Type = "libraries"
                                });
                                added++;
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
                                    string relPath = native["path"].ToString();
                                    string sha1 = native.ContainsKey("sha1") ? native["sha1"].ToString() : null;
                                    string url = native["url"].ToString();

                                    tasks.Add(new DownloadTask
                                    {
                                        Url = url,
                                        Dest = System.IO.Path.Combine(librariesDir, relPath),
                                        Sha1 = sha1,
                                        Name = relPath,
                                        Type = "libraries"
                                    });
                                    added++;
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
                        added++;
                    }
                }
            }
            Log($"[Vanilla] Libraries 任务: {added}");
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
            if (!string.IsNullOrEmpty(forcedExtension))
                ext = forcedExtension;

            string fileName = $"{artifact}-{version}";
            if (!string.IsNullOrEmpty(classifier))
                fileName += "-" + classifier;
            fileName += ext;

            return $"{group}/{artifact}/{version}/{fileName}";
        }

        /// <summary>
        /// 判断库在 Windows 上是否允许。
        /// 支持 os.name / os.arch / features（features 按精确语义匹配）。
        /// ★ 注意：本方法假设安装场景下所有 feature（is_demo_user、has_custom_resolution 等）
        ///   均为 false。若将来用于带自定义分辨率的启动流程，需要把 feature 状态作为参数传入。
        /// </summary>
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
                            string osName = os["name"].ToString();
                            if (osName != "windows") matches = false;
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

                // features 精确匹配：安装场景下所有 feature 均为 false
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
                Log($"[Vanilla] 未找到资源索引: {idxPath}");
                return;
            }

            var idxRoot = ParseJsonObject(ReadAllTextShared(idxPath));
            if (idxRoot == null || !idxRoot.ContainsKey("objects")) return;

            var objects = idxRoot["objects"] as Dictionary<string, object>;
            if (objects == null) return;

            string objectsDir = System.IO.Path.Combine(assetsDir, "objects");
            Directory.CreateDirectory(objectsDir);

            int count = 0;
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
                count++;
            }
            Log($"[Vanilla] Assets 任务: {count}");
        }

        private static void BuildVirtualAssets(string assetsDir, string assetIndexId)
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

            string targetDir;
            if (isVirtual)
                targetDir = System.IO.Path.Combine(assetsDir, "virtual", assetIndexId);
            else
                targetDir = System.IO.Path.Combine(assetsDir, "minecraft");

            Directory.CreateDirectory(targetDir);
            Log($"[Vanilla] 生成 assets 映射到: {targetDir}");

            string objectsDir = System.IO.Path.Combine(assetsDir, "objects");
            int copied = 0;
            foreach (var kv in objects)
            {
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
            Log($"[Vanilla] assets 映射完成: {copied} 个文件");
        }

        // ===================== 任务去重（保序） =====================
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

                if (seen.Add(key))
                {
                    byDest[key] = t;
                    result.Add(t);
                }
                else
                {
                    var existing = byDest[key];
                    bool eSha1Empty = string.IsNullOrEmpty(existing.Sha1);
                    bool tSha1Empty = string.IsNullOrEmpty(t.Sha1);

                    if (eSha1Empty && !tSha1Empty)
                    {
                        int idx = result.IndexOf(existing);
                        if (idx >= 0) result[idx] = t;
                        byDest[key] = t;
                    }
                    else if (!eSha1Empty && !tSha1Empty &&
                             !existing.Sha1.Equals(t.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"[Vanilla] 警告: 同路径 {t.Dest} 有两个不同 SHA1，保留第一个");
                    }
                }
            }

            if (result.Count != tasks.Count)
                Log($"[Vanilla] 去重: {tasks.Count} -> {result.Count}");
            return result;
        }

        // ===================== 并行下载 =====================
        private static void ParallelDownload(List<DownloadTask> tasks, int maxConcurrency)
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

            Log($"[Vanilla] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");

            Interlocked.Exchange(ref _lastProgressTick, 0);
            lock (_consoleLock)
            {
                PrintProgressLine(doneLib, totalLib, doneAsset, totalAsset,
                                  skipped, failed, completed, total);
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
                            bool wasSkipped;
                            DownloadFile(task.Url, task.Dest, task.Sha1, task.Name,
                                         showProgress: false, strictMode: false, out wasSkipped);

                            lock (statLock)
                            {
                                if (task.Type == "libraries") doneLib++;
                                else if (task.Type == "asset") doneAsset++;
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
                                if (failures.Count < 200)
                                    failures.Add($"{task.Name} <- {ex.Message}");
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
                                PrintProgressLine(doneLib, totalLib, doneAsset, totalAsset,
                                                  skipped, failed, completed, total);
                            }
                        }
                    }
                });
                worker.IsBackground = true;
                worker.Name = "Downloader-" + i;
                worker.Priority = ThreadPriority.Normal;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            lock (_consoleLock)
            {
                PrintProgressLine(doneLib, totalLib, doneAsset, totalAsset,
                                  skipped, failed, completed, total);
                Console.WriteLine();
                Console.WriteLine($"[Vanilla] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");

                if (failed > 0 && failures.Count > 0)
                {
                    Console.WriteLine($"[Vanilla] 失败清单（前 {Math.Min(failures.Count, MAX_FAILURE_PRINT)} 条）:");
                    int n = Math.Min(failures.Count, MAX_FAILURE_PRINT);
                    for (int i = 0; i < n; i++)
                        Console.WriteLine($"  - {failures[i]}");
                    if (failures.Count > MAX_FAILURE_PRINT)
                        Console.WriteLine($"  ... 还有 {failures.Count - MAX_FAILURE_PRINT} 条未显示");
                }
            }
        }

        private static void PrintProgressLine(
            int doneLib, int totalLib,
            int doneAsset, int totalAsset,
            int skipped, int failed,
            int completed, int total)
        {
            double pct = total == 0 ? 100 : (completed * 100.0 / total);

            Console.Write(
                $"\r[libraries] {doneLib}/{totalLib} | [asset] {doneAsset}/{totalAsset} | " +
                $"跳过 {skipped} | 失败 {failed} | 总进度 {pct:F1}%  ");
            try { Console.Out.Flush(); } catch { }
        }

        // ===================== 底层下载（严格/宽松 + .tmp 原子替换 + 断点续传） =====================
        private static void DownloadFile(
            string url, string dest, string sha1, string displayName,
            bool showProgress, bool strictMode, out bool outSkipped)
        {
            outSkipped = false;

            // ★ tmpPath 提前到函数开头，供前面严格模式清理使用
            string tmpPath = dest + ".tmp";

            if (File.Exists(dest))
            {
                if (strictMode)
                {
                    if (string.IsNullOrEmpty(sha1))
                    {
                        Log($"[Vanilla] 警告: {displayName} 严格模式但无 SHA1，降级为宽松模式");
                        try
                        {
                            if (new FileInfo(dest).Length > 0)
                            {
                                outSkipped = true;
                                return;
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        try
                        {
                            if (ComputeSha1(dest).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                            {
                                outSkipped = true;
                                return;
                            }
                            Log($"[Vanilla] {displayName} 校验失败，重新下载");
                            try { File.Delete(dest); } catch { }

                            // ★ 同时清掉可能存在的 .tmp，避免拿脏残留去续传
                            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        }
                        catch { }
                    }
                }
                else
                {
                    try
                    {
                        if (new FileInfo(dest).Length > 0)
                        {
                            outSkipped = true;
                            return;
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(url))
                throw new Exception($"下载 {displayName} 失败：URL 为空");

            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                try
                {
                    // ★ 不删除 .tmp：让 SingleStreamDownload 内部处理续传/从头
                    SingleStreamDownload(url, tmpPath, showProgress);

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
                                // 校验失败 → 删除损坏的 .tmp，下次重试从头
                                try { File.Delete(tmpPath); } catch { }
                                throw new Exception($"SHA1 校验失败: 期望 {sha1}, 实际 {actual}");
                            }
                        }
                    }

                    // ★ 原子替换：优先 File.Replace，失败回退 Move
                    MoveOrReplace(tmpPath, dest);
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    // ★ 不删 .tmp，下次重试继续续传
                    //   SHA1 失败已在上面删除；网络中断保留 .tmp
                    if (retry < RETRY_COUNT - 1)
                        Thread.Sleep(500 * (retry + 1));
                }
            }

            // 彻底失败：保留 .tmp，供下次安装/重试续传
            // （超过 TMP_MAX_AGE_HOURS 小时的会被 CleanupTempFiles 清理）
            throw new Exception(
                $"下载 {displayName} 失败（重试 {RETRY_COUNT} 次）: {lastEx?.Message}", lastEx);
        }

        /// <summary>
        /// 把 src 原子替换为 dst。
        /// 优先 File.Replace（原子、保留失败时的原文件）；
        /// 失败则回退到删除 + Move。
        /// </summary>
        private static void MoveOrReplace(string src, string dst)
        {
            if (File.Exists(dst))
            {
                try
                {
                    File.Replace(src, dst, null);
                    return;
                }
                catch (IOException) { }
                catch (PlatformNotSupportedException) { }
                // 回退
                try { File.Delete(dst); } catch { }
            }
            File.Move(src, dst);
        }

        // ===================== 单流下载（断点续传） =====================
        /// <summary>
        /// 单流下载，支持断点续传。
        /// 不预检 Accept-Ranges：直接带 Range 头发请求，让服务器用状态码回答。
        /// - 206 PartialContent → 续传
        /// - 200 OK           → 服务器忽略 Range，退化为从头
        /// - 416 RangeNotSatisfiable → .tmp 已完整或过期，删除后从头
        /// </summary>
        private static void SingleStreamDownload(string url, string dest, bool showProgress)
        {
            string dir = System.IO.Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

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
                // ★ 释放 416 响应，避免连接泄漏
                if (ex.Response != null)
                {
                    try { ex.Response.Close(); } catch { }
                }

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
                // 服务器忽略 Range 返回 200 → 从头
                if (existingLength > 0 && !isPartial)
                    existingLength = 0;

                long totalBytes;
                if (existingLength > 0 && isPartial)
                {
                    totalBytes = ParseContentRangeTotal(resp.Headers["Content-Range"]);
                    if (totalBytes <= 0) totalBytes = existingLength + resp.ContentLength;
                }
                else
                {
                    totalBytes = resp.ContentLength;
                }

                // 已经完整
                if (existingLength > 0 && totalBytes > 0 && existingLength == totalBytes)
                    return;

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
                        throw new Exception($"下载不完整: 收到 {received} 字节，期望 {totalBytes} 字节");
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

            if (existingLength > 0)
                req.AddRange(existingLength);

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

        private static void TryAddJavaCandidate(
            string exePath, List<KeyValuePair<int, string>> candidates, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            string full;
            try { full = System.IO.Path.GetFullPath(exePath); }
            catch { return; }

            if (seen.Contains(full)) return;
            seen.Add(full);

            int major = GetJavaMajorVersion(full);
            if (major > 0)
                candidates.Add(new KeyValuePair<int, string>(major, full));
        }

        private static int GetJavaMajorVersion(string javaExePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = javaExePath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);

                    var m = Regex.Match(output, @"version\s+""(\d+)(?:\.(\d+))?");
                    if (m.Success)
                    {
                        int major = int.Parse(m.Groups[1].Value);
                        if (major == 1 && m.Groups[2].Success)
                            major = int.Parse(m.Groups[2].Value);
                        return major;
                    }
                }
            }
            catch { }
            return 0;
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
                catch (IOException) when (i < maxRetry - 1)
                {
                    Thread.Sleep(100 * (i + 1));
                }
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
                    if (i < RETRY_COUNT - 1)
                        Thread.Sleep(500 * (i + 1));
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

        // ===================== 工具 =====================
        private static void Log(string msg)
        {
            lock (_consoleLock)
            {
                Console.Write("\r" + new string(' ', 140) + "\r");
                Console.WriteLine(msg);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "\n... (已截断)";
        }

        // ===================== 内部类 =====================
        private class DownloadTask
        {
            public string Url { get; set; }
            public string Dest { get; set; }
            public string Sha1 { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
        }

        private class AtomicFlag
        {
            private int _value;
            public bool TrySet() { return Interlocked.CompareExchange(ref _value, 1, 0) == 0; }
            public bool IsSet { get { return Volatile.Read(ref _value) != 0; } }
        }
    }
}