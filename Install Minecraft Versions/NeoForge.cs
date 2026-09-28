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
    internal class NeoForge
    {
        // ===================== NeoForge 源 =====================
        private const string NEOFORGE_MAVEN = "https://maven.neoforged.net/releases/";
        private const string NEOFORGE_METADATA = NEOFORGE_MAVEN + "net/neoforged/neoforge/maven-metadata.xml";
        private const string BMCL_BASE = "https://bmclapi2.bangbang93.com";
        private const string SERVER_STARTER_URL =
            "https://github.com/NeoForged/serverstarterjar/releases/latest/download/server.jar";

        // ===================== 下载参数 =====================
        private const int MAX_CONCURRENCY = 6;
        private const int PER_HOST_CONCURRENCY = 2;
        private const int RETRY_COUNT = 5;
        private const int BUFFER_SIZE = 256 * 1024;
        private const int PROGRESS_REFRESH_MS = 100;

        private const int BMCLAPI_TIMEOUT_MS = 30000;
        private const int OFFICIAL_TIMEOUT_MS = 15000;
        private const int READ_WRITE_TIMEOUT_MS = 30000;
        private const int PROCESSOR_TIMEOUT_MS = 15 * 60 * 1000;
        private const int INSTALLER_CHUNKS = 8;

        private const int STALL_THRESHOLD_SECONDS = 10;

        private const int INSTALLER_ROUNDS = 3;
        private const int LIBRARY_ROUNDS = 3;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly object _consoleLock = new object();
        private static int _lastProgressTick = 0;

        private static readonly Dictionary<string, SemaphoreSlim> _hostSems =
            new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _hostSemsLock = new object();

        // ===================== 错误分类 =====================
        private enum DownloadErrorKind
        {
            Unknown, NotFound, Forbidden, RateLimited,
            Timeout, ConnectionReset, ContentType, ServerError,
        }

        private class DownloadException : Exception
        {
            public DownloadErrorKind Kind { get; private set; }
            public DownloadException(DownloadErrorKind kind, string msg, Exception inner = null)
                : base(msg, inner) { Kind = kind; }
        }

        private static DownloadException ClassifyException(Exception ex)
        {
            var dex = ex as DownloadException;
            if (dex != null) return dex;

            var wex = ex as WebException;
            if (wex != null)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp != null)
                {
                    int code = (int)resp.StatusCode;
                    if (code == 404) return new DownloadException(DownloadErrorKind.NotFound, wex.Message, wex);
                    if (code == 403) return new DownloadException(DownloadErrorKind.Forbidden, wex.Message, wex);
                    if (code == 429) return new DownloadException(DownloadErrorKind.RateLimited, wex.Message, wex);
                    if (code >= 500) return new DownloadException(DownloadErrorKind.ServerError, wex.Message, wex);
                }
                if (wex.Status == WebExceptionStatus.Timeout ||
                    wex.Status == WebExceptionStatus.NameResolutionFailure ||
                    wex.Status == WebExceptionStatus.ConnectFailure)
                    return new DownloadException(DownloadErrorKind.Timeout, wex.Message, wex);
                if (wex.Status == WebExceptionStatus.ConnectionClosed ||
                    wex.Status == WebExceptionStatus.KeepAliveFailure)
                    return new DownloadException(DownloadErrorKind.ConnectionReset, wex.Message, wex);
            }

            string msg = ex.Message ?? "";
            if (msg.Contains("远程主机") || msg.Contains("连接被意外关闭") ||
                msg.Contains("连接被强制关闭") || msg.Contains("基础连接已经关闭") ||
                msg.Contains("发送时发生错误"))
                return new DownloadException(DownloadErrorKind.ConnectionReset, msg, ex);
            if (msg.Contains("超时") || msg.Contains("timed out"))
                return new DownloadException(DownloadErrorKind.Timeout, msg, ex);
            return new DownloadException(DownloadErrorKind.Unknown, msg, ex);
        }

        private static bool IsRetryable(DownloadErrorKind kind)
        {
            switch (kind)
            {
                case DownloadErrorKind.Timeout:
                case DownloadErrorKind.ConnectionReset:
                case DownloadErrorKind.ServerError:
                case DownloadErrorKind.RateLimited:
                case DownloadErrorKind.Unknown:
                    return true;
                default: return false;
            }
        }

        private static SemaphoreSlim GetHostSemaphore(string url)
        {
            string host;
            try { host = new Uri(url).Host; }
            catch { host = "unknown"; }

            lock (_hostSemsLock)
            {
                SemaphoreSlim sem;
                if (!_hostSems.TryGetValue(host, out sem))
                {
                    sem = new SemaphoreSlim(PER_HOST_CONCURRENCY, PER_HOST_CONCURRENCY);
                    _hostSems[host] = sem;
                }
                return sem;
            }
        }

        private static int GetTimeoutForUrl(string url)
        {
            if (!string.IsNullOrEmpty(url) &&
                url.StartsWith(BMCL_BASE, StringComparison.OrdinalIgnoreCase))
                return BMCLAPI_TIMEOUT_MS;
            return OFFICIAL_TIMEOUT_MS;
        }

        // ===================== 进度追踪 =====================
        private class ProgressTracker
        {
            private readonly string _label;
            private readonly string _fileName;
            private readonly long _total;
            private readonly object _lock = new object();
            private long _received;
            private bool _done;
            private long _lastSnapshot = -1;
            private DateTime _lastChangeTime = DateTime.Now;
            private bool _stall;
            private Thread _watcher;
            private bool _started;

            public ProgressTracker(string label, string fileName, long total)
            {
                _label = label; _fileName = fileName; _total = total;
            }

            public void Start()
            {
                if (_started) return;
                _started = true;
                _watcher = new Thread(Loop);
                _watcher.IsBackground = true;
                _watcher.Name = "ProgressWatcher";
                _watcher.Start();
            }

            private void Loop()
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    long current;
                    bool done;
                    lock (_lock) { current = _received; done = _done; }

                    if (current != _lastSnapshot)
                    {
                        _lastSnapshot = current;
                        _lastChangeTime = DateTime.Now;
                        if (_stall) _stall = false;
                    }
                    else if (!_stall &&
                             (DateTime.Now - _lastChangeTime).TotalSeconds >= STALL_THRESHOLD_SECONDS)
                    {
                        _stall = true;
                    }

                    Print(current, _stall);
                    if (done) break;
                }
                long finalCurrent;
                lock (_lock) { finalCurrent = _received; }
                Print(finalCurrent, _stall);
            }

            public void Update(long received) { lock (_lock) { _received = received; } }

            public void Stop()
            {
                lock (_lock) { _done = true; }
                if (_watcher != null && _watcher.IsAlive)
                    _watcher.Join(2000);

                lock (_consoleLock)
                {
                    Console.Write("\r" + new string(' ', 160) + "\r");
                    try { Console.Out.Flush(); } catch { }
                }
            }

            private void Print(long received, bool stall)
            {
                string sizeText;
                if (stall)
                    sizeText = _total > 0 ? $"{received} B / {_total} B" : $"{received} B";
                else
                    sizeText = _total > 0
                        ? $"{FormatSize(received)} / {FormatSize(_total)}"
                        : FormatSize(received);

                int pct = _total > 0 ? (int)(received * 100 / _total) : 0;
                string tail = "";
                if (stall)
                {
                    double stallSecs = (DateTime.Now - _lastChangeTime).TotalSeconds;
                    tail = $"  [卡住 {stallSecs:F0}s]";
                }

                lock (_consoleLock)
                {
                    Console.Write($"\r[{_label}] {_fileName} - {pct}% ({sizeText}){tail}   ");
                    try { Console.Out.Flush(); } catch { }
                }
            }
        }

        // ===================== 客户端 =====================
        public static void InstallClient(string version, string minecraftDir, string loaderParam)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            minecraftDir = Path.GetFullPath(minecraftDir);
            Log($"[NeoForge] 开始安装客户端 NeoForge {version}");

            string neoVersion = ResolveNeoForgeVersion(version, loaderParam);
            if (string.IsNullOrEmpty(neoVersion))
                throw new Exception($"无法找到 NeoForge {version} 的版本");
            Log($"[NeoForge] 使用 NeoForge 版本: {neoVersion}");

            string versionId = $"{neoVersion}";
            string versionsDir = Path.Combine(minecraftDir, "versions");
            string versionDir = Path.Combine(versionsDir, versionId);
            string librariesDir = Path.Combine(minecraftDir, "libraries");

            Directory.CreateDirectory(versionDir);
            Directory.CreateDirectory(librariesDir);

            string installerPath = DownloadInstaller(neoVersion);

            string tempDir = Path.Combine(Path.GetTempPath(),
                $"neoforge_unpack_{neoVersion.Replace('.', '_')}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                ExtractInstaller(installerPath, tempDir);

                string profilePath = Path.Combine(tempDir, "install_profile.json");
                if (!File.Exists(profilePath))
                    throw new Exception("installer 中缺少 install_profile.json");

                var profile = ParseJson(File.ReadAllText(profilePath));
                if (profile == null) throw new Exception("install_profile.json 解析失败");

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

                if (versionJson == null) throw new Exception("installer 中缺少 version.json");

                string mavenSrc = Path.Combine(tempDir, "maven");
                if (Directory.Exists(mavenSrc))
                    CopyDirectory(mavenSrc, librariesDir, skipExisting: true);

                var tasks = new List<DownloadTask>();
                CollectLibraryTasks(versionJson, librariesDir, tasks, "client");
                if (profile.ContainsKey("libraries"))
                    CollectLibraryTasks(profile, librariesDir, tasks, "client");
                tasks = DeduplicateTasks(tasks);

                Log($"[NeoForge] Libraries 任务: {tasks.Count}");
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
                            Log($"[NeoForge] 已合并父版本 {parentId}（inheritsFrom 已展开）");
                        }
                        else
                        {
                            Log($"[NeoForge] 警告: 父版本 JSON 解析失败，保留 inheritsFrom: {parentJsonPath}");
                        }
                    }
                    else
                    {
                        Log($"[NeoForge] 警告: 找不到父版本 JSON，保留 inheritsFrom: {parentJsonPath}");
                    }
                }

                string targetJson = Path.Combine(versionDir, versionId + ".json");
                File.WriteAllText(targetJson,
                    new JavaScriptSerializer().Serialize(versionJson), Encoding.UTF8);
                Log($"[NeoForge] 版本 JSON 已保存: {targetJson}");

                if (profile.ContainsKey("processors"))
                {
                    Log("[NeoForge] 执行 processor（打补丁）...");
                    RunProcessors(profile, minecraftDir, tempDir, "client", installerPath);
                }
                else
                {
                    Log("[NeoForge] 无 processor，跳过");
                }

                string originalClientJar = Path.Combine(versionsDir, version, version + ".jar");
                string neoVersionJar = Path.Combine(versionDir, versionId + ".jar");
                if (File.Exists(originalClientJar) && !File.Exists(neoVersionJar))
                {
                    try
                    {
                        File.Copy(originalClientJar, neoVersionJar, true);
                        Log($"[NeoForge] 已复制原版核心到: {neoVersionJar}");
                    }
                    catch { }
                }

                Log($"[NeoForge] 客户端 NeoForge {version} 安装完成");
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
            Log($"[NeoForge] 开始安装服务端 NeoForge {version}");

            string neoVersion = ResolveNeoForgeVersion(version, loaderParam);
            if (string.IsNullOrEmpty(neoVersion))
                throw new Exception($"无法找到 NeoForge {version} 的版本");
            Log($"[NeoForge] 使用 NeoForge 版本: {neoVersion}");

            Directory.CreateDirectory(serverDir);
            string librariesDir = Path.Combine(serverDir, "libraries");
            Directory.CreateDirectory(librariesDir);

            string installerPath = DownloadInstaller(neoVersion);

            string tempDir = Path.Combine(Path.GetTempPath(),
                $"neoforge_unpack_{neoVersion.Replace('.', '_')}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                ExtractInstaller(installerPath, tempDir);

                string profilePath = Path.Combine(tempDir, "install_profile.json");
                if (!File.Exists(profilePath))
                    throw new Exception("installer 中缺少 install_profile.json");

                var profile = ParseJson(File.ReadAllText(profilePath));
                if (profile == null) throw new Exception("install_profile.json 解析失败");

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

                if (versionJson == null) throw new Exception("installer 中缺少 version.json");

                string mavenSrc = Path.Combine(tempDir, "maven");
                if (Directory.Exists(mavenSrc))
                    CopyDirectory(mavenSrc, librariesDir, skipExisting: true);

                var tasks = new List<DownloadTask>();
                CollectLibraryTasks(versionJson, librariesDir, tasks, "server");
                if (profile.ContainsKey("libraries"))
                    CollectLibraryTasks(profile, librariesDir, tasks, "server");
                tasks = DeduplicateTasks(tasks);

                Log($"[NeoForge] Libraries 任务: {tasks.Count}");
                if (tasks.Count > 0)
                    ParallelDownload(tasks, MAX_CONCURRENCY);

                if (profile.ContainsKey("processors"))
                {
                    Log("[NeoForge] 执行 processor（打补丁）...");
                    RunProcessors(profile, serverDir, tempDir, "server", installerPath);
                }

                string starterJar = Path.Combine(serverDir, "server.jar");
                try
                {
                    Log("[NeoForge] 下载 ServerStarterJar...");
                    DownloadFile(SERVER_STARTER_URL, starterJar, false);
                    Log($"[NeoForge] ServerStarterJar 已就绪: {starterJar}");
                }
                catch (Exception ex)
                {
                    Log($"[NeoForge] ServerStarterJar 下载失败: {ex.Message}");
                }

                string runBat = Path.Combine(serverDir, "run.bat");
                string jvmArgs = Path.Combine(serverDir, "user_jvm_args.txt");

                if (File.Exists(runBat))
                    Log($"[NeoForge] run.bat 已就绪: {runBat}");
                else
                    Log("[NeoForge] 警告: 未找到 run.bat");

                if (File.Exists(jvmArgs))
                    Log("[NeoForge] user_jvm_args.txt 已就绪，可在此文件里修改内存等 JVM 参数");

                Log($"[NeoForge] 服务端 NeoForge {version} 安装完成");
                Log("[NeoForge] 启动方式：java @user_jvm_args.txt -jar server.jar nogui");
                Log("[NeoForge] 首次启动前请修改 eula.txt 接受 EULA");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ===================== 版本解析 =====================
        private static string McVersionToNeoPrefix(string mcVersion)
        {
            if (mcVersion.StartsWith("1."))
                return mcVersion.Substring(2);
            return mcVersion;
        }

        private static string ResolveNeoForgeVersion(string mcVersion, string loaderParam)
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
                if (Regex.IsMatch(param, @"^\d+\.\d+"))
                    return param;
            }
            return GetLatestNeoForgeVersion(mcVersion);
        }

        private static string GetLatestNeoForgeVersion(string mcVersion)
        {
            try
            {
                Log("[NeoForge] 获取版本列表...");
                string xml = DownloadString(NEOFORGE_METADATA);
                if (string.IsNullOrEmpty(xml)) return null;

                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var nodes = doc.SelectNodes("//metadata/versioning/versions/version");
                if (nodes == null) return null;

                string prefix = McVersionToNeoPrefix(mcVersion) + ".";
                var candidates = new List<string>();
                foreach (XmlNode node in nodes)
                {
                    string v = node.InnerText.Trim();
                    if (v.StartsWith(prefix) && !v.Contains("-pre") && !v.Contains("-alpha"))
                        candidates.Add(v);
                }

                if (candidates.Count == 0)
                {
                    Log($"[NeoForge] 未找到匹配前缀 {prefix} 的版本");
                    return null;
                }

                candidates.Sort((a, b) =>
                {
                    bool aBeta = a.Contains("-beta");
                    bool bBeta = b.Contains("-beta");
                    if (aBeta != bBeta) return aBeta ? 1 : -1;
                    string va = a.Split('-')[0];
                    string vb = b.Split('-')[0];
                    return CompareVersions(vb, va);
                });

                Log($"[NeoForge] 最新版本: {candidates[0]}");
                return candidates[0];
            }
            catch (Exception ex)
            {
                Log($"[NeoForge] 获取版本列表失败: {ex.Message}");
                return null;
            }
        }

        private static int CompareVersions(string a, string b)
        {
            try { return new Version(a).CompareTo(new Version(b)); }
            catch { return string.Compare(a, b, StringComparison.Ordinal); }
        }

        // ===================== 下载安装器（★ 多轮重试） =====================
        private static string DownloadInstaller(string neoVersion)
        {
            string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
            string cacheDir = Path.Combine(launcherDir, "Launcher Setting", "Mode Loader Installer");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

            string fileName = $"neoforge-{neoVersion}-installer.jar";
            string installerPath = Path.Combine(cacheDir, fileName);

            if (File.Exists(installerPath) && new FileInfo(installerPath).Length > 0)
            {
                try { ValidateJarFile(installerPath); }
                catch { try { File.Delete(installerPath); } catch { } }

                if (File.Exists(installerPath))
                {
                    Log($"[NeoForge] 安装器已存在: {installerPath}");
                    return installerPath;
                }
            }

            string[] mirrors =
            {
                $"{BMCL_BASE}/maven/net/neoforged/neoforge/{neoVersion}/{fileName}",
                $"{NEOFORGE_MAVEN}net/neoforged/neoforge/{neoVersion}/{fileName}",
            };

            Log($"[NeoForge] 下载安装器（{INSTALLER_CHUNKS} 段并行，最多 {INSTALLER_ROUNDS} 轮）: {fileName}");

            string lastError = null;

            for (int round = 1; round <= INSTALLER_ROUNDS; round++)
            {
                if (round > 1)
                    Log($"[NeoForge] 第 {round}/{INSTALLER_ROUNDS} 轮尝试...");

                foreach (var url in mirrors)
                {
                    try
                    {
                        try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }

                        DownloadLargeFile(url, installerPath, INSTALLER_CHUNKS);
                        Log($"[NeoForge] 安装器已下载: {installerPath}");
                        return installerPath;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        Log($"[NeoForge] 第 {round} 轮源失败({url}): {ex.Message}");

                        try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
                        try { if (File.Exists(installerPath + ".tmp")) File.Delete(installerPath + ".tmp"); } catch { }

                        var dex = ex as DownloadException;
                        if (dex == null || IsRetryable(dex.Kind))
                            Thread.Sleep(500);
                    }
                }

                if (round < INSTALLER_ROUNDS)
                {
                    int waitSec = round * 3;
                    Log($"[NeoForge] 本轮所有源失败，{waitSec} 秒后重试...");
                    Thread.Sleep(waitSec * 1000);
                }
            }

            throw new Exception(
                $"下载 NeoForge 安装器失败（{INSTALLER_ROUNDS} 轮 × {mirrors.Length} 源全部失败）: {lastError}");
        }

        // ===================== 大文件分段下载 =====================
        private static void DownloadLargeFile(string url, string dest, int chunks)
        {
            long totalSize = GetContentLength(url);

            if (totalSize <= 0)
            {
                Log("[NeoForge] 服务器不返回大小，退化为单线程下载");
                SingleDownloadWithProgress(url, dest, true, GetTimeoutForUrl(url));
                return;
            }

            Log($"[NeoForge] 文件大小: {FormatSize(totalSize)}，分 {chunks} 段并行");

            using (var fs = File.Create(dest))
                fs.SetLength(totalSize);

            long chunkSize = totalSize / chunks;
            var threads = new List<Thread>();
            var errors = new List<Exception>();
            var progress = new long[chunks];
            object progressLock = new object();
            long totalReceived = 0;
            long displayedReceived = 0;
            string fileName = Path.GetFileName(dest);
            int reporterStop = 0;

            var sw = Stopwatch.StartNew();
            var reporter = new Thread(() =>
            {
                while (Volatile.Read(ref reporterStop) == 0)
                {
                    Thread.Sleep(500);
                    if (Volatile.Read(ref reporterStop) != 0) break;

                    long snapshot;
                    bool complete;
                    lock (progressLock)
                    {
                        snapshot = displayedReceived;
                        complete = (totalReceived >= totalSize);
                    }
                    if (complete) break;

                    double secs = sw.Elapsed.TotalSeconds;
                    double speed = secs > 0 ? snapshot / secs : 0;
                    int pct = (int)(snapshot * 100 / totalSize);
                    lock (_consoleLock)
                    {
                        Console.Write($"\r[下载中] {fileName} - {pct}% " +
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
                                Thread.Sleep(800 * (retry + 1));
                        }
                    }
                    lock (progressLock) errors.Add(lastEx);
                });
                th.IsBackground = true;
                th.Name = "NeoForgeInstallerChunk-" + i;
                th.Start();
                threads.Add(th);
            }

            foreach (var t in threads) t.Join();

            Volatile.Write(ref reporterStop, 1);
            reporter.Join(2000);

            lock (_consoleLock)
            {
                Console.Write("\r" + new string(' ', 140) + "\r");
                try { Console.Out.Flush(); } catch { }
            }

            if (errors.Count > 0)
            {
                Log($"[NeoForge] 分段失败（{errors.Count} 段），回退单线程重下");
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                SingleDownloadWithProgress(url, dest, true, GetTimeoutForUrl(url));
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
                req.Timeout = GetTimeoutForUrl(url);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.ContentLength > 0) return resp.ContentLength;
                }
            }
            catch { }

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA_STRING;
                req.AddRange(0, 0);
                req.AllowAutoRedirect = true;
                req.KeepAlive = false;
                req.Timeout = GetTimeoutForUrl(url);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    long total = ParseContentRangeTotal(resp.Headers["Content-Range"]);
                    if (total > 0) return total;
                }
            }
            catch { }

            return -1;
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
            req.Timeout = GetTimeoutForUrl(url);
            req.ReadWriteTimeout = READ_WRITE_TIMEOUT_MS;

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
                    throw new DownloadException(DownloadErrorKind.ConnectionReset,
                        $"段 {segmentIndex} 不完整: {received}/{expected}");
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
                Log($"[NeoForge] 安装器已解压到: {destDir}");
            }
            catch (Exception ex)
            {
                throw new Exception($"解压安装器失败: {ex.Message}", ex);
            }
        }

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
                merged[kv.Key] = kv.Value;
            }

            merged["id"] = versionId;

            merged["libraries"] = MergeLibrariesForge(childRoot, parentRoot);

            var mergedArgs = MergeArgumentsForge(childRoot, parentRoot);
            if (mergedArgs != null)
                merged["arguments"] = mergedArgs;

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
                else { allowed = (action == "allow"); }
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
                Log($"[NeoForge] 去重: {tasks.Count} -> {result.Count}");
            return result;
        }

        // ===================== 运行 processors =====================
        private static void RunProcessors(
            Dictionary<string, object> profile, string rootDir,
            string installerDir, string side, string installerPathArg)
        {
            string binPatchFile = Path.Combine(installerDir, "data",
                side == "client" ? "client.lzma" : "server.lzma");
            Log($"[NeoForge] 预期 BINPATCH 文件: {binPatchFile} (存在={File.Exists(binPatchFile)})");

            var processors = profile["processors"] as ArrayList;
            if (processors == null) return;

            var dataMap = BuildDataMap(profile, rootDir, side);
            string librariesDir = Path.Combine(rootDir, "libraries");

            string installerPath = null;
            if (profile.ContainsKey("installer_path"))
                installerPath = profile["installer_path"].ToString();
            if (string.IsNullOrEmpty(installerPath) || !File.Exists(installerPath))
                installerPath = installerPathArg;

            Log($"[NeoForge] processor 使用的 INSTALLER 路径: {installerPath}");

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
                            Log($"[NeoForge] 跳过 processor {idx}（不适用 {side}）");
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
                            { allExist = false; break; }
                        }
                        if (allExist)
                        {
                            Log($"[NeoForge] processor {idx} 输出已存在，跳过");
                            continue;
                        }
                    }
                }

                string jarCoord = proc.ContainsKey("jar") ? proc["jar"].ToString() : null;
                if (string.IsNullOrEmpty(jarCoord))
                {
                    Log($"[NeoForge] processor {idx} 无 jar 字段，跳过");
                    continue;
                }

                string procJarRel = MavenNameToPath(jarCoord);
                string procJar = Path.Combine(librariesDir, procJarRel);
                if (!File.Exists(procJar))
                {
                    Log($"[NeoForge] processor {idx} jar 不存在: {procJar}");
                    continue;
                }

                try { ValidateJarFile(procJar); }
                catch (Exception ex)
                {
                    Log($"[NeoForge] processor {idx} jar 损坏: {ex.Message}");
                    continue;
                }

                string mainClass = proc.ContainsKey("mainClass")
                    ? proc["mainClass"].ToString() : null;
                if (string.IsNullOrEmpty(mainClass))
                    mainClass = GetMainClassFromJar(procJar);
                if (string.IsNullOrEmpty(mainClass))
                {
                    Log($"[NeoForge] processor {idx} 无法确定 mainClass，跳过");
                    continue;
                }

                Log($"[NeoForge] processor {idx} mainClass = {mainClass}");

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
                            string resolved = ResolveProcessorArg(raw, dataMap, rootDir,
                                librariesDir, mcJar, installerPath, side, installerDir);
                            rawArgList.Add(raw);
                            argList.Add(resolved);
                        }
                    }
                }

                Log($"[NeoForge] processor {idx} 参数 ({argList.Count} 个):");
                for (int i = 0; i < argList.Count; i++)
                {
                    string mark = string.IsNullOrEmpty(argList[i]) ? " ★空!" : "";
                    Log($"[NeoForge]   [{i}] RAW='{rawArgList[i]}' -> '{argList[i]}'{mark}");
                }

                Log($"[NeoForge] 执行 processor {idx}: {jarCoord}");
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
                if (val.ContainsKey(side)) sideVal = val[side].ToString();
                else if (val.ContainsKey("client")) sideVal = val["client"].ToString();

                if (!string.IsNullOrEmpty(sideVal))
                    map[kv.Key] = sideVal;
            }
            return map;
        }

        private static string ResolveProcessorArg(
            string arg, Dictionary<string, string> dataMap,
            string rootDir, string librariesDir, string mcJar,
            string installerPath, string side, string installerDir)
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
                            throw new Exception("processors 需要 {INSTALLER}，但 installerPath 为空。");
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
                        $"Processor 参数 [{i}] 为空字符串，会导致 --from/--to 解析错位。");
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

        // ===================== 并行下载（★ 多轮重试） =====================
        private static void ParallelDownload(List<DownloadTask> tasks, int maxConcurrency)
        {
            var pending = new List<DownloadTask>(tasks);
            int originalTotal = tasks.Count;

            for (int round = 1; round <= LIBRARY_ROUNDS; round++)
            {
                if (pending.Count == 0) break;

                if (round == 1)
                {
                    Log($"[NeoForge] 开始并行下载（{maxConcurrency} 线程，共 {pending.Count} 个任务）...");
                }
                else
                {
                    int waitSec = (round - 1) * 3;
                    Log($"[NeoForge] 第 {round}/{LIBRARY_ROUNDS} 轮：{pending.Count} 个失败库，等待 {waitSec} 秒后重试...");
                    Thread.Sleep(waitSec * 1000);
                }

                int failed = 0, skipped = 0;
                int completed = 0;
                int total = pending.Count;
                int nextIndex = 0;
                object queueLock = new object();
                object statLock = new object();
                var roundFailures = new List<DownloadTask>();

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
                                if (nextIndex >= pending.Count) return;
                                task = pending[nextIndex++];
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
                                    roundFailures.Add(task);
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
                    worker.Name = "NeoForgeDownloader-" + i;
                    worker.Priority = ThreadPriority.Normal;
                    worker.Start();
                    threads.Add(worker);
                }

                foreach (var t in threads) t.Join();

                lock (_consoleLock)
                {
                    Console.WriteLine();
                    Console.WriteLine($"[NeoForge] 第 {round} 轮完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");
                }

                pending = roundFailures;
            }

            if (pending.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"[NeoForge] 有 {pending.Count}/{originalTotal} 个库下载失败");
                sb.Append($"（已重试 {LIBRARY_ROUNDS} 轮，每轮单文件 {RETRY_COUNT} 次）");

                int n = Math.Min(pending.Count, 20);
                sb.Append("\n失败清单（前 ").Append(n).Append(" 条）:");
                for (int i = 0; i < n; i++)
                    sb.Append("\n  - ").Append(pending[i].Name);

                throw new Exception(sb.ToString());
            }
        }

        private static void PrintProgressLine(int skipped, int failed, int completed, int total)
        {
            double pct = total == 0 ? 100 : (completed * 100.0 / total);
            Console.Write($"\r[NeoForge] 跳过 {skipped} | 失败 {failed} | " +
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
                    try { ValidateJarFile(dest); return true; }
                    catch { try { File.Delete(dest); } catch { } }
                }
                else { return true; }
            }

            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = dest + ".tmp";
            string[] urls = BuildUrlCandidates(url);

            var urlErrors = new Dictionary<string, DownloadErrorKind>(StringComparer.OrdinalIgnoreCase);
            bool isJarFile = dest.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                             dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                bool anyRetryable = false;
                int startOffset = retry % urls.Length;

                for (int k = 0; k < urls.Length; k++)
                {
                    int idx = (startOffset + k) % urls.Length;
                    string u = urls[idx];

                    DownloadErrorKind prevKind;
                    if (urlErrors.TryGetValue(u, out prevKind) && !IsRetryable(prevKind))
                        continue;

                    int timeoutMs = GetTimeoutForUrl(u);
                    var sem = GetHostSemaphore(u);
                    sem.Wait();
                    try
                    {
                        SingleDownloadWithProgress(u, tmpPath, showProgress, timeoutMs);

                        if (isJarFile)
                        {
                            try { ValidateJarFile(tmpPath); }
                            catch (Exception ex)
                            {
                                try { File.Delete(tmpPath); } catch { }
                                throw new DownloadException(DownloadErrorKind.ContentType,
                                    "文件校验失败: " + ex.Message, ex);
                            }
                        }

                        if (File.Exists(dest)) { try { File.Delete(dest); } catch { } }
                        File.Move(tmpPath, dest);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        var dex = ClassifyException(ex);

                        if (!IsRetryable(dex.Kind))
                        {
                            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        }

                        urlErrors[u] = dex.Kind;
                        if (!IsRetryable(dex.Kind)) continue;

                        anyRetryable = true;

                        if (dex.Kind == DownloadErrorKind.Timeout ||
                            dex.Kind == DownloadErrorKind.ConnectionReset)
                        {
                            Thread.Sleep(600 + retry * 300);
                        }
                        else if (dex.Kind == DownloadErrorKind.RateLimited)
                        {
                            Thread.Sleep(2000 + retry * 800);
                        }
                    }
                    finally { sem.Release(); }
                }

                if (!anyRetryable) break;
            }

            var sb = new StringBuilder();
            sb.Append("下载 ").Append(Path.GetFileName(dest)).Append(" 失败");
            foreach (var kv in urlErrors)
                sb.Append("\n    [").Append(kv.Value).Append("] ").Append(kv.Key);
            throw new DownloadException(DownloadErrorKind.Unknown, sb.ToString());
        }

        private static string[] BuildUrlCandidates(string url)
        {
            var list = new List<string>();

            if (!string.IsNullOrEmpty(url))
            {
                if (url.StartsWith(NEOFORGE_MAVEN, StringComparison.OrdinalIgnoreCase))
                {
                    string rest = url.Substring(NEOFORGE_MAVEN.Length);
                    list.Add(BMCL_BASE + "/maven/" + rest);
                    list.Add(url);
                }
                else { list.Add(url); }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var u in list)
                if (!string.IsNullOrEmpty(u) && seen.Add(u)) result.Add(u);
            return result.ToArray();
        }

        // ===================== 单流下载 + 断点续传 + 卡顿检测 =====================
        private static void SingleDownloadWithProgress(string url, string dest,
                                                        bool showProgress, int timeoutMs)
        {
            long existingLength = 0;
            if (File.Exists(dest))
            {
                try { existingLength = new FileInfo(dest).Length; } catch { }
            }

            if (existingLength > 0)
            {
                if (TryResumeDownload(url, dest, existingLength, showProgress, timeoutMs))
                    return;
                try { File.Delete(dest); } catch { }
                existingLength = 0;
            }

            ProgressTracker tracker = null;

            try
            {
                using (var resp = SendRequestIPv4First(url, timeoutMs, READ_WRITE_TIMEOUT_MS))
                using (var netStream = resp.GetResponseStream())
                using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                               FileShare.None, BUFFER_SIZE))
                {
                    long total = resp.ContentLength;
                    long received = 0;
                    byte[] buffer = new byte[BUFFER_SIZE];
                    int read;

                    bool displayProgress = showProgress && total > 1024 * 1024;
                    if (displayProgress)
                    {
                        tracker = new ProgressTracker("下载中", Path.GetFileName(dest), total);
                        tracker.Start();
                    }

                    while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fs.Write(buffer, 0, read);
                        received += read;

                        if (displayProgress && tracker != null)
                            tracker.Update(received);
                    }

                    if (total > 0 && received != total)
                        throw new DownloadException(DownloadErrorKind.ConnectionReset,
                            $"下载不完整: {received}/{total}");
                }
            }
            finally
            {
                if (tracker != null) tracker.Stop();
            }
        }

        private static bool TryResumeDownload(string url, string tmpPath, long existingLength,
                                               bool showProgress, int timeoutMs)
        {
            ProgressTracker tracker = null;

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA_STRING;
                req.AddRange(existingLength);
                req.AllowAutoRedirect = true;
                req.KeepAlive = false;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = READ_WRITE_TIMEOUT_MS;

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        return false;

                    long totalBytes = ParseContentRangeTotal(resp.Headers["Content-Range"]);
                    if (totalBytes <= 0) return false;
                    if (existingLength >= totalBytes) return true;

                    bool displayProgress = showProgress && totalBytes > 1024 * 1024;
                    if (displayProgress)
                    {
                        tracker = new ProgressTracker("续传中",
                            Path.GetFileName(tmpPath), totalBytes);
                        tracker.Start();
                        tracker.Update(existingLength);
                    }

                    using (var netStream = resp.GetResponseStream())
                    using (var fs = new FileStream(tmpPath, FileMode.Append, FileAccess.Write,
                                                   FileShare.None, BUFFER_SIZE))
                    {
                        long received = existingLength;
                        byte[] buffer = new byte[BUFFER_SIZE];
                        int read;

                        while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            fs.Write(buffer, 0, read);
                            received += read;

                            if (displayProgress && tracker != null)
                                tracker.Update(received);
                        }

                        if (received != totalBytes)
                            throw new DownloadException(DownloadErrorKind.ConnectionReset,
                                $"续传不完整: {received}/{totalBytes}");
                    }
                    return true;
                }
            }
            catch (WebException wex)
            {
                var httpResp = wex.Response as HttpWebResponse;
                if (httpResp != null && (int)httpResp.StatusCode == 416)
                {
                    try { httpResp.Close(); } catch { }
                    return false;
                }
                return false;
            }
            catch { return false; }
            finally
            {
                if (tracker != null) tracker.Stop();
            }
        }

        private static string DownloadString(string url)
        {
            Exception lastEx = null;
            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                try
                {
                    using (var resp = SendRequestIPv4First(url, GetTimeoutForUrl(url), READ_WRITE_TIMEOUT_MS))
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
                        ipv4List.Add(a);
                }
            }
            catch { }

            if (ipv4List.Count == 0)
            {
                Log($"[NeoForge] 未解析到 IPv4，退回原 URL: {url}");
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
                catch (DownloadException dex)
                {
                    if (!IsRetryable(dex.Kind)) throw;
                    lastEx = dex;
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
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            if (!string.IsNullOrEmpty(hostHeader)) req.Host = hostHeader;

            try
            {
                var resp = (HttpWebResponse)req.GetResponse();

                string path = req.RequestUri.AbsolutePath;
                bool expectBinary =
                    path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".lzma", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

                if (expectBinary)
                {
                    string contentType = resp.ContentType ?? "";
                    if (contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        resp.Close();
                        throw new DownloadException(DownloadErrorKind.ContentType,
                            "服务器返回 HTML（可能是错误页）");
                    }
                }
                return resp;
            }
            catch (WebException wex) { throw ClassifyException(wex); }
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
                Console.Write("\r" + new string(' ', 160) + "\r");
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