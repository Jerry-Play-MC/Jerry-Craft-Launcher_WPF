using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public static class ModApiService
    {
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
            ServicePointManager.DefaultConnectionLimit = 64;
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "JerryStudioLauncher/1.0");
        }

        private static string DownloadJson(string url)
        {
            try
            {
                return HttpClient.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                App.AppLog.recordLog(App.AppLog.LogLevel.Error, $"[API请求失败] {url} : {ex.Message}");
                throw new Exception("请求服务器失败，请检查网络连接。", ex);
            }
        }

        public static string GetMirrorUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            if (url.Contains("cdn.modrinth.com"))
            {
                try
                {
                    var uri = new Uri(url);
                    return "https://bmcmirror.bangbang93.com" + uri.PathAndQuery;
                }
                catch { return url; }
            }
            if (url.Contains("edge.forgecdn.net") || url.Contains("mediafilez.forgecdn.net"))
            {
                try
                {
                    var uri = new Uri(url);
                    return "https://bmclapi2.bangbang93.com" + uri.PathAndQuery;
                }
                catch { return url; }
            }
            if (url.Contains("github.com") ||
                url.Contains("raw.githubusercontent.com") ||
                url.Contains("objects.githubusercontent.com"))
            {
                return "https://gh-proxy.net/" + url;
            }
            return url;
        }

        // ---------- 原版搜索（单 query） ----------
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

        // ---------- 智能搜索（中英文自适应） ----------
        /// <summary>
        /// - 空关键字 → 默认榜单
        /// - 英文输入 → 单 query 直搜（Modrinth 自带分词模糊）
        /// - 中文输入 → 译名表构建 ≤8 候选 → 并发搜 → 合并去重 → 客户端分页
        /// - 中文无候选 / 合并无结果 → 回退原始输入直搜（保证不空）
        /// 本方法内部会并发阻塞，调用方应在后台线程（Task.Run）中执行。
        /// </summary>
        public static List<ModrinthMod> SearchModsSmart(
            string keyword, int page, int limit, ModProjectType type, out int totalHits)
        {
            totalHits = 0;
            keyword = (keyword ?? "").Trim();

            // 空关键字 → 默认榜单
            if (keyword.Length == 0)
            {
                var empty = SearchModrinth("", "", page, limit, type, out totalHits);
                FillChineseTitles(empty);
                return empty;
            }

            bool isChinese = ModTranslationService.ContainsCjk(keyword);

            // 英文直搜
            if (!isChinese)
            {
                var result = SearchModrinth(keyword, "", page, limit, type, out totalHits);
                FillChineseTitles(result);
                return result;
            }

            // 中文 → 构建候选
            var candidates = ModTranslationService.BuildChineseCandidates(keyword, 8);

            if (candidates.Count == 0)
            {
                App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                    $"[搜索] 中文输入 \"{keyword}\" 未命中译名表，回退原始输入直搜");
                var result = SearchModrinth(keyword, "", page, limit, type, out totalHits);
                FillChineseTitles(result);
                return result;
            }

            App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                $"[搜索] 中文输入 \"{keyword}\" → {candidates.Count} 个候选: "
                + string.Join(", ", candidates.Select(c => $"{c.Query}[P{c.Priority}]").ToArray()));

            // 并发查询候选
            const int perQueryLimit = 20;
            var tasks = candidates.Select(c => Task.Run(() =>
            {
                try
                {
                    int t;
                    var list = SearchModrinth(c.Query, "", 1, perQueryLimit, type, out t);
                    return new { Candidate = c, Result = list ?? new List<ModrinthMod>() };
                }
                catch (Exception ex)
                {
                    App.AppLog.recordLog(App.AppLog.LogLevel.Warn,
                        $"[搜索] 候选 \"{c.Query}\" 失败: {ex.Message}");
                    return new { Candidate = c, Result = new List<ModrinthMod>() };
                }
            })).ToArray();

            try { Task.WaitAll(tasks, TimeSpan.FromSeconds(30)); } catch { }

            // 合并去重：Priority 升序 → 组内 Downloads 降序
            var merged = new List<ModrinthMod>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in tasks
                .Where(t => t.Status == TaskStatus.RanToCompletion)
                .Select(t => t.Result)
                .OrderBy(x => x.Candidate.Priority))
            {
                foreach (var mod in item.Result.OrderByDescending(m => m.Downloads))
                {
                    if (!string.IsNullOrEmpty(mod.Id) && seen.Add(mod.Id))
                        merged.Add(mod);
                }
            }

            // 合并无结果 → 回退原始输入
            if (merged.Count == 0)
            {
                App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                    $"[搜索] 中文候选均无结果，回退原始输入直搜");
                var result = SearchModrinth(keyword, "", page, limit, type, out totalHits);
                FillChineseTitles(result);
                return result;
            }

            // 客户端分页
            totalHits = merged.Count;
            int offset = (page - 1) * limit;
            var pageData = merged.Skip(offset).Take(limit).ToList();

            FillChineseTitles(pageData);

            App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                $"[搜索] 中文合并后共 {merged.Count} 条，本页 {pageData.Count} 条");

            return pageData;
        }

        /// <summary>兼容壳，内部转发到 SearchModsSmart。</summary>
        public static List<ModrinthMod> SearchModsWithTranslation(
            string keyword, int page, int limit, ModProjectType type, out int totalHits)
        {
            return SearchModsSmart(keyword, page, limit, type, out totalHits);
        }

        private static void FillChineseTitles(List<ModrinthMod> mods)
        {
            if (mods == null || mods.Count == 0) return;
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
                catch { }
            }
            if (hit > 0)
            {
                App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                    $"[搜索] 本页 {mods.Count} 条，命中译名 {hit} 条");
            }
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