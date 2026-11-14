using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public static class ModApiService
    {
        // 【提速优化】全局复用 HttpClient，显著提升高并发下载性能
        // 注意：.NET Framework 4.5 不支持 MaxConnectionsPerServer，并发限制由 ServicePointManager 接管
        public static readonly HttpClient HttpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        static ModApiService()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            ServicePointManager.Expect100Continue = false;

            // 【核心提速】在 .NET Framework 中，这行代码决定了 HttpClient 的最大并发连接数
            ServicePointManager.DefaultConnectionLimit = 64;

            HttpClient.DefaultRequestHeaders.Add("User-Agent", "JerryStudioLauncher/1.0");
        }

        // ---------- HTTP (用于 JSON 接口) ----------
        private static string DownloadJson(string url)
        {
            try
            {
                // 对于纯文本接口，直接同步等待
                return HttpClient.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                App.AppLog.recordLog(App.AppLog.LogLevel.Error, $"[API请求失败] {url} : {ex.Message}");
                throw new Exception("请求服务器失败，请检查网络连接。", ex);
            }
        }

        // ---------- 镜像规则 ----------
        public static string GetMirrorUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            // 1. Modrinth 官方 CDN
            if (url.Contains("cdn.modrinth.com"))
            {
                try
                {
                    var uri = new Uri(url);
                    return "https://bmclapi2.bangbang93.com" + uri.PathAndQuery;
                }
                catch { return url; }
            }

            // 2. CurseForge 官方 CDN
            if (url.Contains("edge.forgecdn.net") || url.Contains("mediafilez.forgecdn.net"))
            {
                try
                {
                    var uri = new Uri(url);
                    return "https://bmclapi2.bangbang93.com" + uri.PathAndQuery;
                }
                catch { return url; }
            }

            // 3. GitHub 国内加速镜像 (包含实际文件下载域名)
            if (url.Contains("github.com") ||
                url.Contains("raw.githubusercontent.com") ||
                url.Contains("objects.githubusercontent.com"))
            {
                // 使用相对稳定且支持大文件的 gh-proxy 镜像
                return "https://gh-proxy.net/" + url;
            }

            return url;
        }

        // ---------- 搜索 (原版 Modrinth API) ----------
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

        public static List<ModrinthMod> SearchModsWithTranslation(
    string keyword, int page, int limit, ModProjectType type, out int totalHits)
        {
            // 1. 搜索仍走 Modrinth 官方
            var mods = SearchModrinth(keyword, "", page, limit, type, out totalHits);
            if (mods == null || mods.Count == 0) return mods;

            // 2. 用本地译名表填 TitleZh（命中率越高，列表越"中文化"）
            int hit = 0;
            foreach (var mod in mods)
            {
                if (string.IsNullOrEmpty(mod.Title)) continue;
                try
                {
                    string zh = ModTranslationService.GetChineseName(mod.Title);
                    if (!string.IsNullOrEmpty(zh))
                    {
                        mod.TitleZh = zh;
                        hit++;
                    }
                }
                catch (Exception ex)
                {
                    App.AppLog.recordLog(App.AppLog.LogLevel.Warn,
                        $"[ModTranslation] 查询 \"{mod.Title}\" 失败: {ex.Message}");
                }
            }

            App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                $"[ModTranslation] 搜索 \"{keyword}\" 命中译名 {hit}/{mods.Count}");

            return mods;
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