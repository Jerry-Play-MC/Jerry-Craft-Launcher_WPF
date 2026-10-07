using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Launch_Minecraft
{
    // ============================================================
    //   启动阶段 / 进度
    // ============================================================
    public enum LaunchPhase
    {
        Preparing,
        CheckingFiles,
        StartingProcess,
        WaitingWindow,
        Running,
        Stopped,
        Failed,
    }

    public class LaunchProgress
    {
        public LaunchPhase Phase;
        public string Message;
    }

    public class LaunchContext
    {
        public string MinecraftDir { get; set; }
        public string VersionName { get; set; }
        public bool IsServer { get; set; }
        public bool IsVersionIsolated { get; set; }
        public bool IsModernNativeLayout { get; set; }

        public string JavaPath { get; set; }
        public string JavaBaseDir { get; set; }

        public string Username { get; set; }
        public string Uuid { get; set; }
        public string AccessToken { get; set; }
        public string UserType { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }
        public string InitMemory { get; set; }
        public string MaxMemory { get; set; }

        public Action<LaunchProgress> OnProgress { get; set; }

        public LaunchContext()
        {
            Username = "Player";
            Uuid = "00000000-0000-0000-0000-000000000000";
            AccessToken = "0";
            UserType = "legacy";
            Width = 854;
            Height = 480;
            InitMemory = "1G";
            MaxMemory = "4G";
        }

        public string GetGameDir()
        {
            if (IsServer) return MinecraftDir;
            if (IsVersionIsolated)
                return Path.Combine(Path.Combine(MinecraftDir, "versions"), VersionName);
            return MinecraftDir;
        }

        public string GetNativesDir()
        {
            if (IsVersionIsolated)
                return Path.Combine(Path.Combine(MinecraftDir, "versions"), VersionName, "natives");
            return Path.Combine(MinecraftDir, "natives");
        }
    }

    public abstract class BaseLoaderLauncher
    {
        public abstract string LoaderName { get; }

        protected static LaunchContext CreateClientContext(
            string minecraftDir, string versionName, bool isolated,
            string javaBaseDir = null,
            string username = null, string uuid = null,
            string accessToken = null, string userType = null,
            Action<LaunchProgress> onProgress = null)
        {
            var ctx = new LaunchContext
            {
                MinecraftDir = Path.GetFullPath(minecraftDir),
                VersionName = versionName,
                IsServer = false,
                IsVersionIsolated = isolated,
                JavaBaseDir = javaBaseDir,
            };

            if (!string.IsNullOrEmpty(username)) ctx.Username = username;
            if (!string.IsNullOrEmpty(uuid)) ctx.Uuid = uuid;
            if (!string.IsNullOrEmpty(accessToken)) ctx.AccessToken = accessToken;
            if (!string.IsNullOrEmpty(userType)) ctx.UserType = userType;
            if (onProgress != null) ctx.OnProgress = onProgress;

            return ctx;
        }

        protected static LaunchContext CreateServerContext(
            string serverDir, string versionName, string javaBaseDir = null)
        {
            return new LaunchContext
            {
                MinecraftDir = Path.GetFullPath(serverDir),
                VersionName = versionName,
                IsServer = true,
                IsVersionIsolated = false,
                JavaBaseDir = javaBaseDir,
            };
        }

        public void Launch(LaunchContext context)
        {
            try
            {
                Report(context, LaunchPhase.Preparing, "准备启动...");

                Console.WriteLine($"[{LoaderName}] 开始构建启动命令...");
                Console.WriteLine($"[{LoaderName}] 目录: {context.MinecraftDir}");
                Console.WriteLine($"[{LoaderName}] 版本: {context.VersionName}");
                Console.WriteLine($"[{LoaderName}] 账号: {context.Username} ({context.UserType})");

                var cmd = new List<string>();

                var root = LoadRootJson(context);
                if (root == null)
                    throw new Exception($"[{LoaderName}] 无法加载版本 JSON：{context.VersionName}");

                context.IsModernNativeLayout = HasModernNativeLayout(root);
                Console.WriteLine($"[{LoaderName}] natives 布局: {(context.IsModernNativeLayout ? "modern (1.19+)" : "legacy (1.18-)")}");

                int requiredJava = GetRequiredJavaMajorVersion(root, context.VersionName);
                Console.WriteLine($"[{LoaderName}] 版本要求 Java 主版本: {requiredJava}");

                string javaPath = context.JavaPath;
                if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
                    javaPath = JavaLocator.Find(requiredJava, context.JavaBaseDir);
                cmd.Add(javaPath);
                Console.WriteLine($"[{LoaderName}] Java: {javaPath}");

                PreLaunchFix(context, root);

                string os = GetOsName();
                var libs = CollectLibraries(root, context, os);
                libs = FilterLibraries(libs, context);
                Console.WriteLine($"[{LoaderName}] 库数量: {libs.Count}");

                CheckAndDownloadFiles(context, os, libs);

                string nativesDir = null;
                if (!context.IsServer)
                {
                    nativesDir = context.GetNativesDir();
                    PrepareNatives(libs, context.MinecraftDir, nativesDir);
                }

                var entries = BuildClasspathEntries(libs, context);
                string mainClass = GetMainClass(root);
                entries = ReorderClasspath(entries, context, root, mainClass);

                cmd.Add($"-Xms{context.InitMemory}");
                cmd.Add($"-Xmx{context.MaxMemory}");
                cmd.Add("-Dfile.encoding=GBK");
                cmd.Add("-Dstdout.encoding=GBK");
                cmd.Add("-Dstderr.encoding=GBK");

                AppendLoaderJvmArgs(cmd, context, root);
                AddJvmArgsFromJson(cmd, root, context, nativesDir);

                if (!string.IsNullOrEmpty(nativesDir))
                    cmd.Add($"-Djava.library.path={nativesDir}");

                cmd.Add("-cp");
                cmd.Add(string.Join(";", entries));
                cmd.Add(mainClass);

                if (context.IsServer)
                    AppendServerGameArgs(cmd, context, root);
                else if (UseLaunchWrapperArgs(root))
                    AppendLaunchWrapperArgs(cmd, context, root);
                else
                    AddGameArgs(cmd, context, root);

                PostProcessCommand(cmd, context, root);

                cmd.RemoveAll(a => a == "--demo");
                RemoveQuickPlayArgs(cmd);

                Console.WriteLine($"[{LoaderName}] 完整命令行:");
                Console.WriteLine(string.Join(" ",
                    cmd.ConvertAll(a => a.Contains(" ") ? "\"" + a + "\"" : a).ToArray()));

                StartProcess(cmd, context);
            }
            catch (Exception ex)
            {
                Report(context, LaunchPhase.Failed, "启动失败：" + ex.Message);
                throw;
            }
        }

        // ============================================================
        //   文件资源完整性检查
        // ============================================================
        protected void CheckAndDownloadFiles(
            LaunchContext context, string os, List<Dictionary<string, object>> libs)
        {
            Report(context, LaunchPhase.CheckingFiles, "检查文件资源完整性...");
            Console.WriteLine($"[{LoaderName}] 检查文件资源完整性...");

            string libDir = Path.Combine(context.MinecraftDir, "libraries");
            int missing = 0, downloaded = 0, skippedNoUrl = 0, skippedNatives = 0, failed = 0;
            var failedNames = new List<string>();

            foreach (var lib in libs)
            {
                string relPath = ResolveLibraryPath(lib);
                if (string.IsNullOrEmpty(relPath)) continue;

                string url = null;
                if (lib.ContainsKey("downloads"))
                {
                    var dl = lib["downloads"] as Dictionary<string, object>;
                    if (dl != null && dl.ContainsKey("artifact"))
                    {
                        var art = dl["artifact"] as Dictionary<string, object>;
                        if (art != null && art.ContainsKey("url"))
                            url = Convert.ToString(art["url"]);
                    }
                }
                if (string.IsNullOrEmpty(url) && lib.ContainsKey("url"))
                {
                    string baseUrl = Convert.ToString(lib["url"]);
                    if (!string.IsNullOrEmpty(baseUrl))
                    {
                        if (!baseUrl.EndsWith("/")) baseUrl += "/";
                        url = baseUrl + relPath;
                    }
                }

                string full = Path.Combine(libDir, relPath);
                bool exists = false;
                try
                {
                    if (File.Exists(full) && new FileInfo(full).Length > 0)
                        exists = true;
                }
                catch { }

                if (exists) continue;

                missing++;

                // natives-only 占位库（同时有 natives 字段或名字带 -platform），
                // 主 jar 在 Maven 上不存在，缺失是正常现象，不应触发下载
                bool isNativesOnly =
                    lib.ContainsKey("natives") ||
                    (lib.ContainsKey("name") &&
                     Convert.ToString(lib["name"])
                           .IndexOf("-platform", StringComparison.OrdinalIgnoreCase) >= 0);

                if (isNativesOnly)
                {
                    Console.WriteLine($"[{LoaderName}] [Files] 跳过 natives-only 占位库：{relPath}");
                    skippedNatives++;
                    continue;
                }

                if (string.IsNullOrEmpty(url))
                {
                    Console.WriteLine($"[{LoaderName}] [Files] 缺少且无 URL，跳过：{relPath}");
                    skippedNoUrl++;
                    continue;
                }

                try
                {
                    Console.WriteLine($"[{LoaderName}] [Files] 补全：{relPath}");
                    DownloadFileSimple(url, full);
                    downloaded++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{LoaderName}] [Files] 下载失败：{url} - {ex.Message}");
                    failedNames.Add(relPath);
                    failed++;
                }
            }

            Console.WriteLine($"[{LoaderName}] [Files] 检查完成：缺失 {missing}，" +
                              $"下载 {downloaded}，无 URL 跳过 {skippedNoUrl}，" +
                              $"natives 跳过 {skippedNatives}，失败 {failed}");

            if (failed > 0)
            {
                int show = Math.Min(3, failedNames.Count);
                string names = string.Join("、", failedNames.GetRange(0, show).ToArray());
                throw new Exception(
                    $"有 {failed} 个文件下载失败（网络原因），请检查网络后重试。\n例如：{names}");
            }
        }

        /// <summary>
        /// 从启动器缓存的 LiteLoader 安装器 jar 中提取本体。
        /// 安装器位置：exe目录\Launcher Setting\Mode Loader Installer\liteloader-installer-*.jar
        /// 安装器内部的 liteloader 本体为 liteloader-*-release.jar。
        /// </summary>
        private static bool TryExtractLiteLoaderFromInstaller(string targetPath)
        {
            string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
            string cacheDir = Path.Combine(launcherDir,
                                           "Launcher Setting", "Mode Loader Installer");

            if (!Directory.Exists(cacheDir)) return false;

            string[] installers;
            try { installers = Directory.GetFiles(cacheDir, "liteloader-installer-*.jar"); }
            catch { return false; }

            if (installers.Length == 0) return false;

            foreach (var installer in installers)
            {
                try
                {
                    using (var fs = File.OpenRead(installer))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        // 遍历所有条目，找出 liteloader 本体：
                        //   - 文件名以 "liteloader-" 开头
                        //   - 以 ".jar" 结尾
                        //   - 名字里不含 "installer"（排除安装器自身）
                        //   - 名字里含 "-release" 或形如 liteloader-<版本>.jar
                        foreach (var entry in zip.Entries)
                        {
                            string name = entry.Name;
                            if (string.IsNullOrEmpty(name)) continue;

                            if (!name.StartsWith("liteloader-", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (name.IndexOf("installer", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;
                            if (entry.Length < 1000) continue;   // 明显不是本体的小文件

                            string dir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);

                            using (var es = entry.Open())
                            using (var outFs = File.Create(targetPath))
                            {
                                es.CopyTo(outFs);
                            }

                            Console.WriteLine($"[Java/LiteLoader] 从安装器提取: {name} -> {targetPath}");
                            Console.WriteLine($"[Java/LiteLoader] 安装器: {installer}");

                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Java/LiteLoader] 处理 {installer} 失败: {ex.Message}");
                }
            }

            return false;
        }

        private static void DownloadFileSimple(string url, string dest)
        {
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var netStream = resp.GetResponseStream())
            using (var fs = File.Create(dest))
            {
                byte[] buf = new byte[81920];
                int read;
                while ((read = netStream.Read(buf, 0, buf.Length)) > 0)
                    fs.Write(buf, 0, read);
            }
        }

        private void Report(LaunchContext ctx, LaunchPhase phase, string message)
        {
            try { Console.WriteLine($"[{LoaderName}] [{phase}] {message}"); }
            catch { }

            var h = ctx != null ? ctx.OnProgress : null;
            if (h == null) return;
            try
            {
                h(new LaunchProgress { Phase = phase, Message = message });
            }
            catch { }
        }

        private static bool IsRunningSignal(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;

            string[] keys = {
                "Backend library:",
                "LWJGL Version:",
                "Created window using",
                "Reloading ResourceManager",
                "Sound engine started",
            };

            foreach (var k in keys)
            {
                if (line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool HasModernNativeLayout(Dictionary<string, object> root)
        {
            if (!root.ContainsKey("arguments")) return false;
            var argsObj = root["arguments"] as Dictionary<string, object>;
            if (argsObj == null || !argsObj.ContainsKey("jvm")) return false;
            var jvmList = argsObj["jvm"] as ArrayList;
            if (jvmList == null) return false;

            foreach (var item in jvmList)
            {
                string s = item as string;
                if (s != null)
                {
                    if (s.IndexOf("${natives_directory}/java", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.IndexOf("${natives_directory}/lwjgl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.IndexOf("-Djna.tmpdir", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                else if (item is Dictionary<string, object> dict && dict.ContainsKey("value"))
                {
                    string vs = dict["value"] as string;
                    if (vs != null)
                    {
                        if (vs.IndexOf("${natives_directory}/java", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            vs.IndexOf("${natives_directory}/lwjgl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            vs.IndexOf("-Djna.tmpdir", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            return false;
        }

        protected static int GetRequiredJavaMajorVersion(
            Dictionary<string, object> root, string fallbackVersionName)
        {
            if (root.ContainsKey("javaVersion"))
            {
                var jv = root["javaVersion"] as Dictionary<string, object>;
                if (jv != null && jv.ContainsKey("majorVersion"))
                {
                    int m;
                    if (int.TryParse(Convert.ToString(jv["majorVersion"]), out m) && m > 0)
                        return m;
                }
            }

            string gameVersion = null;
            if (root.ContainsKey("minecraftVersion"))
                gameVersion = Convert.ToString(root["minecraftVersion"]);
            else if (root.ContainsKey("inheritsFrom"))
                gameVersion = Convert.ToString(root["inheritsFrom"]);
            else if (root.ContainsKey("id"))
                gameVersion = Convert.ToString(root["id"]);

            if (string.IsNullOrEmpty(gameVersion))
                gameVersion = fallbackVersionName;

            return InferJavaMajorFromGameVersion(gameVersion);
        }

        internal static int InferJavaMajorFromGameVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return 8;

            var m = Regex.Match(version, @"^(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return 8;

            int major = int.Parse(m.Groups[1].Value);
            int minor = int.Parse(m.Groups[2].Value);
            int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;

            if (major != 1) return 21;

            if (minor <= 16) return 8;
            if (minor == 17) return 16;
            if (minor == 18) return 17;
            if (minor == 19) return 17;
            if (minor == 20)
            {
                if (patch >= 5) return 21;
                return 17;
            }
            return 21;
        }

        protected virtual void PreLaunchFix(LaunchContext context, Dictionary<string, object> root) { }

        protected virtual Dictionary<string, object> LoadRootJson(LaunchContext context)
        {
            return LoadVersionJsonWithInheritance(context.VersionName, context.MinecraftDir);
        }

        protected virtual List<Dictionary<string, object>> FilterLibraries(
            List<Dictionary<string, object>> libs, LaunchContext context)
        {
            return libs;
        }

        protected virtual void AppendLoaderJvmArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        { }

        protected virtual List<string> ReorderClasspath(
            List<string> entries, LaunchContext context,
            Dictionary<string, object> root, string mainClass)
        {
            string coreJar = Path.Combine(
                Path.Combine(Path.Combine(context.MinecraftDir, "versions"), context.VersionName),
                context.VersionName + ".jar");

            if (File.Exists(coreJar))
            {
                entries.RemoveAll(e => string.Equals(e, coreJar, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, coreJar);
            }

            return entries;
        }

        protected virtual bool UseLaunchWrapperArgs(Dictionary<string, object> root)
        {
            return false;
        }

        protected virtual void AppendLaunchWrapperArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            cmd.Add("--username"); cmd.Add(context.Username);
            cmd.Add("--version"); cmd.Add(context.VersionName);
            cmd.Add("--gameDir"); cmd.Add(context.GetGameDir());
            cmd.Add("--assetsDir"); cmd.Add(Path.Combine(context.MinecraftDir, "assets"));
            cmd.Add("--assetIndex"); cmd.Add(GetAssetIndexId(root, context.MinecraftDir));
            cmd.Add("--uuid"); cmd.Add(context.Uuid);
            cmd.Add("--accessToken"); cmd.Add(context.AccessToken);
            cmd.Add("--userType"); cmd.Add(context.UserType);
        }

        protected virtual void PostProcessCommand(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        { }

        protected static List<Dictionary<string, object>> DeduplicateLibrariesByGA(
            List<Dictionary<string, object>> libs)
        {
            var byName = new Dictionary<string, Dictionary<string, object>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var lib in libs)
            {
                string name = lib.ContainsKey("name")
                    ? Convert.ToString(lib["name"]) : null;
                if (string.IsNullOrEmpty(name)) continue;

                if (!byName.ContainsKey(name))
                {
                    byName[name] = lib;
                }
                else
                {
                    bool hasCls = HasClassifiers(lib);
                    bool existingHasCls = HasClassifiers(byName[name]);
                    if (hasCls && !existingHasCls)
                        byName[name] = lib;
                }
            }

            var log4jBest = new Dictionary<string, Dictionary<string, object>>(
                StringComparer.OrdinalIgnoreCase);
            var others = new List<Dictionary<string, object>>();

            foreach (var lib in byName.Values)
            {
                string name = Convert.ToString(lib["name"]);
                string[] parts = name.Split(':');
                if (parts.Length < 3) { others.Add(lib); continue; }

                string group = parts[0];
                string artifact = parts[1];

                if (group.Equals("org.apache.logging.log4j", StringComparison.OrdinalIgnoreCase) &&
                    (artifact.Equals("log4j-api", StringComparison.OrdinalIgnoreCase) ||
                     artifact.Equals("log4j-core", StringComparison.OrdinalIgnoreCase)))
                {
                    string key = group + ":" + artifact;
                    string version = parts[2];
                    int at = version.IndexOf('@');
                    if (at >= 0) version = version.Substring(0, at);

                    if (!log4jBest.ContainsKey(key))
                    {
                        log4jBest[key] = lib;
                    }
                    else
                    {
                        string existingVer = Convert.ToString(log4jBest[key]["name"]).Split(':')[2];
                        int eAt = existingVer.IndexOf('@');
                        if (eAt >= 0) existingVer = existingVer.Substring(0, eAt);
                        if (CompareVersionStrings(version, existingVer) > 0)
                            log4jBest[key] = lib;
                    }
                }
                else
                {
                    others.Add(lib);
                }
            }

            var result = new List<Dictionary<string, object>>(others);
            result.AddRange(log4jBest.Values);
            return result;
        }

        protected static bool HasClassifiers(Dictionary<string, object> lib)
        {
            if (!lib.ContainsKey("downloads")) return false;
            var dl = lib["downloads"] as Dictionary<string, object>;
            return dl != null && dl.ContainsKey("classifiers");
        }

        protected static int CompareVersionStrings(string v1, string v2)
        {
            try { return new Version(v1).CompareTo(new Version(v2)); }
            catch { return string.Compare(v1, v2, StringComparison.Ordinal); }
        }

        protected Dictionary<string, object> LoadVersionJsonWithInheritance(
            string versionId, string minecraftDir)
        {
            return LoadVersionJsonWithInheritance(versionId, minecraftDir,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private Dictionary<string, object> LoadVersionJsonWithInheritance(
            string versionId, string minecraftDir, HashSet<string> visited)
        {
            if (!visited.Add(versionId)) return null;

            string jsonPath = Path.Combine(
                Path.Combine(Path.Combine(minecraftDir, "versions"), versionId),
                versionId + ".json");

            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"版本 JSON 不存在：{jsonPath}");

            Dictionary<string, object> root;
            try
            {
                string json = File.ReadAllText(jsonPath, Encoding.UTF8);
                root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception ex)
            {
                throw new Exception($"解析版本 JSON 失败：{jsonPath} - {ex.Message}");
            }

            if (root == null) return null;

            if (root.ContainsKey("inheritsFrom"))
            {
                string parentId = Convert.ToString(root["inheritsFrom"]);
                var parent = LoadVersionJsonWithInheritance(parentId, minecraftDir, visited);
                if (parent != null)
                    root = MergeVersionJson(parent, root);
            }

            return root;
        }

        private static Dictionary<string, object> MergeVersionJson(
            Dictionary<string, object> parent, Dictionary<string, object> child)
        {
            var result = new Dictionary<string, object>(parent);

            foreach (var kv in child)
            {
                if (kv.Key == "libraries")
                {
                    var parentLibs = parent.ContainsKey("libraries")
                        ? parent["libraries"] as ArrayList : null;
                    var childLibs = kv.Value as ArrayList;

                    var merged = new ArrayList();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (childLibs != null)
                    {
                        foreach (var l in childLibs)
                        {
                            var d = l as Dictionary<string, object>;
                            string n = d != null && d.ContainsKey("name")
                                ? Convert.ToString(d["name"]) : null;
                            if (n != null && seen.Add(n)) merged.Add(l);
                        }
                    }
                    if (parentLibs != null)
                    {
                        foreach (var l in parentLibs)
                        {
                            var d = l as Dictionary<string, object>;
                            string n = d != null && d.ContainsKey("name")
                                ? Convert.ToString(d["name"]) : null;
                            if (n != null && seen.Add(n)) merged.Add(l);
                        }
                    }
                    result["libraries"] = merged;
                }
                else if (kv.Key == "arguments")
                {
                    var parentArgs = parent.ContainsKey("arguments")
                        ? parent["arguments"] as Dictionary<string, object> : null;
                    var childArgs = kv.Value as Dictionary<string, object>;

                    var mergedArgs = new Dictionary<string, object>();

                    var jvmList = new ArrayList();
                    if (parentArgs != null && parentArgs.ContainsKey("jvm"))
                    {
                        var l = parentArgs["jvm"] as ArrayList;
                        if (l != null) foreach (var x in l) jvmList.Add(x);
                    }
                    if (childArgs != null && childArgs.ContainsKey("jvm"))
                    {
                        var l = childArgs["jvm"] as ArrayList;
                        if (l != null) foreach (var x in l) jvmList.Add(x);
                    }
                    if (jvmList.Count > 0) mergedArgs["jvm"] = jvmList;

                    var gameList = new ArrayList();
                    if (parentArgs != null && parentArgs.ContainsKey("game"))
                    {
                        var l = parentArgs["game"] as ArrayList;
                        if (l != null) foreach (var x in l) gameList.Add(x);
                    }
                    if (childArgs != null && childArgs.ContainsKey("game"))
                    {
                        var l = childArgs["game"] as ArrayList;
                        if (l != null) foreach (var x in l) gameList.Add(x);
                    }
                    if (gameList.Count > 0) mergedArgs["game"] = gameList;

                    result["arguments"] = mergedArgs;
                }
                else
                {
                    result[kv.Key] = kv.Value;
                }
            }

            return result;
        }

        protected List<Dictionary<string, object>> CollectLibraries(
            Dictionary<string, object> root, LaunchContext context, string os)
        {
            var result = new List<Dictionary<string, object>>();
            var libs = root.ContainsKey("libraries")
                ? root["libraries"] as ArrayList : null;
            if (libs == null) return result;

            string side = context.IsServer ? "server" : "client";
            string arch = GetHostArch();

            foreach (var libObj in libs)
            {
                var lib = libObj as Dictionary<string, object>;
                if (lib == null) continue;
                if (!IsLibraryAllowed(lib, side, os, arch)) continue;
                result.Add(lib);
            }
            return result;
        }

        protected static bool IsLibraryAllowed(
            Dictionary<string, object> lib, string side, string os, string arch)
        {
            // ★ 过滤掉 Legacy Fabric 提供的 LWJGL 补丁版
            if (lib.ContainsKey("name"))
            {
                string n = Convert.ToString(lib["name"]);
                if (!string.IsNullOrEmpty(n) &&
                    n.StartsWith("org.lwjgl.lwjgl:", StringComparison.OrdinalIgnoreCase) &&
                    n.IndexOf("+legacyfabric", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            if (!lib.ContainsKey("rules")) return true;
            var rules = lib["rules"] as ArrayList;
            if (rules == null || rules.Count == 0) return true;

            bool allowed = false;
            foreach (var r in rules)
            {
                var rule = r as Dictionary<string, object>;
                if (rule == null) continue;
                string action = rule.ContainsKey("action")
                    ? Convert.ToString(rule["action"]) : "allow";

                bool applies = true;

                if (rule.ContainsKey("os"))
                {
                    var osRule = rule["os"] as Dictionary<string, object>;
                    if (osRule != null)
                    {
                        if (osRule.ContainsKey("name"))
                        {
                            string ruleOs = Convert.ToString(osRule["name"]);
                            if (!ruleOs.Equals(os, StringComparison.OrdinalIgnoreCase))
                                applies = false;
                        }
                        if (applies && osRule.ContainsKey("arch"))
                        {
                            string ruleArch = Convert.ToString(osRule["arch"]);
                            if (!ruleArch.Equals(arch, StringComparison.OrdinalIgnoreCase))
                                applies = false;
                        }
                    }
                }

                if (rule.ContainsKey("sides"))
                {
                    var sides = rule["sides"] as ArrayList;
                    if (sides != null)
                    {
                        bool hit = false;
                        foreach (var s in sides)
                            if (Convert.ToString(s).Equals(side, StringComparison.OrdinalIgnoreCase))
                                hit = true;
                        if (!hit) applies = false;
                    }
                }

                if (applies) allowed = (action == "allow");
            }

            return allowed;
        }

        protected static string GetHostArch()
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

        protected List<string> BuildClasspathEntries(
            List<Dictionary<string, object>> libs, LaunchContext context)
        {
            var entries = new List<string>();
            string libDir = Path.Combine(context.MinecraftDir, "libraries");

            foreach (var lib in libs)
            {
                if (!context.IsModernNativeLayout && IsNativeLibrary(lib))
                    continue;

                string relPath = ResolveLibraryPath(lib);
                if (string.IsNullOrEmpty(relPath)) continue;

                string full = Path.Combine(libDir, relPath);
                if (File.Exists(full)) entries.Add(full);
            }

            return entries;
        }

        protected static bool IsNativeLibrary(Dictionary<string, object> lib)
        {
            if (!lib.ContainsKey("name")) return false;
            string name = Convert.ToString(lib["name"]);
            if (string.IsNullOrEmpty(name)) return false;

            string[] parts = name.Split(':');
            if (parts.Length >= 4 && parts[3].StartsWith("natives-", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        protected static string ResolveLibraryPath(Dictionary<string, object> lib)
        {
            if (lib.ContainsKey("downloads"))
            {
                var dl = lib["downloads"] as Dictionary<string, object>;
                if (dl != null && dl.ContainsKey("artifact"))
                {
                    var art = dl["artifact"] as Dictionary<string, object>;
                    if (art != null && art.ContainsKey("path"))
                        return Convert.ToString(art["path"]);
                }
            }

            if (!lib.ContainsKey("name")) return null;
            string name = Convert.ToString(lib["name"]);
            if (string.IsNullOrEmpty(name)) return null;

            string[] parts = name.Split(':');
            if (parts.Length < 3) return null;

            string group = parts[0].Replace('.', '/');
            string artifact = parts[1];
            string version = parts[2];
            string classifier = parts.Length >= 4 ? parts[3] : null;
            string ext = ".jar";

            int at = version.IndexOf('@');
            if (at >= 0) { ext = "." + version.Substring(at + 1); version = version.Substring(0, at); }

            if (!string.IsNullOrEmpty(classifier) && classifier.Contains("@"))
            {
                int a2 = classifier.IndexOf('@');
                ext = "." + classifier.Substring(a2 + 1);
                classifier = classifier.Substring(0, a2);
            }

            string fileName = $"{artifact}-{version}";
            if (!string.IsNullOrEmpty(classifier)) fileName += "-" + classifier;
            fileName += ext;

            return $"{group}/{artifact}/{version}/{fileName}";
        }

        protected void PrepareNatives(
            List<Dictionary<string, object>> libs, string minecraftDir, string nativesDir)
        {
            if (Directory.Exists(nativesDir))
            {
                try { Directory.Delete(nativesDir, true); } catch { }
            }
            Directory.CreateDirectory(nativesDir);

            Directory.CreateDirectory(Path.Combine(nativesDir, "java"));
            Directory.CreateDirectory(Path.Combine(nativesDir, "jna"));
            Directory.CreateDirectory(Path.Combine(nativesDir, "lwjgl"));
            Directory.CreateDirectory(Path.Combine(nativesDir, "netty"));

            int extracted = 0;
            string libDir = Path.Combine(minecraftDir, "libraries");

            foreach (var lib in libs)
            {
                if (lib.ContainsKey("downloads"))
                {
                    var dl = lib["downloads"] as Dictionary<string, object>;
                    if (dl != null && dl.ContainsKey("classifiers"))
                    {
                        var classifiers = dl["classifiers"] as Dictionary<string, object>;
                        if (classifiers != null)
                        {
                            foreach (var kv in classifiers)
                            {
                                string key = kv.Key;
                                if (!key.Contains("windows")) continue;

                                var native = kv.Value as Dictionary<string, object>;
                                if (native == null || !native.ContainsKey("path")) continue;

                                string full = Path.Combine(libDir, Convert.ToString(native["path"]));
                                extracted += ExtractNativeJar(full, nativesDir);
                            }
                        }
                        continue;
                    }
                }

                if (IsNativeLibrary(lib))
                {
                    var dl = lib.ContainsKey("downloads")
                        ? lib["downloads"] as Dictionary<string, object>
                        : null;
                    var art = dl != null && dl.ContainsKey("artifact")
                        ? dl["artifact"] as Dictionary<string, object>
                        : null;

                    if (art != null && art.ContainsKey("path"))
                    {
                        string full = Path.Combine(libDir, Convert.ToString(art["path"]));
                        extracted += ExtractNativeJar(full, nativesDir);
                    }
                }
            }
            Console.WriteLine($"[Natives] 已解压 {extracted} 个文件到 {nativesDir}");
        }

        private int ExtractNativeJar(string jarPath, string nativesDir)
        {
            if (!File.Exists(jarPath)) return 0;

            int count = 0;
            try
            {
                using (var fs = File.OpenRead(jarPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        if (entry.FullName.StartsWith("META-INF")) continue;

                        string dest = Path.Combine(nativesDir,
                            entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        string destDir = Path.GetDirectoryName(dest);
                        if (!Directory.Exists(destDir))
                            Directory.CreateDirectory(destDir);

                        entry.ExtractToFile(dest, true);
                        count++;
                    }
                }
            }
            catch { }
            return count;
        }

        protected string GetMainClass(Dictionary<string, object> root)
        {
            if (root.ContainsKey("mainClass"))
            {
                string mc = Convert.ToString(root["mainClass"]);
                if (!string.IsNullOrEmpty(mc)) return mc;
            }
            throw new Exception("版本 JSON 缺少 mainClass");
        }

        protected string GetAssetIndexId(Dictionary<string, object> root, string minecraftDir)
        {
            if (root.ContainsKey("assetIndex"))
            {
                var ai = root["assetIndex"] as Dictionary<string, object>;
                if (ai != null && ai.ContainsKey("id"))
                    return Convert.ToString(ai["id"]);
            }
            return "legacy";
        }

        protected void AddJvmArgsFromJson(
    List<string> cmd, Dictionary<string, object> root,
    LaunchContext context, string nativesDir)
        {
            if (!root.ContainsKey("arguments")) return;
            var argsObj = root["arguments"] as Dictionary<string, object>;
            if (argsObj == null || !argsObj.ContainsKey("jvm")) return;

            var jvmList = argsObj["jvm"] as ArrayList;
            if (jvmList == null) return;

            string libDir = Path.Combine(context.MinecraftDir, "libraries");
            string classpathSep = ";";

            for (int i = 0; i < jvmList.Count; i++)
            {
                var item = jvmList[i];

                string arg = null;
                if (item is string s)
                    arg = s;
                else if (item is Dictionary<string, object> dict)
                {
                    if (!ShouldIncludeJvmArg(dict)) continue;
                    if (dict.ContainsKey("value"))
                    {
                        var val = dict["value"];
                        if (val is string vs)
                            arg = vs;
                        else if (val is ArrayList list)
                        {
                            foreach (var sub in list)
                            {
                                if (sub is string subStr)
                                    cmd.Add(ReplaceJvmPlaceholders(subStr,
                                        context, root, libDir, nativesDir, classpathSep));
                            }
                            continue;
                        }
                    }
                }

                if (string.IsNullOrEmpty(arg)) continue;

                // -cp / -classpath / --class-path：由 BuildClasspathEntries 统一处理，
                // 这里跳过它及后面紧跟的路径参数
                if (arg == "-cp" || arg == "-classpath" || arg == "--class-path")
                {
                    if (i + 1 < jvmList.Count && jvmList[i + 1] is string next
                        && !next.StartsWith("-"))
                        i++;
                    continue;
                }

                // java.library.path 由外部统一追加，避免重复
                if (arg.StartsWith("-Djava.library.path", StringComparison.OrdinalIgnoreCase))
                    continue;

                cmd.Add(ReplaceJvmPlaceholders(arg, context, root, libDir, nativesDir, classpathSep));
            }
        }

        protected static bool ShouldIncludeJvmArg(Dictionary<string, object> dict)
        {
            if (!dict.ContainsKey("rules")) return true;
            var rules = dict["rules"] as ArrayList;
            if (rules == null) return true;

            bool allowed = false;
            foreach (var r in rules)
            {
                var rule = r as Dictionary<string, object>;
                if (rule == null) continue;
                string action = rule.ContainsKey("action")
                    ? Convert.ToString(rule["action"]) : "allow";

                bool applies = true;
                if (rule.ContainsKey("os"))
                {
                    var osRule = rule["os"] as Dictionary<string, object>;
                    if (osRule != null && osRule.ContainsKey("name"))
                    {
                        string ruleOs = Convert.ToString(osRule["name"]);
                        bool isWin = Environment.OSVersion.Platform == PlatformID.Win32NT;
                        bool match = (ruleOs == "windows" && isWin) ||
                                     (ruleOs != "windows" && !isWin);
                        if (!match) applies = false;
                    }
                }
                if (applies) allowed = (action == "allow");
            }
            return allowed;
        }

        protected string ReplaceJvmPlaceholders(
    string s, LaunchContext context, Dictionary<string, object> root,
    string libDir, string nativesDir, string cpSep)
        {
            if (string.IsNullOrEmpty(s)) return s;

            string assetsRoot = Path.Combine(context.MinecraftDir, "assets");
            string assetIndexId = GetAssetIndexId(root, context.MinecraftDir);

            return s
                .Replace("${library_directory}", libDir)
                .Replace("${libraries_directory}", libDir)
                .Replace("${natives_directory}", nativesDir ?? "")
                .Replace("${launcher_name}", "LaunchMinecraft")
                .Replace("${launcher_version}", "1.0")
                .Replace("${classpath_separator}", cpSep)
                .Replace("${version_name}", context.VersionName)
                .Replace("${game_directory}", context.GetGameDir())
                .Replace("${assets_root}", assetsRoot)
                .Replace("${assets_index_name}", assetIndexId)
                .Replace("${auth_player_name}", context.Username)
                .Replace("${auth_uuid}", context.Uuid)
                .Replace("${auth_access_token}", context.AccessToken)
                .Replace("${auth_session}", context.AccessToken)
                .Replace("${user_type}", context.UserType)
                .Replace("${user_properties}", "{}")
                .Replace("${version_type}", "release")
                .Replace("${clientid}", "0")
                .Replace("${auth_xuid}", "")
                .Replace("${resolution_width}", context.Width.ToString())
                .Replace("${resolution_height}", context.Height.ToString());
        }

        protected void AppendServerGameArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            cmd.Add("nogui");
        }

        protected void AddGameArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            string assetIndexId = GetAssetIndexId(root, context.MinecraftDir);

            if (root.ContainsKey("arguments"))
            {
                var argsObj = root["arguments"] as Dictionary<string, object>;
                if (argsObj != null && argsObj.ContainsKey("game"))
                {
                    var gameList = argsObj["game"] as ArrayList;
                    if (gameList != null)
                    {
                        foreach (var item in gameList)
                        {
                            if (item is string s)
                            {
                                cmd.Add(ReplaceGamePlaceholders(s, context, assetIndexId));
                            }
                            else if (item is Dictionary<string, object> dict)
                            {
                                if (!ShouldIncludeJvmArg(dict)) continue;
                                if (dict.ContainsKey("value"))
                                {
                                    var val = dict["value"];
                                    if (val is string vs)
                                        cmd.Add(ReplaceGamePlaceholders(vs, context, assetIndexId));
                                    else if (val is ArrayList list)
                                    {
                                        foreach (var sub in list)
                                            if (sub is string subStr)
                                                cmd.Add(ReplaceGamePlaceholders(subStr, context, assetIndexId));
                                    }
                                }
                            }
                        }
                        return;
                    }
                }
            }

            if (root.ContainsKey("minecraftArguments"))
            {
                string mcArgs = Convert.ToString(root["minecraftArguments"]);
                foreach (var raw in mcArgs.Split(' '))
                {
                    string s = raw.Trim();
                    if (s.Length == 0) continue;
                    cmd.Add(ReplaceGamePlaceholders(s, context, assetIndexId));
                }
                if (!mcArgs.Contains("--width"))
                {
                    cmd.Add("--width"); cmd.Add(context.Width.ToString());
                    cmd.Add("--height"); cmd.Add(context.Height.ToString());
                }
                return;
            }

            AppendFallbackGameArgs(cmd, context, assetIndexId);
        }

        private void AppendFallbackGameArgs(
            List<string> cmd, LaunchContext context, string assetIndexId)
        {
            cmd.Add("--username"); cmd.Add(context.Username);
            cmd.Add("--version"); cmd.Add(context.VersionName);
            cmd.Add("--gameDir"); cmd.Add(context.GetGameDir());
            cmd.Add("--assetsDir"); cmd.Add(Path.Combine(context.MinecraftDir, "assets"));
            cmd.Add("--assetIndex"); cmd.Add(assetIndexId);
            cmd.Add("--uuid"); cmd.Add(context.Uuid);
            cmd.Add("--accessToken"); cmd.Add(context.AccessToken);
            cmd.Add("--userType"); cmd.Add(context.UserType);
            cmd.Add("--width"); cmd.Add(context.Width.ToString());
            cmd.Add("--height"); cmd.Add(context.Height.ToString());
        }

        protected string ReplaceGamePlaceholders(
            string s, LaunchContext context, string assetIndexId)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s
                .Replace("${auth_player_name}", context.Username)
                .Replace("${version_name}", context.VersionName)
                .Replace("${game_directory}", context.GetGameDir())
                .Replace("${assets_root}", Path.Combine(context.MinecraftDir, "assets"))
                .Replace("${assets_index_name}", assetIndexId)
                .Replace("${auth_uuid}", context.Uuid)
                .Replace("${auth_access_token}", context.AccessToken)
                .Replace("${auth_session}", context.AccessToken)
                .Replace("${user_type}", context.UserType)
                .Replace("${user_properties}", "{}")
                .Replace("${version_type}", "release")
                .Replace("${launcher_name}", "LaunchMinecraft")
                .Replace("${launcher_version}", "1.0")
                .Replace("${natives_directory}", context.GetNativesDir())
                .Replace("${library_directory}", Path.Combine(context.MinecraftDir, "libraries"))
                .Replace("${resolution_width}", context.Width.ToString())
                .Replace("${resolution_height}", context.Height.ToString())
                .Replace("${quickPlayPath}", "")
                .Replace("${quickPlaySingleplayer}", "")
                .Replace("${quickPlayMultiplayer}", "")
                .Replace("${quickPlayRealms}", "")
                .Replace("${clientid}", "0")
                .Replace("${auth_xuid}", "")
                .Replace("${classpath}", "");
        }

        protected static string GetOsName()
        {
            var p = Environment.OSVersion.Platform;
            if (p == PlatformID.Win32NT || p == PlatformID.Win32Windows)
                return "windows";
            return "linux";
        }

        protected void StartProcess(List<string> cmd, LaunchContext context)
        {
            string argsStr = BuildArgumentString(cmd);

            var psi = new ProcessStartInfo
            {
                FileName = cmd[0],
                Arguments = argsStr,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = context.GetGameDir(),

                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };

            var proc = new Process { StartInfo = psi };

            bool windowReported = false;
            bool runningReported = false;

            proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Console.WriteLine(e.Data);

                if (!windowReported)
                {
                    windowReported = true;
                    Report(context, LaunchPhase.WaitingWindow, "等待游戏窗口出现...");
                }

                if (!runningReported && IsRunningSignal(e.Data))
                {
                    runningReported = true;
                    Report(context, LaunchPhase.Running, "正在运行");
                }
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) Console.Error.WriteLine(e.Data);
            };

            try
            {
                Report(context, LaunchPhase.StartingProcess, "启动进程...");

                proc.Start();
                proc.StandardInput.Close();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                Console.WriteLine($"[{LoaderName}] 进程已启动，PID = {proc.Id}");
                Console.WriteLine($"[{LoaderName}] 等待 Java 退出...");

                proc.WaitForExit();
                proc.WaitForExit(int.MaxValue);

                Console.WriteLine($"[{LoaderName}] Java 进程已退出，退出码 {proc.ExitCode}");

                if (!runningReported)
                {
                    Report(context, LaunchPhase.Failed,
                        $"启动失败（退出码 {proc.ExitCode}）");
                }
                else
                {
                    Report(context, LaunchPhase.Stopped, "游戏已退出");
                }
            }
            catch (Exception ex)
            {
                Report(context, LaunchPhase.Failed, "启动失败：" + ex.Message);
                throw new Exception($"启动进程失败：{ex.Message}", ex);
            }
        }

        private static string BuildArgumentString(List<string> cmd)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < cmd.Count; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                string a = cmd[i];
                if (a.Contains(" ") && !a.StartsWith("\""))
                    a = "\"" + a + "\"";
                sb.Append(a);
            }
            return sb.ToString();
        }

        protected static void RemoveQuickPlayArgs(List<string> cmd)
        {
            string[] keys = {
                "--quickPlayPath",
                "--quickPlaySingleplayer",
                "--quickPlayMultiplayer",
                "--quickPlayRealms"
            };

            for (int i = 0; i < cmd.Count; i++)
            {
                bool hit = false;
                foreach (var k in keys)
                {
                    if (cmd[i] == k) { hit = true; break; }
                }
                if (!hit) continue;

                if (i + 1 < cmd.Count) cmd.RemoveAt(i + 1);
                cmd.RemoveAt(i);
                i--;
            }
        }

        protected static void RunBatHidden(
            string workingDir, string batPath, string javaBaseDir = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + batPath + "\"",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false,
            };

            string javaBin = ResolveJavaBinDir(javaBaseDir);
            if (!string.IsNullOrEmpty(javaBin))
            {
                string oldPath = psi.EnvironmentVariables.ContainsKey("PATH")
                    ? psi.EnvironmentVariables["PATH"]
                    : (Environment.GetEnvironmentVariable("PATH") ?? "");

                psi.EnvironmentVariables["PATH"] = javaBin + ";" + oldPath;

                string javaHome = Path.GetDirectoryName(javaBin);
                if (!string.IsNullOrEmpty(javaHome))
                    psi.EnvironmentVariables["JAVA_HOME"] = javaHome;

                Console.WriteLine($"[Java] run.bat 使用 Java bin: {javaBin}");
            }

            try
            {
                var proc = Process.Start(psi);
                Console.WriteLine($"[run.bat] 已启动 PID = {proc?.Id}，脚本: {batPath}");
            }
            catch (Exception ex)
            {
                throw new Exception($"运行启动脚本失败：{batPath} - {ex.Message}", ex);
            }
        }

        private static string ResolveJavaBinDir(string javaBaseDir)
        {
            if (string.IsNullOrEmpty(javaBaseDir) || !Directory.Exists(javaBaseDir))
                return null;

            if (File.Exists(Path.Combine(javaBaseDir, "bin", "java.exe")))
                return Path.Combine(javaBaseDir, "bin");

            try
            {
                foreach (var sub in Directory.GetDirectories(javaBaseDir))
                {
                    if (File.Exists(Path.Combine(sub, "bin", "java.exe")))
                        return Path.Combine(sub, "bin");
                }
            }
            catch { }

            return null;
        }

        protected static bool IsForgeOrNeoForgeRunBat(string batPath)
        {
            if (string.IsNullOrEmpty(batPath) || !File.Exists(batPath))
                return false;

            try
            {
                string content = File.ReadAllText(batPath);

                bool hasUserJvm =
                    content.IndexOf("user_jvm_args.txt", StringComparison.OrdinalIgnoreCase) >= 0;

                bool hasForgeOrNeo =
                    content.IndexOf("minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    content.IndexOf("neoforged", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    content.IndexOf("forge-", StringComparison.OrdinalIgnoreCase) >= 0;

                return hasUserJvm && hasForgeOrNeo;
            }
            catch
            {
                return false;
            }
        }

        public static bool NeedsAcceptEula(string serverDir, out string eulaPath)
        {
            eulaPath = Path.Combine(serverDir, "eula.txt");
            if (!File.Exists(eulaPath)) return false;

            try
            {
                string content = File.ReadAllText(eulaPath);
                var m = Regex.Match(content, @"^\s*eula\s*=\s*(true|false)\s*$",
                                    RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (!m.Success) return false;
                return m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void AcceptEula(string serverDir)
        {
            string eulaPath = Path.Combine(serverDir, "eula.txt");
            var sb = new StringBuilder();
            bool found = false;

            if (File.Exists(eulaPath))
            {
                foreach (var raw in File.ReadAllLines(eulaPath))
                {
                    if (Regex.IsMatch(raw, @"^\s*eula\s*=", RegexOptions.IgnoreCase))
                    {
                        sb.AppendLine("eula=true");
                        found = true;
                    }
                    else sb.AppendLine(raw);
                }
            }

            if (!found) sb.AppendLine("eula=true");
            File.WriteAllText(eulaPath, sb.ToString(), new UTF8Encoding(false));
        }
    }

    internal static class JavaLocator
    {
        private class JavaCandidate
        {
            public string Path;
            public int Major;
        }

        /// <summary>启动器内置 Java 根目录：exe目录\Launcher Setting\Java</summary>
        public static string GetBuiltInJavaRoot()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "Launcher Setting", "Java");
        }

        /// <summary>
        /// 查找 Java。
        /// 优先级：
        ///   1) 启动器内置：Launcher Setting\Java\{requiredMajor}\bin\java.exe
        ///   2) 用户指定 javaBaseDir 及它的一级子目录
        ///   3) 注册表（各种 JDK 发行版）
        /// 都找不到就抛异常，由 UI 层捕获后引导下载。
        /// </summary>
        public static string Find(int requiredMajor, string javaBaseDir)
        {
            var candidates = new List<JavaCandidate>();

            // 1) 内置 Java
            string builtIn = Path.Combine(GetBuiltInJavaRoot(),
                                          requiredMajor.ToString(), "bin", "java.exe");
            if (File.Exists(builtIn))
            {
                int actual = GetJavaMajorVersion(builtIn);
                if (actual == requiredMajor)
                {
                    Console.WriteLine($"[Java] 使用内置 Java {requiredMajor}: {builtIn}");
                    return builtIn;
                }
                Console.WriteLine($"[Java] 内置 Java 版本不符（需要 {requiredMajor}，实际 {actual}）: {builtIn}");
            }

            // 2) 用户指定目录
            if (!string.IsNullOrEmpty(javaBaseDir))
            {
                if (Directory.Exists(javaBaseDir))
                {
                    Console.WriteLine($"[Java] 从用户指定目录扫描: {javaBaseDir}");
                    ScanJavaBaseDir(javaBaseDir, candidates);

                    if (candidates.Count > 0)
                        Console.WriteLine($"[Java] 从指定目录找到 {candidates.Count} 个 Java");
                    else
                        Console.WriteLine($"[Java] 指定目录中未找到任何 Java，继续扫描注册表");
                }
                else
                {
                    Console.WriteLine($"[Java] 警告: 指定目录不存在: {javaBaseDir}，继续扫描注册表");
                }
            }

            // 3) 注册表
            ScanAllRegistryPaths(candidates);

            if (candidates.Count == 0)
                throw new Exception(
                    $"未找到任何 Java 运行时（已扫描内置目录 + 指定目录 + 注册表）。\n" +
                    $"请通过启动器下载 Java {requiredMajor}（启动前会弹出提示），\n" +
                    $"或手动放置到：\n" +
                    $"  {Path.Combine(GetBuiltInJavaRoot(), requiredMajor.ToString())}\\bin\\java.exe");

            return PickBest(candidates, requiredMajor);
        }

        /// <summary>
        /// 只判断是否存在精确匹配 requiredMajor 的 Java（不返回路径）。
        /// 用于 UI 预检，避免"有 Java 8 但版本不精确"时误报。
        /// </summary>
        public static bool HasExact(int requiredMajor, string javaBaseDir)
        {
            try
            {
                // 1) 内置
                string builtIn = Path.Combine(GetBuiltInJavaRoot(),
                                              requiredMajor.ToString(), "bin", "java.exe");
                if (File.Exists(builtIn) && GetJavaMajorVersion(builtIn) == requiredMajor)
                    return true;

                // 2) 用户指定目录 + 注册表
                var candidates = new List<JavaCandidate>();

                if (!string.IsNullOrEmpty(javaBaseDir) && Directory.Exists(javaBaseDir))
                    ScanJavaBaseDir(javaBaseDir, candidates);

                ScanAllRegistryPaths(candidates);

                foreach (var c in candidates)
                    if (c.Major == requiredMajor) return true;

                return false;
            }
            catch { return false; }
        }

        // ============================================================
        //   用户指定目录扫描
        // ============================================================
        private static void ScanJavaBaseDir(string baseDir, List<JavaCandidate> list)
        {
            TryAddCandidate(list, baseDir);

            try
            {
                foreach (var subDir in Directory.GetDirectories(baseDir))
                    TryAddCandidate(list, subDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Java] 扫描 {baseDir} 失败: {ex.Message}");
            }
        }

        // ============================================================
        //   注册表扫描
        // ============================================================
        private static void ScanAllRegistryPaths(List<JavaCandidate> list)
        {
            Console.WriteLine("[Java] 开始扫描注册表...");

            string[] javaSoftBasePaths = {
                @"SOFTWARE\JavaSoft\Java Development Kit",
                @"SOFTWARE\JavaSoft\Java Runtime Environment",
                @"SOFTWARE\JavaSoft\JDK",
                @"SOFTWARE\JavaSoft\JRE",
                @"SOFTWARE\JavaSoft\Java SE Development Kit",
                @"SOFTWARE\JavaSoft\Java SE Runtime Environment",
            };

            foreach (var basePath in javaSoftBasePaths)
                ScanRegistryKey(basePath, list, "JavaHome");

            string[] adoptiumPaths = {
                @"SOFTWARE\Eclipse Adoptium\JDK",
                @"SOFTWARE\Eclipse Adoptium\JRE",
                @"SOFTWARE\AdoptOpenJDK\JDK",
                @"SOFTWARE\AdoptOpenJDK\JRE",
                @"SOFTWARE\Eclipse Foundation\JDK",
                @"SOFTWARE\Eclipse Foundation\JRE",
            };

            foreach (var basePath in adoptiumPaths)
                ScanRegistryKeyWithMsiPath(basePath, list);

            string[] correttoPaths = {
                @"SOFTWARE\Amazon Corretto\JDK",
                @"SOFTWARE\Amazon Corretto\JRE",
                @"SOFTWARE\Amazon\Corretto\JDK",
                @"SOFTWARE\Amazon\Corretto\JRE",
            };

            foreach (var basePath in correttoPaths)
                ScanRegistryKeyWithMsiPath(basePath, list);

            ScanRegistryKey(@"SOFTWARE\Azul Systems\Zulu", list, "JavaHome");

            string[] libericaPaths = {
                @"SOFTWARE\BellSoft\LibericaJDK",
                @"SOFTWARE\BellSoft\LibericaJRE",
            };

            foreach (var basePath in libericaPaths)
                ScanRegistryKey(basePath, list, "JavaHome");

            string[] ibmPaths = {
                @"SOFTWARE\IBM\Java Development Kit",
                @"SOFTWARE\IBM\Java Runtime Environment",
                @"SOFTWARE\IBM\Java2 Runtime Environment",
                @"SOFTWARE\IBM\Semeru",
                @"SOFTWARE\Semeru\SemeruJDK",
            };

            foreach (var basePath in ibmPaths)
                ScanRegistryKey(basePath, list, "JavaHome");

            string[] microsoftPaths = {
                @"SOFTWARE\Microsoft\JDK",
                @"SOFTWARE\Microsoft\OpenJDK",
            };

            foreach (var basePath in microsoftPaths)
                ScanRegistryKey(basePath, list, "JavaHome");

            Console.WriteLine($"[Java] 注册表扫描完成，累计 {list.Count} 个候选");
        }

        private static void ScanRegistryKey(
            string basePath, List<JavaCandidate> list, string javaHomeValueName)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var rootKey = baseKey.OpenSubKey(basePath))
                {
                    if (rootKey != null)
                        ScanSubKeysForJavaHome(rootKey, list, javaHomeValueName);
                }
            }
            catch { }

            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var rootKey = baseKey.OpenSubKey(basePath))
                {
                    if (rootKey != null)
                        ScanSubKeysForJavaHome(rootKey, list, javaHomeValueName);
                }
            }
            catch { }

            try
            {
                using (var rootKey = Registry.CurrentUser.OpenSubKey(basePath))
                {
                    if (rootKey != null)
                        ScanSubKeysForJavaHome(rootKey, list, javaHomeValueName);
                }
            }
            catch { }
        }

        private static void ScanSubKeysForJavaHome(
            RegistryKey rootKey, List<JavaCandidate> list, string valueName)
        {
            try
            {
                string javaHome = rootKey.GetValue(valueName) as string;
                if (!string.IsNullOrEmpty(javaHome))
                {
                    TryAddCandidate(list, javaHome);
                    return;
                }
            }
            catch { }

            foreach (var subName in rootKey.GetSubKeyNames())
            {
                try
                {
                    using (var subKey = rootKey.OpenSubKey(subName))
                    {
                        if (subKey == null) continue;
                        ScanSubKeysForJavaHome(subKey, list, valueName);
                    }
                }
                catch { }
            }
        }

        private static void ScanRegistryKeyWithMsiPath(
            string basePath, List<JavaCandidate> list)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var rootKey = baseKey.OpenSubKey(basePath))
                {
                    if (rootKey != null)
                        ScanMsiSubKeys(rootKey, list);
                }
            }
            catch { }

            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var rootKey = baseKey.OpenSubKey(basePath))
                {
                    if (rootKey != null)
                        ScanMsiSubKeys(rootKey, list);
                }
            }
            catch { }

            try
            {
                using (var rootKey = Registry.CurrentUser.OpenSubKey(basePath))
                {
                    if (rootKey != null)
                        ScanMsiSubKeys(rootKey, list);
                }
            }
            catch { }
        }

        private static void ScanMsiSubKeys(RegistryKey rootKey, List<JavaCandidate> list)
        {
            foreach (var versionName in rootKey.GetSubKeyNames())
            {
                try
                {
                    using (var versionKey = rootKey.OpenSubKey(versionName))
                    {
                        if (versionKey == null) continue;

                        string[] possibleSubPaths = {
                            @"hotspot\MSI",
                            @"MSI",
                            @"hotspot",
                            "",
                        };

                        foreach (var subPath in possibleSubPaths)
                        {
                            RegistryKey keyToRead = versionKey;
                            if (!string.IsNullOrEmpty(subPath))
                                keyToRead = versionKey.OpenSubKey(subPath);

                            if (keyToRead == null) continue;

                            try
                            {
                                string path = keyToRead.GetValue("Path") as string;
                                if (string.IsNullOrEmpty(path))
                                    path = keyToRead.GetValue("JavaHome") as string;

                                if (!string.IsNullOrEmpty(path))
                                {
                                    TryAddCandidate(list, path);
                                    break;
                                }
                            }
                            finally
                            {
                                if (keyToRead != versionKey)
                                    keyToRead.Close();
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // ============================================================
        //   候选管理
        // ============================================================
        private static void TryAddCandidate(List<JavaCandidate> list, string javaHome)
        {
            if (string.IsNullOrEmpty(javaHome)) return;
            javaHome = javaHome.Trim().Trim('"');

            string exe;
            if (Path.GetFileName(javaHome).Equals("bin", StringComparison.OrdinalIgnoreCase))
                exe = Path.Combine(javaHome, "java.exe");
            else
                exe = Path.Combine(javaHome, "bin", "java.exe");

            if (!File.Exists(exe)) return;

            string full;
            try { full = Path.GetFullPath(exe); }
            catch { return; }

            foreach (var c in list)
                if (string.Equals(c.Path, full, StringComparison.OrdinalIgnoreCase))
                    return;

            int major = GetJavaMajorVersion(full);
            if (major <= 0) return;

            list.Add(new JavaCandidate { Path = full, Major = major });
            Console.WriteLine($"[Java] 发现: Java {major} -> {full}");
        }

        private static string PickBest(List<JavaCandidate> candidates, int requiredMajor)
        {
            foreach (var c in candidates)
                if (c.Major == requiredMajor)
                {
                    Console.WriteLine($"[Java] 精确匹配到 Java {requiredMajor}: {c.Path}");
                    return c.Path;
                }

            JavaCandidate bestGreater = null;
            JavaCandidate bestClosest = null;

            foreach (var c in candidates)
            {
                if (c.Major >= requiredMajor)
                    if (bestGreater == null || c.Major < bestGreater.Major)
                        bestGreater = c;

                if (bestClosest == null ||
                    Math.Abs(c.Major - requiredMajor) < Math.Abs(bestClosest.Major - requiredMajor))
                    bestClosest = c;
            }

            JavaCandidate chosen = bestGreater ?? bestClosest;
            if (chosen == null)
                throw new Exception("没有可用的 Java 候选");

            Console.WriteLine($"[Java] 警告: 未找到 Java {requiredMajor}，改用 Java {chosen.Major}: {chosen.Path}");
            Console.WriteLine($"[Java] 已检测到的 Java 版本: " +
                string.Join(", ", candidates.ConvertAll(c => c.Major.ToString()).ToArray()));

            return chosen.Path;
        }

        private static int GetJavaMajorVersion(string javaExePath)
        {
            try
            {
                string javaHome = Path.GetDirectoryName(Path.GetDirectoryName(javaExePath));
                string releaseFile = Path.Combine(javaHome, "release");
                if (File.Exists(releaseFile))
                {
                    foreach (var line in File.ReadAllLines(releaseFile))
                    {
                        if (line.StartsWith("JAVA_VERSION="))
                        {
                            string v = line.Substring("JAVA_VERSION=".Length).Trim('"');
                            var m = Regex.Match(v, @"^(\d+)(?:\.(\d+))?");
                            if (m.Success)
                            {
                                int major = int.Parse(m.Groups[1].Value);
                                if (major == 1 && m.Groups[2].Success)
                                    major = int.Parse(m.Groups[2].Value);
                                return major;
                            }
                        }
                    }
                }
            }
            catch { }

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

                    var m = Regex.Match(output, "version\\s+\"(\\d+)(?:\\.(\\d+))?");
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
    }
}