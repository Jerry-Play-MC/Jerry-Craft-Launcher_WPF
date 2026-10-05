using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Install_Minecraft_Versions
{
    internal class LiteLoader
    {
        // ===================== 源 =====================
        private const string BMCL_BASE = "https://bmclapi2.bangbang93.com";
        private const string BMCL_MAVEN = BMCL_BASE + "/maven/";
        private const string QLU_MAVEN = "https://mirrors.qlu.edu.cn/bmclapi/maven/";   // ★ 新增
        private const string LITELOADER_MAVEN = "http://dl.liteloader.com/versions/";
        private const string MOJANG_LIBS = "https://libraries.minecraft.net/";
        private const string MAVEN_CENTRAL = "https://repo1.maven.org/maven2/";

        // ===================== 下载参数 =====================
        private const int RETRY_ROUNDS = 8;
        private const int BUFFER_SIZE = 256 * 1024;
        private const int HTTP_TIMEOUT_MS = 30000;
        private const int NORMAL_DELAY_MS = 1000;
        private const int RATE_LIMIT_DELAY_MS = 20000;
        private const int ROUND_DELAY_MS = 3000;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly object _consoleLock = new object();

        private class LiteLoaderLibrary
        {
            public string Name { get; set; }
            public string Url { get; set; }
        }

        private class LiteLoaderInfo
        {
            public string Version { get; set; }
            public string TweakClass { get; set; }
            public string FileName { get; set; }
            public string Md5 { get; set; }
            public List<LiteLoaderLibrary> Libraries = new List<LiteLoaderLibrary>();
        }

        // ===================== 公开入口 =====================

        /// <summary>独立安装 LiteLoader（不与 Forge 组合）。</summary>
        public static void InstallClient(string version, string minecraftDir, string loaderParam)
        {
            InstallClientInternal(version, minecraftDir, loaderParam, null);
        }

        /// <summary>
        /// 与 Forge 组合安装。inheritsFrom 指向 Forge 版本，
        /// minecraftArguments 使用 --tweakClass FMLTweaker --cascadedTweaks LiteLoaderTweaker。
        /// </summary>
        /// <returns>生成的 LiteLoader 版本 ID</returns>
        public static string InstallClientForForge(
            string version, string minecraftDir, string loaderParam, string forgeVersionId)
        {
            if (string.IsNullOrEmpty(forgeVersionId))
                throw new ArgumentException("forgeVersionId 不能为空", "forgeVersionId");

            return InstallClientInternal(version, minecraftDir, loaderParam, forgeVersionId);
        }

        // ===================== 服务端：不支持 =====================
        public static void InstallServer(string version, string serverDir, string loaderParam)
        {
            throw new NotSupportedException(
                "LiteLoader 是纯客户端模组加载器，没有独立的服务端。\n" +
                "如需服务端请改用 Forge / NeoForge / Fabric / Quilt。");
        }

        // ===================== 核心安装逻辑 =====================
        private static string InstallClientInternal(
            string version, string minecraftDir, string loaderParam, string forgeVersionId)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            minecraftDir = Path.GetFullPath(minecraftDir);

            bool withForge = !string.IsNullOrEmpty(forgeVersionId);

            Log($"[LiteLoader] 开始安装客户端 LiteLoader {version}"
                + (withForge ? $"（与 Forge 组合，继承 {forgeVersionId}）" : ""));

            var info = GetLiteLoaderInfo(version, loaderParam);
            if (info == null)
                throw new Exception(
                    $"未找到适用于 Minecraft {version} 的 LiteLoader。\n" +
                    "LiteLoader 只支持 1.5.2 ~ 1.12.2。");

            Log($"[LiteLoader] 使用版本: {info.Version}");
            Log($"[LiteLoader] TweakClass: {info.TweakClass}");

            // 父版本：Forge 存在则继承 Forge 版本
            string parentId = withForge ? forgeVersionId : version;

            // 版本 ID
            string versionId = withForge
                ? $"{forgeVersionId}-LiteLoader{info.Version}"
                : $"{version}-LiteLoader{info.Version}";

            string versionsDir = Path.Combine(minecraftDir, "versions");
            string versionDir = Path.Combine(versionsDir, versionId);
            string librariesDir = Path.Combine(minecraftDir, "libraries");

            Directory.CreateDirectory(versionDir);
            Directory.CreateDirectory(librariesDir);

            // ★ 复制父版本核心 jar（Forge 版本目录或原版目录）
            CopyParentJar(versionsDir, parentId, versionDir, versionId);

            // 构造版本 JSON
            var versionJson = BuildVersionJson(parentId, versionId, info, withForge);

            // 合并父版本
            string parentJsonPath = Path.Combine(versionsDir, parentId, parentId + ".json");
            if (File.Exists(parentJsonPath))
            {
                var parentJson = ParseJson(File.ReadAllText(parentJsonPath));
                if (parentJson != null)
                {
                    versionJson = MergeParentJson(parentJson, versionJson, versionId);
                    Log($"[LiteLoader] 已合并父版本 {parentId}");
                }
                else
                {
                    Log($"[LiteLoader] 警告: 父版本 JSON 解析失败，保留 inheritsFrom");
                }
            }
            else
            {
                Log($"[LiteLoader] 警告: 未找到父版本 JSON: {parentJsonPath}（保留 inheritsFrom）");
            }

            // 保存版本 JSON
            string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(versionJsonPath,
                new JavaScriptSerializer().Serialize(versionJson), Encoding.UTF8);
            Log($"[LiteLoader] 版本 JSON 已保存: {versionJsonPath}");

            // 下载 libraries
            var tasks = CollectLibraryTasks(info, librariesDir);
            Log($"[LiteLoader] Libraries 任务: {tasks.Count}");

            if (tasks.Count > 0)
                DownloadAllSerial(tasks);

            Log($"[LiteLoader] 客户端 LiteLoader {version} 安装完成");
            Log($"[LiteLoader] 版本 ID: {versionId}");
            return versionId;
        }

        // ===================== 元数据 =====================
        private static LiteLoaderInfo GetLiteLoaderInfo(string mcVersion, string loaderParam)
        {
            string url = $"{BMCL_BASE}/liteloader/list?mcversion={Uri.EscapeDataString(mcVersion)}";
            Log($"[LiteLoader] 获取版本信息: {url}");

            string json = DownloadString(url);
            if (string.IsNullOrEmpty(json))
            {
                Log("[LiteLoader] BMCLAPI 返回为空");
                return null;
            }

            if (json.IndexOf("\"error\"", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("[LiteLoader] BMCLAPI 返回 error（该版本无 LiteLoader）");
                return null;
            }

            var root = ParseJson(json);
            if (root == null || !root.ContainsKey("build"))
            {
                Log("[LiteLoader] 返回数据缺少 build 字段");
                return null;
            }

            var build = root["build"] as Dictionary<string, object>;
            if (build == null) return null;

            var info = new LiteLoaderInfo
            {
                Version = build.ContainsKey("version") ? build["version"].ToString() : null,
                TweakClass = build.ContainsKey("tweakClass")
                    ? build["tweakClass"].ToString()
                    : "com.mumfrey.liteloader.launch.LiteLoaderTweaker",
                FileName = build.ContainsKey("file") ? build["file"].ToString() : null,
                Md5 = build.ContainsKey("md5") ? build["md5"].ToString() : null
            };

            if (string.IsNullOrEmpty(info.Version))
            {
                Log("[LiteLoader] build 中缺少 version 字段");
                return null;
            }

            if (build.ContainsKey("libraries"))
            {
                var libs = build["libraries"] as ArrayList;
                if (libs != null)
                {
                    foreach (var item in libs)
                    {
                        var libStr = item as string;
                        if (libStr != null)
                        {
                            info.Libraries.Add(new LiteLoaderLibrary { Name = libStr, Url = BMCL_MAVEN });
                            continue;
                        }

                        var libObj = item as Dictionary<string, object>;
                        if (libObj != null && libObj.ContainsKey("name"))
                        {
                            info.Libraries.Add(new LiteLoaderLibrary
                            {
                                Name = libObj["name"].ToString(),
                                Url = BMCL_MAVEN
                            });
                        }
                    }
                }
            }

            EnsureLibrary(info, $"com.mumfrey:liteloader:{info.Version}");
            EnsureLibrary(info, "net.minecraft:launchwrapper:1.12");
            EnsureLibrary(info, "org.ow2.asm:asm-all:5.2");

            return info;
        }

        private static void EnsureLibrary(LiteLoaderInfo info, string coord)
        {
            foreach (var lib in info.Libraries)
            {
                if (string.Equals(lib.Name, coord, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            info.Libraries.Add(new LiteLoaderLibrary
            {
                Name = coord,
                Url = BMCL_MAVEN
            });
        }

        // ===================== JSON 构造 =====================
        private static Dictionary<string, object> BuildVersionJson(
            string mcVersion, string versionId, LiteLoaderInfo info, bool withForge)
        {
            var libs = new List<object>();
            foreach (var lib in info.Libraries)
            {
                var libObj = new Dictionary<string, object> { { "name", lib.Name } };

                // ★ 无条件写 url 字段：
                //   - lib.Url 非空 → 用它
                //   - lib.Url 为空 → 兜底为 BMCL_MAVEN
                string url = string.IsNullOrEmpty(lib.Url) ? BMCL_MAVEN : lib.Url;
                libObj["url"] = url;

                libs.Add(libObj);
            }

            // ★ 根据是否与 Forge 组合，采用不同的 tweak 参数
            string tweakArgs = withForge
                ? $"--tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker " +
                  $"--cascadedTweaks {info.TweakClass}"
                : $"--tweakClass {info.TweakClass}";

            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00");

            return new Dictionary<string, object>
            {
                { "id", versionId },
                { "inheritsFrom", mcVersion },
                { "type", "release" },
                { "time", now },
                { "releaseTime", now },
                { "mainClass", "net.minecraft.launchwrapper.Launch" },
                { "minecraftArguments", tweakArgs },
                { "libraries", libs },
                { "minimumLauncherVersion", 18 },
                { "jar", versionId }
            };
        }

        // ===================== 合并父版本 =====================
        private static Dictionary<string, object> MergeParentJson(
            Dictionary<string, object> parentRoot,
            Dictionary<string, object> childRoot,
            string versionId)
        {
            var merged = new Dictionary<string, object>();

            foreach (var kv in parentRoot)
                merged[kv.Key] = kv.Value;

            foreach (var kv in childRoot)
            {
                if (kv.Key == "inheritsFrom") continue;
                if (kv.Key == "libraries") continue;
                if (kv.Key == "arguments") continue;
                if (kv.Key == "minecraftArguments") continue;
                merged[kv.Key] = kv.Value;
            }

            merged["id"] = versionId;
            merged["jar"] = versionId;

            merged["libraries"] = MergeLibraries(childRoot, parentRoot);

            // minecraftArguments：父 + 子 拼接
            string parentMcArgs = parentRoot.ContainsKey("minecraftArguments")
                ? parentRoot["minecraftArguments"].ToString() : null;
            string childMcArgs = childRoot.ContainsKey("minecraftArguments")
                ? childRoot["minecraftArguments"].ToString() : null;

            if (!string.IsNullOrEmpty(parentMcArgs) || !string.IsNullOrEmpty(childMcArgs))
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(parentMcArgs)) parts.Add(parentMcArgs);
                if (!string.IsNullOrEmpty(childMcArgs)) parts.Add(childMcArgs);
                merged["minecraftArguments"] = string.Join(" ", parts);
            }

            var mergedArgs = MergeArguments(childRoot, parentRoot);
            if (mergedArgs != null)
                merged["arguments"] = mergedArgs;

            return merged;
        }

        private static List<object> MergeLibraries(
            Dictionary<string, object> childRoot,
            Dictionary<string, object> parentRoot)
        {
            var result = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (childRoot.ContainsKey("libraries"))
            {
                var libs = childRoot["libraries"] as IList;
                if (libs != null)
                {
                    foreach (var lib in libs)
                    {
                        var d = lib as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("name")) continue;
                        if (seen.Add(d["name"].ToString())) result.Add(lib);
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
                        var d = lib as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("name")) continue;
                        if (seen.Add(d["name"].ToString())) result.Add(lib);
                    }
                }
            }

            return result;
        }

        private static Dictionary<string, object> MergeArguments(
            Dictionary<string, object> childRoot,
            Dictionary<string, object> parentRoot)
        {
            var parentArgs = parentRoot.ContainsKey("arguments")
                ? parentRoot["arguments"] as Dictionary<string, object> : null;
            var childArgs = childRoot.ContainsKey("arguments")
                ? childRoot["arguments"] as Dictionary<string, object> : null;

            if (parentArgs == null && childArgs == null) return null;

            var mergedArgs = new Dictionary<string, object>();

            var jvmList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("jvm"))
            {
                var l = parentArgs["jvm"] as IList;
                if (l != null) foreach (var item in l) jvmList.Add(item);
            }
            if (childArgs != null && childArgs.ContainsKey("jvm"))
            {
                var l = childArgs["jvm"] as IList;
                if (l != null) foreach (var item in l) jvmList.Add(item);
            }
            if (jvmList.Count > 0) mergedArgs["jvm"] = jvmList;

            var gameList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("game"))
            {
                var l = parentArgs["game"] as IList;
                if (l != null) foreach (var item in l) gameList.Add(item);
            }
            if (childArgs != null && childArgs.ContainsKey("game"))
            {
                var l = childArgs["game"] as IList;
                if (l != null) foreach (var item in l) gameList.Add(item);
            }
            if (gameList.Count > 0) mergedArgs["game"] = gameList;

            return mergedArgs.Count > 0 ? mergedArgs : null;
        }

        // ===================== Libraries =====================
        private static List<DownloadTask> CollectLibraryTasks(
            LiteLoaderInfo info, string librariesDir)
        {
            var tasks = new List<DownloadTask>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var lib in info.Libraries)
            {
                if (string.IsNullOrEmpty(lib.Name)) continue;

                string relPath = MavenNameToPath(lib.Name);
                if (string.IsNullOrEmpty(relPath)) continue;

                if (!seen.Add(relPath)) continue;

                var urls = BuildCandidateUrls(lib.Name, relPath);
                if (urls.Length == 0) continue;

                tasks.Add(new DownloadTask
                {
                    Urls = urls,
                    Dest = Path.Combine(librariesDir, relPath),
                    Name = relPath
                });
            }

            return tasks;
        }

        // ★ BuildCandidateUrls 加了 QLU 优先
        private static string[] BuildCandidateUrls(string coord, string relPath)
        {
            var list = new List<string>();
            var parts = coord.Split(':');
            string group = parts.Length > 0 ? parts[0] : "";

            if (group.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase))
            {
                // launchwrapper：Mojang 官方优先，BMCLAPI / QLU 兜底
                list.Add(MOJANG_LIBS + relPath);
                list.Add(BMCL_MAVEN + relPath);
                list.Add(QLU_MAVEN + relPath);
            }
            else if (group.Equals("org.ow2.asm", StringComparison.OrdinalIgnoreCase))
            {
                // ASM：Maven Central 优先，BMCLAPI / QLU 兜底
                list.Add(MAVEN_CENTRAL + relPath);
                list.Add(BMCL_MAVEN + relPath);
                list.Add(QLU_MAVEN + relPath);
            }
            else if (group.Equals("com.mumfrey", StringComparison.OrdinalIgnoreCase))
            {
                // ★ LiteLoader 本体：
                //   1) QLU 镜像优先（同数据，不同服务器，不受 BMCLAPI 主站限流）
                //   2) BMCLAPI 兜底
                //   官方 dl.liteloader.com 已挂，不再尝试
                list.Add(QLU_MAVEN + relPath);
                list.Add(BMCL_MAVEN + relPath);
            }
            else
            {
                list.Add(BMCL_MAVEN + relPath);
                list.Add(QLU_MAVEN + relPath);
                list.Add(MAVEN_CENTRAL + relPath);
            }

            return list.ToArray();
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

        // ===================== 串行下载 =====================
        private static void DownloadAllSerial(List<DownloadTask> tasks)
        {
            int failed = 0, skipped = 0;
            int total = tasks.Count;

            Log($"[LiteLoader] 开始下载（共 {total} 个文件，串行）...");

            for (int i = 0; i < total; i++)
            {
                var task = tasks[i];

                lock (_consoleLock)
                {
                    Console.Write($"\r[LiteLoader] 进度 {i + 1}/{total} - {task.Name}   ");
                    try { Console.Out.Flush(); } catch { }
                }

                try
                {
                    bool wasSkipped = DownloadFileWithRetry(task);
                    if (wasSkipped) skipped++;
                    else Log($"[LiteLoader] 已下载: {task.Name}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Log($"[LiteLoader] 下载失败: {task.Name} -> {ex.Message}");
                }
            }

            lock (_consoleLock)
            {
                Console.Write("\r" + new string(' ', 140) + "\r");
                try { Console.Out.Flush(); } catch { }
            }

            Log($"[LiteLoader] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");

            if (failed > 0)
                throw new Exception($"[LiteLoader] 有 {failed}/{total} 个库下载失败");
        }

        private static bool DownloadFileWithRetry(DownloadTask task)
        {
            if (File.Exists(task.Dest) && new FileInfo(task.Dest).Length > 0)
                return true;

            string dir = Path.GetDirectoryName(task.Dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = task.Dest + ".tmp";
            Exception lastEx = null;
            var permanentFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int round = 0; round < RETRY_ROUNDS; round++)
            {
                if (round > 0)
                {
                    Log($"[LiteLoader] 第 {round + 1}/{RETRY_ROUNDS} 轮重试: {task.Name}");
                    Thread.Sleep(ROUND_DELAY_MS);
                }

                bool anyAttempted = false;

                foreach (string url in task.Urls)
                {
                    if (permanentFailures.Contains(url)) continue;
                    anyAttempted = true;

                    try
                    {
                        SingleDownload(url, tmpPath);

                        if (File.Exists(task.Dest))
                        {
                            try { File.Delete(task.Dest); } catch { }
                        }
                        File.Move(tmpPath, task.Dest);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

                        string msg = ex.Message ?? "";
                        bool forbidden =
                            msg.IndexOf("403", StringComparison.Ordinal) >= 0 ||
                            msg.IndexOf("404", StringComparison.Ordinal) >= 0;
                        bool rateLimited =
                            msg.IndexOf("429", StringComparison.Ordinal) >= 0 ||
                            msg.IndexOf("Too Many", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (forbidden)
                        {
                            permanentFailures.Add(url);
                            Log($"[LiteLoader] 源不可用({GetHostSafe(url)})，切换备用源");
                            continue;
                        }

                        if (rateLimited)
                        {
                            Log($"[LiteLoader] 限流，等待 {RATE_LIMIT_DELAY_MS / 1000} 秒: {GetHostSafe(url)}");
                            Thread.Sleep(RATE_LIMIT_DELAY_MS);
                        }
                        else
                        {
                            Thread.Sleep(NORMAL_DELAY_MS);
                        }
                    }
                }

                if (!anyAttempted) break;
            }

            throw new Exception($"下载 {task.Name} 失败: {lastEx?.Message}", lastEx);
        }

        private static string GetHostSafe(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }

        private static void SingleDownload(string url, string dest)
        {
            using (var resp = SendRequest(url, HTTP_TIMEOUT_MS, HTTP_TIMEOUT_MS))
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
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    using (var resp = SendRequest(url, HTTP_TIMEOUT_MS, HTTP_TIMEOUT_MS))
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (retry < 2) Thread.Sleep(2000 * (retry + 1));
                }
            }
            throw new Exception($"下载 {url} 失败: {lastEx?.Message}", lastEx);
        }

        // ★ UA 改成简短版，避免被 BMCLAPI 的 WAF 拦
        private static HttpWebResponse SendRequest(
            string url, int timeoutMs, int readWriteTimeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0";   // ★ 从 UA_STRING 改成简短版
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = readWriteTimeoutMs;
            req.AutomaticDecompression = DecompressionMethods.None;

            return (HttpWebResponse)req.GetResponse();
        }

        // ===================== 复制父版本 jar =====================
        private static void CopyParentJar(
            string versionsDir, string parentId, string versionDir, string versionId)
        {
            string parentJar = Path.Combine(versionsDir, parentId, parentId + ".jar");
            string targetJar = Path.Combine(versionDir, versionId + ".jar");

            if (!File.Exists(parentJar) || new FileInfo(parentJar).Length == 0)
            {
                Log($"[LiteLoader] 警告: 未找到父版本核心 {parentJar}");
                return;
            }

            try
            {
                if (File.Exists(targetJar) &&
                    new FileInfo(targetJar).Length == new FileInfo(parentJar).Length)
                {
                    Log($"[LiteLoader] 版本核心已存在: {targetJar}");
                    return;
                }

                File.Copy(parentJar, targetJar, true);
                Log($"[LiteLoader] 已复制父版本核心: {targetJar}");
            }
            catch (Exception ex)
            {
                Log($"[LiteLoader] 复制父版本核心失败(不致命): {ex.Message}");
            }
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

        private class DownloadTask
        {
            public string[] Urls { get; set; }
            public string Dest { get; set; }
            public string Name { get; set; }
        }
    }
}