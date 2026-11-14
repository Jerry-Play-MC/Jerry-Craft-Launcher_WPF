using System;
using System.Collections.Generic;
using System.IO;
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
    /// </summary>
    public static class ModTranslationService
    {
        // GitHub raw 地址（blob 页要换成 raw）
        private const string RemoteRawUrl =
            "https://raw.githubusercontent.com/Jerry-Play-MC/Jerry-Craft-Launcher_WPF/main/Java/Mod/mod-translations.json";

        private static readonly string CacheFilePath =
            Path.Combine(App.cachePath, "mod-translations.json");

        // 用 volatile + 替换引用实现无锁读；写侧在 lock 里换引用
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

        // ---------------- 启动加载 ----------------

        /// <summary>
        /// 启动时同步调用。只读本地缓存，不访问网络。
        /// </summary>
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

        /// <summary>
        /// 启动时调用一次。后台异步刷新，不阻塞 UI。
        /// 成功 → 覆盖缓存 + 重载内存；失败 → 保留旧缓存。
        /// </summary>
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

                    // 校验通过 → 安全覆盖缓存
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
            // 1) 先走镜像（GetMirrorUrl 对 raw.githubusercontent.com 会加 gh-proxy）
            string mirror = ModApiService.GetMirrorUrl(RemoteRawUrl);
            if (!string.IsNullOrEmpty(mirror) && mirror != RemoteRawUrl)
            {
                var s = await TryDownloadAsync(mirror);
                if (!string.IsNullOrEmpty(s)) return s;
                App.AppLog.recordLog(App.AppLog.LogLevel.Warn,
                    "[ModTranslation] 镜像失败，回退原始地址");
            }

            // 2) 原始地址
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

        /// <summary>
        /// 反序列化为 { "enToZh": {...}, "zhToEn": {...} }。
        /// 结构不对 / JSON 损坏 → null。
        /// </summary>
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

        /// <summary>
        /// 原子替换内存字典。读侧无锁（volatile 引用），写侧 lock 保证一致性。
        /// </summary>
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