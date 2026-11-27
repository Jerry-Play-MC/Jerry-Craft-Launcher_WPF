using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    internal class InstallLabyMod
    {
        // ===================== 源 =====================
        // R2 (Cloudflare) 和 OVH (S3) 是两个独立 CDN，国内速度常不同 → 都试，取快的
        private const string HOST_R2 = "releases.r2.labymod.net";
        private const string HOST_OVH = "laby-releases.s3.de.io.cloud.ovh.net";

        private static readonly string[] MANIFEST_URLS = {
            "https://releases.r2.labymod.net/api/v1/manifest/production/latest.json",
            "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/manifest/production/latest.json",
        };

        private static readonly string[] LIBS_URL_TEMPLATES = {
            "https://releases.r2.labymod.net/api/v1/libraries/{channel}.json",
            "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/libraries/{channel}.json",
        };

        private static readonly string[] CORE_JAR_TEMPLATES = {
            "https://releases.r2.labymod.net/api/v1/download/labymod4/{channel}/{commit}.jar",
            "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/download/labymod4/{channel}/{commit}.jar",
        };

        private static readonly string[] ASSET_TEMPLATES = {
            "https://releases.r2.labymod.net/api/v1/download/assets/labymod4/{channel}/{commit}/{name}/{hash}.jar",
            "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/download/assets/labymod4/{channel}/{commit}/{name}/{hash}.jar",
        };

        private const string DEFAULT_CHANNEL = "production";

        // ===================== 下载参数 =====================
        // ★ LabyMod 专用：156 个文件，源只有 2 个 CDN → 24 并发会让国内单连接互相挤兑
        //   8 并发稳定，总时间几乎不降，进度更平滑
        private const int MAX_CONCURRENCY = 8;
        private const int RETRY_COUNT = 3;
        private const int BUFFER_SIZE = 256 * 1024;
        private const int PROGRESS_REFRESH_MS = 100;
        private const int HTTP_TIMEOUT_MS = 20000;   // 15~20 秒足够，太快误杀慢源，太慢用户不耐烦

        private const string UA_STRING = "Mozilla/5.0";

        private static readonly object _consoleLock = new object();
        private static int _lastProgressTick = 0;

        // ===================== 统一异常日志 =====================
        private static void LogEx(string context, Exception ex, LogLevel level = LogLevel.Error)
        {
            recordLog(level, context + " -> " + (ex == null ? "null" : ex.ToString()));
        }

        // ===================== 公开入口 =====================
        public static string InstallClient(string version, string minecraftDir, string loaderParam)
        {
            return InstallClient(version, minecraftDir, loaderParam, DEFAULT_CHANNEL);
        }

        public static string InstallClient(string version, string minecraftDir, string loaderParam, string channel,
    TaskGroup group = null, CancellationToken ct = default(CancellationToken), InstallStages stages = null)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");
            if (string.IsNullOrEmpty(channel))
                channel = DEFAULT_CHANNEL;

            // fallback：老调用没传 stages 但有 group
            if (stages == null && group != null)
            {
                stages = new InstallStages
                {
                    LabyMeta = group.AddStage("Fetch LabyMod manifest", 5),
                    LabyCore = group.AddStage("Download LabyMod core", 30),
                    LabyLibraries = group.AddStage("Download LabyMod libraries", 40),
                    LabyAssets = group.AddStage("Download LabyMod assets", 20),
                };
            }

            ct.ThrowIfCancellationRequested();
            minecraftDir = Path.GetFullPath(minecraftDir);
            recordLog(LogLevel.Info, $"[LabyMod] 开始安装客户端 LabyMod {version}（channel={channel}）");

            // ═══ 阶段 1：获取元数据 ═══
            if (stages?.LabyMeta != null)
                group?.UpdateStage(stages.LabyMeta, 0, "Fetching manifest");

            recordLog(LogLevel.Info, $"[LabyMod] 步骤 1/15: 拉取 manifest（多源并行）");
            string manifestJson = DownloadStringMulti(MANIFEST_URLS, "manifest", ct);
            var manifest = ParseJson(manifestJson);
            if (manifest == null) throw new Exception("[LabyMod] manifest 解析失败");

            string labyModVersion = GetStr(manifest, "labyModVersion");
            string commitRef = GetStr(manifest, "commitReference");
            string coreJarSha1 = GetStr(manifest, "sha1");
            string coreJarSizeStr = GetStr(manifest, "size");
            long coreJarSize = 0;
            if (!string.IsNullOrEmpty(coreJarSizeStr)) long.TryParse(coreJarSizeStr, out coreJarSize);
            if (string.IsNullOrEmpty(commitRef)) throw new Exception("[LabyMod] manifest 缺少 commitReference");

            recordLog(LogLevel.Info, $"[LabyMod] LabyMod 版本: {labyModVersion}, commit: {commitRef}");

            string mcTag = null;
            string customManifestUrl = null;
            var mcVersions = manifest.ContainsKey("minecraftVersions") ? manifest["minecraftVersions"] as ArrayList : null;
            if (mcVersions != null)
            {
                foreach (var item in mcVersions)
                {
                    var mcv = item as Dictionary<string, object>;
                    if (mcv == null) continue;
                    string tag = GetStr(mcv, "tag");
                    string ver = GetStr(mcv, "version");
                    if (string.Equals(tag, version, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ver, version, StringComparison.OrdinalIgnoreCase))
                    {
                        mcTag = string.IsNullOrEmpty(tag) ? ver : tag;
                        customManifestUrl = GetStr(mcv, "customManifestUrl");
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(mcTag) || string.IsNullOrEmpty(customManifestUrl))
                throw new Exception($"LabyMod 不支持 Minecraft {version}");

            recordLog(LogLevel.Info, $"[LabyMod] 步骤 3/15: 下载基础 JSON（多源并行）");
            string baseJson = DownloadStringMulti(DeriveMirrorUrls(customManifestUrl), "基础 JSON", ct);
            var versionRoot = ParseJson(baseJson);
            if (versionRoot == null) throw new Exception("[LabyMod] 基础版本 JSON 解析失败");

            recordLog(LogLevel.Info, $"[LabyMod] 步骤 5/15: 下载完整依赖清单（多源并行）");
            try
            {
                string libText = DownloadStringMulti(LIBS_URL_TEMPLATES.Select(t => t.Replace("{channel}", channel)).ToArray(), "完整依赖清单", ct);
                var libRoot = ParseJson(libText);
                if (libRoot != null)
                {
                    var isolatedSet = CollectIsolatedLibraries(libRoot, mcTag);
                    FilterIsolatedFromBase(versionRoot, isolatedSet);
                    AppendFullLibraries(versionRoot, libRoot);
                    AppendLabyModCoreJar(versionRoot, channel, commitRef, coreJarSha1, coreJarSize);
                    versionRoot["labymod_data"] = new Dictionary<string, object>
            {
                { "channelType", channel }, { "commitReference", commitRef },
                { "version", labyModVersion ?? "" }, { "versionType", "release" }
            };
                }
            }
            catch (Exception ex)
            {
                LogEx("[LabyMod] ⚠ 完整依赖清单处理失败，仅使用基础 JSON", ex, LogLevel.Warn);
            }

            string shortCommit = commitRef.Length > 8 ? commitRef.Substring(0, 8) : commitRef;
            string versionId = $"LabyMod-4-{version}-{shortCommit}";
            versionRoot["id"] = versionId;
            versionRoot["jar"] = versionId;

            if (versionRoot.ContainsKey("downloads"))
            {
                var dl = versionRoot["downloads"] as Dictionary<string, object>;
                dl?.Remove("client");
                dl?.Remove("client_mappings");
            }
            EnsureLabyModLibraries(versionRoot);

            string versionsDir = Path.Combine(minecraftDir, "versions");
            string versionDir = Path.Combine(versionsDir, versionId);
            string librariesDir = Path.Combine(minecraftDir, "libraries");
            string assetsDir = Path.Combine(minecraftDir, "assets");
            string labyModNeoDir = Path.Combine(minecraftDir, "labymod-neo");

            Directory.CreateDirectory(versionDir);
            Directory.CreateDirectory(librariesDir);
            Directory.CreateDirectory(assetsDir);
            Directory.CreateDirectory(labyModNeoDir);
            Directory.CreateDirectory(Path.Combine(labyModNeoDir, "assets"));

            group?.CompleteStage(stages?.LabyMeta);

            // ═══ 阶段 2：构建下载任务并拆分 ═══
            var allTasks = new List<DownloadTask>();

            // Core Jar
            string coreJarDest = Path.Combine(versionDir, versionId + ".jar");
            string[] coreJarUrls = CORE_JAR_TEMPLATES.Select(t => t.Replace("{channel}", channel).Replace("{commit}", commitRef)).ToArray();
            allTasks.Add(new DownloadTask { Urls = coreJarUrls, Dest = coreJarDest, Sha1 = coreJarSha1, Name = versionId + ".jar", Type = "labymod-core" });

            // AssetIndex & Logging
            if (versionRoot.ContainsKey("assetIndex"))
            {
                var ai = versionRoot["assetIndex"] as Dictionary<string, object>;
                string aiUrl = GetStr(ai, "url");
                if (!string.IsNullOrEmpty(aiUrl))
                    allTasks.Add(new DownloadTask { Urls = DeriveMirrorUrls(aiUrl), Dest = Path.Combine(assetsDir, "indexes", GetStr(ai, "id") + ".json"), Sha1 = GetStr(ai, "sha1"), Name = GetStr(ai, "id") + ".json", Type = "asset-index" });
            }
            if (versionRoot.ContainsKey("logging"))
            {
                var logging = versionRoot["logging"] as Dictionary<string, object>;
                var logClient = logging != null && logging.ContainsKey("client") ? logging["client"] as Dictionary<string, object> : null;
                var logFile = logClient != null && logClient.ContainsKey("file") ? logClient["file"] as Dictionary<string, object> : null;
                if (logFile != null && logFile.ContainsKey("id"))
                {
                    string logUrl = GetStr(logFile, "url");
                    if (!string.IsNullOrEmpty(logUrl))
                        allTasks.Add(new DownloadTask { Urls = DeriveMirrorUrls(logUrl), Dest = Path.Combine(assetsDir, "log_configs", GetStr(logFile, "id")), Sha1 = GetStr(logFile, "sha1"), Name = GetStr(logFile, "id"), Type = "logging" });
                }
            }

            // Libraries
            CollectLibraryTasks(versionRoot, librariesDir, allTasks);

            // Assets
            if (manifest.ContainsKey("assets"))
            {
                var assets = manifest["assets"] as Dictionary<string, object>;
                if (assets != null)
                {
                    foreach (var kv in assets)
                    {
                        string name = kv.Key;
                        string hash = Convert.ToString(kv.Value);
                        if (string.IsNullOrEmpty(hash)) continue;
                        string[] urls = ASSET_TEMPLATES.Select(t => t.Replace("{channel}", channel).Replace("{commit}", commitRef).Replace("{name}", name).Replace("{hash}", hash)).ToArray();
                        allTasks.Add(new DownloadTask { Urls = urls, Dest = Path.Combine(labyModNeoDir, "assets", name + ".jar"), Name = name + ".jar", Type = "labymod-asset" });
                    }
                }
            }

            // 保存 Version JSON
            string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(versionJsonPath, new JavaScriptSerializer().Serialize(versionRoot), new UTF8Encoding(false));
            recordLog(LogLevel.Info, $"[LabyMod] 步骤 15/15: 版本 JSON 已保存: {versionJsonPath}");

            // ═══ 阶段 3：并行下载 ═══
            var coreTasks = allTasks.Where(t => t.Type == "labymod-core").ToList();
            var libTasks = allTasks.Where(t => t.Type == "libraries").ToList();
            var assetTasks = allTasks.Where(t => t.Type != "labymod-core" && t.Type != "libraries").ToList();

            Exception ex1 = null, ex2 = null, ex3 = null;
            var t1 = new Thread(() => { try { ParallelDownload(coreTasks, MAX_CONCURRENCY, group, stages?.LabyCore, ct); } catch (Exception ex) { ex1 = ex; } });
            var t2 = new Thread(() => { try { ParallelDownload(libTasks, MAX_CONCURRENCY, group, stages?.LabyLibraries, ct); } catch (Exception ex) { ex2 = ex; } });
            var t3 = new Thread(() => { try { ParallelDownload(assetTasks, MAX_CONCURRENCY, group, stages?.LabyAssets, ct); } catch (Exception ex) { ex3 = ex; } });

            t1.IsBackground = true; t2.IsBackground = true; t3.IsBackground = true;
            t1.Start(); t2.Start(); t3.Start();
            t1.Join(); t2.Join(); t3.Join();

            if (ex1 != null) throw ex1;
            if (ex2 != null) throw ex2;
            if (ex3 != null) throw ex3;

            recordLog(LogLevel.Info, $"[LabyMod] 客户端 LabyMod {version} 安装完成");
            return versionId;
        }

        // ===================== 派生镜像 URL =====================
        /// <summary>
        /// 如果 URL 里含 R2 或 OVH 域名，派生另一个域名的等价 URL。
        /// 两个 CDN 内容完全一样，路径结构一致，可直接替换域名。
        /// </summary>
        private static string[] DeriveMirrorUrls(string url)
        {
            if (string.IsNullOrEmpty(url))
                return new string[] { url };

            var list = new List<string> { url };

            if (url.IndexOf(HOST_R2, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                list.Add(url.Replace(HOST_R2, HOST_OVH));
            }
            else if (url.IndexOf(HOST_OVH, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                list.Add(url.Replace(HOST_OVH, HOST_R2));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return list.Where(u => !string.IsNullOrEmpty(u) && seen.Add(u)).ToArray();
        }

        // ===================== 多源并行下载字符串 =====================
        /// <summary>
        /// 多源并行探测：同时向所有源发请求，第一个成功的胜出，其它源主动放弃。
        /// 内部带 RETRY_COUNT 轮重试。
        /// </summary>
        private static string DownloadStringMulti(string[] urls, string logTag, CancellationToken ct)
        {
            if (urls == null || urls.Length == 0) throw new ArgumentException("urls 不能为空");
            if (urls.Length == 1) return DownloadStringWithRetry(urls[0], logTag, ct);

            Exception lastEx = null;
            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (retry > 0)
                    {
                        if (ct.WaitHandle.WaitOne(1000 * retry)) ct.ThrowIfCancellationRequested();
                    }
                    return DownloadStringMultiOnce(urls, logTag, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastEx = ex; }
            }
            throw new Exception($"[LabyMod] {logTag} 最终失败: {lastEx?.Message}", lastEx);
        }

        private static string DownloadStringMultiOnce(string[] urls, string logTag, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>();
            var cts = new CancellationTokenSource();
            int remaining = urls.Length;
            object remainingLock = new object();

            // 将外部的取消信号与内部的 cts 关联
            var reg = ct.Register(() => { try { cts.Cancel(); } catch { } tcs.TrySetCanceled(); });

            foreach (var url in urls)
            {
                var u = url;
                Task.Run(() =>
                {
                    string content = null;
                    try { content = DownloadStringSingle(u, cts.Token); }
                    catch (OperationCanceledException) { }
                    catch { }

                    if (content != null)
                    {
                        if (tcs.TrySetResult(content)) { try { cts.Cancel(); } catch { } }
                    }
                    else
                    {
                        bool allDone;
                        lock (remainingLock) { allDone = (--remaining == 0); }
                        if (allDone && !tcs.Task.IsCompleted) tcs.TrySetException(new Exception("所有源都失败"));
                    }
                });
            }

            try { return tcs.Task.Result; }
            finally { reg.Dispose(); cts.Dispose(); }
        }

        private static string DownloadStringWithRetry(string url, string logTag, CancellationToken ct)
        {
            Exception lastEx = null;
            for (int i = 0; i < RETRY_COUNT; i++)
            {
                ct.ThrowIfCancellationRequested();
                try { return DownloadStringSingle(url, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastEx = ex; }
                if (i < RETRY_COUNT - 1) { if (ct.WaitHandle.WaitOne(1000 * (i + 1))) ct.ThrowIfCancellationRequested(); }
            }
            throw new Exception($"失败: {url}: {lastEx?.Message}", lastEx);
        }

        /// <summary>
        /// 一次并行探测：谁先成功用谁的结果，成功后 cancel 其它源。
        /// </summary>
        private static string DownloadStringMultiOnce(string[] urls, string logTag)
        {
            var tcs = new TaskCompletionSource<string>();
            var cts = new CancellationTokenSource();
            int remaining = urls.Length;
            object remainingLock = new object();

            foreach (var url in urls)
            {
                var u = url;
                Task.Run(() =>
                {
                    string content = null;
                    try
                    {
                        content = DownloadStringSingle(u, cts.Token);
                    }
                    catch (OperationCanceledException) { /* 被其它源抢先，正常 */ }
                    catch (Exception ex)
                    {
                        LogEx($"[LabyMod] 源失败: {GetHostSafe(u)}", ex, LogLevel.Info);
                    }

                    if (content != null)
                    {
                        if (tcs.TrySetResult(content))
                        {
                            recordLog(LogLevel.Info, $"[LabyMod] {logTag} 胜出源: {GetHostSafe(u)}");
                            cts.Cancel();
                        }
                    }
                    else
                    {
                        bool allDone;
                        lock (remainingLock) { allDone = (--remaining == 0); }
                        if (allDone && !tcs.Task.IsCompleted)
                            tcs.TrySetException(new Exception(
                                $"{logTag} 所有源都失败: {string.Join(", ", urls.Select(GetHostSafe))}"));
                    }
                });
            }

            // 阻塞等待第一个成功或全部失败
            return tcs.Task.Result;
        }

        private static string DownloadStringWithRetry(string url, string logTag)
        {
            Exception lastEx = null;
            for (int i = 0; i < RETRY_COUNT; i++)
            {
                try
                {
                    return DownloadStringSingle(url, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    LogEx($"[LabyMod] {logTag} 请求失败（第 {i + 1}/{RETRY_COUNT} 次）: {GetHostSafe(url)}", ex, LogLevel.Warn);
                    if (i < RETRY_COUNT - 1) Thread.Sleep(1000 * (i + 1));
                }
            }
            throw new Exception($"[LabyMod] {logTag} 失败: {url}: {lastEx?.Message}", lastEx);
        }

        /// <summary>
        /// 单源下载字符串。支持 CancellationToken（token.Cancel 时 Abort 请求）。
        /// ★ 关键：ReadWriteTimeout = HTTP_TIMEOUT_MS，否则 Body 卡住阻塞默认 5 分钟。
        /// </summary>
        private static string DownloadStringSingle(string url, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("url 为空");

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.Timeout = HTTP_TIMEOUT_MS;
            req.ReadWriteTimeout = HTTP_TIMEOUT_MS;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;

            // 注册 abort，让其它源抢先时本请求立刻停止
            token.Register(() => { try { req.Abort(); } catch { } });

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException();

                    // 分块读，每块检查 token
                    var sb = new StringBuilder();
                    char[] buf = new char[8192];
                    int read;
                    while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (token.IsCancellationRequested)
                            throw new OperationCanceledException();
                        sb.Append(buf, 0, read);
                    }
                    return sb.ToString();
                }
            }
            catch (WebException wex)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException();
                throw new Exception($"HTTP 失败: {url}: {wex.Message}", wex);
            }
        }

        // ===================== 隔离列表处理 =====================
        private static HashSet<string> CollectIsolatedLibraries(
            Dictionary<string, object> libRoot, string mcTag)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var isolated = libRoot.ContainsKey("isolated_libraries")
                ? libRoot["isolated_libraries"] as ArrayList : null;
            if (isolated == null) return result;

            foreach (var item in isolated)
            {
                var iso = item as Dictionary<string, object>;
                if (iso == null) continue;
                string isoName = GetStr(iso, "name");
                if (string.IsNullOrEmpty(isoName)) continue;

                var versions = iso.ContainsKey("versions") ? iso["versions"] as ArrayList : null;
                if (versions == null) continue;

                bool hit = false;
                foreach (var v in versions)
                {
                    if (string.Equals(Convert.ToString(v), mcTag, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit) result.Add(isoName);
            }
            return result;
        }

        private static void FilterIsolatedFromBase(
            Dictionary<string, object> versionRoot, HashSet<string> isolatedSet)
        {
            if (isolatedSet == null || isolatedSet.Count == 0) return;

            var libs = versionRoot.ContainsKey("libraries")
                ? versionRoot["libraries"] as ArrayList : null;
            if (libs == null) return;

            int before = libs.Count;
            var kept = new ArrayList();
            foreach (var l in libs)
            {
                var lib = l as Dictionary<string, object>;
                if (lib == null)
                {
                    kept.Add(l);
                    continue;
                }
                string name = GetStr(lib, "name");
                if (!ShouldIsolate(name, isolatedSet))
                    kept.Add(l);
            }
            versionRoot["libraries"] = kept;
            recordLog(LogLevel.Info, $"[LabyMod] 隔离过滤: {before} -> {kept.Count}");
        }

        private static bool ShouldIsolate(string libName, HashSet<string> isolatedSet)
        {
            if (string.IsNullOrEmpty(libName)) return false;
            if (isolatedSet.Contains(libName)) return true;

            var parts = libName.Split(':');
            if (parts.Length >= 2)
            {
                string artifact = parts[1];
                if (isolatedSet.Contains(artifact)) return true;
                string combined = parts[0] + ":" + artifact;
                if (isolatedSet.Contains(combined)) return true;
            }

            foreach (var iso in isolatedSet)
            {
                if (libName.EndsWith(":" + iso, StringComparison.OrdinalIgnoreCase) ||
                    libName.EndsWith(":" + iso + ":", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ===================== 追加完整依赖 =====================
        private static int AppendFullLibraries(
            Dictionary<string, object> versionRoot, Dictionary<string, object> libRoot)
        {
            var prodLibs = libRoot.ContainsKey("libraries")
                ? libRoot["libraries"] as ArrayList : null;
            if (prodLibs == null) return 0;

            var merged = versionRoot.ContainsKey("libraries")
                ? versionRoot["libraries"] as ArrayList : null;
            if (merged == null)
            {
                merged = new ArrayList();
                versionRoot["libraries"] = merged;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in merged)
            {
                var d = l as Dictionary<string, object>;
                if (d != null && d.ContainsKey("name"))
                    seen.Add(Convert.ToString(d["name"]));
            }

            int added = 0;
            foreach (var item in prodLibs)
            {
                var lib = item as Dictionary<string, object>;
                if (lib == null) continue;
                string name = GetStr(lib, "name");
                string url = GetStr(lib, "url");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                if (seen.Contains(name)) continue;

                string path = ExtractArtifactPath(url);
                if (string.IsNullOrEmpty(path)) continue;

                var newLib = new Dictionary<string, object>
                {
                    { "name", name },
                    { "downloads", new Dictionary<string, object>
                        {
                            { "artifact", new Dictionary<string, object>
                                {
                                    { "path", path },
                                    { "sha1", GetStr(lib, "sha1") ?? "" },
                                    { "size", lib.ContainsKey("size") ? lib["size"] : (object)0 },
                                    { "url", url }
                                }
                            }
                        }
                    }
                };

                merged.Add(newLib);
                seen.Add(name);
                added++;
            }
            return added;
        }

        private static string ExtractArtifactPath(string url)
        {
            const string marker = "/libraries/";
            int idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return url.Substring(idx + marker.Length);
        }

        // ===================== 追加 LabyMod 主 jar =====================
        private static void AppendLabyModCoreJar(
            Dictionary<string, object> versionRoot,
            string channel, string commitRef,
            string coreJarSha1, long coreJarSize)
        {
            var merged = versionRoot.ContainsKey("libraries")
                ? versionRoot["libraries"] as ArrayList : null;
            if (merged == null)
            {
                merged = new ArrayList();
                versionRoot["libraries"] = merged;
            }

            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var d = merged[i] as Dictionary<string, object>;
                if (d != null && string.Equals(
                        Convert.ToString(d.ContainsKey("name") ? d["name"] : null),
                        "net.labymod:LabyMod:4", StringComparison.OrdinalIgnoreCase))
                {
                    merged.RemoveAt(i);
                }
            }

            string jarUrl = $"https://{HOST_R2}/api/v1/download/labymod4/{channel}/{commitRef}.jar";

            merged.Add(new Dictionary<string, object>
            {
                { "name", "net.labymod:LabyMod:4" },
                { "downloads", new Dictionary<string, object>
                    {
                        { "artifact", new Dictionary<string, object>
                            {
                                { "path", "net/labymod/LabyMod/4/LabyMod-4.jar" },
                                { "sha1", coreJarSha1 ?? "" },
                                { "size", (object)coreJarSize },
                                { "url", jarUrl }
                            }
                        }
                    }
                }
            });
        }

        // ===================== 兜底注入 =====================
        private static void EnsureLabyModLibraries(Dictionary<string, object> versionRoot)
        {
            var libs = versionRoot.ContainsKey("libraries")
                ? versionRoot["libraries"] as ArrayList : null;
            if (libs == null)
            {
                libs = new ArrayList();
                versionRoot["libraries"] = libs;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var libObj in libs)
            {
                var d = libObj as Dictionary<string, object>;
                if (d != null && d.ContainsKey("name"))
                    seen.Add(Convert.ToString(d["name"]));
            }

            var required = new[]
            {
                new
                {
                    Name = "net.minecraft:launchwrapper:4.1.3",
                    Url  = $"https://{HOST_R2}/libraries/"
                },
            };

            foreach (var r in required)
            {
                if (seen.Contains(r.Name)) continue;
                var libObj = new Dictionary<string, object>
                {
                    { "name", r.Name },
                    { "url", r.Url }
                };
                libs.Add(libObj);
                recordLog(LogLevel.Info, $"[LabyMod] 注入兜底库: {r.Name}");
            }

            if (libs.Count < 10)
            {
                recordLog(LogLevel.Warn,
                    $"[LabyMod] ⚠ 库总数仅 {libs.Count} 个，远少于正常值（几百个）。\n" +
                    "  可能原因：完整依赖清单合并失败。启动时可能报 ClassNotFoundException。");
            }
        }

        // ===================== libraries 收集 =====================
        private static void CollectLibraryTasks(
            Dictionary<string, object> root, string librariesDir, List<DownloadTask> tasks)
        {
            var libs = root.ContainsKey("libraries") ? root["libraries"] as ArrayList : null;
            if (libs == null) return;

            string os = GetOsName();
            string arch = GetHostArch();

            foreach (var libObj in libs)
            {
                var lib = libObj as Dictionary<string, object>;
                if (lib == null) continue;
                if (!IsLibraryAllowed(lib, "client", os, arch)) continue;

                string url = null;
                string relPath = null;
                string sha1 = null;

                if (lib.ContainsKey("downloads"))
                {
                    var dl = lib["downloads"] as Dictionary<string, object>;

                    if (dl != null && dl.ContainsKey("artifact"))
                    {
                        var art = dl["artifact"] as Dictionary<string, object>;
                        if (art != null && art.ContainsKey("url") && art.ContainsKey("path"))
                        {
                            url = GetStr(art, "url");
                            relPath = GetStr(art, "path");
                            sha1 = GetStr(art, "sha1");
                        }
                    }

                    if (dl != null && dl.ContainsKey("classifiers"))
                    {
                        var cls = dl["classifiers"] as Dictionary<string, object>;
                        if (cls != null && cls.ContainsKey("natives-windows"))
                        {
                            var nat = cls["natives-windows"] as Dictionary<string, object>;
                            if (nat != null && nat.ContainsKey("url") && nat.ContainsKey("path"))
                            {
                                string natUrl = GetStr(nat, "url");
                                if (!string.IsNullOrEmpty(natUrl))
                                {
                                    tasks.Add(new DownloadTask
                                    {
                                        Urls = DeriveMirrorUrls(natUrl),
                                        Dest = Path.Combine(librariesDir, GetStr(nat, "path")),
                                        Sha1 = GetStr(nat, "sha1"),
                                        Name = GetStr(nat, "path"),
                                        Type = "libraries"
                                    });
                                }
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(relPath))
                {
                    tasks.Add(new DownloadTask
                    {
                        Urls = DeriveMirrorUrls(url),
                        Dest = Path.Combine(librariesDir, relPath),
                        Sha1 = sha1,
                        Name = relPath,
                        Type = "libraries"
                    });
                }
                else if (lib.ContainsKey("name"))
                {
                    string name = Convert.ToString(lib["name"]);
                    string baseUrl = lib.ContainsKey("url") ? Convert.ToString(lib["url"]) : null;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(baseUrl))
                    {
                        if (!baseUrl.EndsWith("/")) baseUrl += "/";
                        string rel = MavenNameToPath(name);
                        if (!string.IsNullOrEmpty(rel))
                        {
                            tasks.Add(new DownloadTask
                            {
                                Urls = DeriveMirrorUrls(baseUrl + rel),
                                Dest = Path.Combine(librariesDir, rel),
                                Sha1 = null,
                                Name = rel,
                                Type = "libraries"
                            });
                        }
                    }
                    else
                    {
                        recordLog(LogLevel.Warn, $"[LabyMod] 库无 url 字段，无法下载: {name}");
                    }
                }
            }
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

            if (!string.IsNullOrEmpty(version) && version.Contains("@"))
            {
                int at = version.IndexOf('@');
                ext = "." + version.Substring(at + 1);
                version = version.Substring(0, at);
            }

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
        private static void ParallelDownload(List<DownloadTask> tasks, int maxConcurrency,
    TaskGroup group = null, SubTaskItem stage = null, CancellationToken ct = default(CancellationToken))
        {
            if (tasks.Count == 0)
            {
                group?.CompleteStage(stage);
                return;
            }

            int failed = 0, skipped = 0, completed = 0;
            int total = tasks.Count;
            int nextIndex = 0;
            object queueLock = new object();
            object statLock = new object();
            var failures = new List<string>();

            recordLog(LogLevel.Info, $"[LabyMod] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");

            if (stage != null) stage.State = TaskState.Running;

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
                            bool wasSkipped = DownloadFile(task, ct);
                            int n;
                            lock (statLock)
                            {
                                if (wasSkipped) skipped++;
                                n = ++completed;
                            }

                            if (stage != null && (n % 5 == 0 || n == total))
                                group?.UpdateStage(stage, (double)n / total, $"{n}/{total}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lock (statLock)
                            {
                                failed++;
                                completed++;
                                if (failures.Count < 50) failures.Add($"{task.Name} <- {ex.Message}");
                            }
                            LogEx($"[LabyMod] 下载失败: {task.Name}", ex);
                        }
                    }
                });
                worker.IsBackground = true;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();
            ct.ThrowIfCancellationRequested();

            if (failed > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"[LabyMod] 有 {failed}/{total} 个文件下载失败");
                int n = Math.Min(failures.Count, 20);
                if (n > 0)
                {
                    sb.Append("\n失败清单（前 ").Append(n).Append(" 条）:");
                    for (int i = 0; i < n; i++) sb.Append("\n  - ").Append(failures[i]);
                }
                group?.FailStage(stage, $"{failed} failed");
                throw new Exception(sb.ToString());
            }

            group?.CompleteStage(stage);
        }

        // ===================== 单文件下载（多源 + 断点续传 + SHA1） =====================
        private static bool DownloadFile(DownloadTask task, CancellationToken ct = default(CancellationToken))
        {
            if (File.Exists(task.Dest) && new FileInfo(task.Dest).Length > 0)
            {
                if (!string.IsNullOrEmpty(task.Sha1))
                {
                    try
                    {
                        string existingSha1 = ComputeSha1(task.Dest);
                        if (existingSha1.Equals(task.Sha1, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
                else return true;
            }

            string dir = Path.GetDirectoryName(task.Dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = task.Dest + ".tmp";
            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                ct.ThrowIfCancellationRequested();

                foreach (string url in task.Urls)
                {
                    try
                    {
                        SingleStreamDownloadWithResume(url, tmpPath, ct);

                        if (!string.IsNullOrEmpty(task.Sha1))
                        {
                            string actual = ComputeSha1(tmpPath);
                            if (!actual.Equals(task.Sha1, StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Delete(tmpPath); } catch { }
                                throw new Exception($"SHA1 校验失败: 期望 {task.Sha1}, 实际 {actual}");
                            }
                        }

                        MoveOrReplace(tmpPath, task.Dest);
                        return false;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        LogEx($"[LabyMod] 下载失败({GetHostSafe(url)})（第 {retry + 1}/{RETRY_COUNT} 次）", ex, LogLevel.Warn);
                    }
                }
                if (retry < RETRY_COUNT - 1)
                {
                    if (ct.WaitHandle.WaitOne(1000 * (retry + 1)))
                        ct.ThrowIfCancellationRequested();
                }
            }

            throw new Exception($"下载 {task.Name} 失败: {lastEx?.Message}", lastEx);
        }

        // ===================== 断点续传核心 =====================
        /// <summary>
        /// 单流下载，支持断点续传。
        /// - .tmp 不存在：从头下
        /// - .tmp 存在：发 Range 请求
        ///   · 服务器返回 206 → 从断点 Append
        ///   · 服务器返回 200 → 忽略 Range，从头覆盖
        ///   · 服务器返回 416 → .tmp 已完整或损坏，删了从头
        /// </summary>
        private static void SingleStreamDownloadWithResume(string url, string tmpPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // ★ 全局下载许可
            DownloadGate.Global.Wait(ct);
            try
            {
                string dir = Path.GetDirectoryName(tmpPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                long existingLength = 0;
                if (File.Exists(tmpPath))
                {
                    try { existingLength = new FileInfo(tmpPath).Length; } catch { }
                }

                HttpWebResponse resp = null;
                bool isPartial = false;

                try
                {
                    resp = SendDownloadRequest(url, existingLength);
                    isPartial = resp.StatusCode == HttpStatusCode.PartialContent;
                }
                catch (WebException wex) when (IsRangeNotSatisfiable(wex))
                {
                    if (wex.Response != null) { try { wex.Response.Close(); } catch { } }
                    if (existingLength > 0)
                    {
                        try { File.Delete(tmpPath); } catch { }
                        existingLength = 0;
                    }
                    resp = SendDownloadRequest(url, 0);
                    isPartial = false;
                }

                using (resp)
                using (var netStream = resp.GetResponseStream())
                {
                    if (existingLength > 0 && !isPartial) existingLength = 0;

                    long totalBytes = (existingLength > 0 && isPartial)
                        ? (ParseContentRangeTotal(resp.Headers["Content-Range"]) > 0 ? ParseContentRangeTotal(resp.Headers["Content-Range"]) : existingLength + resp.ContentLength)
                        : resp.ContentLength;

                    if (existingLength > 0 && totalBytes > 0 && existingLength == totalBytes) return;

                    FileMode mode = existingLength > 0 ? FileMode.Append : FileMode.Create;

                    using (var fs = new FileStream(tmpPath, mode, FileAccess.Write, FileShare.None, BUFFER_SIZE))
                    {
                        long received = existingLength;
                        byte[] buffer = new byte[BUFFER_SIZE];
                        int read;

                        while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            fs.Write(buffer, 0, read);
                            received += read;
                        }

                        if (totalBytes > 0 && received != totalBytes)
                            throw new Exception($"下载不完整: {received}/{totalBytes}");
                    }
                }
            }
            finally
            {
                DownloadGate.Global.Release();
            }
        }

        private static HttpWebResponse SendDownloadRequest(string url, long existingLength)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.Timeout = HTTP_TIMEOUT_MS;
            req.ReadWriteTimeout = HTTP_TIMEOUT_MS;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;

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

        private static string ComputeSha1(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        // ===================== 原子替换 =====================
        private static void MoveOrReplace(string src, string dst)
        {
            if (File.Exists(dst))
            {
                try
                {
                    File.Replace(src, dst, null);
                    return;
                }
                catch (PlatformNotSupportedException ex)
                {
                    LogEx($"[LabyMod] File.Replace 平台不支持，回退 Delete+Move: {dst}", ex, LogLevel.Warn);
                }
                catch (IOException ex)
                {
                    LogEx($"[LabyMod] File.Replace 失败（可能被占用），回退 Delete+Move: {dst}", ex, LogLevel.Warn);
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { File.Delete(dst); break; }
                    catch (IOException ex)
                    {
                        if (attempt == 2)
                            throw new Exception($"[LabyMod] 删除目标文件失败（可能被占用）: {dst}", ex);
                        LogEx($"[LabyMod] 删除目标文件失败，重试 {attempt + 1}: {dst}", ex, LogLevel.Warn);
                        Thread.Sleep(200 * (attempt + 1));
                    }
                }
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.Move(src, dst);
                    return;
                }
                catch (IOException ex)
                {
                    if (attempt == 2)
                        throw new Exception($"[LabyMod] 移动临时文件失败（可能被占用）: {src} -> {dst}", ex);
                    LogEx($"[LabyMod] File.Move 失败，重试 {attempt + 1}: {dst}", ex, LogLevel.Warn);
                    Thread.Sleep(200 * (attempt + 1));
                }
            }
        }

        // ===================== 规则判断 =====================
        private static bool IsLibraryAllowed(
            Dictionary<string, object> lib, string side, string os, string arch)
        {
            if (!lib.ContainsKey("rules")) return true;
            var rules = lib["rules"] as ArrayList;
            if (rules == null || rules.Count == 0) return true;

            bool allowed = false;
            foreach (var r in rules)
            {
                var rule = r as Dictionary<string, object>;
                if (rule == null) continue;
                string action = GetStr(rule, "action") ?? "allow";

                bool applies = true;
                if (rule.ContainsKey("os"))
                {
                    var osRule = rule["os"] as Dictionary<string, object>;
                    if (osRule != null)
                    {
                        string ruleOs = GetStr(osRule, "name");
                        if (!string.IsNullOrEmpty(ruleOs) &&
                            !ruleOs.Equals(os, StringComparison.OrdinalIgnoreCase))
                            applies = false;
                        string ruleArch = GetStr(osRule, "arch");
                        if (applies && !string.IsNullOrEmpty(ruleArch) &&
                            !ruleArch.Equals(arch, StringComparison.OrdinalIgnoreCase))
                            applies = false;
                    }
                }
                if (applies) allowed = (action == "allow");
            }
            return allowed;
        }

        private static string GetOsName()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT ? "windows" : "linux";
        }

        private static string GetHostArch()
        {
            string pa = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            switch (pa.ToUpperInvariant())
            {
                case "AMD64": return "x86_64";
                case "X86": return "x86";
                case "ARM64": return "arm64";
                default: return pa.ToLowerInvariant();
            }
        }

        private static string GetHostSafe(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }

        // ===================== 工具 =====================
        private static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return null;
            return Convert.ToString(d[key]);
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
                LogEx("[LabyMod] JSON 解析失败", ex);
                return null;
            }
        }

        private class DownloadTask
        {
            public string[] Urls { get; set; }
            public string Dest { get; set; }
            public string Sha1 { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public bool ShowProgress { get; set; }
        }
    }
}