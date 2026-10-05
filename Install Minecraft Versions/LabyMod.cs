using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Install_Minecraft_Versions
{
    internal class LabyMod
    {
        // ===================== 源 =====================
        private const string MANIFEST_URL =
            "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/manifest/production/latest.json";

        // ★ 完整依赖清单（LabyMod 全部运行时库 + isolated_libraries 隔离列表）
        private const string LIBS_URL_TEMPLATE =
            "https://releases.r2.labymod.net/api/v1/libraries/{channel}.json";

        // 核心 jar 候选源（第一个优先）
        private static readonly string[] CORE_JAR_TEMPLATES = {
            "https://releases.r2.labymod.net/api/v1/download/labymod4/{channel}/{commit}.jar",
            "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/download/labymod4/{channel}/{commit}.jar",
        };

        // 额外 asset 候选源（第一个优先）
        private static readonly string[] ASSET_TEMPLATES = {
            "https://releases.r2.labymod.net/api/v1/download/assets/labymod4/{channel}/{commit}/{name}/{hash}.jar",
            "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/download/assets/labymod4/{channel}/{commit}/{name}/{hash}.jar",
        };

        // 默认 channel。若你的启动器支持 snapshot 测试版，把这里改成参数传入即可。
        private const string DEFAULT_CHANNEL = "production";

        // ===================== 下载参数 =====================
        private const int MAX_CONCURRENCY = 24;
        private const int RETRY_COUNT = 3;
        private const int BUFFER_SIZE = 256 * 1024;
        private const int PROGRESS_REFRESH_MS = 100;
        private const int HTTP_TIMEOUT_MS = 30000;

        private const string UA_STRING = "Mozilla/5.0";

        private static readonly object _consoleLock = new object();
        private static int _lastProgressTick = 0;

        // ===================== 公开入口 =====================
        public static string InstallClient(string version, string minecraftDir, string loaderParam)
        {
            return InstallClient(version, minecraftDir, loaderParam, DEFAULT_CHANNEL);
        }

        public static string InstallClient(string version, string minecraftDir, string loaderParam, string channel)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");
            if (string.IsNullOrEmpty(channel))
                channel = DEFAULT_CHANNEL;

            minecraftDir = Path.GetFullPath(minecraftDir);
            Log($"[LabyMod] 开始安装客户端 LabyMod {version}（channel={channel}）");

            // ---------- 1. 拉取 manifest ----------
            Log($"[LabyMod] 拉取 manifest: {MANIFEST_URL}");
            string manifestJson = DownloadString(MANIFEST_URL);
            var manifest = ParseJson(manifestJson);
            if (manifest == null)
                throw new Exception("manifest 解析失败");

            string labyModVersion = GetStr(manifest, "labyModVersion");
            string commitRef = GetStr(manifest, "commitReference");
            string coreJarSha1 = GetStr(manifest, "sha1");
            string coreJarSizeStr = GetStr(manifest, "size");
            long coreJarSize = 0;
            if (!string.IsNullOrEmpty(coreJarSizeStr))
                long.TryParse(coreJarSizeStr, out coreJarSize);

            if (string.IsNullOrEmpty(commitRef))
                throw new Exception("manifest 缺少 commitReference");

            Log($"[LabyMod] LabyMod 版本: {labyModVersion}");
            Log($"[LabyMod] commitReference: {commitRef}");

            // ---------- 2. 找目标 MC 版本条目 ----------
            string mcTag = null;
            string customManifestUrl = null;

            var mcVersions = manifest.ContainsKey("minecraftVersions")
                ? manifest["minecraftVersions"] as ArrayList : null;
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

            Log($"[LabyMod] 目标 MC tag: {mcTag}");
            Log($"[LabyMod] 基础 JSON URL: {customManifestUrl}");

            // ---------- 3. 下载基础版本 JSON ----------
            var versionRoot = ParseJson(DownloadString(customManifestUrl));
            if (versionRoot == null)
                throw new Exception("基础版本 JSON 解析失败");

            int baseLibCount = (versionRoot.ContainsKey("libraries")
                ? versionRoot["libraries"] as ArrayList : null)?.Count ?? 0;
            Log($"[LabyMod] 基础 JSON 库数量: {baseLibCount}");

            // ---------- 4. ★ 下载完整依赖清单 + 隔离列表 ----------
            string libUrl = LIBS_URL_TEMPLATE.Replace("{channel}", channel);
            Log($"[LabyMod] 下载完整依赖清单: {libUrl}");

            try
            {
                string libText = DownloadString(libUrl);
                var libRoot = ParseJson(libText);

                if (libRoot == null)
                {
                    Log($"[LabyMod] ⚠ 完整依赖清单解析失败，仅使用基础 JSON");
                }
                else
                {
                    // 4.1 收集隔离列表
                    var isolatedSet = CollectIsolatedLibraries(libRoot, mcTag);
                    Log($"[LabyMod] 需要隔离 {isolatedSet.Count} 个库");

                    // 4.2 过滤基础 JSON 中被隔离的库
                    FilterIsolatedFromBase(versionRoot, isolatedSet);

                    // 4.3 追加完整依赖清单里的全部库
                    int appended = AppendFullLibraries(versionRoot, libRoot);
                    Log($"[LabyMod] 从完整依赖清单追加 {appended} 个库");

                    // 4.4 追加 LabyMod 主 jar
                    AppendLabyModCoreJar(versionRoot, channel, commitRef, coreJarSha1, coreJarSize);

                    // 4.5 写入 labymod_data
                    versionRoot["labymod_data"] = new Dictionary<string, object>
                    {
                        { "channelType", channel },
                        { "commitReference", commitRef },
                        { "version", labyModVersion ?? "" },
                        { "versionType", "release" }
                    };
                }
            }
            catch (Exception ex)
            {
                Log($"[LabyMod] ⚠ 完整依赖清单处理失败: {ex.Message}");
            }

            int mergedLibCount = (versionRoot.ContainsKey("libraries")
                ? versionRoot["libraries"] as ArrayList : null)?.Count ?? 0;
            Log($"[LabyMod] 最终库数量: {mergedLibCount}");

            if (mergedLibCount < 50)
                Log($"[LabyMod] ⚠ 警告：库数量偏少（<50），启动时可能报 ClassNotFoundException");

            // ---------- 5. 构建版本 ID ----------
            string shortCommit = commitRef.Length > 8 ? commitRef.Substring(0, 8) : commitRef;
            string versionId = $"LabyMod-4-{version}-{shortCommit}";
            Log($"[LabyMod] 版本 ID: {versionId}");

            versionRoot["id"] = versionId;
            versionRoot["jar"] = versionId;

            // ---------- 6. 删掉 downloads.client（主 jar 由 LabyMod 核心 jar 提供）----------
            if (versionRoot.ContainsKey("downloads"))
            {
                var dl = versionRoot["downloads"] as Dictionary<string, object>;
                if (dl != null)
                {
                    dl.Remove("client");
                    dl.Remove("client_mappings");
                }
            }

            // ---------- 7. 兜底注入 launchwrapper（极少数基础 JSON 缺失时）----------
            EnsureLabyModLibraries(versionRoot);

            // ---------- 8. 准备目录 ----------
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

            var tasks = new List<DownloadTask>();

            // ---------- 9. LabyMod 核心 jar ----------
            string coreJarDest = Path.Combine(versionDir, versionId + ".jar");
            string coreJarUrl = CORE_JAR_TEMPLATES[0]
                .Replace("{channel}", channel)
                .Replace("{commit}", commitRef);

            Log($"[LabyMod] 核心 jar: {coreJarUrl}");
            tasks.Add(new DownloadTask
            {
                Url = coreJarUrl,
                Dest = coreJarDest,
                Sha1 = coreJarSha1,
                Name = versionId + ".jar",
                Type = "labymod-core",
                ShowProgress = true
            });

            // ---------- 10. assetIndex ----------
            if (versionRoot.ContainsKey("assetIndex"))
            {
                var ai = versionRoot["assetIndex"] as Dictionary<string, object>;
                if (ai != null && ai.ContainsKey("id"))
                {
                    string assetIndexId = GetStr(ai, "id");
                    tasks.Add(new DownloadTask
                    {
                        Url = GetStr(ai, "url"),
                        Dest = Path.Combine(assetsDir, "indexes", assetIndexId + ".json"),
                        Sha1 = GetStr(ai, "sha1"),
                        Name = assetIndexId + ".json",
                        Type = "asset-index"
                    });
                }
            }

            // ---------- 11. logging ----------
            if (versionRoot.ContainsKey("logging"))
            {
                var logging = versionRoot["logging"] as Dictionary<string, object>;
                var logClient = logging != null && logging.ContainsKey("client")
                    ? logging["client"] as Dictionary<string, object> : null;
                var logFile = logClient != null && logClient.ContainsKey("file")
                    ? logClient["file"] as Dictionary<string, object> : null;
                if (logFile != null && logFile.ContainsKey("id") && logFile.ContainsKey("url"))
                {
                    string id = GetStr(logFile, "id");
                    tasks.Add(new DownloadTask
                    {
                        Url = GetStr(logFile, "url"),
                        Dest = Path.Combine(assetsDir, "log_configs", id),
                        Sha1 = GetStr(logFile, "sha1"),
                        Name = id,
                        Type = "logging"
                    });
                }
            }

            // ---------- 12. libraries ----------
            CollectLibraryTasks(versionRoot, librariesDir, tasks);

            // ---------- 13. LabyMod 额外 assets ----------
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

                        string url = ASSET_TEMPLATES[0]
                            .Replace("{channel}", channel)
                            .Replace("{commit}", commitRef)
                            .Replace("{name}", name)
                            .Replace("{hash}", hash);

                        tasks.Add(new DownloadTask
                        {
                            Url = url,
                            Dest = Path.Combine(labyModNeoDir, "assets", name + ".jar"),
                            Sha1 = null,
                            Name = name + ".jar",
                            Type = "labymod-asset"
                        });
                    }
                }
            }

            // ---------- 14. 保存版本 JSON ----------
            string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(versionJsonPath,
                new JavaScriptSerializer().Serialize(versionRoot), Encoding.UTF8);
            Log($"[LabyMod] 版本 JSON 已保存: {versionJsonPath}");

            // ---------- 15. 下载 ----------
            Log($"[LabyMod] 下载任务: {tasks.Count}");
            ParallelDownload(tasks, MAX_CONCURRENCY);

            Log($"[LabyMod] 客户端 LabyMod {version} 安装完成");
            Log($"[LabyMod] 版本 ID: {versionId}");
            return versionId;
        }

        // ===================== 隔离列表处理 =====================
        /// <summary>
        /// 从完整依赖清单里收集当前 MC 版本需要隔离的库名。
        /// isolated_libraries 里的 name 是简写（如 lwjgl、lwjgl-freetype、thin-lwjgl），
        /// 需要和基础 JSON 中的 org.lwjgl:lwjgl:... 之类做匹配。
        /// </summary>
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

        /// <summary>
        /// 从基础 JSON 的 libraries 里剔除需要隔离的库。
        /// 匹配规则：取 name 的第二段（artifact），看是否等于隔离短名；
        /// 例如 org.lwjgl:lwjgl:3.4.3:natives-windows 的 artifact 是 lwjgl，等于隔离名 lwjgl 就剔除。
        /// </summary>
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
            Log($"[LabyMod] 隔离过滤: {before} -> {kept.Count}");
        }

        private static bool ShouldIsolate(string libName, HashSet<string> isolatedSet)
        {
            if (string.IsNullOrEmpty(libName)) return false;

            // 完整匹配
            if (isolatedSet.Contains(libName)) return true;

            // 按 Maven 坐标拆分：group:artifact:version[:classifier]
            var parts = libName.Split(':');
            if (parts.Length >= 2)
            {
                string artifact = parts[1];
                if (isolatedSet.Contains(artifact)) return true;

                // 对 org.lwjgl 前缀的做特殊处理：thin-lwjgl 在 net.labymod 下
                string group = parts[0];
                string combined = group + ":" + artifact;
                if (isolatedSet.Contains(combined)) return true;
            }

            // 兜底：名字尾部是否精确等于隔离名
            foreach (var iso in isolatedSet)
            {
                if (libName.EndsWith(":" + iso, StringComparison.OrdinalIgnoreCase) ||
                    libName.EndsWith(":" + iso + ":", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ===================== 追加完整依赖 =====================
        /// <summary>
        /// 把完整依赖清单里的 libraries 全部追加到版本 JSON。
        /// 每条 item 形如：{ name, url, sha1, size, ... }，转换成 MC 标准格式：
        /// { name, downloads: { artifact: { path, sha1, size, url } } }
        /// </summary>
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

            // 按 name 去重
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

                // 从 URL 提取 artifact path
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

        /// <summary>
        /// 从 LabyMod Maven URL 中提取 artifact 相对路径。
        /// 例如 https://releases.r2.labymod.net/libraries/org/lwjgl/lwjgl/3.4.3/lwjgl-3.4.3.jar
        /// 返回 org/lwjgl/lwjgl/3.4.3/lwjgl-3.4.3.jar
        /// </summary>
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

            // 去重：如果已经存在 net.labymod:LabyMod:4，先移除
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

            string jarUrl = $"https://releases.r2.labymod.net/api/v1/download/labymod4/{channel}/{commitRef}.jar";

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

            // 仅注入 launchwrapper，log4j 已由完整依赖清单提供，不再手动注入
            var required = new[]
            {
                new
                {
                    Name = "net.minecraft:launchwrapper:4.1.3",
                    Url  = "https://releases.r2.labymod.net/libraries/"
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
                Log($"[LabyMod] 注入兜底库: {r.Name}");
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
                                tasks.Add(new DownloadTask
                                {
                                    Url = GetStr(nat, "url"),
                                    Dest = Path.Combine(librariesDir, GetStr(nat, "path")),
                                    Sha1 = GetStr(nat, "sha1"),
                                    Name = GetStr(nat, "path"),
                                    Type = "libraries"
                                });
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(relPath))
                {
                    tasks.Add(new DownloadTask
                    {
                        Url = url,
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
                                Url = baseUrl + rel,
                                Dest = Path.Combine(librariesDir, rel),
                                Sha1 = null,
                                Name = rel,
                                Type = "libraries"
                            });
                        }
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

        // ===================== 下载 =====================
        private static void ParallelDownload(List<DownloadTask> tasks, int maxConcurrency)
        {
            int failed = 0, skipped = 0, completed = 0;
            int total = tasks.Count;
            int nextIndex = 0;
            object queueLock = new object();
            object statLock = new object();

            Log($"[LabyMod] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");
            Interlocked.Exchange(ref _lastProgressTick, 0);

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
                            bool wasSkipped = DownloadFile(task);
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
                                Console.WriteLine($"[LabyMod] 下载失败: {task.Name} -> {ex.Message}");
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
                                double pct = total == 0 ? 100 : (completed * 100.0 / total);
                                Console.Write($"\r[LabyMod] 跳过 {skipped} | 失败 {failed} | " +
                                              $"总进度 {pct:F1}% ({completed}/{total})  ");
                                try { Console.Out.Flush(); } catch { }
                            }
                        }
                    }
                });
                worker.IsBackground = true;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.WriteLine($"[LabyMod] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");
            }

            if (failed > 0)
                throw new Exception($"[LabyMod] 有 {failed}/{total} 个文件下载失败");
        }

        private static bool DownloadFile(DownloadTask task)
        {
            if (File.Exists(task.Dest) && new FileInfo(task.Dest).Length > 0)
            {
                if (!string.IsNullOrEmpty(task.Sha1))
                {
                    try
                    {
                        if (ComputeSha1(task.Dest).Equals(task.Sha1, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
                else
                {
                    return true;
                }
            }

            string dir = Path.GetDirectoryName(task.Dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = task.Dest + ".tmp";
            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                try
                {
                    SingleDownload(task.Url, tmpPath);

                    if (!string.IsNullOrEmpty(task.Sha1))
                    {
                        string actual = ComputeSha1(tmpPath);
                        if (!actual.Equals(task.Sha1, StringComparison.OrdinalIgnoreCase))
                            throw new Exception($"SHA1 校验失败: 期望 {task.Sha1}, 实际 {actual}");
                    }

                    if (File.Exists(task.Dest)) { try { File.Delete(task.Dest); } catch { } }
                    File.Move(tmpPath, task.Dest);
                    return false;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    if (retry < RETRY_COUNT - 1) Thread.Sleep(1000 * (retry + 1));
                }
            }

            throw new Exception($"下载 {task.Url} 失败: {lastEx?.Message}", lastEx);
        }

        private static void SingleDownload(string url, string dest)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.Timeout = HTTP_TIMEOUT_MS;
            req.ReadWriteTimeout = HTTP_TIMEOUT_MS;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var netStream = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                           FileShare.None, BUFFER_SIZE))
            {
                byte[] buf = new byte[BUFFER_SIZE];
                int read;
                while ((read = netStream.Read(buf, 0, buf.Length)) > 0)
                    fs.Write(buf, 0, read);
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
                    req.Timeout = HTTP_TIMEOUT_MS;
                    req.AllowAutoRedirect = true;
                    req.KeepAlive = false;

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    using (var r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (i < RETRY_COUNT - 1) Thread.Sleep(1000 * (i + 1));
                }
            }
            throw new Exception($"下载 {url} 失败: {lastEx?.Message}", lastEx);
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

        private class DownloadTask
        {
            public string Url { get; set; }
            public string Dest { get; set; }
            public string Sha1 { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public bool ShowProgress { get; set; }
        }
    }
}