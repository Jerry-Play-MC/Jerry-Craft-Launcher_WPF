using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public enum VersionCategory
    {
        Release,
        Snapshot,
        OldBeta,
        OldAlpha,
        AprilFools
    }

    public class ManifestVersion
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
        public string ReleaseTime { get; set; }
        public VersionCategory Category { get; set; }

        public string DisplayType
        {
            get
            {
                switch (Category)
                {
                    case VersionCategory.Release: return "正式版";
                    case VersionCategory.Snapshot: return "快照版";
                    case VersionCategory.OldBeta: return "远古版 Beta";
                    case VersionCategory.OldAlpha: return "远古版 Alpha";
                    case VersionCategory.AprilFools: return "愚人节版";
                    default: return Type;
                }
            }
        }
    }

    public static class VersionManifestScanner
    {
        private const string MANIFEST_URL =
            "https://piston-meta.mojang.com/mc/game/version_manifest.json";

        // 社区公认的愚人节版本（官方 type 可能是 release 或 snapshot）
        private static readonly HashSet<string> KnownAprilFools =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
                "1.RV-Pre1",
        };

        public static List<ManifestVersion> Fetch()
        {
            string json = DownloadString(MANIFEST_URL);
            return Parse(json);
        }

        public static List<ManifestVersion> Parse(string json)
        {
            var result = new List<ManifestVersion>();
            if (string.IsNullOrEmpty(json)) return result;

            var serializer = new JavaScriptSerializer();
            var root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.ContainsKey("versions")) return result;

            var versions = root["versions"] as System.Collections.ArrayList;
            if (versions == null) return result;

            foreach (var item in versions)
            {
                var dict = item as Dictionary<string, object>;
                if (dict == null) continue;

                string id = dict.ContainsKey("id") ? Convert.ToString(dict["id"]) : null;
                string type = dict.ContainsKey("type") ? Convert.ToString(dict["type"]) : null;
                string url = dict.ContainsKey("url") ? Convert.ToString(dict["url"]) : null;
                string time = dict.ContainsKey("releaseTime") ? Convert.ToString(dict["releaseTime"]) : null;
                if (string.IsNullOrEmpty(id)) continue;

                var v = new ManifestVersion
                {
                    Id = id,
                    Type = type,
                    Url = url,
                    ReleaseTime = time,
                    Category = Classify(id, type, time)
                };
                result.Add(v);
            }

            return result;
        }

        private static VersionCategory Classify(string id, string type, string releaseTime)
        {
            // 1. 社区公认愚人节清单优先
            if (KnownAprilFools.Contains(id))
                return VersionCategory.AprilFools;

            // 2. 发布日期是 4 月 1 日（考虑时区，用 3 月 31 日 ~ 4 月 2 日做宽松判断）
            if (IsAprilFoolsTime(releaseTime))
                return VersionCategory.AprilFools;

            // 3. 按官方 type 分
            switch (type)
            {
                case "release": return VersionCategory.Release;
                case "snapshot": return VersionCategory.Snapshot;
                case "old_beta": return VersionCategory.OldBeta;
                case "old_alpha": return VersionCategory.OldAlpha;
                default: return VersionCategory.Snapshot;
            }
        }

        private static bool IsAprilFoolsTime(string releaseTime)
        {
            if (string.IsNullOrEmpty(releaseTime)) return false;
            try
            {
                var dt = DateTime.Parse(releaseTime).ToUniversalTime();
                // 3月31日 ~ 4月2日，覆盖时区偏移
                if (dt.Month == 3 && dt.Day == 31) return true;
                if (dt.Month == 4 && (dt.Day == 1 || dt.Day == 2)) return true;
            }
            catch { }
            return false;
        }

        private static string DownloadString(string url)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "Mozilla/5.0";
                    req.Timeout = 15000;
                    req.AllowAutoRedirect = true;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
                catch
                {
                    if (retry < 2) System.Threading.Thread.Sleep(500);
                }
            }
            return null;
        }
    }
}