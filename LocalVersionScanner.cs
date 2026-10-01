using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Launch_Minecraft;   // ★ 引用 LoaderDetector / LoaderInfo / LoaderType

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class LocalVersionInfo
    {
        public string Name { get; set; }              // 版本目录名
        public string FolderPath { get; set; }        // 完整路径
        public string MinecraftVersion { get; set; }  // 如 1.20.1
        public string LoaderType { get; set; }        // Vanilla / Forge / NeoForge / Fabric / Quilt
        public string LoaderVersion { get; set; }

        public string DisplayText
        {
            get
            {
                if (string.IsNullOrEmpty(LoaderType) ||
                    LoaderType.Equals("Vanilla", StringComparison.OrdinalIgnoreCase))
                    return MinecraftVersion + "  （原版）";
                if (string.IsNullOrEmpty(LoaderVersion))
                    return MinecraftVersion + "  （" + LoaderType + "）";
                return MinecraftVersion + "  （" + LoaderType + " " + LoaderVersion + "）";
            }
        }

        public override string ToString() { return Name; }
    }

    public static class LocalVersionScanner
    {
        public static List<LocalVersionInfo> Scan()
        {
            var result = new List<LocalVersionInfo>();
            try
            {
                string versionsDir = Path.Combine(App.Config.GameDir, "versions");
                if (!Directory.Exists(versionsDir)) return result;

                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    string name = Path.GetFileName(dir);
                    string jar = Path.Combine(dir, name + ".jar");
                    string json = Path.Combine(dir, name + ".json");
                    if (!File.Exists(jar) || !File.Exists(json)) continue;

                    var info = BuildInfo(name, dir, json);
                    if (info != null) result.Add(info);
                }
            }
            catch { }

            result.Sort((a, b) =>
                string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static LocalVersionInfo BuildInfo(string versionName, string versionDir, string jsonPath)
        {
            // ============================================================
            // ★ 加载器判断：完全委托给 LoaderDetector
            //   LoaderDetector 会从 libraries（Maven 坐标）、id、mainClass 三层判断，
            //   比从目录名猜可靠得多。
            // ============================================================
            LoaderInfo loader;
            try
            {
                loader = LoaderDetector.Detect(jsonPath);
            }
            catch
            {
                loader = new LoaderInfo { Type = LoaderType.Unknown };
            }

            if (loader == null || loader.Type == LoaderType.Unknown)
                return null;

            // ============================================================
            // 游戏版本：优先从版本 JSON 内部提取
            //   1) inheritsFrom
            //   2) libraries 里的 net.minecraft:client / server
            //   3) id 字段里的 1.x / 1.x.x
            //   4) 目录名兜底
            // ============================================================
            string mcVersion = ExtractMinecraftVersion(jsonPath, versionName);
            if (string.IsNullOrEmpty(mcVersion)) return null;

            return new LocalVersionInfo
            {
                Name = versionName,
                FolderPath = versionDir,
                MinecraftVersion = mcVersion,
                LoaderType = MapLoaderType(loader.Type),
                LoaderVersion = loader.Version ?? ""
            };
        }

        private static string MapLoaderType(LoaderType t)
        {
            switch (t)
            {
                case LoaderType.Vanilla: return "Vanilla";
                case LoaderType.Forge: return "Forge";
                case LoaderType.NeoForge: return "NeoForge";
                case LoaderType.Fabric: return "Fabric";
                case LoaderType.Quilt: return "Quilt";
                default: return "Vanilla";
            }
        }

        /// <summary>
        /// 从版本 JSON 内部提取游戏版本号（如 "1.20.1"）。
        /// </summary>
        private static string ExtractMinecraftVersion(string jsonPath, string fallbackVersionName)
        {
            try
            {
                string json = File.ReadAllText(jsonPath);
                var root = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(json);

                if (root != null)
                {
                    // 1) inheritsFrom 最直接（Fabric 官方 profile 会有，合并后没有）
                    if (root.ContainsKey("inheritsFrom"))
                    {
                        string v = Convert.ToString(root["inheritsFrom"]);
                        if (!string.IsNullOrEmpty(v)) return v;
                    }

                    // 2) 老版本：libraries 里 net.minecraft:client / server
                    if (root.ContainsKey("libraries"))
                    {
                        var libs = root["libraries"] as ArrayList;
                        if (libs != null)
                        {
                            foreach (var libObj in libs)
                            {
                                var lib = libObj as Dictionary<string, object>;
                                if (lib == null || !lib.ContainsKey("name")) continue;

                                string name = Convert.ToString(lib["name"]);
                                var m = Regex.Match(name,
                                    @"^net\.minecraft:(?:client|server):([\w\.\-]+)");
                                if (m.Success) return m.Groups[1].Value;
                            }
                        }
                    }

                    // 3) ★ 新主路径：从 id 里剥掉加载器段
                    if (root.ContainsKey("id"))
                    {
                        string v = ExtractMcVersionFromLoaderId(Convert.ToString(root["id"]));
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
            }
            catch { }

            // 4) 目录名兜底
            return ExtractMcVersionFromLoaderId(fallbackVersionName);
        }

        /// <summary>
        /// 从形如：
        ///   fabric-loader-0.19.5-26.3       → 26.3
        ///   fabric-loader-0.15.0-1.20.1     → 1.20.1
        ///   1.20.1-forge-47.2.0             → 1.20.1
        ///   quilt-loader-0.23.0-1.20.1      → 1.20.1
        ///   1.20.1-neoforge-20.4.237        → 1.20.1
        /// 的字符串里提取 MC 版本。
        /// 策略：把已知加载器模式（含其后紧邻的版本号）整段删掉，
        ///       剩下的就是 MC 版本。这样新旧格式都能覆盖。
        /// </summary>
        private static string ExtractMcVersionFromLoaderId(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;

            string work = source;

            // 顺序很重要：先复合词（fabric-loader），再单纯词（fabric）
            // 每个模式吃掉 "关键词 + 紧随其后的版本号"
            string[] patterns =
            {
        @"fabric-loader[-_]?[\d\.]+",
        @"quilt-loader[-_]?[\d\.]+",
        @"neoforge[-_]?[\d\.]+",
        @"forge[-_]?[\d\.]+",
        @"fabric[-_]?[\d\.]+",
        @"quilt[-_]?[\d\.]+",
    };

            foreach (var p in patterns)
                work = Regex.Replace(work, p, "", RegexOptions.IgnoreCase);

            // 清理残留的分隔符
            work = Regex.Replace(work, @"[-_]{2,}", "-").Trim('-', '_', ' ');

            if (string.IsNullOrEmpty(work)) return null;

            // 匹配新版和旧版 MC 版本格式：
            //   1.20.1 / 26.3 / 25w45a / 1.21.5-rc1 之类
            var m = Regex.Match(work,
                @"(\d+(?:\.\d+)+[a-z]?|\d{2}w\d{2}[a-z])",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;

            // 兜底：整个 work 就是个版本号
            if (Regex.IsMatch(work, @"^\d+(\.\d+)+$"))
                return work;

            return null;
        }
    }
}