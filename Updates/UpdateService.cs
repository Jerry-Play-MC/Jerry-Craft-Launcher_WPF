using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Updater
{
    #region 数据模型

    public class UpdateFileInfo
    {
        /// <summary>
        /// 安装包完整 URL。可以是 raw.githubusercontent.com，
        /// 也可以是 github.com/.../releases/download/...。
        /// 启动器会在运行时自动转成各镜像地址。
        /// </summary>
        public string url { get; set; }
        public string sha256 { get; set; }
        public long size { get; set; }
    }

    public class UpdateInfo
    {
        public string version { get; set; }
        public string notes { get; set; }
        public bool force { get; set; }
        public Dictionary<string, UpdateFileInfo> files { get; set; }
    }

    public class UpdateCheckResult
    {
        public bool Success { get; set; }
        public bool HasUpdate { get; set; }
        public string Message { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public UpdateInfo Info { get; set; }
        public string SourceUrl { get; set; }
    }

    public enum UpdateSource
    {
        Repository,   // 仓库内文件（测试阶段用）
        Release       // Release 资产（正式发布用）
    }

    #endregion

    public static class UpdateService
    {
        // ============================================================
        //  发布配置
        // ============================================================

        public const string Owner = "Jerry-Play-MC";
        public const string Repo = "Jerry-Craft-Launcher_WPF";
        public const string Branch = "main";

        /// <summary>
        /// 测试阶段用 Repository，正式发布切 Release。
        /// 只影响 latest.json 从哪读，不影响 JSON 里的 url 字段。
        /// </summary>
        public const UpdateSource JsonSource = UpdateSource.Repository;

        /// <summary>Repository 模式下的 JSON 路径</summary>
        public const string JsonPath = "Updates/latest.json";

        /// <summary>Release 模式下的 JSON 资产名</summary>
        public const string JsonFileName = "latest.json";

        public const string Platform = "windows";
        public const string Token = null;

        // ============================================================
        //  镜像配置
        // ============================================================

        public const string DirectRawBase = "https://raw.githubusercontent.com";
        public const string DirectGitBase = "https://github.com";

        private static readonly MirrorEntry[] Mirrors = new[]
        {
            // 直接源（原 URL 原样用）
            new MirrorEntry { Base = DirectRawBase, Kind = MirrorKind.Direct },
            new MirrorEntry { Base = DirectGitBase, Kind = MirrorKind.Direct },

            // 换域名（只对 raw 生效）
            new MirrorEntry { Base = "https://raw.gitmirror.com", Kind = MirrorKind.RawDomainReplace },

            // jsDelivr（只对 raw 生效）
            new MirrorEntry { Base = "https://cdn.jsdelivr.net/gh", Kind = MirrorKind.JsDelivr },
            new MirrorEntry { Base = "https://gcore.jsdelivr.net/gh", Kind = MirrorKind.JsDelivr },

            // 前缀代理（raw 和 github.com 都能加前缀）
            new MirrorEntry { Base = "https://ghproxy.net", Kind = MirrorKind.PrefixProxy },
            new MirrorEntry { Base = "https://gh-proxy.com", Kind = MirrorKind.PrefixProxy },
            new MirrorEntry { Base = "https://ghfast.top", Kind = MirrorKind.PrefixProxy },
        };

        private enum MirrorKind
        {
            Direct,
            RawDomainReplace,
            JsDelivr,
            PrefixProxy
        }

        private class MirrorEntry
        {
            public string Base;
            public MirrorKind Kind;
        }

        // ============================================================

        public static string CurrentVersion = "1.1.0";

        public static string ExeName =
            Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);

        // ============================================================
        //  检查更新
        // ============================================================

        public static async Task<UpdateCheckResult> CheckAsync(
            CancellationToken ct = default(CancellationToken))
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                Success = false
            };

            string jsonFullUrl = BuildJsonFullUrl();
            var urls = BuildMirrorUrls(jsonFullUrl);

            Exception lastEx = null;

            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    string json = await DownloadStringAsync(url, ct).ConfigureAwait(false);
                    var info = new JavaScriptSerializer().Deserialize<UpdateInfo>(json);

                    if (info == null || string.IsNullOrEmpty(info.version))
                        throw new InvalidDataException("latest.json 内容无效");

                    result.Success = true;
                    result.Info = info;
                    result.LatestVersion = info.version;
                    result.SourceUrl = url;
                    result.HasUpdate = IsNewer(info.version, CurrentVersion);
                    result.Message = result.HasUpdate
                        ? "发现新版本 " + info.version
                        : "已是最新版本";
                    return result;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }

            result.Message = "检查更新失败："
                + (lastEx != null ? lastEx.Message : "未知错误");
            return result;
        }

        private static string BuildJsonFullUrl()
        {
            if (JsonSource == UpdateSource.Release)
            {
                return string.Format(
                    "{0}/{1}/{2}/releases/latest/download/{3}",
                    DirectGitBase, Owner, Repo, JsonFileName);
            }

            return string.Format(
                "{0}/{1}/{2}/{3}/{4}",
                DirectRawBase, Owner, Repo, Branch,
                (JsonPath ?? "").TrimStart('/'));
        }

        // ============================================================
        //  下载 + 校验
        // ============================================================

        public static async Task<string> DownloadAsync(
            UpdateInfo info, IProgress<double> progress,
            CancellationToken ct = default(CancellationToken))
        {
            if (info == null) throw new ArgumentNullException("info");
            if (info.files == null || !info.files.ContainsKey(Platform))
                throw new InvalidOperationException(
                    "更新信息里没有平台 " + Platform + " 的文件");

            var file = info.files[Platform];
            if (string.IsNullOrEmpty(file.url))
                throw new InvalidOperationException(
                    "更新信息里 " + Platform + " 的 url 字段为空");

            string tempDir = Path.Combine(Path.GetTempPath(),
                "JerryCraftLauncherUpdate");
            Directory.CreateDirectory(tempDir);

            string fileName = GetFileNameFromUrl(file.url);
            string destPath = Path.Combine(tempDir, fileName);

            // 从 JSON 里的完整 URL 派生镜像列表
            var urls = BuildMirrorUrls(file.url);

            Exception lastEx = null;

            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await DownloadFileAsync(url, destPath, progress, ct)
                        .ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(file.sha256))
                    {
                        string actual = ComputeSha256(destPath);
                        if (!string.Equals(actual, file.sha256,
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("SHA256 校验失败");
                    }

                    return destPath;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    try { if (File.Exists(destPath)) File.Delete(destPath); }
                    catch { }
                }
            }

            throw new Exception("所有镜像下载失败", lastEx);
        }

        private static string GetFileNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                string name = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return "update_" + Guid.NewGuid().ToString("N") + ".zip";
        }

        // ============================================================
        //  URL 镜像改写
        // ============================================================

        private static List<string> BuildMirrorUrls(string fullUrl)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(fullUrl)) return result;

            foreach (var m in Mirrors)
            {
                var url = RewriteForMirror(fullUrl, m);
                if (!string.IsNullOrEmpty(url) && !result.Contains(url))
                    result.Add(url);
            }
            return result;
        }

        private static string RewriteForMirror(string fullUrl, MirrorEntry mirror)
        {
            if (string.IsNullOrEmpty(fullUrl) || mirror == null) return null;

            bool isRaw = fullUrl.StartsWith(
                "https://raw.githubusercontent.com/",
                StringComparison.OrdinalIgnoreCase);

            bool isGit = fullUrl.StartsWith(
                "https://github.com/",
                StringComparison.OrdinalIgnoreCase);

            switch (mirror.Kind)
            {
                case MirrorKind.Direct:
                    if (mirror.Base.Equals(DirectRawBase,
                            StringComparison.OrdinalIgnoreCase) && isRaw)
                        return fullUrl;
                    if (mirror.Base.Equals(DirectGitBase,
                            StringComparison.OrdinalIgnoreCase) && isGit)
                        return fullUrl;
                    return null;

                case MirrorKind.RawDomainReplace:
                    if (!isRaw) return null;
                    return ReplaceHost(fullUrl, mirror.Base);

                case MirrorKind.JsDelivr:
                    if (!isRaw) return null;
                    return ConvertToJsDelivr(fullUrl, mirror.Base);

                case MirrorKind.PrefixProxy:
                    if (!isRaw && !isGit) return null;
                    return mirror.Base.TrimEnd('/') + "/" + fullUrl;
            }

            return null;
        }

        private static string ReplaceHost(string url, string newBase)
        {
            try
            {
                var uri = new Uri(url);
                var newUri = new Uri(newBase.TrimEnd('/'));
                string port = newUri.IsDefaultPort ? "" : ":" + newUri.Port;
                return newUri.Scheme + "://" + newUri.Host + port
                    + uri.PathAndQuery;
            }
            catch { return null; }
        }

        /// <summary>
        /// raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
        ///   → {jsdelivrBase}/{owner}/{repo}@{branch}/{path}
        /// </summary>
        private static string ConvertToJsDelivr(string rawUrl, string jsdelivrBase)
        {
            const string prefix = "https://raw.githubusercontent.com/";
            if (!rawUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            string rest = rawUrl.Substring(prefix.Length);
            var parts = rest.Split(new[] { '/' }, 4);
            if (parts.Length < 4) return null;

            return jsdelivrBase.TrimEnd('/') + "/"
                + parts[0] + "/" + parts[1] + "@" + parts[2] + "/" + parts[3];
        }

        // ============================================================
        //  网络与工具
        // ============================================================

        private static async Task<string> DownloadStringAsync(
            string url, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var handler = new HttpClientHandler())
            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "JerryCraftLauncher/1.0");

                if (!string.IsNullOrEmpty(Token))
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Authorization", "token " + Token);

                using (var resp = await client.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException("HTTP " + (int)resp.StatusCode);
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task DownloadFileAsync(
            string url, string destPath,
            IProgress<double> progress, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var handler = new HttpClientHandler())
            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "JerryCraftLauncher/1.0");

                if (!string.IsNullOrEmpty(Token))
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Authorization", "token " + Token);

                using (var resp = await client.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException("HTTP " + (int)resp.StatusCode);

                    long total = resp.Content.Headers.ContentLength ?? -1;
                    long received = 0;

                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(destPath, FileMode.Create,
                        FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        int read;
                        while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct)
                            .ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                            received += read;
                            if (progress != null && total > 0)
                                progress.Report((double)received / total);
                        }
                    }
                }
            }
        }

        public static bool IsNewer(string latest, string current)
        {
            var v1 = ParseVersion(latest);
            var v2 = ParseVersion(current);
            int len = Math.Max(v1.Length, v2.Length);
            for (int i = 0; i < len; i++)
            {
                int a = i < v1.Length ? v1[i] : 0;
                int b = i < v2.Length ? v2[i] : 0;
                if (a > b) return true;
                if (a < b) return false;
            }
            return false;
        }

        private static int[] ParseVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return new int[0];
            v = v.TrimStart('v', 'V');
            var parts = v.Split(new[] { '.', '-', '_', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            var list = new List<int>();
            foreach (var p in parts)
            {
                int n;
                if (int.TryParse(p, out n)) list.Add(n);
                else break;
            }
            return list.ToArray();
        }

        private static string ComputeSha256(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(filePath))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ============================================================
        //  应用更新
        // ============================================================

        /// <summary>
        /// 应用更新。根据下载文件类型自动处理：
        ///   · .exe → 直接覆盖主程序
        ///   · .zip → 解压后覆盖目标目录
        /// </summary>
        public static void ApplyUpdateAndRestart(string downloadedFile, string targetDir)
        {
            if (string.IsNullOrEmpty(downloadedFile) || !File.Exists(downloadedFile))
                throw new FileNotFoundException("更新包不存在", downloadedFile);

            string ext = Path.GetExtension(downloadedFile).ToLowerInvariant();
            string batPath = Path.Combine(Path.GetTempPath(),
                "jerry_launcher_apply_update.bat");
            int pid = Process.GetCurrentProcess().Id;

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine(":wait");
            sb.AppendLine("tasklist /FI \"PID eq " + pid + "\" | find \"" + pid
                + "\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto wait");
            sb.AppendLine(")");

            if (ext == ".exe")
            {
                // 直接替换主程序 exe
                string targetExe = Path.Combine(targetDir, ExeName);
                sb.AppendLine("copy /Y \"" + downloadedFile + "\" \"" + targetExe
                    + "\" >nul");
                sb.AppendLine("start \"\" \"" + targetExe + "\"");
            }
            else if (ext == ".zip")
            {
                // 解压到 staging，再整体覆盖
                string stagingDir = Path.Combine(Path.GetTempPath(),
                    "JerryCraftLauncherUpdate", "staging");
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, true); } catch { }
                }
                Directory.CreateDirectory(stagingDir);
                ZipFile.ExtractToDirectory(downloadedFile, stagingDir);

                sb.AppendLine("xcopy /E /Y /I \"" + stagingDir + "\\*\" \""
                    + targetDir + "\\\" >nul");
                sb.AppendLine("start \"\" \"" + Path.Combine(targetDir, ExeName) + "\"");
            }
            else
            {
                throw new NotSupportedException("不支持的更新包类型：" + ext);
            }

            sb.AppendLine("del \"%~f0\"");
            File.WriteAllText(batPath, sb.ToString(), Encoding.Default);

            var psi = new ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
    }
}