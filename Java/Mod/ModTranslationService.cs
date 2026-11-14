using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// Mod 中英译名表。
    /// - 启动时 Initialize() 同步加载本地缓存（毫秒级）。
    /// - StartRefresh() 后台拉取远程 JSON，校验通过才覆盖缓存并重载。
    /// - 远程失败 / JSON 非法 → 保留旧缓存，不影响使用。
    /// - 查询走内存 Dictionary，O(1)。
    /// - 提供 BuildChineseCandidates() 供中文搜索翻译为英文候选。
    /// </summary>
    public static class ModTranslationService
    {
        private const string RemoteRawUrl =
            "https://raw.githubusercontent.com/Jerry-Play-MC/Jerry-Craft-Launcher_WPF/main/Java/Mod/mod-translations.json";

        private static readonly string CacheFilePath =
            Path.Combine(App.cachePath, "mod-translations.json");

        private static volatile Dictionary<string, string> _enToZh =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static volatile Dictionary<string, string> _zhToEn =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _swapLock = new object();
        private static int _refreshStarted = 0;

        // ---------------- 查询 API ----------------

        /// <summary>用 mod 英文名反查中文名；找不到返回 null。</summary>
        public static string GetChineseName(string englishName)
        {
            if (string.IsNullOrEmpty(englishName)) return null;
            var map = _enToZh;
            if (map == null) return null;
            string v;
            return map.TryGetValue(englishName, out v) ? v : null;
        }

        /// <summary>用中文名反查英文名；找不到返回 null。</summary>
        public static string GetEnglishName(string chineseName)
        {
            if (string.IsNullOrEmpty(chineseName)) return null;
            var map = _zhToEn;
            if (map == null) return null;
            string v;
            return map.TryGetValue(chineseName, out v) ? v : null;
        }

        /// <summary>快照枚举中文键（线程安全：遍历中 swap 不影响本次枚举）。</summary>
        public static IEnumerable<string> EnumerateZhKeys()
        {
            var map = _zhToEn;
            if (map == null) return Enumerable.Empty<string>();
            return map.Keys;
        }

        /// <summary>快照枚举英文键。</summary>
        public static IEnumerable<string> EnumerateEnKeys()
        {
            var map = _enToZh;
            if (map == null) return Enumerable.Empty<string>();
            return map.Keys;
        }

        /// <summary>判断字符串是否含中日韩字符（用于区分中英文搜索）。</summary>
        public static bool ContainsCjk(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) return true; // 常用汉字
                if (c >= 0x3400 && c <= 0x4DBF) return true; // 扩展A
                if (c >= 0xF900 && c <= 0xFAFF) return true; // 兼容汉字
            }
            return false;
        }

        // ---------------- 中文搜索候选构建 ----------------

        public class SearchCandidate
        {
            /// <summary>发给 Modrinth 的英文 query。</summary>
            public string Query { get; set; }
            /// <summary>0=精确 1=前缀 2=子串 3=反向包含 4=相似度。</summary>
            public int Priority { get; set; }
            /// <summary>命中的中文译名（调试用）。</summary>
            public string MatchedZh { get; set; }
        }

        /// <summary>
        /// 中文输入 → 英文候选队列。
        /// 最多返回 maxQueries 个，按 Priority 升序（0 最相关）。
        /// </summary>
        public static List<SearchCandidate> BuildChineseCandidates(string input, int maxQueries = 8)
        {
            var result = new List<SearchCandidate>();
            if (string.IsNullOrWhiteSpace(input)) return result;
            input = input.Trim();
            if (input.Length == 0) return result;

            var zhKeys = EnumerateZhKeys();

            // [1] 精确匹配
            string exact = GetEnglishName(input);
            if (!string.IsNullOrEmpty(exact))
                result.Add(new SearchCandidate { Query = exact, Priority = 0, MatchedZh = input });

            // [2] 前缀（中文译名以输入开头）→ 按 key 长度升序取前 5
            var prefixHits = new List<string>();
            foreach (var key in zhKeys)
                if (key.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                    prefixHits.Add(key);

            foreach (var key in prefixHits.OrderBy(k => k.Length).Take(5))
            {
                var en = GetEnglishName(key);
                if (!string.IsNullOrEmpty(en))
                    result.Add(new SearchCandidate { Query = en, Priority = 1, MatchedZh = key });
            }

            // [3] 子串（中文译名包含输入，且不是前缀）→ 按 key 长度升序取前 2
            var substringHits = new List<string>();
            foreach (var key in zhKeys)
                if (key.Length > input.Length &&
                    key.IndexOf(input, StringComparison.OrdinalIgnoreCase) > 0)
                    substringHits.Add(key);

            foreach (var key in substringHits.OrderBy(k => k.Length).Take(2))
            {
                var en = GetEnglishName(key);
                if (!string.IsNullOrEmpty(en))
                    result.Add(new SearchCandidate { Query = en, Priority = 2, MatchedZh = key });
            }

            // [4] 反向包含（用户输入较长，包含某个译名）→ 按 key 长度降序取前 2
            if (input.Length >= 3)
            {
                var reverseHits = new List<string>();
                foreach (var key in zhKeys)
                    if (key.Length >= 2 &&
                        key.Length < input.Length &&
                        input.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        reverseHits.Add(key);

                foreach (var key in reverseHits.OrderByDescending(k => k.Length).Take(2))
                {
                    var en = GetEnglishName(key);
                    if (!string.IsNullOrEmpty(en))
                        result.Add(new SearchCandidate { Query = en, Priority = 3, MatchedZh = key });
                }
            }

            // [5] 相似度兜底：仅当 1~4 步都无结果时才启用，最多 1 个
            if (result.Count == 0 && input.Length >= 2)
            {
                string bestKey = null;
                double bestScore = 0.75; // 阈值
                foreach (var key in zhKeys)
                {
                    double score = Similarity(input, key);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestKey = key;
                    }
                }
                if (bestKey != null)
                {
                    var en = GetEnglishName(bestKey);
                    if (!string.IsNullOrEmpty(en))
                        result.Add(new SearchCandidate { Query = en, Priority = 4, MatchedZh = bestKey });
                }
            }

            // 按英文 query 去重（保留最小 Priority），截断到 maxQueries
            return result
                .GroupBy(c => c.Query, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(c => c.Priority).First())
                .OrderBy(c => c.Priority)
                .Take(maxQueries)
                .ToList();
        }

        // ---------------- 相似度：归一化 Levenshtein ----------------

        /// <summary>返回 0.0 ~ 1.0；1.0 完全相同。带长度差保护。</summary>
        public static double Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            int maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0) return 1;
            // 长度差 > 50% 直接判 0，避免无意义的远距离匹配
            if (Math.Abs(a.Length - b.Length) > maxLen * 0.5) return 0;
            int dist = Levenshtein(a, b);
            return 1.0 - (double)dist / maxLen;
        }

        private static int Levenshtein(string a, string b)
        {
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            // 两行滚动数组，空间 O(m)
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(
                        Math.Min(cur[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                var tmp = prev; prev = cur; cur = tmp;
            }
            return prev[m];
        }

        // ---------------- 启动加载 ----------------

        public static void Initialize()
        {
            try
            {
                Directory.CreateDirectory(App.cachePath);

                if (!File.Exists(CacheFilePath))
                {
                    App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                        "[ModTranslation] 本地缓存不存在，将等待后台下载: " + CacheFilePath);
                    return;
                }

                string json = File.ReadAllText(CacheFilePath, new UTF8Encoding(false));
                var data = Parse(json);
                if (data == null)
                {
                    App.AppLog.recordLog(App.AppLog.LogLevel.Warn,
                        "[ModTranslation] 本地缓存解析失败（可能损坏），跳过加载");
                    return;
                }

                Swap(data);

                App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                    $"[ModTranslation] 已加载本地缓存: 英文→中文 {_enToZh.Count} 条 / 中文→英文 {_zhToEn.Count} 条");
            }
            catch (Exception ex)
            {
                App.AppLog.recordLog(App.AppLog.LogLevel.Error,
                    "[ModTranslation] 加载本地缓存失败: " + ex.Message);
            }
        }

        public static void StartRefresh()
        {
            if (Interlocked.Exchange(ref _refreshStarted, 1) == 1) return;

            Task.Run(async () =>
            {
                try
                {
                    App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                        "[ModTranslation] 开始后台刷新译名表...");

                    string json = await DownloadWithFallbackAsync();
                    if (string.IsNullOrEmpty(json))
                    {
                        App.AppLog.recordLog(App.AppLog.LogLevel.Warn,
                            "[ModTranslation] 所有节点下载失败，保留本地缓存");
                        return;
                    }

                    var data = Parse(json);
                    if (data == null)
                    {
                        App.AppLog.recordLog(App.AppLog.LogLevel.Warn,
                            "[ModTranslation] 远程 JSON 解析失败，跳过覆盖");
                        return;
                    }

                    try
                    {
                        Directory.CreateDirectory(App.cachePath);
                        string tempPath = CacheFilePath + ".tmp";
                        File.WriteAllText(tempPath, json, new UTF8Encoding(false));

                        if (File.Exists(CacheFilePath))
                            File.Delete(CacheFilePath);
                        File.Move(tempPath, CacheFilePath);

                        App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                            "[ModTranslation] 远程译名表已覆盖到缓存: " + CacheFilePath);
                    }
                    catch (Exception ex)
                    {
                        App.AppLog.recordLog(App.AppLog.LogLevel.Error,
                            "[ModTranslation] 写入缓存失败（不影响内存使用）: " + ex.Message);
                    }

                    Swap(data);

                    App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                        $"[ModTranslation] 刷新完成: 英文→中文 {_enToZh.Count} 条 / 中文→英文 {_zhToEn.Count} 条");
                }
                catch (Exception ex)
                {
                    App.AppLog.recordLog(App.AppLog.LogLevel.Error,
                        "[ModTranslation] 刷新异常: " + ex.Message);
                }
            });
        }

        // ---------------- 内部实现 ----------------

        private static async Task<string> DownloadWithFallbackAsync()
        {
            string mirror = ModApiService.GetMirrorUrl(RemoteRawUrl);
            if (!string.IsNullOrEmpty(mirror) && mirror != RemoteRawUrl)
            {
                var s = await TryDownloadAsync(mirror);
                if (!string.IsNullOrEmpty(s)) return s;
                App.AppLog.recordLog(App.AppLog.LogLevel.Warn,
                    "[ModTranslation] 镜像失败，回退原始地址");
            }
            return await TryDownloadAsync(RemoteRawUrl);
        }

        private static async Task<string> TryDownloadAsync(string url)
        {
            try
            {
                App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                    "[ModTranslation] 尝试下载: " + url);

                byte[] bytes = await ModApiService.HttpClient.GetByteArrayAsync(url);
                if (bytes == null || bytes.Length < 1024) return null;

                string json = new UTF8Encoding(false).GetString(bytes);

                App.AppLog.recordLog(App.AppLog.LogLevel.Info,
                    $"[ModTranslation] 下载成功: {bytes.Length} 字节");
                return json;
            }
            catch (Exception ex)
            {
                App.AppLog.recordLog(App.AppLog.LogLevel.Warn,
                    $"[ModTranslation] 下载失败 {url} : {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, Dictionary<string, string>> Parse(string json)
        {
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
                if (data == null) return null;
                if (!data.ContainsKey("enToZh") || !data.ContainsKey("zhToEn")) return null;
                if (data["enToZh"] == null || data["zhToEn"] == null) return null;
                return data;
            }
            catch
            {
                return null;
            }
        }

        private static void Swap(Dictionary<string, Dictionary<string, string>> data)
        {
            var en = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var zh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in data["enToZh"])
                if (!string.IsNullOrEmpty(kv.Key)) en[kv.Key] = kv.Value;

            foreach (var kv in data["zhToEn"])
                if (!string.IsNullOrEmpty(kv.Key)) zh[kv.Key] = kv.Value;

            lock (_swapLock)
            {
                _enToZh = en;
                _zhToEn = zh;
            }
        }
    }
}