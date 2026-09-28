using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Install_Minecraft_Versions
{
    internal class OptiFine
    {
        // ===================== OptiFine 源 =====================
        private const string BMCL_BASE = "https://bmclapi2.bangbang93.com";
        private const string OPTIFINE_OFFICIAL = "https://optifine.net";

        // ===================== 下载参数 =====================
        private const int RETRY_COUNT = 3;
        private const int INSTALLER_ROUNDS = 3;
        private const int HTTP_TIMEOUT_MS = 30000;
        private const int READ_WRITE_TIMEOUT_MS = 60000;
        private const int INSTALLER_TIMEOUT_MS = 5 * 60 * 1000;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly object _consoleLock = new object();

        // ===================== OptiFine 版本模型 =====================
        private class OptiFineVersion
        {
            public string McVersion { get; set; }
            public string Patch { get; set; }
            public string Type { get; set; }
            public string FileName { get; set; }
            public string Forge { get; set; }
        }

        // ===================== 客户端安装入口 =====================
        public static void InstallClient(string version, string minecraftDir,
                                  string loaderParam, bool forForge = false)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("版本号不能为空", "version");
            if (string.IsNullOrEmpty(minecraftDir))
                throw new ArgumentException(".minecraft 路径不能为空", "minecraftDir");

            minecraftDir = Path.GetFullPath(minecraftDir);

            Log($"[OptiFine] 开始安装客户端 OptiFine {version}" +
                (forForge ? "（Forge 模式）" : "（独立模式）"));

            var versions = GetOptiFineVersions(version);
            if (versions.Count == 0)
                throw new Exception($"未找到适用于 Minecraft {version} 的 OptiFine 版本");

            OptiFineVersion target = PickVersion(versions, loaderParam);
            Log($"[OptiFine] 使用版本: {target.FileName}" +
                (string.IsNullOrEmpty(target.Forge) ? "" : $"（配套 {target.Forge}）"));

            string installerPath = DownloadInstaller(target);
            Log($"[OptiFine] 安装器已下载: {installerPath}");

            // ============================================================
            // Forge 模式：不运行安装器，直接把 OptiFine jar 复制到 mods 文件夹
            // ============================================================
            if (forForge)
            {
                InstallToModsFolder(installerPath, minecraftDir);
                Log($"[OptiFine] 客户端 OptiFine {version} 安装完成（Forge 模式）");
                return;
            }

            // ============================================================
            // 独立模式：运行安装器，生成 1.20.1-OptiFine_xxx 独立版本
            // ============================================================
            RunInstaller(installerPath, minecraftDir, version);
            Log($"[OptiFine] 安装器执行完毕");

            string optifineVersionId = $"{version}-OptiFine_{target.Type}_{target.Patch}";
            string versionDir = Path.Combine(
                Path.Combine(minecraftDir, "versions"), optifineVersionId);
            string versionJsonPath = Path.Combine(versionDir, optifineVersionId + ".json");

            if (!File.Exists(versionJsonPath))
                throw new Exception(
                    $"OptiFine 安装器未生成版本 JSON: {versionJsonPath}\n" +
                    "可能原因：原版 Minecraft 未安装/未运行过，或 Java 版本不兼容。");

            MergeParentJson(versionJsonPath, minecraftDir, version, forForge);

            Log($"[OptiFine] 客户端 OptiFine {version} 安装完成");
        }

        // ===================== 服务端：不提供 =====================
        public static void InstallServer(string version, string serverDir, string loaderParam)
        {
            throw new NotSupportedException(
                "OptiFine 是纯客户端模组，没有服务端安装。\n" +
                "请只在客户端安装 OptiFine，服务端保持原版即可。");
        }

        // ===================== Forge 模式：复制到 mods 文件夹 =====================
        /// <summary>
        /// Forge 模式下，把 OptiFine 的 jar 直接复制到 .minecraft\mods\。
        /// 启动时选择 Forge 版本，Forge 会把 OptiFine 当作普通模组加载。
        /// 不生成独立的 OptiFine 版本配置。
        /// </summary>
        private static void InstallToModsFolder(string optifineJarPath, string minecraftDir)
        {
            string modsDir = Path.Combine(minecraftDir, "mods");
            if (!Directory.Exists(modsDir))
            {
                Directory.CreateDirectory(modsDir);
                Log($"[OptiFine] 已创建 mods 文件夹: {modsDir}");
            }

            string fileName = Path.GetFileName(optifineJarPath);
            string destPath = Path.Combine(modsDir, fileName);

            // ---- 清理 mods 里其他旧版 OptiFine jar ----
            try
            {
                foreach (string old in Directory.GetFiles(modsDir, "OptiFine_*.jar"))
                {
                    if (string.Equals(old, destPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        File.Delete(old);
                        Log($"[OptiFine] 已删除 mods 中旧版本: {Path.GetFileName(old)}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[OptiFine] 删除旧文件失败（忽略）: {ex.Message}");
                    }
                }
            }
            catch { }

            // ---- 目标已存在且大小一致：跳过 ----
            if (File.Exists(destPath))
            {
                try
                {
                    ValidateJarFile(destPath);
                    long srcLen = new FileInfo(optifineJarPath).Length;
                    long dstLen = new FileInfo(destPath).Length;
                    if (srcLen == dstLen)
                    {
                        Log($"[OptiFine] mods 中已存在相同文件，跳过: {fileName}");
                        return;
                    }
                }
                catch { }

                try { File.Delete(destPath); } catch { }
            }

            // ---- 复制 ----
            File.Copy(optifineJarPath, destPath, true);
            Log($"[OptiFine] 已复制到 mods: {destPath}");
        }

        // ===================== 版本列表获取 =====================
        private static List<OptiFineVersion> GetOptiFineVersions(string mcVersion)
        {
            string url = $"{BMCL_BASE}/optifine/{Uri.EscapeDataString(mcVersion)}";
            Log($"[OptiFine] 获取版本列表: {url}");

            string json = DownloadString(url);
            if (string.IsNullOrEmpty(json))
                throw new Exception("OptiFine 版本列表为空");

            var list = new List<OptiFineVersion>();
            try
            {
                var arr = new JavaScriptSerializer().Deserialize<ArrayList>(json);
                if (arr == null) return list;

                foreach (var item in arr)
                {
                    var dict = item as Dictionary<string, object>;
                    if (dict == null) continue;

                    var v = new OptiFineVersion
                    {
                        McVersion = dict.ContainsKey("mcversion") ? dict["mcversion"].ToString() : null,
                        Patch = dict.ContainsKey("patch") ? dict["patch"].ToString() : null,
                        Type = dict.ContainsKey("type") ? dict["type"].ToString() : null,
                        FileName = dict.ContainsKey("filename") ? dict["filename"].ToString() : null,
                        Forge = dict.ContainsKey("forge") ? dict["forge"].ToString() : null,
                    };

                    if (!string.IsNullOrEmpty(v.FileName))
                        list.Add(v);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"解析 OptiFine 版本列表失败: {ex.Message}", ex);
            }

            list.Sort((a, b) => string.Compare(b.Patch, a.Patch, StringComparison.Ordinal));
            return list;
        }

        // ===================== 版本选择 =====================
        private static OptiFineVersion PickVersion(List<OptiFineVersion> versions, string loaderParam)
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
                foreach (var v in versions)
                {
                    if (v.Patch.Equals(param, StringComparison.OrdinalIgnoreCase))
                        return v;
                    if ($"{v.Type}_{v.Patch}".Equals(param, StringComparison.OrdinalIgnoreCase))
                        return v;
                }
                Log($"[OptiFine] 未找到指定版本 {param}，改用最新版");
            }

            return versions[0];
        }

        // ===================== 下载安装器（★ 双源 × 多轮） =====================
        private static string DownloadInstaller(OptiFineVersion target)
        {
            string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
            string cacheDir = Path.Combine(launcherDir, "Launcher Setting", "Mode Loader Installer");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

            string fileName = target.FileName;
            if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                fileName += ".jar";

            string installerPath = Path.Combine(cacheDir, fileName);

            // ---- 缓存命中 ----
            if (File.Exists(installerPath) && new FileInfo(installerPath).Length > 0)
            {
                try { ValidateJarFile(installerPath); }
                catch { try { File.Delete(installerPath); } catch { } }

                if (File.Exists(installerPath))
                {
                    Log($"[OptiFine] 安装器已存在: {installerPath}");
                    return installerPath;
                }
            }

            // ---- BMCLAPI 专用下载端点 ----
            string bmclUrl = $"{BMCL_BASE}/optifine/{Uri.EscapeDataString(target.McVersion)}/" +
                             $"{Uri.EscapeDataString(target.Type)}/{Uri.EscapeDataString(target.Patch)}";

            Log($"[OptiFine] 下载安装器（最多 {INSTALLER_ROUNDS} 轮，每轮 2 源）: {fileName}");

            string lastError = null;

            for (int round = 1; round <= INSTALLER_ROUNDS; round++)
            {
                if (round > 1)
                    Log($"[OptiFine] 第 {round}/{INSTALLER_ROUNDS} 轮尝试...");

                // ============================================================
                // 源 1：BMCLAPI
                // ============================================================
                try
                {
                    Log($"[OptiFine] 尝试源: {bmclUrl}");
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }

                    DownloadFile(bmclUrl, installerPath);
                    ValidateJarFile(installerPath);

                    Log($"[OptiFine] 安装器已下载: {installerPath}");
                    return installerPath;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Log($"[OptiFine] 第 {round} 轮 BMCLAPI 失败: {ex.Message}");
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
                }

                // ============================================================
                // 源 2：OptiFine 官方（adloadx → 解析 → downloadx）
                // ============================================================
                try
                {
                    Log($"[OptiFine] 尝试源: OptiFine 官方");
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }

                    DownloadInstallerFromOfficial(fileName, installerPath);
                    ValidateJarFile(installerPath);

                    Log($"[OptiFine] 安装器已下载: {installerPath}");
                    return installerPath;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Log($"[OptiFine] 第 {round} 轮官方源失败: {ex.Message}");
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
                }

                if (round < INSTALLER_ROUNDS)
                {
                    int waitSec = round * 3;
                    Log($"[OptiFine] 本轮全部源失败，{waitSec} 秒后重试...");
                    Thread.Sleep(waitSec * 1000);
                }
            }

            throw new Exception(
                $"下载 OptiFine 安装器失败（{INSTALLER_ROUNDS} 轮全部失败）: {lastError}");
        }

        // ===================== 官方源下载（解析 adloadx → downloadx） =====================
        /// <summary>
        /// OptiFine 官网不提供静态文件路径。正确的下载流程是：
        ///   1. GET adloadx?f=文件名   → 返回一个带广告的 HTML
        ///   2. 从 HTML 里解析出真实 downloadx 链接
        ///   3. GET downloadx?f=...&x=...  → 下载真实文件
        /// </summary>
        private static void DownloadInstallerFromOfficial(string fileName, string dest)
        {
            // ---- 1. 获取 adloadx 页面 ----
            string adloadxUrl = $"{OPTIFINE_OFFICIAL}/adloadx?f={Uri.EscapeDataString(fileName)}";

            string html;
            var req = (HttpWebRequest)WebRequest.Create(adloadxUrl);
            req.UserAgent = UA_STRING;
            req.Timeout = HTTP_TIMEOUT_MS;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.Referer = $"{OPTIFINE_OFFICIAL}/downloads";

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                html = reader.ReadToEnd();
            }

            if (string.IsNullOrEmpty(html))
                throw new Exception("adloadx 页面为空");

            // ---- 2. 从 HTML 中解析 downloadx 链接 ----
            string downloadPath = null;

            var m1 = Regex.Match(html,
                @"href\s*=\s*[""'](/?(?:https?://(?:www\.)?optifine\.net/)?downloadx\?[^""'<>\s]+)[""']",
                RegexOptions.IgnoreCase);
            if (m1.Success)
                downloadPath = m1.Groups[1].Value;

            if (string.IsNullOrEmpty(downloadPath))
            {
                var m2 = Regex.Match(html,
                    @"""(/?(?:https?://(?:www\.)?optifine\.net/)?downloadx\?[^""<>\s]+)""",
                    RegexOptions.IgnoreCase);
                if (m2.Success)
                    downloadPath = m2.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(downloadPath))
            {
                var m3 = Regex.Match(html, @"(downloadx\?f=[^""'<>\s]+)", RegexOptions.IgnoreCase);
                if (m3.Success)
                    downloadPath = m3.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(downloadPath))
                throw new Exception("无法从 adloadx 页面解析出 downloadx 链接");

            // 归一化成完整 URL
            if (downloadPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // 已经是完整 URL
            }
            else if (downloadPath.StartsWith("/"))
            {
                downloadPath = OPTIFINE_OFFICIAL + downloadPath;
            }
            else
            {
                downloadPath = OPTIFINE_OFFICIAL + "/" + downloadPath;
            }

            Log($"[OptiFine] 官方下载链接: {downloadPath}");

            // ---- 3. 下载真实文件 ----
            DownloadFile(downloadPath, dest);
        }

        // ===================== 调用 OptiFine Installer =====================
        private static void RunInstaller(string installerPath, string minecraftDir, string mcVersion)
        {
            string javaPath = FindJavaForOptiFine(mcVersion);
            Log($"[OptiFine] Java: {javaPath}");

            // ★ 关键修复：OptiFine 安装器需要 launcher_profiles.json
            EnsureLauncherProfiles(minecraftDir);

            // .minecraft 的上一级目录
            string gameParentDir = Path.GetDirectoryName(minecraftDir);
            if (string.IsNullOrEmpty(gameParentDir))
                gameParentDir = minecraftDir;

            string args =
                $"-Duser.home=\"{gameParentDir}\" " +
                $"-Djava.awt.headless=true " +
                $"-cp \"{installerPath}\" optifine.Installer";

            Log($"[OptiFine] 执行安装器...");
            Log($"[OptiFine] WorkingDir: {gameParentDir}");
            Log($"[OptiFine] APPDATA   : {gameParentDir}");

            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = args,
                WorkingDirectory = gameParentDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // ★ 关键：OptiFine 安装器在 Windows 上通过 APPDATA 定位 .minecraft
            //   必须把 APPDATA 指向 .minecraft 的上一级目录
            psi.EnvironmentVariables["APPDATA"] = gameParentDir;

            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log("[OptiFine] " + e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log("[OptiFine ERR] " + e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(INSTALLER_TIMEOUT_MS);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    process.WaitForExit(5000);
                    throw new Exception("OptiFine 安装器超时");
                }

                // 确保异步输出全部读完
                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new Exception($"OptiFine 安装器失败，退出码 {process.ExitCode}");
            }
        }

        // ===================== 确保 launcher_profiles.json 存在且无 BOM =====================
        /// <summary>
        /// OptiFine 安装器在更新启动器配置（updateLauncherJson）时，
        /// 需要 .minecraft\launcher_profiles.json 存在，并且必须是合法的 JSON（不能带 BOM）。
        /// 这里总是写入一个干净的、不带 BOM 的 launcher_profiles.json。
        /// </summary>
        private static void EnsureLauncherProfiles(string minecraftDir)
        {
            string path = Path.Combine(minecraftDir, "launcher_profiles.json");

            var root = new Dictionary<string, object>
            {
                { "profiles", new Dictionary<string, object>() },
                { "settings", new Dictionary<string, object>() },
                { "version", 3 }
            };

            string json = new JavaScriptSerializer().Serialize(root);

            // ★ 使用不带 BOM 的 UTF-8 编码
            var utf8NoBom = new UTF8Encoding(false);
            File.WriteAllText(path, json, utf8NoBom);

            Log($"[OptiFine] 已写入 launcher_profiles.json（无 BOM）: {path}");
        }

        // ===================== 合并 JSON =====================
        private static void MergeParentJson(string optifineJsonPath, string minecraftDir,
                                             string mcVersion, bool forForge)
        {
            var optifineRoot = ParseJson(File.ReadAllText(optifineJsonPath));
            if (optifineRoot == null)
                throw new Exception("OptiFine 版本 JSON 解析失败");

            if (!optifineRoot.ContainsKey("inheritsFrom"))
            {
                Log("[OptiFine] 无 inheritsFrom，无需合并");
                return;
            }

            string parentId = optifineRoot["inheritsFrom"].ToString();
            string parentPath = Path.Combine(
                Path.Combine(Path.Combine(minecraftDir, "versions"), parentId),
                parentId + ".json");

            if (!File.Exists(parentPath))
                throw new Exception($"找不到父版本 JSON: {parentPath}");

            var parentRoot = ParseJson(File.ReadAllText(parentPath));
            if (parentRoot == null)
                throw new Exception("父版本 JSON 解析失败");

            Log($"[OptiFine] 合并父版本 {parentId}" +
                (forForge ? "（Forge 模式，tweakClass 将替换）" : ""));

            var merged = new Dictionary<string, object>();

            foreach (var kv in parentRoot)
                merged[kv.Key] = kv.Value;

            foreach (var kv in optifineRoot)
            {
                if (kv.Key == "inheritsFrom") continue;
                if (kv.Key == "libraries") continue;
                if (kv.Key == "arguments") continue;
                merged[kv.Key] = kv.Value;
            }

            // ---- 合并 libraries ----
            var mergedLibs = new List<object>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (parentRoot.ContainsKey("libraries"))
            {
                var parentLibs = parentRoot["libraries"] as ArrayList;
                if (parentLibs != null)
                {
                    foreach (var lib in parentLibs)
                    {
                        var d = lib as Dictionary<string, object>;
                        string n = d != null && d.ContainsKey("name") ? d["name"].ToString() : null;
                        if (n != null && seenNames.Add(n))
                            mergedLibs.Add(lib);
                    }
                }
            }

            if (optifineRoot.ContainsKey("libraries"))
            {
                var ofLibs = optifineRoot["libraries"] as ArrayList;
                if (ofLibs != null)
                {
                    foreach (var lib in ofLibs)
                    {
                        var d = lib as Dictionary<string, object>;
                        string n = d != null && d.ContainsKey("name") ? d["name"].ToString() : null;
                        if (n != null && seenNames.Add(n))
                            mergedLibs.Add(lib);
                    }
                }
            }

            merged["libraries"] = mergedLibs;

            // ---- 合并 arguments ----
            var parentArgs = parentRoot.ContainsKey("arguments")
                ? parentRoot["arguments"] as Dictionary<string, object> : null;
            var ofArgs = optifineRoot.ContainsKey("arguments")
                ? optifineRoot["arguments"] as Dictionary<string, object> : null;

            if (parentArgs != null || ofArgs != null)
            {
                var mergedArgs = new Dictionary<string, object>();

                var gameList = new List<object>();
                if (parentArgs != null && parentArgs.ContainsKey("game"))
                {
                    var l = parentArgs["game"] as ArrayList;
                    if (l != null)
                        foreach (var item in l) gameList.Add(item);
                }

                if (ofArgs != null && ofArgs.ContainsKey("game"))
                {
                    var l = ofArgs["game"] as ArrayList;
                    if (l != null)
                    {
                        foreach (var item in l)
                        {
                            string s = item as string;
                            if (s != null)
                            {
                                if (forForge && s == "optifine.OptiFineTweaker")
                                {
                                    gameList.Add("optifine.OptiFineForgeTweaker");
                                    Log("[OptiFine] tweakClass: optifine.OptiFineTweaker → optifine.OptiFineForgeTweaker");
                                }
                                else gameList.Add(s);
                            }
                            else gameList.Add(item);
                        }
                    }
                }

                if (gameList.Count > 0) mergedArgs["game"] = gameList;

                var jvmList = new List<object>();
                if (parentArgs != null && parentArgs.ContainsKey("jvm"))
                {
                    var l = parentArgs["jvm"] as ArrayList;
                    if (l != null)
                        foreach (var item in l) jvmList.Add(item);
                }
                if (ofArgs != null && ofArgs.ContainsKey("jvm"))
                {
                    var l = ofArgs["jvm"] as ArrayList;
                    if (l != null)
                        foreach (var item in l) jvmList.Add(item);
                }
                if (jvmList.Count > 0) mergedArgs["jvm"] = jvmList;

                merged["arguments"] = mergedArgs;
            }

            File.WriteAllText(optifineJsonPath,
                new JavaScriptSerializer().Serialize(merged), Encoding.UTF8);
            Log($"[OptiFine] 合并完成: {optifineJsonPath}");
        }

        // ===================== Java 查找 =====================
        private static string FindJavaForOptiFine(string mcVersion)
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

        // ===================== 下载 =====================
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
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (i < RETRY_COUNT - 1) Thread.Sleep(500 * (i + 1));
                }
            }
            throw new Exception($"下载 {url} 失败: {lastEx?.Message}", lastEx);
        }

        /// <summary>
        /// 单文件下载（带单行进度）。失败时抛异常，由外层重试。
        /// </summary>
        private static void DownloadFile(string url, string dest)
        {
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA_STRING;
            req.Timeout = HTTP_TIMEOUT_MS;
            req.ReadWriteTimeout = READ_WRITE_TIMEOUT_MS;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                // 拒绝 HTML 错误页
                string contentType = resp.ContentType ?? "";
                if (contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    resp.Close();
                    throw new Exception($"服务器返回 {contentType}（可能是错误页）");
                }

                using (var netStream = resp.GetResponseStream())
                using (var fs = File.Create(dest))
                {
                    long total = resp.ContentLength;
                    long received = 0;
                    byte[] buffer = new byte[256 * 1024];
                    int read;

                    var sw = Stopwatch.StartNew();
                    long lastReportMs = -1;
                    string fileName = Path.GetFileName(dest);

                    while ((read = netStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fs.Write(buffer, 0, read);
                        received += read;

                        long nowMs = sw.ElapsedMilliseconds;
                        if (lastReportMs < 0 || nowMs - lastReportMs >= 200
                            || (total > 0 && received >= total))
                        {
                            lastReportMs = nowMs;

                            double secs = sw.Elapsed.TotalSeconds;
                            double speed = secs > 0.1 ? received / secs : 0;
                            int pct = total > 0 ? (int)(received * 100 / total) : 0;
                            string sizeText = total > 0
                                ? $"{FormatSize(received)}/{FormatSize(total)}"
                                : FormatSize(received);

                            lock (_consoleLock)
                            {
                                Console.Write($"\r[下载中] {fileName} - {pct}% " +
                                              $"({sizeText}, {FormatSize((long)speed)}/s)   ");
                                try { Console.Out.Flush(); } catch { }
                            }
                        }
                    }

                    lock (_consoleLock)
                    {
                        Console.Write("\r" + new string(' ', 160) + "\r");
                        try { Console.Out.Flush(); } catch { }
                    }

                    if (total > 0 && received != total)
                        throw new Exception($"下载不完整: {received}/{total}");
                }
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

        private static void ValidateJarFile(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                if (fs.Length < 4)
                    throw new Exception("文件太小");

                byte[] header = new byte[4];
                int n = fs.Read(header, 0, 4);
                if (n < 4 || header[0] != 0x50 || header[1] != 0x4B)
                    throw new Exception("不是有效的 zip/jar 文件");
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }

        private static void Log(string msg)
        {
            lock (_consoleLock)
            {
                Console.Write("\r" + new string(' ', 160) + "\r");
                Console.WriteLine(msg);
            }
        }
    }
}