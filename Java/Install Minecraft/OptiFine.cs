using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    internal class InstallOptiFine
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
        private const int BUFFER_SIZE = 256 * 1024;

        private const string UA_STRING =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly object _consoleLock = new object();

        // ===================== 统一异常日志 =====================
        private static void LogEx(string context, Exception ex, LogLevel level = LogLevel.Error)
        {
            recordLog(level, context + " -> " + (ex == null ? "null" : ex.ToString()));
        }

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

            recordLog(LogLevel.Info, $"[OptiFine] 开始安装客户端 OptiFine {version}" +
                (forForge ? "（Forge 模式）" : "（独立模式）"));

            var versions = GetOptiFineVersions(version);
            if (versions.Count == 0)
                throw new Exception($"未找到适用于 Minecraft {version} 的 OptiFine 版本");

            OptiFineVersion target = PickVersion(versions, loaderParam);
            recordLog(LogLevel.Info, $"[OptiFine] 使用版本: {target.FileName}" +
                (string.IsNullOrEmpty(target.Forge) ? "" : $"（配套 {target.Forge}）"));

            string installerPath = DownloadInstaller(target);
            recordLog(LogLevel.Info, $"[OptiFine] 安装器已下载: {installerPath}");

            // ============================================================
            // Forge 模式：不运行安装器，直接把 OptiFine jar 复制到 mods 文件夹
            // ============================================================
            if (forForge)
            {
                InstallToModsFolder(installerPath, minecraftDir);
                recordLog(LogLevel.Info, $"[OptiFine] 客户端 OptiFine {version} 安装完成（Forge 模式）");
                return;
            }

            // ============================================================
            // 独立模式：运行安装器，然后枚举 versions 目录找到实际生成的版本
            // ============================================================
            CheckVanillaInstalled(version, minecraftDir);

            RunInstaller(installerPath, minecraftDir, version);
            recordLog(LogLevel.Info, $"[OptiFine] 安装器执行完毕");

            // ★ 关键修复：不再用 {version}-OptiFine_{type}_{patch} 拼字符串，
            //   改为枚举 versions 目录找实际生成的那个
            string optifineVersionId = FindOptiFineVersionId(minecraftDir, version);
            if (string.IsNullOrEmpty(optifineVersionId))
            {
                throw new Exception(
                    $"[OptiFine] 安装器未生成版本目录（期望 {version}-OptiFine_*）。\n" +
                    "可能原因：\n" +
                    "  1) 原版 Minecraft 未安装/未运行过\n" +
                    "  2) Java 版本不兼容\n" +
                    "  3) OptiFine 安装器异常退出（请查看上方日志）");
            }

            recordLog(LogLevel.Info, $"[OptiFine] 实际生成版本目录: {optifineVersionId}");

            string versionDir = Path.Combine(minecraftDir, "versions", optifineVersionId);
            string versionJsonPath = Path.Combine(versionDir, optifineVersionId + ".json");
            if (!File.Exists(versionJsonPath))
                throw new Exception($"[OptiFine] 版本 JSON 缺失: {versionJsonPath}");

            MergeParentJson(versionJsonPath, minecraftDir, version, optifineVersionId, forForge);

            recordLog(LogLevel.Info, $"[OptiFine] 客户端 OptiFine {version} 安装完成");
        }

        // ===================== 服务端：不提供 =====================
        public static void InstallServer(string version, string serverDir, string loaderParam)
        {
            throw new NotSupportedException(
                "OptiFine 是纯客户端模组，没有服务端安装。\n" +
                "请只在客户端安装 OptiFine，服务端保持原版即可。");
        }

        // ===================== ★ 枚举 versions 目录找实际生成的版本 =====================
        /// <summary>
        /// OptiFine installer 生成的目录名历史上格式不稳（大小写 / 是否带 _preview 后缀），
        /// 不再拼字符串，改为枚举 versions/ 下所有 `{mcVersion}-OptiFine_*` 目录，
        /// 选 JSON 创建时间最新的那个（installer 刚生成的就是最新的）。
        /// </summary>
        private static string FindOptiFineVersionId(string minecraftDir, string mcVersion)
        {
            string versionsDir = Path.Combine(minecraftDir, "versions");
            if (!Directory.Exists(versionsDir)) return null;

            string prefix = mcVersion + "-OptiFine_";
            var matches = new List<KeyValuePair<DateTime, string>>();

            try
            {
                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    string id = Path.GetFileName(dir);
                    if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string json = Path.Combine(dir, id + ".json");
                    if (!File.Exists(json)) continue;

                    DateTime time;
                    try { time = File.GetCreationTimeUtc(json); }
                    catch (Exception ex)
                    {
                        LogEx($"[OptiFine] 读取版本 JSON 创建时间失败: {json}", ex, LogLevel.Warn);
                        time = DateTime.MinValue;
                    }

                    matches.Add(new KeyValuePair<DateTime, string>(time, id));
                }
            }
            catch (Exception ex)
            {
                LogEx($"[OptiFine] 枚举 versions 目录失败: {versionsDir}", ex);
                return null;
            }

            if (matches.Count == 0) return null;

            // 最新创建的在前
            matches.Sort((a, b) => b.Key.CompareTo(a.Key));
            return matches[0].Value;
        }

        // ===================== Forge 模式：复制到 mods 文件夹 =====================
        private static void InstallToModsFolder(string optifineJarPath, string minecraftDir)
        {
            string modsDir = Path.Combine(minecraftDir, "mods");
            if (!Directory.Exists(modsDir))
            {
                try { Directory.CreateDirectory(modsDir); }
                catch (Exception ex) { throw new Exception($"[OptiFine] 创建 mods 目录失败: {modsDir}", ex); }

                recordLog(LogLevel.Info, $"[OptiFine] 已创建 mods 文件夹: {modsDir}");
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
                        recordLog(LogLevel.Info, $"[OptiFine] 已删除 mods 中旧版本: {Path.GetFileName(old)}");
                    }
                    catch (Exception ex)
                    {
                        // 删除旧文件失败不致命，但不吞
                        LogEx($"[OptiFine] 删除 mods 旧文件失败（忽略）: {old}", ex, LogLevel.Warn);
                    }
                }
            }
            catch (Exception ex)
            {
                // Directory.GetFiles 本身失败（无权限 / IO）
                LogEx($"[OptiFine] 枚举 mods 目录失败: {modsDir}", ex, LogLevel.Warn);
            }

            // ---- 目标已存在且大小一致：跳过 ----
            if (File.Exists(destPath))
            {
                bool same = false;
                try
                {
                    ValidateJarFile(destPath);
                    long srcLen = new FileInfo(optifineJarPath).Length;
                    long dstLen = new FileInfo(destPath).Length;
                    same = (srcLen == dstLen);
                }
                catch (Exception ex)
                {
                    // 已有 mods 文件损坏，准备删除后重拷
                    LogEx($"[OptiFine] 已有 mods 文件校验失败: {destPath}", ex, LogLevel.Warn);
                }

                if (same)
                {
                    recordLog(LogLevel.Info, $"[OptiFine] mods 中已存在相同文件，跳过: {fileName}");
                    return;
                }

                try { File.Delete(destPath); }
                catch (Exception ex) { LogEx($"[OptiFine] 删除已有 mods 文件失败: {destPath}", ex, LogLevel.Warn); }
            }

            // ---- 复制（失败必须 throw，否则 Forge 模式安装等于没装）----
            try
            {
                File.Copy(optifineJarPath, destPath, true);
                recordLog(LogLevel.Info, $"[OptiFine] 已复制到 mods: {destPath}");
            }
            catch (Exception ex)
            {
                throw new Exception($"[OptiFine] 复制到 mods 失败: {optifineJarPath} -> {destPath}", ex);
            }
        }

        // ===================== 版本列表获取 =====================
        private static List<OptiFineVersion> GetOptiFineVersions(string mcVersion)
        {
            string url = $"{BMCL_BASE}/optifine/{Uri.EscapeDataString(mcVersion)}";
            recordLog(LogLevel.Info, $"[OptiFine] 获取版本列表: {url}");

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
                throw new Exception($"解析 OptiFine 版本列表失败: {url}", ex);
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
                recordLog(LogLevel.Info, $"[OptiFine] 未找到指定版本 {param}，改用最新版");
            }

            return versions[0];
        }

        // ===================== 下载安装器（双源 × 多轮） =====================
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
                catch (Exception ex)
                {
                    LogEx($"[OptiFine] 已有安装器校验失败，删除重下: {installerPath}", ex, LogLevel.Warn);
                    try { File.Delete(installerPath); }
                    catch (Exception delEx) { LogEx($"[OptiFine] 删除损坏安装器失败: {installerPath}", delEx, LogLevel.Warn); }
                }

                if (File.Exists(installerPath))
                {
                    recordLog(LogLevel.Info, $"[OptiFine] 安装器已存在: {installerPath}");
                    return installerPath;
                }
            }

            // ---- BMCLAPI 专用下载端点 ----
            string bmclUrl = $"{BMCL_BASE}/optifine/{Uri.EscapeDataString(target.McVersion)}/" +
                             $"{Uri.EscapeDataString(target.Type)}/{Uri.EscapeDataString(target.Patch)}";

            recordLog(LogLevel.Info, $"[OptiFine] 下载安装器（最多 {INSTALLER_ROUNDS} 轮，每轮 2 源）: {fileName}");

            string lastError = null;

            for (int round = 1; round <= INSTALLER_ROUNDS; round++)
            {
                if (round > 1)
                    recordLog(LogLevel.Info, $"[OptiFine] 第 {round}/{INSTALLER_ROUNDS} 轮尝试...");

                // ============================================================
                // 源 1：BMCLAPI
                // ============================================================
                try
                {
                    recordLog(LogLevel.Info, $"[OptiFine] 尝试源: {bmclUrl}");
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); }
                    catch (Exception delEx) { LogEx($"[OptiFine] 清理旧安装器失败: {installerPath}", delEx, LogLevel.Warn); }

                    DownloadFile(bmclUrl, installerPath);
                    ValidateJarFile(installerPath);

                    recordLog(LogLevel.Info, $"[OptiFine] 安装器已下载: {installerPath}");
                    return installerPath;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    LogEx($"[OptiFine] 第 {round} 轮 BMCLAPI 失败", ex, LogLevel.Warn);
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); }
                    catch (Exception delEx) { LogEx($"[OptiFine] 清理失败安装器失败: {installerPath}", delEx, LogLevel.Warn); }
                }

                // ============================================================
                // 源 2：OptiFine 官方（adloadx → 解析 → downloadx）
                // ============================================================
                try
                {
                    recordLog(LogLevel.Info, $"[OptiFine] 尝试源: OptiFine 官方");
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); }
                    catch (Exception delEx) { LogEx($"[OptiFine] 清理旧安装器失败: {installerPath}", delEx, LogLevel.Warn); }

                    DownloadInstallerFromOfficial(fileName, installerPath);
                    ValidateJarFile(installerPath);

                    recordLog(LogLevel.Info, $"[OptiFine] 安装器已下载: {installerPath}");
                    return installerPath;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    LogEx($"[OptiFine] 第 {round} 轮官方源失败", ex, LogLevel.Warn);
                    try { if (File.Exists(installerPath)) File.Delete(installerPath); }
                    catch (Exception delEx) { LogEx($"[OptiFine] 清理失败安装器失败: {installerPath}", delEx, LogLevel.Warn); }
                }

                if (round < INSTALLER_ROUNDS)
                {
                    int waitSec = round * 3;
                    recordLog(LogLevel.Info, $"[OptiFine] 本轮全部源失败，{waitSec} 秒后重试...");
                    Thread.Sleep(waitSec * 1000);
                }
            }

            throw new Exception(
                $"下载 OptiFine 安装器失败（{INSTALLER_ROUNDS} 轮全部失败）: {lastError}");
        }

        // ===================== 官方源下载（解析 adloadx → downloadx） =====================
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

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    html = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"[OptiFine] 获取 adloadx 页面失败: {adloadxUrl}", ex);
            }

            if (string.IsNullOrEmpty(html))
                throw new Exception("[OptiFine] adloadx 页面为空");

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
                throw new Exception("[OptiFine] 无法从 adloadx 页面解析出 downloadx 链接（官网可能改版）");

            // 归一化成完整 URL
            if (downloadPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // 已是完整 URL
            }
            else if (downloadPath.StartsWith("/"))
            {
                downloadPath = OPTIFINE_OFFICIAL + downloadPath;
            }
            else
            {
                downloadPath = OPTIFINE_OFFICIAL + "/" + downloadPath;
            }

            recordLog(LogLevel.Info, $"[OptiFine] 官方下载链接: {downloadPath}");

            // ---- 3. 下载真实文件 ----
            DownloadFile(downloadPath, dest);
        }

        // ===================== ★ 安装前检查 =====================
        /// <summary>
        /// 跑 OptiFine installer 前检查原版是否已安装。
        /// installer 缺原版时会输出 "please run 1.x.x first" 然后退出，
        /// 但退出码是 0，如果不预检查就会表现为"安装器跑完了但 versions 目录没新东西"。
        /// </summary>
        private static void CheckVanillaInstalled(string mcVersion, string minecraftDir)
        {
            string versionDir = Path.Combine(minecraftDir, "versions", mcVersion);
            string jsonPath = Path.Combine(versionDir, mcVersion + ".json");
            string jarPath = Path.Combine(versionDir, mcVersion + ".jar");

            if (!File.Exists(jsonPath))
                throw new Exception(
                    $"[OptiFine] 安装 OptiFine 前需先完成原版 {mcVersion} 安装（缺少 {jsonPath}）");
            if (!File.Exists(jarPath) || new FileInfo(jarPath).Length == 0)
                throw new Exception(
                    $"[OptiFine] 安装 OptiFine 前需先完成原版 {mcVersion} 安装（缺少 {jarPath}）");
        }

        // ===================== ★ 调用 OptiFine Installer =====================
        private static void RunInstaller(string installerPath, string minecraftDir, string mcVersion)
        {
            string javaPath = FindJavaForOptiFine(mcVersion);
            recordLog(LogLevel.Info, $"[OptiFine] Java: {javaPath}");

            // ★ Java 版本预检
            int javaMajor = GetJavaMajorVersion(javaPath);
            if (javaMajor > 0)
            {
                bool needsModernJava = IsMcVersionOrNewer(mcVersion, "1.17");
                if (needsModernJava && javaMajor < 17)
                    throw new Exception(
                        $"[OptiFine] Minecraft {mcVersion} 的 OptiFine 安装器需要 Java 17+（当前 {javaMajor}）\n" +
                        $"Java: {javaPath}");
                if (!needsModernJava && javaMajor < 8)
                    throw new Exception(
                        $"[OptiFine] Minecraft {mcVersion} 的 OptiFine 安装器需要 Java 8+（当前 {javaMajor}）\n" +
                        $"Java: {javaPath}");
                recordLog(LogLevel.Info, $"[OptiFine] Java 版本: {javaMajor}（OK）");
            }
            else
            {
                recordLog(LogLevel.Warn, $"[OptiFine] 无法探测 Java 版本，继续尝试");
            }

            // 确保 launcher_profiles.json 存在（OptiFine installer 依赖它定位 .minecraft）
            try
            {
                EnsureLauncherProfiles(minecraftDir);
            }
            catch (Exception ex)
            {
                // 不致命，但 installer 可能因此找不到 .minecraft
                LogEx($"[OptiFine] 创建 launcher_profiles.json 失败(不致命): {minecraftDir}", ex, LogLevel.Warn);
            }

            // .minecraft 的上一级目录
            string gameParentDir = Path.GetDirectoryName(minecraftDir);
            if (string.IsNullOrEmpty(gameParentDir))
                gameParentDir = minecraftDir;

            string args =
                $"-Duser.home=\"{gameParentDir}\" " +
                $"-Djava.awt.headless=true " +
                $"-cp \"{installerPath}\" optifine.Installer";

            recordLog(LogLevel.Info, $"[OptiFine] 执行安装器...");
            recordLog(LogLevel.Info, $"[OptiFine] WorkingDir: {gameParentDir}");
            recordLog(LogLevel.Info, $"[OptiFine] APPDATA   : {gameParentDir}");

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

            // OptiFine 安装器在 Windows 上通过 APPDATA 定位 .minecraft
            psi.EnvironmentVariables["APPDATA"] = gameParentDir;

            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        recordLog(LogLevel.Info, $"[OptiFine] " + e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        recordLog(LogLevel.Error, $"[OptiFine ERR] " + e.Data);
                };

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    throw new Exception($"[OptiFine] 启动 installer java 进程失败: {javaPath}", ex);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(INSTALLER_TIMEOUT_MS);
                if (!exited)
                {
                    try { process.Kill(); }
                    catch (Exception ex) { LogEx($"[OptiFine] Kill 超时 installer 进程失败", ex, LogLevel.Warn); }
                    process.WaitForExit(5000);
                    throw new Exception("OptiFine 安装器超时");
                }

                // 确保异步输出全部读完
                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new Exception($"OptiFine 安装器失败，退出码 {process.ExitCode}");
            }
        }

        // ===================== ★ 合并 JSON（强制 id/jar = 实际目录名） =====================
        private static void MergeParentJson(string optifineJsonPath, string minecraftDir,
                                             string mcVersion, string optifineVersionId,
                                             bool forForge)
        {
            var optifineRoot = ParseJson(File.ReadAllText(optifineJsonPath));
            if (optifineRoot == null)
                throw new Exception($"[OptiFine] 版本 JSON 解析失败: {optifineJsonPath}");

            // ★ 无论是否 has inheritsFrom，都强制锁定 id / jar = 实际目录名
            optifineRoot["id"] = optifineVersionId;
            optifineRoot["jar"] = optifineVersionId;

            if (!optifineRoot.ContainsKey("inheritsFrom"))
            {
                recordLog(LogLevel.Info, $"[OptiFine] 无 inheritsFrom，仅改写 id/jar={optifineVersionId}");
                try
                {
                    File.WriteAllText(optifineJsonPath,
                        new JavaScriptSerializer().Serialize(optifineRoot),
                        new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    throw new Exception($"[OptiFine] 写回版本 JSON 失败: {optifineJsonPath}", ex);
                }
                return;
            }

            string parentId = optifineRoot["inheritsFrom"].ToString();
            string parentPath = Path.Combine(
                Path.Combine(Path.Combine(minecraftDir, "versions"), parentId),
                parentId + ".json");

            if (!File.Exists(parentPath))
                throw new Exception($"[OptiFine] 找不到父版本 JSON: {parentPath}");

            var parentRoot = ParseJson(File.ReadAllText(parentPath));
            if (parentRoot == null)
                throw new Exception($"[OptiFine] 父版本 JSON 解析失败: {parentPath}");

            recordLog(LogLevel.Info, $"[OptiFine] 合并父版本 {parentId}" +
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

            // ★ 强制锁定 id / jar（防止父版本 JSON 带 jar 字段导致冲突）
            merged["id"] = optifineVersionId;
            merged["jar"] = optifineVersionId;

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
                                    recordLog(LogLevel.Info, $"[OptiFine] tweakClass: optifine.OptiFineTweaker → optifine.OptiFineForgeTweaker");
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

            try
            {
                File.WriteAllText(optifineJsonPath,
                    new JavaScriptSerializer().Serialize(merged),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                throw new Exception($"[OptiFine] 写回合并结果失败: {optifineJsonPath}", ex);
            }
            recordLog(LogLevel.Info, $"[OptiFine] 合并完成: {optifineJsonPath}");
        }

        // ===================== Java 查找 + 版本探测 =====================
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
                    LogEx($"[OptiFine] 枚举 Java 目录失败: {root}", ex, LogLevel.Warn);
                }
            }

            return "java";
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
            catch (Exception ex)
            {
                LogEx($"[OptiFine] 探测 java 版本失败: {javaExePath}", ex, LogLevel.Warn);
            }
            return 0;
        }

        private static bool IsMcVersionOrNewer(string mcVersion, string baseline)
        {
            try
            {
                return ParseMcVersion(mcVersion) >= ParseMcVersion(baseline);
            }
            catch (Exception ex)
            {
                LogEx($"[OptiFine] 比较 MC 版本失败: {mcVersion} vs {baseline}", ex, LogLevel.Warn);
                return false;
            }
        }

        private static Version ParseMcVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return new Version(0, 0);
            var parts = v.Split('.');
            if (parts.Length == 1) v = v + ".0.0";
            else if (parts.Length == 2) v = v + ".0";
            return new Version(v);
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
                    LogEx($"[OptiFine] 请求失败({url})（第 {i + 1}/{RETRY_COUNT} 次）", ex, LogLevel.Warn);
                    if (i < RETRY_COUNT - 1) Thread.Sleep(500 * (i + 1));
                }
            }
            throw new Exception($"下载 {url} 失败: {lastEx?.Message}", lastEx);
        }

        /// <summary>
        /// 单文件下载 + .tmp 原子替换 + 失败后 jar 校验。
        /// 若目标已存在且通过 ValidateJarFile，则直接返回（缓存命中）。
        /// </summary>
        private static void DownloadFile(string url, string dest)
        {
            // 缓存命中检查
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            {
                try
                {
                    ValidateJarFile(dest);
                    return;
                }
                catch (Exception ex)
                {
                    LogEx($"[OptiFine] 已有文件校验失败，删除重下: {dest}", ex, LogLevel.Warn);
                    try { File.Delete(dest); }
                    catch (Exception delEx) { LogEx($"[OptiFine] 删除损坏文件失败: {dest}", delEx, LogLevel.Warn); }
                }
            }

            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { throw new Exception($"[OptiFine] 创建目录失败: {dir}", ex); }
            }

            string tmpPath = dest + ".tmp";
            Exception lastEx = null;

            for (int retry = 0; retry < RETRY_COUNT; retry++)
            {
                try
                {
                    SingleDownloadStream(url, tmpPath);

                    try { ValidateJarFile(tmpPath); }
                    catch (Exception ex)
                    {
                        try { File.Delete(tmpPath); }
                        catch (Exception delEx) { LogEx($"[OptiFine] 删除损坏 .tmp 失败: {tmpPath}", delEx, LogLevel.Warn); }
                        throw new Exception($"[OptiFine] jar 校验失败: {tmpPath}", ex);
                    }

                    MoveOrReplace(tmpPath, dest);
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                    catch (Exception delEx) { LogEx($"[OptiFine] 清理 .tmp 失败: {tmpPath}", delEx, LogLevel.Warn); }
                    LogEx($"[OptiFine] 下载 {Path.GetFileName(dest)} 失败（第 {retry + 1}/{RETRY_COUNT} 次）", ex, LogLevel.Warn);
                    if (retry < RETRY_COUNT - 1) Thread.Sleep(500 * (retry + 1));
                }
            }

            throw new Exception($"下载 {dest} 失败: {lastEx?.Message}", lastEx);
        }

        /// <summary>
        /// 单流下载（带进度条）。
        /// </summary>
        private static void SingleDownloadStream(string url, string dest)
        {
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
                    throw new Exception($"服务器返回 {contentType}（可能是错误页）: {url}");
                }

                using (var netStream = resp.GetResponseStream())
                using (var fs = File.Create(dest))
                {
                    long total = resp.ContentLength;
                    long received = 0;
                    byte[] buffer = new byte[BUFFER_SIZE];
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
                                try { Console.Out.Flush(); }
                                catch (Exception flushEx)
                                {
                                    System.Diagnostics.Debug.WriteLine("[OptiFine] Console.Flush 失败: " + flushEx.Message);
                                }
                            }
                        }
                    }

                    lock (_consoleLock)
                    {
                        Console.Write("\r" + new string(' ', 160) + "\r");
                        try { Console.Out.Flush(); }
                        catch (Exception flushEx)
                        {
                            System.Diagnostics.Debug.WriteLine("[OptiFine] Console.Flush 失败: " + flushEx.Message);
                        }
                    }

                    if (total > 0 && received != total)
                        throw new Exception($"下载不完整: {received}/{total}");
                }
            }
        }

        /// <summary>
        /// 原子替换：优先 File.Replace（原子、保留原文件直到成功），
        /// 失败回退 Delete + Move，并带杀软占用 3 次退避。
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
                    LogEx($"[OptiFine] File.Replace 平台不支持，回退 Delete+Move: {dst}", ex, LogLevel.Warn);
                }
                catch (IOException ex)
                {
                    LogEx($"[OptiFine] File.Replace 失败（可能被占用），回退 Delete+Move: {dst}", ex, LogLevel.Warn);
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { File.Delete(dst); break; }
                    catch (IOException ex)
                    {
                        if (attempt == 2)
                            throw new Exception($"[OptiFine] 删除目标文件失败（可能被占用）: {dst}", ex);
                        LogEx($"[OptiFine] 删除目标文件失败，重试 {attempt + 1}: {dst}", ex, LogLevel.Warn);
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
                        throw new Exception($"[OptiFine] 移动临时文件失败（可能被占用）: {src} -> {dst}", ex);
                    LogEx($"[OptiFine] File.Move 失败，重试 {attempt + 1}: {dst}", ex, LogLevel.Warn);
                    Thread.Sleep(200 * (attempt + 1));
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
            catch (Exception ex)
            {
                LogEx("[OptiFine] JSON 解析失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 完整 jar / zip 校验：magic + 遍历 ZipArchive 条目。
        /// 原版只查 magic，半截 jar（前 4 字节 PK）会通过，installer 一跑就炸。
        /// </summary>
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

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }
    }
}