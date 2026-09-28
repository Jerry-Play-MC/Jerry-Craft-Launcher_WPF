using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace Install_Minecraft_Versions
{
    internal class Forge
    {
        // ===================== Forge 源 =====================
        private const string FORGE_MAVEN = "https://maven.minecraftforge.net/";
        private const string FORGE_METADATA = FORGE_MAVEN + "net/minecraftforge/forge/maven-metadata.xml";
        private const string BMCL_BASE = "https://bmclapi2.bangbang93.com";

        // ===================== 下载参数 =====================
        private const int MAX_CONCURRENCY = 24;
        private const int RETRY_COUNT = 3;
        private const int BUFFER_SIZE = 256 * 1024;
        private const int PROGRESS_REFRESH_MS = 100;
        private const int HTTP_TIMEOUT_MS = 15000;
        private const int PROCESSOR_TIMEOUT_MS = 15 * 60 * 1000;

        // ===================== 分段参数 =====================
        private const int INSTALLER_CHUNKS = 8;

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

            minecraftDir = Path.GetFullPath(minecraftDir);

            Log($"[Forge] 开始安装客户端 Forge {version}");

            string forgeVersion = ResolveForgeVersion(version, loaderParam);
            if (string.IsNullOrEmpty(forgeVersion))
                throw new Exception($"无法找到 Forge {version} 的版本");
            Log($"[Forge] 使用 Forge 版本: {forgeVersion}");

            string versionId = $"{forgeVersion}";
            string versionsDir = Path.Combine(minecraftDir, "versions");
            string versionDir = Path.Combine(versionsDir, versionId);
            string librariesDir = Path.Combine(minecraftDir, "libraries");

            Directory.CreateDirectory(versionDir);
            Directory.CreateDirectory(librariesDir);

            string installerPath = DownloadInstaller(forgeVersion);

            string tempDir = Path.Combine(Path.GetTempPath(),
                $"forge_unpack_{forgeVersion.Replace('.', '_')}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                ExtractInstaller(installerPath, tempDir);

                string profilePath = Path.Combine(tempDir, "install_profile.json");
                if (!File.Exists(profilePath))
                    throw new Exception("installer 中缺少 install_profile.json");

                var profile = ParseJson(File.ReadAllText(profilePath));
                if (profile == null)
                    throw new Exception("install_profile.json 解析失败");

                Dictionary<string, object> versionJson = null;
                if (profile.ContainsKey("spec"))
                {
                    string vjPath = Path.Combine(tempDir, "version.json");
                    if (File.Exists(vjPath))
                        versionJson = ParseJson(File.ReadAllText(vjPath));
                }
                else if (profile.ContainsKey("versionInfo"))
                {
                    versionJson = profile["versionInfo"] as Dictionary<string, object>;
                }

                if (versionJson == null)
                    throw new Exception("installer 中缺少 version.json");

                string mavenSrc = Path.Combine(tempDir, "maven");
                if (Directory.Exists(mavenSrc))
                    CopyDirectory(mavenSrc, librariesDir, skipExisting: true);

                var tasks = new List<DownloadTask>();
                CollectLibraryTasks(versionJson, librariesDir, tasks, "client");
                if (profile.ContainsKey("libraries"))
                    CollectLibraryTasks(profile, librariesDir, tasks, "client");
                tasks = DeduplicateTasks(tasks);

                Log($"[Forge] Libraries 任务: {tasks.Count}");

                if (tasks.Count > 0)
                    ParallelDownload(tasks, MAX_CONCURRENCY);

                // ============================================================
                // ★ 合并父版本 JSON（展开 inheritsFrom，对标 PCL）
                // ============================================================
                if (versionJson.ContainsKey("inheritsFrom"))
                {
                    string parentId = versionJson["inheritsFrom"].ToString();
                    string parentJsonPath = Path.Combine(versionsDir, parentId, parentId + ".json");

                    if (File.Exists(parentJsonPath))
                    {
                        var parentJson = ParseJson(File.ReadAllText(parentJsonPath));
                        if (parentJson != null)
                        {
                            versionJson = MergeParentJson(parentJson, versionJson, versionId);
                            Log($"[Forge] 已合并父版本 {parentId}（inheritsFrom 已展开）");
                        }
                        else
                        {
                            Log($"[Forge] 警告: 父版本 JSON 解析失败，保留 inheritsFrom: {parentJsonPath}");
                        }
                    }
                    else
                    {
                        Log($"[Forge] 警告: 找不到父版本 JSON，保留 inheritsFrom: {parentJsonPath}");
                    }
                }

                string targetJson = Path.Combine(versionDir, versionId + ".json");
                File.WriteAllText(targetJson,
                    new JavaScriptSerializer().Serialize(versionJson), Encoding.UTF8);
                Log($"[Forge] 版本 JSON 已保存: {targetJson}");

                if (profile.ContainsKey("processors"))
                {
                    Log("[Forge] 执行 processor（打补丁）...");
                    RunProcessors(profile, minecraftDir, tempDir, "client", installerPath);
                }
                else
                {
                    Log("[Forge] 无 processor，跳过");
                }

                string originalClientJar = Path.Combine(versionsDir, version, version + ".jar");
                string forgeVersionJar = Path.Combine(versionDir, versionId + ".jar");
                if (File.Exists(originalClientJar) && !File.Exists(forgeVersionJar))
                {
                    try
                    {
                        File.Copy(originalClientJar, forgeVersionJar, true);
                        Log($"[Forge] 已复制原版核心到: {forgeVersionJar}");
                    }
                    catch { }
                }

                Log($"[Forge] 客户端 Forge {version} 安装完成");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ===================== 服务端 =====================
        public static void InstallServer(string version, string serverDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(serverDir))
                throw new ArgumentException("服务端路径不能为空", "serverDir");

            serverDir = Path.GetFullPath(serverDir);

            Log($"[Forge] 开始安装服务端 Forge {version}");

            string forgeVersion = ResolveForgeVersion(version, loaderParam);
            if (string.IsNullOrEmpty(forgeVersion))
                throw new Exception($"无法找到 Forge {version} 的版本");
            Log($"[Forge] 使用 Forge 版本: {forgeVersion}");

            Directory.CreateDirectory(serverDir);
            string librariesDir = Path.Combine(serverDir, "libraries");
            Directory.CreateDirectory(librariesDir);

            string installerPath = DownloadInstaller(forgeVersion);

            string tempDir = Path.Combine(Path.GetTempPath(),
                $"forge_unpack_{forgeVersion.Replace('.', '_')}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                ExtractInstaller(installerPath, tempDir);

                string profilePath = Path.Combine(tempDir, "install_profile.json");
                if (!File.Exists(profilePath))
                    throw new Exception("installer 中缺少 install_profile.json");

                var profile = ParseJson(File.ReadAllText(profilePath));
                if (profile == null)
                    throw new Exception("install_profile.json 解析失败");

                Dictionary<string, object> versionJson = null;
                if (profile.ContainsKey("spec"))
                {
                    string vjPath = Path.Combine(tempDir, "version.json");
                    if (File.Exists(vjPath))
                        versionJson = ParseJson(File.ReadAllText(vjPath));
                }
                else if (profile.ContainsKey("versionInfo"))
                {
                    versionJson = profile["versionInfo"] as Dictionary<string, object>;
                }

                if (versionJson == null)
                    throw new Exception("installer 中缺少 version.json");

                string mavenSrc = Path.Combine(tempDir, "maven");
                if (Directory.Exists(mavenSrc))
                    CopyDirectory(mavenSrc, librariesDir, skipExisting: true);

                var tasks = new List<DownloadTask>();
                CollectLibraryTasks(versionJson, librariesDir, tasks, "server");
                if (profile.ContainsKey("libraries"))
                    CollectLibraryTasks(profile, librariesDir, tasks, "server");
                tasks = DeduplicateTasks(tasks);

                Log($"[Forge] Libraries 任务: {tasks.Count}");

                if (tasks.Count > 0)
                    ParallelDownload(tasks, MAX_CONCURRENCY);

                if (profile.ContainsKey("processors"))
                {
                    Log("[Forge] 执行 processor（打补丁）...");
                    RunProcessors(profile, serverDir, tempDir, "server", installerPath);
                }

                // ============================================================
                // ★ 复制 shim jar 到服务端根目录（Forge 1.17+ 的 run.bat 需要）
                // ============================================================
                string shimSrc = Path.Combine(serverDir, "libraries", "net",
                    "minecraftforge", "forge", forgeVersion,
                    $"forge-{forgeVersion}-shim.jar");
                string shimDst = Path.Combine(serverDir, $"forge-{forgeVersion}-shim.jar");

                if (File.Exists(shimSrc))
                {
                    try
                    {
                        File.Copy(shimSrc, shimDst, true);
                        Log($"[Forge] 已复制 shim jar 到: {shimDst}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[Forge] 复制 shim jar 失败: {ex.Message}");
                    }
                }
                else
                {
                    Log($"[Forge] 警告: 未找到 shim jar: {shimSrc}");
                }

                // ============================================================
                // ★ 校验官方 run.bat / win_args.txt / user_jvm_args.txt 是否就位
                // ============================================================
                string runBat = Path.Combine(serverDir, "run.bat");
                string winArgs = Path.Combine(serverDir, "libraries", "net",
                    "minecraftforge", "forge", forgeVersion, "win_args.txt");
                string jvmArgs = Path.Combine(serverDir, "user_jvm_args.txt");

                if (File.Exists(runBat))
                    Log($"[Forge] 启动脚本已就绪: {runBat}");
                else
                    Log($"[Forge] 警告: 未找到 run.bat（processor 1 可能未成功）");

                if (File.Exists(winArgs))
                    Log($"[Forge] win_args.txt 已就绪");
                else
                    Log($"[Forge] 警告: 未找到 win_args.txt");

                if (File.Exists(jvmArgs))
                    Log($"[Forge] user_jvm_args.txt 已就绪，可在此文件里修改内存等 JVM 参数");
                else
                    Log($"[Forge] 警告: 未找到 user_jvm_args.txt");

                Log($"[Forge] 服务端 Forge {version} 安装完成");
                Log($"[Forge] 首次启动前请：1) 修改 eula.txt 接受 EULA  2) 编辑 user_jvm_args.txt 设置内存");
                Log($"[Forge] 启动方式：双击 {runBat}");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ===================== 版本解析 =====================
        private static string ResolveForgeVersion(string mcVersion, string loaderParam)
        {
            string param = null;
            if (!string.IsNullOrEmpty(loaderParam))
            {
                string first = loaderParam.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(first) &&
                    !first.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                    first != "不选择")
                {
                    param = first;
                }
            }

            if (!string.IsNullOrEmpty(param))
            {
                if (param.Contains("-") && param.StartsWith(mcVersion))
                    return param;
                if (Regex.IsMatch(param, @"^\d+\.\d+"))
                    return $"{mcVersion}-{param}";
            }

            return GetLatestForgeVersion(mcVersion);
        }

        private static string GetLatestForgeVersion(string mcVersion)
        {
            try
            {
                Log("[Forge] 获取版本列表...");
                string xml = DownloadString(FORGE_METADATA);
                if (string.IsNullOrEmpty(xml)) return null;

                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var nodes = doc.SelectNodes("//metadata/versioning/versions/version");
                if (nodes == null) return null;

                string prefix = mcVersion + "-";
                var candidates = new List<string>();
                foreach (XmlNode node in nodes)
                {
                    string v = node.InnerText.Trim();
                    if (v.StartsWith(prefix) && !v.Contains("-pre"))
                        candidates.Add(v);
                }

                if (candidates.Count == 0) return null;

                candidates.Sort((a, b) =>
                {
                    string va = a.Substring(prefix.Length);
                    string vb = b.Substring(prefix.Length);
                    return CompareVersions(vb, va);
                });

                Log($"[Forge] 最新版本: {candidates[0]}");
                return candidates[0];
            }
            catch (Exception ex)
            {
                Log($"[Forge] 获取版本列表失败: {ex.Message}");
                return null;
            }
        }

        private static int CompareVersions(string a, string b)
        {
            try { return new Version(a).CompareTo(new Version(b)); }
            catch { return string.Compare(a, b, StringComparison.Ordinal); }
        }

        // ===================== 下载安装器 =====================
        private static string DownloadInstaller(string forgeVersion)
        {
            string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
            string cacheDir = Path.Combine(launcherDir, "Launcher Setting", "Mode Loader Installer");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

            string fileName = $"forge-{forgeVersion}-installer.jar";
            string installerPath = Path.Combine(cacheDir, fileName);

            if (File.Exists(installerPath) && new FileInfo(installerPath).Length > 0)
            {
                try { ValidateJarFile(installerPath); }
                catch
                {
                    try { File.Delete(installerPath); } catch { }
                }

                if (File.Exists(installerPath))
                {
                    Log($"[Forge] 安装器已存在: {installerPath}");
                    return installerPath;
                }
            }

            string[] mirrors =
            {
                $"{FORGE_MAVEN}net/minecraftforge/forge/{forgeVersion}/{fileName}",
                $"{BMCL_BASE}/maven/net/minecraftforge/forge/{forgeVersion}/{fileName}",
            };

            Log($"[Forge] 下载安装器（{INSTALLER_CHUNKS} 段并行）: {fileName}");

            string lastError = null;
            foreach (var url in mirrors)
            {
                try
                {
                    DownloadLargeFile(url, installerPath, INSTALLER_CHUNKS);
                    Log($"[Forge] 安装器已下载: {installerPath}");
                    return installerPath;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Log($"[Forge] 源失败({url}): {ex.Message}");
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
                    Thread.Sleep(1000);
                }
            }

            throw new Exception($"下载 Forge 安装器失败（所有镜像均失败）: {lastError}");
        }

        // ===================== 大文件下载 =====================
        private static void DownloadLargeFile(string url, string dest, int chunks)
        {
            long totalSize = GetContentLength(url);

            if (totalSize <= 0)
            {
                Log("[Forge] 服务器不返回大小，退化为单线程下载");
                SingleDownloadWithProgress(url, dest, true);
                return;
            }

            Log($"[Forge] 文件大小: {FormatSize(totalSize)}，分 {chunks} 段并行");

            using (var fs = File.Create(dest))
                fs.SetLength(totalSize);

            long chunkSize = totalSize / chunks;

            var threads = new List<Thread>();
            var errors = new List<Exception>();
            var progress = new long[chunks];
            object progressLock = new object();
            long totalReceived = 0;
            long displayedReceived = 0;

            var sw = Stopwatch.StartNew();
            var reporter = new Thread(() =>
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(500);
                    long snapshot;
                    lock (progressLock)
                    {
                        snapshot = displayedReceived;
                        if (totalReceived >= totalSize) break;
                    }
                    double secs = sw.Elapsed.TotalSeconds;
                    double speed = secs > 0 ? snapshot / secs : 0;
                    int pct = (int)(snapshot * 100 / totalSize);
                    lock (_consoleLock)
                    {
                        Console.Write($"\r[下载中] forge-installer.jar - {pct}% " +
                                      $"({FormatSize(snapshot)}/{FormatSize(totalSize)}, " +
                                      $"{FormatSize((long)speed)}/s)   ");
                        try { Console.Out.Flush(); } catch { }
                    }
                }
            });
            reporter.IsBackground = true;
            reporter.Start();

            for (int i = 0; i < chunks; i++)
            {
                long start = i * chunkSize;
                long end = (i == chunks - 1) ? totalSize - 1 : (start + chunkSize - 1);
                int segIndex = i;

                var th = new Thread(() =>
                {
                    Exception lastEx = null;
                    for (int retry = 0; retry < RETRY_COUNT; retry++)
                    {
                        try
                        {
                            DownloadRangeSegment(url, dest, start, end, segIndex,
                                progress, progressLock, ref totalReceived, ref displayedReceived);
                            return;
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            lock (progressLock)
                            {
                                totalReceived -= progress[segIndex];
                                progress[segIndex] = 0;
                            }
                            if (retry < RETRY_COUNT - 1)
                                Thread.Sleep(1000 * (retry + 1));
                        }
                    }
                    lock (progressLock) errors.Add(lastEx);
                });
                th.IsBackground = true;
                th.Name = "InstallerChunk-" + i;
                th.Start();
                threads.Add(th);
            }

            foreach (var t in threads) t.Join();
            reporter.Join(1000);

            lock (_consoleLock)
            {
                Console.Write("\r" + new string(' ', 120) + "\r");
                try { Console.Out.Flush(); } catch { }
            }

            if (errors.Count > 0)
            {
                Log($"[Forge] 分段失败（{errors.Count} 段），回退单线程重下");
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                SingleDownloadWithProgress(url, dest, true);
                return;
            }

            try
            {
                ValidateJarFile(dest);
            }
            catch (Exception ex)
            {
                try { File.Delete(dest); } catch { }
                throw new Exception($"安装器文件校验失败: {ex.Message}");
            }
        }

        private static long GetContentLength(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.UserAgent = UA_STRING;
                req.AllowAutoRedirect = true;
                req.KeepAlive = false;
                req.Timeout = HTTP_TIMEOUT_MS;

                using (var resp = (HttpWebResponse)req.GetResponse())
                    return resp.ContentLength;
            }
            catch { return -1; }
        }

        private static void DownloadRangeSegment(
            string url, string dest, long start, long end, int segmentIndex,
            long[] progress, object progressLock,
            ref long totalReceived, ref long displayedReceived)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.AddRange(start, end);
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.Timeout = HTTP_TIMEOUT_MS;
            req.ReadWriteTimeout = 60000;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var netStream = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Open, FileAccess.Write,
                                           FileShare.Write, BUFFER_SIZE))
            {
                fs.Seek(start, SeekOrigin.Begin);

                byte[] buffer = new byte[BUFFER_SIZE];
                int read;
                long received = 0;

                while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fs.Write(buffer, 0, read);
                    received += read;

                    lock (progressLock)
                    {
                        long delta = received - progress[segmentIndex];
                        progress[segmentIndex] = received;
                        totalReceived += delta;

                        if (totalReceived > displayedReceived)
                            displayedReceived = totalReceived;
                    }
                }

                long expected = end - start + 1;
                if (received != expected)
                    throw new Exception($"段 {segmentIndex} 不完整: {received}/{expected}");
            }
        }

        // ===================== 解压安装器 =====================
        private static void ExtractInstaller(string installerPath, string destDir)
        {
            try
            {
                using (var fs = File.OpenRead(installerPath))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string target = Path.Combine(destDir, relative);

                        string targetDir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                            Directory.CreateDirectory(targetDir);

                        try
                        {
                            using (var entryStream = entry.Open())
                            using (var outStream = File.Create(target))
                            {
                                entryStream.CopyTo(outStream);
                            }
                        }
                        catch { }
                    }
                }
                Log($"[Forge] 安装器已解压到: {destDir}");
            }
            catch (Exception ex)
            {
                throw new Exception($"解压安装器失败: {ex.Message}", ex);
            }
        }

        // ===================== 复制目录 =====================
        private static void CopyDirectory(string src, string dest, bool skipExisting)
        {
            try
            {
                foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(src.Length).TrimStart('\\', '/');
                    string target = Path.Combine(dest, rel);
                    string targetDir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    if (skipExisting && File.Exists(target) &&
                        new FileInfo(target).Length == new FileInfo(file).Length)
                        continue;

                    try { File.Copy(file, target, true); } catch { }
                }
            }
            catch { }
        }

        // ===================== ★ 合并父版本 JSON =====================
        /// <summary>
        /// 把 Forge 的 version.json 和父版本（原版）JSON 合并，
        /// 去掉 inheritsFrom，把 id 改成与目录名一致，形成独立版本 JSON。
        /// </summary>
        private static Dictionary<string, object> MergeParentJson(
            Dictionary<string, object> parentRoot,
            Dictionary<string, object> childRoot,
            string versionId)
        {
            var merged = new Dictionary<string, object>();

            // 1. 父版本所有字段
            foreach (var kv in parentRoot)
                merged[kv.Key] = kv.Value;

            // 2. Forge 字段覆盖（libraries / arguments / inheritsFrom 除外）
            foreach (var kv in childRoot)
            {
                if (kv.Key == "inheritsFrom") continue;
                if (kv.Key == "libraries") continue;
                if (kv.Key == "arguments") continue;
                merged[kv.Key] = kv.Value;
            }

            // 3. ★ 让 id 与目录名一致
            merged["id"] = versionId;

            // 4. libraries：Forge 优先，父版本补充去重
            merged["libraries"] = MergeLibrariesForge(childRoot, parentRoot);

            // 5. arguments：父 + 子 拼接
            var mergedArgs = MergeArgumentsForge(childRoot, parentRoot);
            if (mergedArgs != null)
                merged["arguments"] = mergedArgs;

            // 6. minecraftArguments（1.12 及以下旧格式）：父 + " " + 子
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

            return merged;
        }

        private static List<object> MergeLibrariesForge(
            Dictionary<string, object> childRoot,
            Dictionary<string, object> parentRoot)
        {
            var result = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Forge 库优先
            if (childRoot.ContainsKey("libraries"))
            {
                var libs = childRoot["libraries"] as ArrayList;
                if (libs != null)
                {
                    foreach (var lib in libs)
                    {
                        var d = lib as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("name")) continue;
                        string name = d["name"].ToString();
                        if (seen.Add(name)) result.Add(lib);
                    }
                }
            }

            // 原版库补充
            if (parentRoot.ContainsKey("libraries"))
            {
                var libs = parentRoot["libraries"] as ArrayList;
                if (libs != null)
                {
                    foreach (var lib in libs)
                    {
                        var d = lib as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("name")) continue;
                        string name = d["name"].ToString();
                        if (seen.Add(name)) result.Add(lib);
                    }
                }
            }

            return result;
        }

        private static Dictionary<string, object> MergeArgumentsForge(
            Dictionary<string, object> childRoot,
            Dictionary<string, object> parentRoot)
        {
            var parentArgs = parentRoot.ContainsKey("arguments")
                ? parentRoot["arguments"] as Dictionary<string, object> : null;
            var childArgs = childRoot.ContainsKey("arguments")
                ? childRoot["arguments"] as Dictionary<string, object> : null;

            if (parentArgs == null && childArgs == null) return null;

            var mergedArgs = new Dictionary<string, object>();

            // jvm：父 + 子
            var jvmList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("jvm"))
            {
                var l = parentArgs["jvm"] as ArrayList;
                if (l != null) foreach (var item in l) jvmList.Add(item);
            }
            if (childArgs != null && childArgs.ContainsKey("jvm"))
            {
                var l = childArgs["jvm"] as ArrayList;
                if (l != null) foreach (var item in l) jvmList.Add(item);
            }
            if (jvmList.Count > 0) mergedArgs["jvm"] = jvmList;

            // game：父 + 子
            var gameList = new List<object>();
            if (parentArgs != null && parentArgs.ContainsKey("game"))
            {
                var l = parentArgs["game"] as ArrayList;
                if (l != null) foreach (var item in l) gameList.Add(item);
            }
            if (childArgs != null && childArgs.ContainsKey("game"))
            {
                var l = childArgs["game"] as ArrayList;
                if (l != null) foreach (var item in l) gameList.Add(item);
            }
            if (gameList.Count > 0) mergedArgs["game"] = gameList;

            return mergedArgs;
        }

        // ===================== libraries 收集 =====================
        private static void CollectLibraryTasks(
            Dictionary<string, object> root, string librariesDir,
            List<DownloadTask> tasks, string side)
        {
            var libraries = root.ContainsKey("libraries")
                ? root["libraries"] as ArrayList : null;
            if (libraries == null) return;

            foreach (var libObj in libraries)
            {
                var lib = libObj as Dictionary<string, object>;
                if (lib == null) continue;

                if (!IsLibraryAllowedForSide(lib, side)) continue;

                string name = lib.ContainsKey("name") ? lib["name"].ToString() : null;
                if (string.IsNullOrEmpty(name)) continue;

                string relPath = MavenNameToPath(name);
                if (string.IsNullOrEmpty(relPath)) continue;

                string url = null;

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

                if (string.IsNullOrEmpty(url) && lib.ContainsKey("url"))
                {
                    string baseUrl = lib["url"].ToString();
                    if (!string.IsNullOrEmpty(baseUrl))
                    {
                        if (!baseUrl.EndsWith("/")) baseUrl += "/";
                        url = baseUrl + relPath;
                    }
                }

                if (string.IsNullOrEmpty(url)) continue;

                tasks.Add(new DownloadTask
                {
                    Url = url,
                    Dest = Path.Combine(librariesDir, relPath),
                    Name = relPath,
                    Type = "libraries",
                    ShowProgress = false
                });
            }
        }

        private static bool IsLibraryAllowedForSide(Dictionary<string, object> lib, string side)
        {
            if (!lib.ContainsKey("rules")) return true;
            var rules = lib["rules"] as ArrayList;
            if (rules == null || rules.Count == 0) return true;

            bool allowed = false;
            foreach (var ruleObj in rules)
            {
                var rule = ruleObj as Dictionary<string, object>;
                if (rule == null) continue;
                string action = rule.ContainsKey("action") ? rule["action"].ToString() : "allow";

                if (rule.ContainsKey("sides"))
                {
                    var sides = rule["sides"] as ArrayList;
                    if (sides != null)
                    {
                        foreach (var s in sides)
                        {
                            if (s.ToString().Equals(side, StringComparison.OrdinalIgnoreCase))
                            {
                                allowed = (action == "allow");
                                break;
                            }
                        }
                    }
                }
                else
                {
                    allowed = (action == "allow");
                }
            }
            return allowed;
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

            if (version.Contains("@"))
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

        private static List<DownloadTask> DeduplicateTasks(List<DownloadTask> tasks)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<DownloadTask>(tasks.Count);
            foreach (var t in tasks)
            {
                if (string.IsNullOrEmpty(t.Dest)) continue;
                string key;
                try { key = Path.GetFullPath(t.Dest); }
                catch { key = t.Dest; }
                if (seen.Add(key)) result.Add(t);
            }
            if (result.Count != tasks.Count)
                Log($"[Forge] 去重: {tasks.Count} -> {result.Count}");
            return result;
        }

        // ===================== 运行 processors =====================
        private static void RunProcessors(
            Dictionary<string, object> profile, string rootDir,
            string installerDir, string side, string installerPathArg)
        {
            string binPatchFile = Path.Combine(installerDir, "data",
                side == "client" ? "client.lzma" : "server.lzma");
            Log($"[Forge] 预期 BINPATCH 文件: {binPatchFile} (存在={File.Exists(binPatchFile)})");

            var processors = profile["processors"] as ArrayList;
            if (processors == null) return;

            var dataMap = BuildDataMap(profile, rootDir, side);
            string librariesDir = Path.Combine(rootDir, "libraries");

            string installerPath = null;
            if (profile.ContainsKey("installer_path"))
                installerPath = profile["installer_path"].ToString();

            if (string.IsNullOrEmpty(installerPath) ||
                !File.Exists(installerPath))
            {
                installerPath = installerPathArg;
            }

            Log($"[Forge] processor 使用的 INSTALLER 路径: {installerPath}");

            string mcJar = side == "client"
                ? Path.Combine(rootDir, "versions", GetMinecraftVersion(profile),
                    GetMinecraftVersion(profile) + ".jar")
                : Path.Combine(rootDir, "server.jar");

            int idx = 0;
            foreach (var procObj in processors)
            {
                idx++;
                var proc = procObj as Dictionary<string, object>;
                if (proc == null) continue;

                if (proc.ContainsKey("sides"))
                {
                    var sides = proc["sides"] as ArrayList;
                    if (sides != null)
                    {
                        bool matched = false;
                        foreach (var s in sides)
                            if (s.ToString().Equals(side, StringComparison.OrdinalIgnoreCase))
                                matched = true;
                        if (!matched)
                        {
                            Log($"[Forge] 跳过 processor {idx}（不适用 {side}）");
                            continue;
                        }
                    }
                }

                if (proc.ContainsKey("outputs"))
                {
                    var outputs = proc["outputs"] as Dictionary<string, object>;
                    if (outputs != null)
                    {
                        bool allExist = true;
                        foreach (var kv in outputs)
                        {
                            string outPath = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                            string fullPath = Path.Combine(librariesDir, outPath);
                            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                            {
                                allExist = false;
                                break;
                            }
                        }
                        if (allExist)
                        {
                            Log($"[Forge] processor {idx} 输出已存在，跳过");
                            continue;
                        }
                    }
                }

                string jarCoord = proc.ContainsKey("jar") ? proc["jar"].ToString() : null;
                if (string.IsNullOrEmpty(jarCoord))
                {
                    Log($"[Forge] processor {idx} 无 jar 字段，跳过");
                    continue;
                }

                string procJarRel = MavenNameToPath(jarCoord);
                string procJar = Path.Combine(librariesDir, procJarRel);
                if (!File.Exists(procJar))
                {
                    Log($"[Forge] processor {idx} jar 不存在: {procJar}");
                    continue;
                }

                try { ValidateJarFile(procJar); }
                catch (Exception ex)
                {
                    Log($"[Forge] processor {idx} jar 损坏: {ex.Message}");
                    continue;
                }

                string mainClass = proc.ContainsKey("mainClass")
                    ? proc["mainClass"].ToString() : null;
                if (string.IsNullOrEmpty(mainClass))
                    mainClass = GetMainClassFromJar(procJar);
                if (string.IsNullOrEmpty(mainClass))
                {
                    Log($"[Forge] processor {idx} 无法确定 mainClass，跳过");
                    continue;
                }

                Log($"[Forge] processor {idx} mainClass = {mainClass}");

                var cpList = new List<string> { procJar };
                if (proc.ContainsKey("classpath"))
                {
                    var cps = proc["classpath"] as ArrayList;
                    if (cps != null)
                    {
                        foreach (var cp in cps)
                        {
                            string cpRel = MavenNameToPath(cp.ToString());
                            if (string.IsNullOrEmpty(cpRel)) continue;
                            string cpPath = Path.Combine(librariesDir, cpRel);
                            if (File.Exists(cpPath) && !cpList.Contains(cpPath))
                                cpList.Add(cpPath);
                        }
                    }
                }

                var argList = new List<string>();
                var rawArgList = new List<string>();
                if (proc.ContainsKey("args"))
                {
                    var args = proc["args"] as ArrayList;
                    if (args != null)
                    {
                        foreach (var a in args)
                        {
                            string raw = a.ToString();
                            string resolved = ResolveProcessorArg(a.ToString(), dataMap, rootDir, librariesDir, mcJar, installerPath, side, installerDir);
                            rawArgList.Add(raw);
                            argList.Add(resolved);
                        }
                    }
                }

                Log($"[Forge] processor {idx} 参数 ({argList.Count} 个):");
                for (int i = 0; i < argList.Count; i++)
                {
                    string mark = string.IsNullOrEmpty(argList[i]) ? " ★空!" : "";
                    Log($"[Forge]   [{i}] RAW='{rawArgList[i]}' -> '{argList[i]}'{mark}");
                }

                Log($"[Forge] 执行 processor {idx}: {jarCoord}");
                RunJavaProcess(cpList, mainClass, argList, rootDir);
            }
        }

        private static Dictionary<string, string> BuildDataMap(
            Dictionary<string, object> profile, string rootDir, string side)
        {
            var map = new Dictionary<string, string>();
            if (!profile.ContainsKey("data")) return map;

            var data = profile["data"] as Dictionary<string, object>;
            if (data == null) return map;

            foreach (var kv in data)
            {
                var val = kv.Value as Dictionary<string, object>;
                if (val == null) continue;

                string sideVal = null;
                if (val.ContainsKey(side))
                    sideVal = val[side].ToString();
                else if (val.ContainsKey("client"))
                    sideVal = val["client"].ToString();

                if (!string.IsNullOrEmpty(sideVal))
                    map[kv.Key] = sideVal;
            }
            return map;
        }

        private static string ResolveProcessorArg(
            string arg, Dictionary<string, string> dataMap,
            string rootDir, string librariesDir, string mcJar,
            string installerPath, string side,
            string installerDir)
        {
            if (string.IsNullOrEmpty(arg)) return arg;

            string result = arg;

            result = Regex.Replace(result, @"\[([^\]]+)\]", m =>
            {
                string coord = m.Groups[1].Value;
                string rel = MavenNameToPath(coord);
                if (string.IsNullOrEmpty(rel)) return m.Value;
                return Path.Combine(librariesDir, rel);
            });

            result = Regex.Replace(result, @"\{([^\}]+)\}", m =>
            {
                string key = m.Groups[1].Value;

                switch (key)
                {
                    case "SIDE": return side;
                    case "MINECRAFT_JAR": return mcJar;
                    case "ROOT": return rootDir;
                    case "LIBRARY_DIR": return librariesDir;
                    case "INSTALLER":
                        if (string.IsNullOrEmpty(installerPath))
                            throw new Exception(
                                "processors 需要 {INSTALLER}，但 installerPath 为空。");
                        return installerPath;
                }

                if (dataMap.ContainsKey(key))
                {
                    string v = dataMap[key];

                    if (v.StartsWith("[") && v.EndsWith("]"))
                    {
                        string rel = MavenNameToPath(v.Trim('[', ']'));
                        if (!string.IsNullOrEmpty(rel))
                            return Path.Combine(librariesDir, rel);
                    }

                    if (v.StartsWith("/") || v.StartsWith("\\"))
                    {
                        string rel = v.TrimStart('/', '\\')
                                      .Replace('/', Path.DirectorySeparatorChar);
                        return Path.Combine(installerDir, rel);
                    }

                    return v;
                }

                return m.Value;
            });

            return result;
        }

        private static string GetMainClassFromJar(string jarPath)
        {
            try
            {
                using (var fs = File.OpenRead(jarPath))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var entry = archive.GetEntry("META-INF/MANIFEST.MF");
                    if (entry == null) return null;

                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream))
                    {
                        string content = reader.ReadToEnd();
                        var m = Regex.Match(content, @"Main-Class:\s*(\S+)");
                        return m.Success ? m.Groups[1].Value : null;
                    }
                }
            }
            catch { }
            return null;
        }

        // ===================== 运行 Java Processor（UTF-8） =====================
        private static void RunJavaProcess(
            List<string> classpath, string mainClass,
            List<string> args, string workingDir)
        {
            string javaPath = FindJavaForServer();
            if (string.IsNullOrEmpty(javaPath))
                throw new Exception("未找到 Java 运行时");

            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == null || args[i].Length == 0)
                    throw new Exception(
                        $"Processor 参数 [{i}] 为空字符串，会导致 --from/--to 解析错位。" +
                        $"请检查 install_profile.json 的 args 是否有未被解析的占位符。");
            }

            var absClasspath = new List<string>(classpath.Count);
            foreach (var p in classpath)
            {
                try { absClasspath.Add(Path.GetFullPath(p)); }
                catch { absClasspath.Add(p); }
            }
            try { workingDir = Path.GetFullPath(workingDir); } catch { }

            string cp = string.Join(";", absClasspath.ToArray());
            string fullArgs =
                "-Dfile.encoding=UTF-8 -Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 " +
                $"-cp \"{cp}\" {mainClass} " +
                string.Join(" ", args.ConvertAll(a =>
                    a.Contains(" ") ? "\"" + a + "\"" : a).ToArray());

            Log($"[Processor] Java: {javaPath}");
            Log($"[Processor] WorkingDir: {workingDir}");
            Log($"[Processor] MainClass: {mainClass}");
            Log($"[Processor] Classpath ({absClasspath.Count} 条):");
            foreach (var p in absClasspath)
            {
                bool exists = false;
                try { exists = File.Exists(p); } catch { }
                Log($"[Processor]   {(exists ? "√" : "×")} {p}");
            }
            Log($"[Processor] 完整命令行: {fullArgs}");

            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = fullArgs,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log("[Processor] " + e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log("[Processor ERR] " + e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(PROCESSOR_TIMEOUT_MS);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    process.WaitForExit(5000);
                    throw new Exception("Processor 超时");
                }

                if (process.ExitCode != 0)
                    throw new Exception($"Processor 失败，退出码 {process.ExitCode}");
            }
        }

        private static string GetMinecraftVersion(Dictionary<string, object> profile)
        {
            if (profile.ContainsKey("minecraft"))
            {
                string v = profile["minecraft"].ToString();
                if (!string.IsNullOrEmpty(v)) return v;
            }

            if (profile.ContainsKey("install"))
            {
                var install = profile["install"] as Dictionary<string, object>;
                if (install != null && install.ContainsKey("minecraft"))
                    return install["minecraft"].ToString();
            }

            if (profile.ContainsKey("version"))
                return profile["version"].ToString();

            return "";
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

            Log($"[Forge] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");
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
                                Console.WriteLine($"[Forge] 下载失败: {task.Name} -> {ex.Message}");
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
                worker.Name = "ForgeDownloader-" + i;
                worker.Priority = ThreadPriority.Normal;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.WriteLine($"[Forge] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");

                if (failed > 0)
                {
                    Console.WriteLine($"[Forge] 失败清单（前 {Math.Min(failures.Count, 20)} 条）:");
                    int n = Math.Min(failures.Count, 20);
                    for (int i = 0; i < n; i++)
                        Console.WriteLine($"  - {failures[i]}");
                }
            }

            if (failed > 0)
                throw new Exception($"[Forge] 有 {failed}/{total} 个库下载失败");
        }

        private static void PrintProgressLine(int skipped, int failed, int completed, int total)
        {
            double pct = total == 0 ? 100 : (completed * 100.0 / total);
            Console.Write($"\r[Forge] 跳过 {skipped} | 失败 {failed} | " +
                          $"总进度 {pct:F1}% ({completed}/{total})  ");
            try { Console.Out.Flush(); } catch { }
        }

        // ===================== 底层下载 =====================
        private static bool DownloadFile(string url, string dest, bool showProgress)
        {
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            {
                bool isJar = dest.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                             dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                if (isJar)
                {
                    try
                    {
                        ValidateJarFile(dest);
                        return true;
                    }
                    catch
                    {
                        try { File.Delete(dest); } catch { }
                    }
                }
                else
                {
                    return true;
                }
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
                        SingleDownloadWithProgress(u, tmpPath, showProgress);

                        bool isJar = dest.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                                     dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                        if (isJar)
                        {
                            try { ValidateJarFile(tmpPath); }
                            catch (Exception ex)
                            {
                                try { File.Delete(tmpPath); } catch { }
                                throw new Exception($"文件校验失败: {ex.Message}");
                            }
                        }

                        if (File.Exists(dest)) { try { File.Delete(dest); } catch { } }
                        File.Move(tmpPath, dest);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        Log($"[Forge] 下载失败({u}): {ex.Message}");

                        string msg = ex.Message ?? "";
                        if (msg.Contains("429") || msg.Contains("403") ||
                            msg.Contains("Too Many"))
                            continue;
                    }
                }
                if (retry < RETRY_COUNT - 1)
                    Thread.Sleep(500 * (retry + 1));
            }

            throw new Exception($"下载 {dest} 失败: {lastEx?.Message}", lastEx);
        }

        private static string[] BuildUrlCandidates(string url)
        {
            var list = new List<string>();

            if (!string.IsNullOrEmpty(url)) list.Add(url);

            if (url.StartsWith(FORGE_MAVEN, StringComparison.OrdinalIgnoreCase))
            {
                string rest = url.Substring(FORGE_MAVEN.Length);
                list.Add(BMCL_BASE + "/maven/" + rest);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var u in list)
                if (!string.IsNullOrEmpty(u) && seen.Add(u)) result.Add(u);
            return result.ToArray();
        }

        private static void SingleDownloadWithProgress(string url, string dest, bool showProgress)
        {
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
            Exception lastEx = null;
            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                try
                {
                    using (var resp = SendRequestIPv4First(url, HTTP_TIMEOUT_MS, HTTP_TIMEOUT_MS))
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (retry < RETRY_COUNT - 1) Thread.Sleep(500 * (retry + 1));
                }
            }
            throw new Exception($"下载 {url} 失败: {lastEx?.Message}", lastEx);
        }

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
                    {
                        ipv4List.Add(a);
                        if (ipv4List.Count >= 2) break;
                    }
                }
            }
            catch { }

            if (ipv4List.Count == 0)
            {
                Log($"[Forge] 未解析到 IPv4，退回原 URL: {url}");
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
                catch (Exception ex) { lastEx = ex; }
            }

            throw new Exception($"IPv4 都失败（{ipv4List.Count} 个）: {lastEx?.Message}", lastEx);
        }

        private static HttpWebResponse SendRaw(string url, string hostHeader,
                                               int timeoutMs, int readWriteTimeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = readWriteTimeoutMs;
            req.AutomaticDecompression = DecompressionMethods.None;

            if (!string.IsNullOrEmpty(hostHeader))
                req.Host = hostHeader;

            var resp = (HttpWebResponse)req.GetResponse();

            string contentType = resp.ContentType ?? "";
            if (contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0 ||
                contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                resp.Close();
                throw new Exception($"服务器返回 {contentType}（可能是错误页），已中止");
            }

            return resp;
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
                    throw new Exception($"zip/jar 文件损坏: {ex.Message}");
                }
            }
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

            string[] roots =
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

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
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