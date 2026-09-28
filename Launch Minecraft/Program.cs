using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Launch_Minecraft
{
    public static class GameLauncher
    {
        // ============================================================
        //   新的公开入口：替代原来的 Main
        // ============================================================
        /// <summary>
        /// 启动 Minecraft。
        /// </summary>
        /// <param name="launchType">client / server</param>
        /// <param name="folderPath">.minecraft 或服务端目录</param>
        /// <param name="launchVersion">版本名，服务端可传 "none"</param>
        /// <param name="isolated">是否开启版本隔离（仅 client 有效）</param>
        /// <param name="javaBaseDir">Java 基准目录，可传 null</param>
        /// <returns>0 = 成功，1 = 失败</returns>
        public static int Run(string launchType, string folderPath,
                              string launchVersion, bool isolated, string javaBaseDir)
        {
            try
            {
                try { Console.OutputEncoding = Encoding.GetEncoding(936); } catch { }

                if (string.IsNullOrEmpty(launchType))
                    throw new ArgumentException("launchType 不能为空");
                if (string.IsNullOrEmpty(folderPath))
                    throw new ArgumentException("文件夹路径不能为空");

                folderPath = Path.GetFullPath(folderPath);
                if (!Directory.Exists(folderPath))
                    throw new DirectoryNotFoundException($"文件夹不存在：{folderPath}");

                if (!string.IsNullOrEmpty(javaBaseDir))
                {
                    javaBaseDir = javaBaseDir.Trim().Trim('"');
                    if (!Directory.Exists(javaBaseDir))
                        throw new DirectoryNotFoundException(
                            $"Java 基准目录不存在：{javaBaseDir}");
                }

                bool isClient = launchType.Equals("client", StringComparison.OrdinalIgnoreCase);
                bool isServer = launchType.Equals("server", StringComparison.OrdinalIgnoreCase);
                if (!isClient && !isServer)
                    throw new ArgumentException($"未知的启动类型：{launchType}");

                string jsonPath;
                if (isClient)
                {
                    if (string.IsNullOrEmpty(launchVersion) ||
                        launchVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("client 模式必须提供版本名");

                    jsonPath = Path.Combine(folderPath, "versions",
                                            launchVersion, launchVersion + ".json");
                }
                else
                {
                    if (string.IsNullOrEmpty(launchVersion) ||
                        launchVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
                        jsonPath = FindServerVersionJson(folderPath);
                    else
                        jsonPath = Path.Combine(folderPath, launchVersion + ".json");
                }

                Console.WriteLine($"[检测] 启动类型  ：{launchType}");
                Console.WriteLine($"[检测] 文件夹路径：{folderPath}");
                Console.WriteLine($"[检测] 启动版本  ：{launchVersion}");
                Console.WriteLine($"[检测] 版本隔离  ：{(isClient ? (isolated ? "开启 (isolation)" : "关闭 (unisolation)") : "不适用（服务端）")}");
                Console.WriteLine($"[检测] Java目录  ：{javaBaseDir ?? "（未指定，使用注册表扫描）"}");
                Console.WriteLine($"[检测] 版本 JSON ：{jsonPath}");

                LoaderInfo info = LoaderDetector.Detect(jsonPath);

                Console.WriteLine($"[检测] 加载器类型：{info.Type}");
                Console.WriteLine($"[检测] 加载器版本：{info.Version ?? "N/A"}");
                Console.WriteLine($"[检测] 主库坐标  ：{info.Coordinates ?? "N/A"}");
                Console.WriteLine($"[检测] 主类      ：{info.MainClass ?? "N/A"}");

                switch (info.Type)
                {
                    case LoaderType.Vanilla:
                        if (isClient) Vanilla.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir);
                        else Vanilla.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.Forge:
                        if (isClient) Forge.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir);
                        else Forge.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.NeoForge:
                        if (isClient) NeoForge.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir);
                        else NeoForge.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.Fabric:
                        if (isClient) Fabric.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir);
                        else Fabric.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.Quilt:
                        if (isClient) Quilt.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir);
                        else Quilt.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.Unknown:
                        throw new Exception($"无法识别加载器，版本 JSON：{info.JsonPath}");
                }

                Console.WriteLine("[完成] 全部处理完成");
                return 0;
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}"); }
                catch { }
                return 1;
            }
        }

        private static string FindServerVersionJson(string folder)
        {
            string[] jsons;
            try { jsons = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly); }
            catch { jsons = new string[0]; }

            if (jsons.Length == 0)
                throw new FileNotFoundException($"在 {folder} 下未找到任何版本 JSON");

            string[] keys = { "neoforge", "forge", "fabric", "quilt" };
            foreach (var k in keys)
                foreach (var j in jsons)
                    if (Path.GetFileName(j).IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        return j;

            return jsons[0];
        }
    }

    // ============================================================
    //   以下枚举、类、检测器 全部保持原样
    // ============================================================
    public enum LoaderType
    {
        Vanilla = 0,
        Forge = 1,
        NeoForge = 2,
        Fabric = 3,
        Quilt = 4,
        Unknown = 99
    }

    public class LoaderInfo
    {
        public LoaderType Type { get; set; }
        public string Version { get; set; }
        public string Coordinates { get; set; }
        public string MainClass { get; set; }
        public string JsonPath { get; set; }

        public override string ToString()
        {
            return string.Format("{0}[{1}]", Type, Version ?? "N/A");
        }
    }

    internal static class LoaderDetector
    {
        public static LoaderInfo Detect(string versionJsonPath)
        {
            var result = new LoaderInfo
            {
                Type = LoaderType.Vanilla,
                JsonPath = versionJsonPath
            };

            if (string.IsNullOrEmpty(versionJsonPath) || !File.Exists(versionJsonPath))
            {
                result.Type = LoaderType.Unknown;
                return result;
            }

            Dictionary<string, object> root;
            try
            {
                string json = File.ReadAllText(versionJsonPath, Encoding.UTF8);
                root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            }
            catch
            {
                result.Type = LoaderType.Unknown;
                return result;
            }

            if (root == null) { result.Type = LoaderType.Unknown; return result; }

            if (root.ContainsKey("mainClass"))
                result.MainClass = root["mainClass"] as string;

            string idField = root.ContainsKey("id") ? Convert.ToString(root["id"]) : "";

            if (idField.IndexOf("neoforge", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Type = LoaderType.NeoForge;
                var m = Regex.Match(idField, @"neoforge[-_]?([\d\.]+)", RegexOptions.IgnoreCase);
                if (m.Success) result.Version = m.Groups[1].Value;
            }
            else if (idField.IndexOf("forge", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Type = LoaderType.Forge;
                var m = Regex.Match(idField, @"forge[-_]?([\d\.]+)", RegexOptions.IgnoreCase);
                if (m.Success) result.Version = m.Groups[1].Value;
            }
            else if (idField.IndexOf("fabric", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Type = LoaderType.Fabric;
            }
            else if (idField.IndexOf("quilt", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Type = LoaderType.Quilt;
            }

            var libraries = root.ContainsKey("libraries")
                ? root["libraries"] as ArrayList : null;

            bool sawForgeLib = false;
            bool sawNeoForgeLib = false;
            string forgeVerFromLib = null;
            string neoVerFromLib = null;

            if (libraries != null)
            {
                foreach (var libObj in libraries)
                {
                    var lib = libObj as Dictionary<string, object>;
                    if (lib == null) continue;
                    string name = lib.ContainsKey("name") ? Convert.ToString(lib["name"]) : null;
                    if (string.IsNullOrEmpty(name)) continue;

                    MavenCoord coord = ParseMavenCoord(name);
                    if (coord == null) continue;

                    if (coord.Group == "net.fabricmc" && coord.Artifact == "fabric-loader")
                    {
                        result.Type = LoaderType.Fabric;
                        result.Version = coord.Version;
                        result.Coordinates = coord.Group + ":" + coord.Artifact;
                        return result;
                    }
                    if (coord.Group == "org.quiltmc" && coord.Artifact == "quilt-loader")
                    {
                        result.Type = LoaderType.Quilt;
                        result.Version = coord.Version;
                        result.Coordinates = coord.Group + ":" + coord.Artifact;
                        return result;
                    }

                    if (coord.Group.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
                    {
                        sawNeoForgeLib = true;
                        if (string.IsNullOrEmpty(neoVerFromLib) &&
                            (coord.Artifact == "neoforge" || coord.Artifact == "forge"))
                        {
                            neoVerFromLib = coord.Version;
                        }
                    }

                    if (coord.Group == "net.minecraftforge")
                    {
                        if (coord.Artifact == "forge" ||
                            coord.Artifact == "fmlloader" ||
                            coord.Artifact == "fmlearlydisplay" ||
                            coord.Artifact == "forgespi" ||
                            coord.Artifact == "coremods")
                        {
                            sawForgeLib = true;

                            if (string.IsNullOrEmpty(forgeVerFromLib) &&
                                (coord.Artifact == "fmlloader" ||
                                 coord.Artifact == "fmlearlydisplay" ||
                                 coord.Artifact == "forge"))
                            {
                                string v = coord.Version;
                                int dash = v.IndexOf('-');
                                if (dash > 0 && dash < v.Length - 1)
                                    v = v.Substring(dash + 1);
                                forgeVerFromLib = v;
                            }
                        }
                    }
                }
            }

            if (result.Type == LoaderType.Vanilla)
            {
                if (sawNeoForgeLib)
                {
                    result.Type = LoaderType.NeoForge;
                    result.Version = neoVerFromLib;
                }
                else if (sawForgeLib)
                {
                    result.Type = LoaderType.Forge;
                    result.Version = forgeVerFromLib;
                }
            }

            if (result.Type == LoaderType.Vanilla && !string.IsNullOrEmpty(result.MainClass))
            {
                string mc = result.MainClass;
                if (mc.IndexOf("net.fabricmc", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Type = LoaderType.Fabric;
                else if (mc.IndexOf("org.quiltmc", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Type = LoaderType.Quilt;
                else if (mc.IndexOf("net.neoforged", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Type = LoaderType.NeoForge;
                else if (mc.IndexOf("net.minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Type = LoaderType.Forge;
            }

            return result;
        }

        private static MavenCoord ParseMavenCoord(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string[] parts = name.Split(':');
            if (parts.Length < 3) return null;

            var coord = new MavenCoord
            {
                Group = parts[0],
                Artifact = parts[1],
                Version = parts[2]
            };
            int at = coord.Version.IndexOf('@');
            if (at >= 0) coord.Version = coord.Version.Substring(0, at);
            return coord;
        }

        private class MavenCoord
        {
            public string Group;
            public string Artifact;
            public string Version;
        }
    }
}