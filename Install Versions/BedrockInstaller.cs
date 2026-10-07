using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace Install_Versions
{
    public class BedrockInstaller
    {
        private const string PREVIEW_CIK = "3FD6491FF58B8D1FED7EDBD89477DAD9802814007571F6A353C710BA972EF113C6F250C54B315AF61A33CCA5DE85B08A";
        private const string RELEASE_CIK = "91E7B9BD7CC93437E1A8BC602552DF06C9A969FBFCBBF5F46D71250AF226CF6AC7D15C25F9546344549391D16857391F";
        private const string DB_URL = "https://data.mcappx.com/v2/bedrock.json";

        public class VersionEntry
        {
            public string Version;
            public string BuildType;
            public string Type;
            public string Date;
            public string Md5;
            public string RawMetaData;   // GDK：直接 URL；UWP：UpdateID (GUID)
            public string DownloadUrl;   // 安装时才填充
        }

        /// <summary>
        /// 从 bedrock.json 获取版本列表（不查询 FE3，快速返回）
        /// </summary>
        public static List<VersionEntry> GetVersions(bool includePreview = false)
        {
            string json;
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "mcappx_developer");
                json = wc.DownloadString(DB_URL);
            }

            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;

            var root = serializer.Deserialize<Dictionary<string, object>>(json);
            var result = new List<VersionEntry>();

            foreach (var sourceKv in root)
            {
                if (sourceKv.Key == "CreationTime") continue;

                var sourceObj = AsObject(sourceKv.Value);
                if (sourceObj == null) continue;

                foreach (var versionKv in sourceObj)
                {
                    var build = AsObject(versionKv.Value);
                    if (build == null) continue;

                    string type = GetString(build, "Type");
                    string buildType = GetString(build, "BuildType");
                    string date = GetString(build, "Date");

                    if (type == "Preview" && !includePreview) continue;
                    if (type != "Release" && type != "Preview") continue;
                    if (buildType != "GDK" && buildType != "UWP") continue;

                    var variations = AsArray(build, "Variations");
                    if (variations == null) continue;

                    foreach (var vObj in variations)
                    {
                        var v = AsObject(vObj);
                        if (v == null) continue;

                        if (GetString(v, "Arch") != "x64") continue;

                        var metaData = AsArray(v, "MetaData");
                        if (metaData == null || metaData.Length == 0) continue;

                        string raw = metaData[metaData.Length - 1] as string ?? "";
                        string md5 = GetString(v, "MD5");

                        if (string.IsNullOrEmpty(raw)) continue;

                        result.Add(new VersionEntry
                        {
                            Version = versionKv.Key,
                            BuildType = buildType,
                            Type = type,
                            Date = date,
                            Md5 = md5,
                            RawMetaData = raw,
                            DownloadUrl = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : null
                        });
                    }
                }
            }

            return result.OrderByDescending(v => v.Date).ToList();
        }

        /// <summary>
        /// 解析出实际的下载 URL（GDK 直接用；UWP 走 FE3）
        /// </summary>
        private static string ResolveDownloadUrl(VersionEntry version, Action<string> log = null)
        {
            if (!string.IsNullOrEmpty(version.DownloadUrl))
                return version.DownloadUrl;

            if (string.IsNullOrEmpty(version.RawMetaData))
                throw new Exception("版本没有可用的下载信息: " + version.Version);

            if (version.RawMetaData.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                version.DownloadUrl = version.RawMetaData;
                return version.DownloadUrl;
            }

            // UWP：MetaData 是 UpdateID，需要走 FE3
            log?.Invoke("查询 FE3: " + version.RawMetaData);
            try
            {
                version.DownloadUrl = Fe3Helper.GetDownloadUrl(version.RawMetaData);
            }
            catch (Exception ex)
            {
                throw new Exception("FE3 查询失败: " + ex.Message, ex);
            }

            if (string.IsNullOrEmpty(version.DownloadUrl))
                throw new Exception("FE3 未返回可用 URL");

            return version.DownloadUrl;
        }

        /// <summary>
        /// 下载并安装
        /// </summary>
        public static void Install(VersionEntry version, string targetDir, Action<string> log = null)
        {
            if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
                throw new Exception("目标目录非空：" + targetDir);

            Directory.CreateDirectory(targetDir);

            // 解析下载 URL（UWP 会在这里才走 FE3）
            string downloadUrl = ResolveDownloadUrl(version, log);
            log?.Invoke("下载 " + downloadUrl);

            string msixvcPath = Path.Combine(targetDir, version.Version + ".msixvc");
            Downloader.DownloadFile(downloadUrl, msixvcPath, (received, total) =>
            {
                if (total > 0)
                {
                    double percent = (double)received / total * 100;
                    Console.Write("\r下载: {0,5:F1}%  {1} / {2}          ",
                        percent, FormatSize(received), FormatSize(total));
                }
                else
                {
                    Console.Write("\r下载: {0}          ", FormatSize(received));
                }
            });
            Console.WriteLine();

            // MD5 校验（如果 JSON 里有）
            if (!string.IsNullOrEmpty(version.Md5))
            {
                log?.Invoke("校验 MD5...");
                if (!Downloader.VerifyMd5(msixvcPath, version.Md5))
                    throw new Exception("MD5 校验失败");
            }
            else
            {
                log?.Invoke("跳过 MD5 校验（JSON 未提供）");
            }

            // 解密
            string cikHex = version.Type == "Preview" ? PREVIEW_CIK : RELEASE_CIK;
            var cik = new CikKey(cikHex);

            log?.Invoke("解密并解压 msixvc...");
            using (var stream = new MsiXvdStream(msixvcPath))
            {
                stream.Parse();
                stream.ExtractAll(targetDir, cik);
            }

            // 清理解压后不需要的文件
            TryDelete(msixvcPath);

            log?.Invoke("安装完成：" + targetDir);
        }

        public static void LaunchGDK(string targetDir)
        {
            string exe = Path.Combine(targetDir, "Minecraft.Windows.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("找不到游戏可执行文件", exe);

            System.Diagnostics.Process.Start(exe);
        }

        public static void LaunchUWP()
        {
            System.Diagnostics.Process.Start("minecraft://launch");
        }

        // ===== 类型安全辅助方法 =====

        private static Dictionary<string, object> AsObject(object o)
        {
            var dict = o as Dictionary<string, object>;
            if (dict != null) return dict;

            var idict = o as System.Collections.IDictionary;
            if (idict != null)
            {
                var result = new Dictionary<string, object>();
                foreach (System.Collections.DictionaryEntry entry in idict)
                    result[entry.Key.ToString()] = entry.Value;
                return result;
            }

            return null;
        }

        private static object[] AsArray(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value)) return null;
            if (value == null) return null;

            var arr = value as object[];
            if (arr != null) return arr;

            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null && !(value is string))
            {
                var list = new List<object>();
                foreach (var item in enumerable) list.Add(item);
                return list.ToArray();
            }

            return null;
        }

        private static string GetString(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value)) return "";
            if (value == null) return "";
            return value as string ?? value.ToString();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
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