using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public static class ModApiService
    {
        static ModApiService()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 20;
        }

        // ---------- HTTP ----------
        private static string DownloadJson(string url)
        {
            using (var client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers.Add("User-Agent", "JerryStudioLauncher/1.0");

                try
                {
                    string json = client.DownloadString(url);

                    // 清理非法控制字符（保留 \t \n \r）
                    char[] chars = json.ToCharArray();
                    int writeIndex = 0;
                    for (int i = 0; i < chars.Length; i++)
                    {
                        char c = chars[i];
                        if (c == '\t' || c == '\n' || c == '\r' || c >= 32)
                            chars[writeIndex++] = c;
                    }
                    return new string(chars, 0, writeIndex);
                }
                catch (WebException ex)
                {
                    string detail = "";
                    int status = 0;
                    if (ex.Response != null)
                    {
                        var resp = (HttpWebResponse)ex.Response;
                        status = (int)resp.StatusCode;
                        try
                        {
                            using (var stream = ex.Response.GetResponseStream())
                            using (var reader = new StreamReader(stream))
                                detail = reader.ReadToEnd();
                        }
                        catch { }
                    }

                    string friendly;
                    if (status == 429) friendly = "请求过于频繁，请稍后再试。";
                    else if (status == 400) friendly = "请求参数错误，请检查搜索条件。";
                    else if (status == 404) friendly = "未找到请求的资源。";
                    else if (status == 0) friendly = "网络连接失败，请检查网络或代理设置。";
                    else friendly = "HTTP " + status + " 错误";

                    throw new Exception(
                        friendly + "\n详细：" + ex.Message + "\n响应内容：" + detail, ex);
                }
            }
        }

        // ---------- 镜像 ----------
        public static string GetMirrorUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.Contains("cdn.modrinth.com"))
            {
                try
                {
                    var uri = new Uri(url);
                    return "https://bmclapi2.bangbang93.com" + uri.PathAndQuery;
                }
                catch { return url; }
            }
            return url;
        }

        // ---------- 搜索 ----------
        public static List<ModrinthMod> SearchModrinth(
            string keyword, string mcVersion, int page, int limit,
            ModProjectType type, out int totalHits)
        {
            totalHits = 0;
            int offset = (page - 1) * limit;

            string facet;
            switch (type)
            {
                case ModProjectType.Mod: facet = "project_type:mod"; break;
                case ModProjectType.ResourcePack: facet = "project_type:resourcepack"; break;
                case ModProjectType.Shader: facet = "project_type:shader"; break;
                case ModProjectType.DataPack: facet = "project_type:datapack"; break;
                case ModProjectType.Modpack: facet = "project_type:modpack"; break;
                default: facet = "project_type:mod"; break;
            }
            string facets = "[[" + "\"" + facet + "\"" + "]]";

            string url = string.IsNullOrEmpty(keyword)
                ? "https://api.modrinth.com/v2/search?index=downloads&limit=" + limit
                  + "&offset=" + offset + "&facets=" + Uri.EscapeDataString(facets)
                : "https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(keyword)
                  + "&index=downloads&limit=" + limit + "&offset=" + offset
                  + "&facets=" + Uri.EscapeDataString(facets);

            string json = DownloadJson(url);

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null) return new List<ModrinthMod>();

            totalHits = JsonHelper.GetInt(root, "total_hits");

            var hits = JsonHelper.GetArray(root, "hits");
            var result = new List<ModrinthMod>();
            if (hits == null) return result;

            foreach (var hit in hits)
            {
                result.Add(new ModrinthMod
                {
                    Id = JsonHelper.GetString(hit, "project_id"),
                    Title = JsonHelper.GetString(hit, "title"),
                    Description = JsonHelper.GetString(hit, "description"),
                    IconUrl = JsonHelper.GetString(hit, "icon_url"),
                    Downloads = JsonHelper.GetInt(hit, "downloads"),
                    Author = JsonHelper.GetString(hit, "author"),
                    ProjectType = JsonHelper.GetString(hit, "project_type"),
                });
            }
            return result;
        }

        // ---------- 版本列表 ----------
        public static List<ModVersion> GetModVersions(string modId)
        {
            if (string.IsNullOrEmpty(modId))
                throw new ArgumentException("Mod ID 不能为空");

            modId = modId.Trim();
            string url = "https://api.modrinth.com/v2/project/" + modId + "/version";

            string json = DownloadJson(url);
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var array = ser.Deserialize<ArrayList>(json);

            var result = new List<ModVersion>();
            if (array == null) return result;

            foreach (var item in array)
            {
                var v = new ModVersion
                {
                    Id = JsonHelper.GetString(item, "id"),
                    ProjectId = JsonHelper.GetString(item, "project_id"),
                    VersionNumber = JsonHelper.GetString(item, "version_number"),
                    Changelog = JsonHelper.GetString(item, "changelog"),
                    DatePublished = ParseDate(JsonHelper.GetString(item, "date_published")),
                    Downloads = JsonHelper.GetInt(item, "downloads"),
                    VersionType = JsonHelper.GetString(item, "version_type"),
                    Featured = JsonHelper.GetBool(item, "featured"),
                    GameVersions = new List<string>(),
                    Loaders = new List<string>(),
                    Files = new List<ModFile>()
                };

                var gvs = JsonHelper.GetArray(item, "game_versions");
                if (gvs != null)
                    foreach (var g in gvs) v.GameVersions.Add(Convert.ToString(g));

                var loaders = JsonHelper.GetArray(item, "loaders");
                if (loaders != null)
                    foreach (var l in loaders) v.Loaders.Add(Convert.ToString(l));

                var files = JsonHelper.GetArray(item, "files");
                if (files != null)
                {
                    foreach (var f in files)
                    {
                        var mf = new ModFile
                        {
                            Url = JsonHelper.GetString(f, "url"),
                            Filename = JsonHelper.GetString(f, "filename"),
                            Primary = JsonHelper.GetBool(f, "primary"),
                            Size = JsonHelper.GetLong(f, "size"),
                            GameVersions = new List<string>(),
                            Loaders = new List<string>()
                        };

                        var hashes = JsonHelper.GetObject(f, "hashes");
                        if (hashes != null)
                        {
                            mf.Sha1 = JsonHelper.GetString(hashes, "sha1");
                            mf.Sha512 = JsonHelper.GetString(hashes, "sha512");
                        }

                        var fgv = JsonHelper.GetArray(f, "game_versions");
                        if (fgv != null)
                            foreach (var g in fgv) mf.GameVersions.Add(Convert.ToString(g));

                        var fl = JsonHelper.GetArray(f, "loaders");
                        if (fl != null)
                            foreach (var l in fl) mf.Loaders.Add(Convert.ToString(l));

                        v.Files.Add(mf);
                    }
                }

                result.Add(v);
            }
            return result;
        }

        private static DateTime ParseDate(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            DateTime dt;
            if (DateTime.TryParse(s, out dt)) return dt;
            return DateTime.MinValue;
        }
    }
}