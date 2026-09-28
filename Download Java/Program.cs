using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Download_Java
{
    public static class JavaDownloader
    {
        // ===================== Mojang 官方源 =====================
        private const string ALL_JSON_URL =
            "https://launchermeta.mojang.com/v1/products/java-runtime/" +
            "2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

        private const int MAX_CONCURRENCY = 24;
        private const int RETRY_COUNT = 3;
        private const int BUFFER_SIZE = 256 * 1024;

        private static readonly object _consoleLock = new object();

        private static readonly Dictionary<int, string[]> JAVA_VERSION_TO_COMPONENT =
            new Dictionary<int, string[]>
        {
            { 8,  new[] { "jre-legacy" } },
            { 16, new[] { "java-runtime-alpha" } },
            { 17, new[] { "java-runtime-gamma", "java-runtime-gamma-snapshot", "java-runtime-beta" } },
            { 21, new[] { "java-runtime-delta" } },
            { 25, new[] { "java-runtime-epsilon" } },
        };

        // ============================================================
        //   新的公开入口：替代原来的 Main
        // ============================================================
        /// <summary>
        /// 下载并安装 Java 运行时。
        /// </summary>
        /// <param name="javaMajor">Java 大版本号，如 8 / 17 / 21</param>
        /// <param name="installPath">安装根目录，最终会装到 installPath\javaMajor\</param>
        /// <returns>0 = 成功，1 = 失败</returns>
        public static int Run(int javaMajor, string installPath)
        {
            try
            {
                try { Console.OutputEncoding = Encoding.GetEncoding(936); } catch { }

                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;

                if (javaMajor <= 0)
                    throw new ArgumentException($"无效的 Java 大版本号: {javaMajor}");

                installPath = (installPath ?? "").Trim().Trim('"');
                if (string.IsNullOrEmpty(installPath))
                    throw new ArgumentException("安装路径不能为空");
                installPath = Path.GetFullPath(installPath);

                string targetDir = Path.Combine(installPath, javaMajor.ToString());

                Console.WriteLine($"[Java] 目标 Java 大版本: {javaMajor}");
                Console.WriteLine($"[Java] 安装路径       : {targetDir}");

                DownloadAndInstallJava(javaMajor, targetDir);

                string javaExe = Path.Combine(targetDir, "bin", "java.exe");
                if (!File.Exists(javaExe))
                    throw new Exception($"安装完成但未找到 java.exe: {javaExe}");

                Console.WriteLine($"[Java] ✅ 安装完成");
                Console.WriteLine($"[Java] java.exe: {javaExe}");

                return 0;
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}"); }
                catch { }
                return 1;
            }
        }

        // ============================================================
        //   以下全部保持原样，只是把类名从 Program 改成 JavaDownloader
        // ============================================================
        private static void DownloadAndInstallJava(int javaMajor, string targetDir)
        {
            string platformKey = GetPlatformKey();
            Console.WriteLine($"[Java] 检测平台: {platformKey}");

            Console.WriteLine("[Java] 获取 Mojang Java 运行时列表...");
            string allJson = DownloadString(ALL_JSON_URL);
            var allRoot = ParseJson(allJson);
            if (allRoot == null)
                throw new Exception("all.json 解析失败");

            var component = FindComponent(allRoot, platformKey, javaMajor);
            if (component == null)
                throw new Exception(
                    $"未在 all.json 中找到 Java {javaMajor} 对应的运行时组件（平台 {platformKey}）");

            Console.WriteLine($"[Java] 使用组件: {component.Name}");

            string manifestUrl = null;
            string manifestSha1 = null;
            if (component.Info.ContainsKey("manifest"))
            {
                var mf = component.Info["manifest"] as Dictionary<string, object>;
                if (mf != null)
                {
                    if (mf.ContainsKey("url")) manifestUrl = mf["url"].ToString();
                    if (mf.ContainsKey("sha1")) manifestSha1 = mf["sha1"].ToString();
                }
            }
            if (string.IsNullOrEmpty(manifestUrl))
                throw new Exception($"组件 {component.Name} 缺少 manifest.url");

            if (component.Info.ContainsKey("version"))
            {
                var ver = component.Info["version"] as Dictionary<string, object>;
                if (ver != null && ver.ContainsKey("name"))
                    Console.WriteLine($"[Java] 组件版本: {ver["name"]}");
            }

            Console.WriteLine($"[Java] 获取 manifest...");
            string manifestJson = DownloadString(manifestUrl);
            var manifestRoot = ParseJson(manifestJson);
            if (manifestRoot == null)
                throw new Exception("manifest.json 解析失败");

            if (!manifestRoot.ContainsKey("files"))
                throw new Exception("manifest.json 缺少 files 字段");

            var files = manifestRoot["files"] as Dictionary<string, object>;
            if (files == null)
                throw new Exception("manifest.json files 格式错误");

            Console.WriteLine($"[Java] manifest 包含 {files.Count} 项");

            Directory.CreateDirectory(targetDir);

            var tasks = new List<FileTask>();
            foreach (var kv in files)
            {
                string relPath = kv.Key;
                var fileInfo = kv.Value as Dictionary<string, object>;
                if (fileInfo == null) continue;

                string type = fileInfo.ContainsKey("type")
                    ? fileInfo["type"].ToString()
                    : "file";

                string winRelPath = relPath.Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.Combine(targetDir, winRelPath);

                if (type == "directory")
                {
                    try { Directory.CreateDirectory(dest); } catch { }
                    continue;
                }

                if (type == "link") continue;

                if (fileInfo.ContainsKey("downloads"))
                {
                    var downloads = fileInfo["downloads"] as Dictionary<string, object>;
                    if (downloads != null && downloads.ContainsKey("raw"))
                    {
                        var raw = downloads["raw"] as Dictionary<string, object>;
                        if (raw != null && raw.ContainsKey("url"))
                        {
                            tasks.Add(new FileTask
                            {
                                Url = raw["url"].ToString(),
                                Dest = dest,
                                Sha1 = raw.ContainsKey("sha1") ? raw["sha1"].ToString() : null,
                                Name = relPath
                            });
                        }
                    }
                }
            }

            Console.WriteLine($"[Java] 需要下载 {tasks.Count} 个文件");

            ParallelDownload(tasks, MAX_CONCURRENCY);
        }

        private class ComponentResult
        {
            public string Name;
            public Dictionary<string, object> Info;
        }

        private static ComponentResult FindComponent(
            Dictionary<string, object> allRoot, string platformKey, int javaMajor)
        {
            if (!allRoot.ContainsKey(platformKey))
                throw new Exception($"all.json 中没有平台 {platformKey}");

            var platformData = allRoot[platformKey] as Dictionary<string, object>;
            if (platformData == null)
                throw new Exception($"平台 {platformKey} 数据结构异常");

            if (JAVA_VERSION_TO_COMPONENT.ContainsKey(javaMajor))
            {
                string[] candidates = JAVA_VERSION_TO_COMPONENT[javaMajor];
                foreach (var name in candidates)
                {
                    if (!platformData.ContainsKey(name)) continue;
                    var list = platformData[name] as ArrayList;
                    if (list == null || list.Count == 0) continue;

                    var first = list[0] as Dictionary<string, object>;
                    if (first == null) continue;

                    return new ComponentResult { Name = name, Info = first };
                }
            }

            Console.WriteLine($"[Java] 映射未命中 Java {javaMajor}，改用版本号匹配");
            foreach (var kv in platformData)
            {
                var list = kv.Value as ArrayList;
                if (list == null || list.Count == 0) continue;

                var first = list[0] as Dictionary<string, object>;
                if (first == null) continue;

                if (first.ContainsKey("version"))
                {
                    var ver = first["version"] as Dictionary<string, object>;
                    if (ver != null && ver.ContainsKey("name"))
                    {
                        string verName = ver["name"].ToString();
                        string firstPart = verName.Split(new[] { '.', 'u' })[0];
                        int major;
                        if (int.TryParse(firstPart, out major) && major == javaMajor)
                        {
                            return new ComponentResult { Name = kv.Key, Info = first };
                        }
                    }
                }
            }

            return null;
        }

        private static string GetPlatformKey()
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            string archW6432 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? "";

            if (!string.IsNullOrEmpty(archW6432))
                arch = archW6432;

            switch (arch.ToUpperInvariant())
            {
                case "AMD64": return "windows-x64";
                case "X86": return "windows-x86";
                case "ARM64": return "windows-arm64";
                default: return "windows-x64";
            }
        }

        private static void ParallelDownload(List<FileTask> tasks, int maxConcurrency)
        {
            int completed = 0;
            int skipped = 0;
            int failed = 0;
            int total = tasks.Count;

            int nextIndex = 0;
            object queueLock = new object();
            object statLock = new object();
            var failures = new List<string>();

            if (total == 0)
            {
                Console.WriteLine("[Java] 无文件需要下载");
                return;
            }

            var threads = new List<Thread>();
            for (int i = 0; i < maxConcurrency; i++)
            {
                var worker = new Thread(() =>
                {
                    while (true)
                    {
                        FileTask task;
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
                            lock (statLock)
                            {
                                failed++;
                                completed++;
                                if (failures.Count < 50)
                                    failures.Add($"{task.Name} <- {ex.Message}");
                            }
                        }

                        lock (_consoleLock)
                        {
                            Console.Write(
                                $"\r[Java] 进度 {completed}/{total} | 跳过 {skipped} | 失败 {failed}   ");
                            try { Console.Out.Flush(); } catch { }
                        }
                    }
                });
                worker.IsBackground = true;
                worker.Name = "JavaDownloader-" + i;
                worker.Start();
                threads.Add(worker);
            }

            foreach (var t in threads) t.Join();

            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.WriteLine($"[Java] 下载完成: 总计 {total}, 跳过 {skipped}, 失败 {failed}");
                if (failed > 0)
                {
                    int n = Math.Min(failures.Count, 20);
                    Console.WriteLine($"[Java] 失败清单（前 {n} 条）:");
                    for (int i = 0; i < n; i++)
                        Console.WriteLine($"  - {failures[i]}");
                }
            }

            if (failed > 0)
                throw new Exception($"有 {failed}/{total} 个文件下载失败");
        }

        private static bool DownloadFile(FileTask task)
        {
            if (File.Exists(task.Dest) && new FileInfo(task.Dest).Length > 0)
            {
                if (!string.IsNullOrEmpty(task.Sha1))
                {
                    try
                    {
                        if (ComputeSha1(task.Dest).Equals(
                            task.Sha1, StringComparison.OrdinalIgnoreCase))
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
                            throw new Exception(
                                $"SHA1 校验失败: 期望 {task.Sha1}, 实际 {actual}");
                    }

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
                    if (retry < RETRY_COUNT - 1)
                        Thread.Sleep(500 * (retry + 1));
                }
            }

            throw new Exception($"下载失败: {lastEx?.Message}", lastEx);
        }

        private static void SingleDownload(string url, string dest)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var netStream = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write,
                                            FileShare.None, BUFFER_SIZE))
            {
                byte[] buffer = new byte[BUFFER_SIZE];
                int read;
                while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                    fs.Write(buffer, 0, read);
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
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    req.Timeout = 30000;
                    req.AllowAutoRedirect = true;
                    req.KeepAlive = false;

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

        private class FileTask
        {
            public string Url { get; set; }
            public string Dest { get; set; }
            public string Sha1 { get; set; }
            public string Name { get; set; }
        }
    }
}