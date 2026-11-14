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
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    internal class InstallForge
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

        // ===================== 统一异常日志 =====================
        private static void LogEx(string context, Exception ex, LogLevel level = LogLevel.Error)
        {
            recordLog(level, context + " -> " + (ex == null ? "null" : ex.ToString()));
        }

        // ===================== 客户端 =====================
        public static string InstallClient(string version, string minecraftDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            minecraftDir = Path.GetFullPath(minecraftDir);

            recordLog(LogLevel.Info, $"[Forge] 开始安装客户端 Forge {version}");

            string forgeVersion = ResolveForgeVersion(version, loaderParam);
            if (string.IsNullOrEmpty(forgeVersion))
                throw new Exception($"无法找到 Forge {version} 的版本");
            recordLog(LogLevel.Info, $"[Forge] 使用 Forge 版本: {forgeVersion}");

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
                    throw new Exception($"[Forge] installer 中缺少 install_profile.json: {profilePath}");

                var profile = ParseJson(File.ReadAllText(profilePath));
                if (profile == null)
                    throw new Exception($"[Forge] install_profile.json 解析失败: {profilePath}");

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
                    throw new Exception("[Forge] installer 中缺少 version.json（或 versionInfo）");

                string mavenSrc = Path.Combine(tempDir, "maven");
                if (Directory.Exists(mavenSrc))
                    CopyDirectory(mavenSrc, librariesDir, skipExisting: true);

                var tasks = new List<DownloadTask>();
                CollectLibraryTasks(versionJson, librariesDir, tasks, "client");
                if (profile.ContainsKey("libraries"))
                    CollectLibraryTasks(profile, librariesDir, tasks, "client");
                tasks = DeduplicateTasks(tasks);

                recordLog(LogLevel.Info, $"[Forge] Libraries 任务: {tasks.Count}");

                if (tasks.Count > 0)
                    ParallelDownload(tasks, MAX_CONCURRENCY);

                // ============================================================
                // ★ 合并父版本 JSON（展开 inheritsFrom，并锁定 id/jar）
                // ============================================================
                if (versionJson.ContainsKey("inheritsFrom"))
                {
                    string parentId = versionJson["inheritsFrom"].ToString();
                    string parentJsonPath = Path.Combine(versionsDir, parentId, parentId + ".json");

                    if (!File.Exists(parentJsonPath))
                    {
                        throw new Exception(
                            $"[Forge] 找不到父版本 JSON: {parentJsonPath}\n" +
                            "请先执行 Vanilla.InstallClient 完成原版安装。");
                    }

                    var parentJson = ParseJson(File.ReadAllText(parentJsonPath));
                    if (parentJson == null)
                        throw new Exception($"[Forge] 父版本 JSON 解析失败: {parentJsonPath}");

                    versionJson = MergeParentJson(parentJson, versionJson, versionId);
                    recordLog(LogLevel.Info, $"[Forge] 已合并父版本 {parentId}（inheritsFrom 已展开，id/jar={versionId}）");
                }
                else
                {
                    // 无 inheritsFrom 也要强制锁定 id/jar，保证目录名 == id
                    versionJson["id"] = versionId;
                    versionJson["jar"] = versionId;
                    recordLog(LogLevel.Info, $"[Forge] 无 inheritsFrom，强制锁定 id/jar={versionId}");
                }

                string targetJson = Path.Combine(versionDir, versionId + ".json");
                try
                {
                    // 用无 BOM 的 UTF-8 写出，避免某些启动器 JSON 解析失败
                    File.WriteAllText(targetJson,
                        new JavaScriptSerializer().Serialize(versionJson),
                        new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    throw new Exception($"[Forge] 写入版本 JSON 失败: {targetJson}", ex);
                }
                recordLog(LogLevel.Info, $"[Forge] 版本 JSON 已保存: {targetJson}");

                // ============================================================
                // ★ 执行 processor（失败一律 throw）
                // ============================================================
                if (profile.ContainsKey("processors"))
                {
                    recordLog(LogLevel.Info, $"[Forge] 执行 processor（打补丁）...");
                    RunProcessors(profile, minecraftDir, tempDir, "client", installerPath);
                }
                else
                {
                    recordLog(LogLevel.Info, $"[Forge] 无 processor，跳过");
                }

                // ============================================================
                // ★ 复制原版核心 jar 到 Forge 版本目录（jar/id 已指向 versionId，必须存在）
                // ============================================================
                CopyOriginalJarOrThrow(versionsDir, version, versionDir, versionId);

                recordLog(LogLevel.Info, $"[Forge] 客户端 Forge {version} 安装完成");
                return versionId;   // 供 LiteLoader 组合使用
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    LogEx($"[Forge] 清理临时目录失败(不致命): {tempDir}", ex, LogLevel.Warn);
                }
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

            recordLog(LogLevel.Info, $"[Forge] 开始安装服务端 Forge {version}");

            string forgeVersion = ResolveForgeVersion(version, loaderParam);
            if (string.IsNullOrEmpty(forgeVersion))
                throw new Exception($"无法找到 Forge {version} 的版本");
            recordLog(LogLevel.Info, $"[Forge] 使用 Forge 版本: {forgeVersion}");

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
                    throw new Exception($"[Forge] installer 中缺少 install_profile.json: {profilePath}");

                var profile = ParseJson(File.ReadAllText(profilePath));
                if (profile == null)
                    throw new Exception($"[Forge] install_profile.json 解析失败: {profilePath}");

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
                    throw new Exception("[Forge] installer 中缺少 version.json（或 versionInfo）");

                string mavenSrc = Path.Combine(tempDir, "maven");
                if (Directory.Exists(mavenSrc))
                    CopyDirectory(mavenSrc, librariesDir, skipExisting: true);

                var tasks = new List<DownloadTask>();
                CollectLibraryTasks(versionJson, librariesDir, tasks, "server");
                if (profile.ContainsKey("libraries"))
                    CollectLibraryTasks(profile, librariesDir, tasks, "server");
                tasks = DeduplicateTasks(tasks);

                recordLog(LogLevel.Info, $"[Forge] Libraries 任务: {tasks.Count}");

                if (tasks.Count > 0)
                    ParallelDownload(tasks, MAX_CONCURRENCY);

                if (profile.ContainsKey("processors"))
                {
                    recordLog(LogLevel.Info, $"[Forge] 执行 processor（打补丁）...");
                    RunProcessors(profile, serverDir, tempDir, "server", installerPath);
                }

                // ============================================================
                // ★ 复制 shim jar 到服务端根目录（Forge 1.17+ 的某些 run.bat 需要）
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
                        recordLog(LogLevel.Info, $"[Forge] 已复制 shim jar 到: {shimDst}");
                    }
                    catch (Exception ex)
                    {
                        // shim 复制失败不致命（大部分 run.bat 引用的仍是 libraries 里的那份），
                        // 但必须完整记录堆栈
                        LogEx($"[Forge] 复制 shim jar 失败(不致命): {shimSrc} -> {shimDst}", ex, LogLevel.Warn);
                    }
                }
                else
                {
                    recordLog(LogLevel.Warn, $"[Forge] 警告: 未找到 shim jar: {shimSrc}");
                }

                // ============================================================
                // ★ 校验官方 run.bat / win_args.txt / user_jvm_args.txt 是否就位
                // ============================================================
                string runBat = Path.Combine(serverDir, "run.bat");
                string winArgs = Path.Combine(serverDir, "libraries", "net",
                    "minecraftforge", "forge", forgeVersion, "win_args.txt");
                string jvmArgs = Path.Combine(serverDir, "user_jvm_args.txt");

                if (File.Exists(runBat))
                    recordLog(LogLevel.Info, $"[Forge] 启动脚本已就绪: {runBat}");
                else
                    throw new Exception(
                        $"[Forge] 安装失败：未找到 run.bat: {runBat}\n" +
                        "processor 1（installertools:generateRuntimeConfig / WriteArgs）可能未成功。");

                if (File.Exists(winArgs))
                    recordLog(LogLevel.Info, $"[Forge] win_args.txt 已就绪");
                else
                    throw new Exception(
                        $"[Forge] 安装失败：未找到 win_args.txt: {winArgs}\n" +
                        "processor 1 可能未成功。");

                if (File.Exists(jvmArgs))
                    recordLog(LogLevel.Info, $"[Forge] user_jvm_args.txt 已就绪，可在此文件里修改内存等 JVM 参数");
                else
                    recordLog(LogLevel.Warn, $"[Forge] 警告: 未找到 user_jvm_args.txt（可能已被 processor 使用默认值）");

                recordLog(LogLevel.Info, $"[Forge] 服务端 Forge {version} 安装完成");
                recordLog(LogLevel.Info, $"[Forge] 首次启动前请：1) 修改 eula.txt 接受 EULA  2) 编辑 user_jvm_args.txt 设置内存");
                recordLog(LogLevel.Info, $"[Forge] 启动方式：双击 {runBat}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    LogEx($"[Forge] 清理临时目录失败(不致命): {tempDir}", ex, LogLevel.Warn);
                }
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
                // ★ 修复：原 StartsWith(mcVersion) 会把 "1.20.10-xxx" 误匹配成 mcVersion "1.20.1"
                if (param.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase))
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
                recordLog(LogLevel.Info, $"[Forge] 获取版本列表...");
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

                recordLog(LogLevel.Info, $"[Forge] 最新版本: {candidates[0]}");
                return candidates[0];
            }
            catch (Exception ex)
            {
                LogEx($"[Forge] 获取版本列表失败: {mcVersion}", ex);
                return null;
            }
        }

        private static int CompareVersions(string a, string b)
        {
            try { return new Version(a).CompareTo(new Version(b)); }
            catch
            {
                // 版本字符串非标准格式（含字母后缀），降级到字符串比较
                return string.Compare(a, b, StringComparison.Ordinal);
            }
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
                catch (Exception ex)
                {
                    LogEx($"[Forge] 已有安装器校验失败，删除重下: {installerPath}", ex, LogLevel.Warn);
                    try { File.Delete(installerPath); }
                    catch (Exception delEx) { LogEx($"[Forge] 删除损坏安装器失败: {installerPath}", delEx, LogLevel.Warn); }
                }

                if (File.Exists(installerPath))
                {
                    recordLog(LogLevel.Info, $"[Forge] 安装器已存在: {installerPath}");
                    return installerPath;
                }
            }

            string[] mirrors =
            {
                $"{FORGE_MAVEN}net/minecraftforge/forge/{forgeVersion}/{fileName}",
                $"{BMCL_BASE}/maven/net/minecraftforge/forge/{forgeVersion}/{fileName}",
            };

            recordLog(LogLevel.Info, $"[Forge] 下载安装器（{INSTALLER_CHUNKS} 段并行）: {fileName}");

            string lastError = null;
            foreach (var url in mirrors)
            {
                try
                {
                    DownloadLargeFile(url, installerPath, INSTALLER_CHUNKS);
                    recordLog(LogLevel.Info, $"[Forge] 安装器已下载: {installerPath}");
                    return installerPath;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    LogEx($"[Forge] 源失败({url})", ex);
                    try
                    {
                        if (File.Exists(installerPath)) File.Delete(installerPath);
                    }
                    catch (Exception delEx) { LogEx($"[Forge] 清理失败安装器失败: {installerPath}", delEx, LogLevel.Warn); }
                    Thread.Sleep(1000);
                }
            }

            throw new Exception($"下载 Forge 安装器失败（所有镜像均失败）: {lastError}");
        }

        // ===================== 大文件分段下载 =====================
        private static void DownloadLargeFile(string url, string dest, int chunks)
        {
            long totalSize = GetContentLength(url);

            if (totalSize <= 0)
            {
                recordLog(LogLevel.Info, $"[Forge] 服务器不返回大小，退化为单线程下载");
                SingleDownloadWithProgress(url, dest, true);
                return;
            }

            recordLog(LogLevel.Info, $"[Forge] 文件大小: {FormatSize(totalSize)}，分 {chunks} 段并行");

            // ★ 预分配文件：失败时明确提示磁盘空间不足
            try
            {
                using (var fs = File.Create(dest))
                    fs.SetLength(totalSize);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(dest)) File.Delete(dest); }
                catch (Exception delEx) { LogEx($"[Forge] 删除预分配失败的文件失败: {dest}", delEx, LogLevel.Warn); }
                throw new Exception(
                    $"[Forge] 预分配安装器文件失败（可能磁盘空间不足）: {dest} ({FormatSize(totalSize)})", ex);
            }

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
                            LogEx($"[Forge] 段 {segIndex} 下载失败（第 {retry + 1}/{RETRY_COUNT} 次）", ex, LogLevel.Warn);
                            if (retry < RETRY_COUNT - 1)
                                Thread.Sleep(1000 * (retry + 1));
                        }
                    }
                    if (lastEx != null)
                        LogEx($"[Forge] 段 {segIndex} 最终失败", lastEx);
                    lock (progressLock) errors.Add(lastEx);
                });
                th.IsBackground = true;
                th.Name = "InstallerChunk-" + i;
                th.Start();
                threads.Add(th);
            }

            foreach (var t in threads) t.Join();
            reporter.Join(2000);

            lock (_consoleLock)
            {
                Console.Write("\r" + new string(' ', 120) + "\r");
                try { Console.Out.Flush(); } catch { }
            }

            if (errors.Count > 0)
            {
                recordLog(LogLevel.Warn, $"[Forge] 分段失败（{errors.Count} 段），回退单线程重下");
                try { if (File.Exists(dest)) File.Delete(dest); }
                catch (Exception delEx) { LogEx($"[Forge] 删除分段失败文件失败: {dest}", delEx, LogLevel.Warn); }
                SingleDownloadWithProgress(url, dest, true);
                return;
            }

            try
            {
                ValidateJarFile(dest);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(dest)) File.Delete(dest); }
                catch (Exception delEx) { LogEx($"[Forge] 删除校验失败文件失败: {dest}", delEx, LogLevel.Warn); }
                throw new Exception($"安装器文件校验失败: {dest}", ex);
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
            catch (Exception ex)
            {
                // 服务器不返回 HEAD 是常见情况，走下面 Range 兜底，不刷屏日志
                LogEx($"[Forge] HEAD 请求失败（将尝试 Range 探测）: {url}", ex, LogLevel.Info);
                return -1;
            }
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
                        catch (Exception ex)
                        {
                            // 单个 entry 失败不立即 throw（有些 installer 里有无关的文档/资源），
                            // 由调用方后续验证 install_profile.json 是否完整
                            LogEx($"[Forge] 解压条目失败: {entry.FullName} -> {target}", ex);
                        }
                    }
                }
                recordLog(LogLevel.Info, $"[Forge] 安装器已解压到: {destDir}");
            }
            catch (Exception ex)
            {
                throw new Exception($"解压安装器失败: {installerPath} -> {destDir}", ex);
            }
        }

        // ===================== 复制目录 =====================
        private static void CopyDirectory(string src, string dest, bool skipExisting)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                throw new Exception($"[Forge] 枚举目录失败: {src}", ex);
            }

            foreach (var file in files)
            {
                try
                {
                    string rel = file.Substring(src.Length).TrimStart('\\', '/');
                    string target = Path.Combine(dest, rel);
                    string targetDir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    if (skipExisting && File.Exists(target) &&
                        new FileInfo(target).Length == new FileInfo(file).Length)
                        continue;

                    File.Copy(file, target, true);
                }
                catch (Exception ex)
                {
                    // 单个文件失败不中断整体（maven 目录很大，个别文件可能被占用），
                    // 但记录完整堆栈
                    LogEx($"[Forge] 复制文件失败: {file} -> {dest}", ex);
                }
            }
        }

        // ===================== ★ 复制原版核心 jar（失败必 throw） =====================
        private static void CopyOriginalJarOrThrow(string versionsDir, string originalVersion,
                                                     string versionDir, string versionId)
        {
            string originalClientJar = Path.Combine(versionsDir, originalVersion,
                                                     originalVersion + ".jar");
            string forgeVersionJar = Path.Combine(versionDir, versionId + ".jar");

            if (!File.Exists(originalClientJar) || new FileInfo(originalClientJar).Length == 0)
            {
                throw new Exception(
                    $"[Forge] 原版 client.jar 缺失: {originalClientJar}\n" +
                    "请先执行 Vanilla.InstallClient 完成原版安装。");
            }

            try
            {
                long srcLen = new FileInfo(originalClientJar).Length;
                if (File.Exists(forgeVersionJar) &&
                    new FileInfo(forgeVersionJar).Length == srcLen)
                {
                    recordLog(LogLevel.Info, $"[Forge] 版本核心已存在: {forgeVersionJar}");
                    return;
                }

                File.Copy(originalClientJar, forgeVersionJar, true);
                recordLog(LogLevel.Info, $"[Forge] 已复制原版核心到: {forgeVersionJar}");
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"[Forge] 复制原版核心失败: {originalClientJar} -> {forgeVersionJar}", ex);
            }
        }

        // ===================== ★ 合并父版本 JSON =====================
        /// <summary>
        /// 把 Forge 的 version.json 和父版本（原版）JSON 合并，
        /// 去掉 inheritsFrom，把 id 和 jar 都锁定为 versionId，形成独立版本 JSON。
        /// ★ jar 必须为 versionId：否则启动器会按 jar 字段去原版目录找 jar，
        ///   用户清空原版目录后就报"找不到版本"。
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

            // 3. ★ 让 id 和 jar 都与目录名一致
            merged["id"] = versionId;
            merged["jar"] = versionId;

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

                if (string.IsNullOrEmpty(url))
                {
                    // ★ 原来静默 continue，改为 Warn，让用户知道有什么库没被计划下载
                    recordLog(LogLevel.Warn, $"[Forge] 库无 url 字段，无法下载: {name}（side={side}）");
                    continue;
                }

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
                else recordLog(LogLevel.Info, $"[Forge] 去重丢弃重复目标: {t.Dest} (URL: {t.Url})");
            }
            if (result.Count != tasks.Count)
                recordLog(LogLevel.Info, $"[Forge] 去重: {tasks.Count} -> {result.Count}");
            return result;
        }

        // ===================== ★ 运行 processors（失败必须 throw） =====================
        /// <summary>
        /// 执行 install_profile.json 里的 processors。
        /// 只放过两类"正常跳过"：
        ///   1. sides 不匹配（仅适用 client / 仅适用 server）
        ///   2. outputs 已存在（重复安装场景）
        /// 其余任何失败（jar 缺失/损坏、mainClass 无法识别、结构错误、参数为空、
        /// Java 进程退出码非 0、超时）一律 throw，避免"装完了但启动崩"。
        /// </summary>
        private static void RunProcessors(
            Dictionary<string, object> profile, string rootDir,
            string installerDir, string side, string installerPathArg)
        {
            string binPatchFile = Path.Combine(installerDir, "data",
                side == "client" ? "client.lzma" : "server.lzma");
            recordLog(LogLevel.Info, $"[Forge] 预期 BINPATCH 文件: {binPatchFile} (存在={File.Exists(binPatchFile)})");

            var processors = profile["processors"] as ArrayList;
            if (processors == null) return;

            var dataMap = BuildDataMap(profile, rootDir, side);
            string librariesDir = Path.Combine(rootDir, "libraries");

            string installerPath = null;
            if (profile.ContainsKey("installer_path") && profile["installer_path"] != null)
                installerPath = profile["installer_path"].ToString();

            if (string.IsNullOrEmpty(installerPath) || !File.Exists(installerPath))
            {
                if (string.IsNullOrEmpty(installerPathArg) || !File.Exists(installerPathArg))
                    throw new Exception(
                        "[Forge] processors 需要 {INSTALLER}，但既无 installer_path 也无 installerPathArg 且文件不存在");
                installerPath = installerPathArg;
            }

            recordLog(LogLevel.Info, $"[Forge] processor 使用的 INSTALLER 路径: {installerPath}");

            string mcVersion = GetMinecraftVersion(profile);
            string mcJar = side == "client"
                ? Path.Combine(rootDir, "versions", mcVersion, mcVersion + ".jar")
                : Path.Combine(rootDir, "server.jar");

            int idx = 0;
            foreach (var procObj in processors)
            {
                idx++;
                var proc = procObj as Dictionary<string, object>;
                if (proc == null)
                    throw new Exception($"[Forge] processor {idx} 结构错误（不是 JSON 对象）");

                // ---- 正常跳过 1：sides 不匹配 ----
                if (proc.ContainsKey("sides"))
                {
                    var sides = proc["sides"] as ArrayList;
                    if (sides != null && sides.Count > 0)
                    {
                        bool matched = false;
                        foreach (var s in sides)
                        {
                            if (s != null && s.ToString().Equals(side, StringComparison.OrdinalIgnoreCase))
                            { matched = true; break; }
                        }
                        if (!matched)
                        {
                            recordLog(LogLevel.Info, $"[Forge] 跳过 processor {idx}（不适用 {side}）");
                            continue;
                        }
                    }
                }

                // ---- 正常跳过 2：outputs 已存在 ----
                if (proc.ContainsKey("outputs"))
                {
                    var outputs = proc["outputs"] as Dictionary<string, object>;
                    if (outputs != null && outputs.Count > 0)
                    {
                        bool allExist = true;
                        foreach (var kv in outputs)
                        {
                            string outPath = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                            string fullPath = Path.Combine(librariesDir, outPath);
                            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                            { allExist = false; break; }
                        }
                        if (allExist)
                        {
                            recordLog(LogLevel.Info, $"[Forge] processor {idx} 输出已存在，跳过");
                            continue;
                        }
                    }
                }

                // ---- 以下全部"必须成功"，失败 throw ----
                string jarCoord = (proc.ContainsKey("jar") && proc["jar"] != null)
                    ? proc["jar"].ToString() : null;
                if (string.IsNullOrEmpty(jarCoord))
                    throw new Exception($"[Forge] processor {idx} 缺少 jar 字段");

                string procJarRel = MavenNameToPath(jarCoord);
                if (string.IsNullOrEmpty(procJarRel))
                    throw new Exception($"[Forge] processor {idx} jar 坐标无法解析: {jarCoord}");

                string procJar = Path.Combine(librariesDir, procJarRel);
                if (!File.Exists(procJar))
                    throw new Exception(
                        $"[Forge] processor {idx} 的 jar 缺失: {procJar}\n" +
                        "可能是 libraries 下载不完整或 maven 目录复制失败。");

                try { ValidateJarFile(procJar); }
                catch (Exception ex)
                {
                    throw new Exception($"[Forge] processor {idx} 的 jar 损坏: {procJar}", ex);
                }

                string mainClass = (proc.ContainsKey("mainClass") && proc["mainClass"] != null)
                    ? proc["mainClass"].ToString() : null;
                if (string.IsNullOrEmpty(mainClass))
                    mainClass = GetMainClassFromJar(procJar);
                if (string.IsNullOrEmpty(mainClass))
                    throw new Exception(
                        $"[Forge] processor {idx} 无法确定 mainClass（JSON 无 mainClass，jar 无 Main-Class）: {procJar}");

                recordLog(LogLevel.Info, $"[Forge] processor {idx} mainClass = {mainClass}");

                var cpList = new List<string> { procJar };
                if (proc.ContainsKey("classpath"))
                {
                    var cps = proc["classpath"] as ArrayList;
                    if (cps != null)
                    {
                        foreach (var cp in cps)
                        {
                            if (cp == null) continue;
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
                            string raw = a?.ToString() ?? "";
                            string resolved = ResolveProcessorArg(raw, dataMap, rootDir,
                                librariesDir, mcJar, installerPath, side, installerDir);
                            rawArgList.Add(raw);
                            argList.Add(resolved);
                        }
                    }
                }

                recordLog(LogLevel.Info, $"[Forge] processor {idx} 参数 ({argList.Count} 个):");
                for (int i = 0; i < argList.Count; i++)
                {
                    string mark = string.IsNullOrEmpty(argList[i]) ? " ★空!" : "";
                    recordLog(LogLevel.Info, $"[Forge]   [{i}] RAW='{rawArgList[i]}' -> '{argList[i]}'{mark}");
                }

                recordLog(LogLevel.Info, $"[Forge] 执行 processor {idx}: {jarCoord}");

                // ★ RunJavaProcess 抛出的异常直接向上传播，不吞
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
                if (val.ContainsKey(side) && val[side] != null)
                    sideVal = val[side].ToString();
                else if (val.ContainsKey("client") && val["client"] != null)
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
            catch (Exception ex)
            {
                LogEx($"[Forge] 读取 jar MANIFEST 失败: {jarPath}", ex, LogLevel.Warn);
            }
            return null;
        }

        // ===================== 运行 Java Processor =====================
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
                catch (Exception ex)
                {
                    LogEx($"[Forge] 解析 classpath 绝对路径失败，使用原值: {p}", ex, LogLevel.Warn);
                    absClasspath.Add(p);
                }
            }
            try { workingDir = Path.GetFullPath(workingDir); }
            catch (Exception ex) { LogEx($"[Forge] 解析 WorkingDir 绝对路径失败: {workingDir}", ex, LogLevel.Warn); }

            string cp = string.Join(";", absClasspath.ToArray());
            string fullArgs =
                "-Dfile.encoding=UTF-8 -Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 " +
                $"-cp \"{cp}\" {mainClass} " +
                string.Join(" ", args.ConvertAll(a =>
                    a.Contains(" ") ? "\"" + a + "\"" : a).ToArray());

            recordLog(LogLevel.Info, $"[Forge-Processor] Java: {javaPath}");
            recordLog(LogLevel.Info, $"[Forge-Processor] WorkingDir: {workingDir}");
            recordLog(LogLevel.Info, $"[Forge-Processor] MainClass: {mainClass}");
            recordLog(LogLevel.Info, $"[Forge-Processor] Classpath ({absClasspath.Count} 条):");
            foreach (var p in absClasspath)
            {
                bool exists = false;
                try { exists = File.Exists(p); }
                catch (Exception ex) { LogEx($"[Forge] 检查 classpath 文件失败: {p}", ex, LogLevel.Warn); }
                recordLog(LogLevel.Info, $"[Forge-Processor]   {(exists ? "√" : "×")} {p}");
            }
            recordLog(LogLevel.Info, $"[Forge-Processor] 完整命令行: {fullArgs}");

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
                        recordLog(LogLevel.Info, "[Forge-Processor] " + e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        recordLog(LogLevel.Info, "[Forge-Processor ERR] " + e.Data);
                };

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    throw new Exception($"[Forge] 启动 processor java 进程失败: {javaPath}", ex);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(PROCESSOR_TIMEOUT_MS);
                if (!exited)
                {
                    try { process.Kill(); }
                    catch (Exception ex) { LogEx($"[Forge] Kill 超时 processor 进程失败", ex, LogLevel.Warn); }
                    process.WaitForExit(5000);
                    throw new Exception($"Processor 超时（>{PROCESSOR_TIMEOUT_MS / 60000} 分钟）: {mainClass}");
                }

                // 等待异步输出全部读完
                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new Exception(
                        $"Processor 失败（退出码 {process.ExitCode}）: {mainClass}\n" +
                        "请查看上方 [Forge-Processor] / [Forge-Processor ERR] 日志定位具体原因。");
            }
        }

        private static string GetMinecraftVersion(Dictionary<string, object> profile)
        {
            if (profile.ContainsKey("minecraft") && profile["minecraft"] != null)
            {
                string v = profile["minecraft"].ToString();
                if (!string.IsNullOrEmpty(v)) return v;
            }

            if (profile.ContainsKey("install"))
            {
                var install = profile["install"] as Dictionary<string, object>;
                if (install != null && install.ContainsKey("minecraft") && install["minecraft"] != null)
                    return install["minecraft"].ToString();
            }

            if (profile.ContainsKey("version") && profile["version"] != null)
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

            recordLog(LogLevel.Info, $"[Forge] 开始并行下载（{maxConcurrency} 线程，共 {total} 个任务）...");
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
                            LogEx($"[Forge] 下载失败: {task.Name}", ex);
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
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.WriteLine($"[Forge] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");

                if (failed > 0 && failures.Count > 0)
                {
                    Console.WriteLine($"[Forge] 失败清单（前 {Math.Min(failures.Count, 20)} 条）:");
                    int n = Math.Min(failures.Count, 20);
                    for (int i = 0; i < n; i++)
                        Console.WriteLine($"  - {failures[i]}");
                }
            }

            if (failed > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"[Forge] 有 {failed}/{total} 个库下载失败");
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
            Console.Write($"\r[Forge] 跳过 {skipped} | 失败 {failed} | " +
                          $"总进度 {pct:F1}% ({completed}/{total})  ");
            try { Console.Out.Flush(); } catch { }
        }

        // ===================== 底层下载 =====================
        private static bool DownloadFile(string url, string dest, bool showProgress)
        {
            bool isJar = dest.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                         dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            {
                if (isJar)
                {
                    try
                    {
                        ValidateJarFile(dest);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogEx($"[Forge] 已有文件校验失败，删除重下: {dest}", ex, LogLevel.Warn);
                        try { File.Delete(dest); }
                        catch (Exception delEx) { LogEx($"[Forge] 删除损坏文件失败: {dest}", delEx, LogLevel.Warn); }
                    }
                }
                else
                {
                    return true;
                }
            }

            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { throw new Exception($"[Forge] 创建目录失败: {dir}", ex); }
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
                        SingleDownloadWithProgress(u, tmpPath, showProgress);

                        if (isJar)
                        {
                            try { ValidateJarFile(tmpPath); }
                            catch (Exception ex)
                            {
                                try { File.Delete(tmpPath); }
                                catch (Exception delEx) { LogEx($"[Forge] 删除损坏 .tmp 失败: {tmpPath}", delEx, LogLevel.Warn); }
                                throw new Exception($"文件校验失败: {tmpPath}", ex);
                            }
                        }

                        MoveOrReplace(tmpPath, dest);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                        catch (Exception delEx) { LogEx($"[Forge] 清理 .tmp 失败: {tmpPath}", delEx, LogLevel.Warn); }
                        LogEx($"[Forge] 下载失败({u})", ex);
                    }
                }
                if (retry < RETRY_COUNT - 1)
                    Thread.Sleep(500 * (retry + 1));
            }

            throw new Exception($"下载 {dest} 失败: {lastEx?.Message}", lastEx);
        }

        /// <summary>
        /// 原子替换文件：优先 File.Replace（保留原文件直到成功），
        /// 失败回退 File.Delete + File.Move，并带杀软占用重试。
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
                catch (PlatformNotSupportedException ex)
                {
                    LogEx($"[Forge] File.Replace 平台不支持，回退 Delete+Move: {dst}", ex, LogLevel.Warn);
                }
                catch (IOException ex)
                {
                    LogEx($"[Forge] File.Replace 失败（可能被占用），回退 Delete+Move: {dst}", ex, LogLevel.Warn);
                }

                // 回退路径：删除目标
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { File.Delete(dst); break; }
                    catch (IOException ex)
                    {
                        if (attempt == 2)
                            throw new Exception($"[Forge] 删除目标文件失败（可能被占用）: {dst}", ex);
                        LogEx($"[Forge] 删除目标文件失败，重试 {attempt + 1}: {dst}", ex, LogLevel.Warn);
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
                        throw new Exception($"[Forge] 移动临时文件失败（可能被占用）: {src} -> {dst}", ex);
                    LogEx($"[Forge] File.Move 失败，重试 {attempt + 1}: {dst}", ex, LogLevel.Warn);
                    Thread.Sleep(200 * (attempt + 1));
                }
            }
        }

        private static string[] BuildUrlCandidates(string url)
        {
            var list = new List<string>();

            if (!string.IsNullOrEmpty(url))
            {
                list.Add(url);

                if (url.StartsWith(FORGE_MAVEN, StringComparison.OrdinalIgnoreCase))
                {
                    string rest = url.Substring(FORGE_MAVEN.Length);
                    list.Add(BMCL_BASE + "/maven/" + rest);
                }
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
                    LogEx($"[Forge] 请求失败({url})（第 {retry + 1}/{RETRY_COUNT} 次）", ex, LogLevel.Warn);
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
            catch (Exception ex)
            {
                LogEx($"[Forge] DNS 解析失败: {uri.Host}", ex, LogLevel.Warn);
            }

            if (ipv4List.Count == 0)
            {
                recordLog(LogLevel.Warn, $"[Forge] 未解析到 IPv4，退回原 URL: {url}");
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

            // ★ 只拒 text/html（真正的错误页）。不再拒 application/json——
            //   某些镜像对 XML 请求返回 application/json 是合法的。
            string contentType = resp.ContentType ?? "";
            if (contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                resp.Close();
                throw new Exception($"服务器返回 {contentType}（可能是错误页），已中止: {url}");
            }

            return resp;
        }

        private static void ValidateJarFile(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                if (fs.Length < 4)
                    throw new Exception($"文件太小，不是有效 zip/jar: {path}（{fs.Length} B）");

                byte[] header = new byte[4];
                int n = fs.Read(header, 0, 4);
                if (n < 4 || header[0] != 0x50 || header[1] != 0x4B)
                    throw new Exception($"不是有效的 zip/jar 文件（可能是 HTML 错误页）: {path}");

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
                            throw new Exception($"zip/jar 内无任何文件（可能是半截下载）: {path}");
                    }
                }
                catch (InvalidDataException ex)
                {
                    throw new Exception($"zip/jar 文件损坏: {path}", ex);
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
                    LogEx($"[Forge] 枚举 Java 目录失败: {root}", ex, LogLevel.Warn);
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
            catch (Exception ex)
            {
                LogEx("[Forge] JSON 解析失败", ex);
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