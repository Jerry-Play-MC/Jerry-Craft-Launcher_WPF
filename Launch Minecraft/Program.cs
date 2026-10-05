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
        public static int Run(string launchType, string folderPath,
                      string launchVersion, bool isolated, string javaBaseDir,
                      string username = null, string uuid = null,
                      string accessToken = null, string userType = null,
                      Action<LaunchProgress> onProgress = null)
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
                Console.WriteLine($"[检测] Java目录  ：{javaBaseDir ?? "（未指定）"}");
                Console.WriteLine($"[检测] 版本 JSON ：{jsonPath}");

                LoaderInfo info = LoaderDetector.Detect(jsonPath);

                Console.WriteLine($"[检测] 加载器类型：{info.Type}");
                Console.WriteLine($"[检测] 加载器版本：{info.Version ?? "N/A"}");
                Console.WriteLine($"[检测] 主库坐标  ：{info.Coordinates ?? "N/A"}");
                Console.WriteLine($"[检测] 主类      ：{info.MainClass ?? "N/A"}");

                switch (info.Type)
                {
                    case LoaderType.Vanilla:
                        if (isClient) Vanilla.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                            username, uuid, accessToken, userType, onProgress);
                        else Vanilla.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.Forge:
                        if (isClient) Forge.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                          username, uuid, accessToken, userType, onProgress);
                        else Forge.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.NeoForge:
                        if (isClient) NeoForge.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                             username, uuid, accessToken, userType, onProgress);
                        else NeoForge.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.Fabric:
                        if (isClient) Fabric.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                           username, uuid, accessToken, userType, onProgress);
                        else Fabric.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.LegacyFabric:
                        if (isClient) Fabric.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                           username, uuid, accessToken, userType, onProgress);
                        else
                            throw new NotSupportedException(
                                "Legacy Fabric 没有官方一键服务端安装器，请通过官方安装器手动部署。");
                        break;

                    case LoaderType.Quilt:
                        if (isClient) Quilt.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                          username, uuid, accessToken, userType, onProgress);
                        else Quilt.LaunchServer(folderPath, launchVersion, javaBaseDir);
                        break;

                    case LoaderType.LiteLoader:
                        if (isClient)
                            LiteLoader.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                    username, uuid, accessToken, userType, onProgress);
                        else
                            throw new NotSupportedException(
                                "LiteLoader 是纯客户端模组加载器，没有独立的服务端。");
                        break;

                    case LoaderType.LabyMod:   // ★
                        if (isClient)
                            LabyMod.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                 username, uuid, accessToken, userType, onProgress);
                        else
                            throw new NotSupportedException(
                                "LabyMod 是纯客户端模组客户端，没有服务端。");
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

        public static int DetectRequiredJavaMajor(string versionJsonPath)
        {
            try
            {
                if (string.IsNullOrEmpty(versionJsonPath) || !File.Exists(versionJsonPath))
                    return 8;

                string json = File.ReadAllText(versionJsonPath, Encoding.UTF8);
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (root == null) return 8;

                if (root.ContainsKey("javaVersion"))
                {
                    var jv = root["javaVersion"] as Dictionary<string, object>;
                    if (jv != null && jv.ContainsKey("majorVersion"))
                    {
                        int m;
                        if (int.TryParse(Convert.ToString(jv["majorVersion"]), out m) && m > 0)
                            return m;
                    }
                }

                string versionId = root.ContainsKey("id") ? Convert.ToString(root["id"]) : null;
                if (!string.IsNullOrEmpty(versionId))
                {
                    var m = Regex.Match(versionId, @"(1\.\d+(?:\.\d+)?)");
                    if (m.Success)
                        return BaseLoaderLauncher.InferJavaMajorFromGameVersion(m.Groups[1].Value);
                }
            }
            catch { }
            return 8;
        }

        public static bool HasExactJava(int requiredMajor, string javaBaseDir)
        {
            return JavaLocator.HasExact(requiredMajor, javaBaseDir);
        }

        private static string FindServerVersionJson(string folder)
        {
            string[] jsons;
            try { jsons = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly); }
            catch { jsons = new string[0]; }

            if (jsons.Length == 0)
                throw new FileNotFoundException($"在 {folder} 下未找到任何版本 JSON");

            // ★ 加入 labymod
            string[] keys = { "neoforge", "legacyfabric", "forge", "fabric", "quilt", "liteloader", "labymod" };
            foreach (var k in keys)
                foreach (var j in jsons)
                    if (Path.GetFileName(j).IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        return j;

            return jsons[0];
        }
    }

    public enum LoaderType
    {
        Vanilla = 0,
        Forge = 1,
        NeoForge = 2,
        Fabric = 3,
        Quilt = 4,
        LiteLoader = 5,
        LegacyFabric = 6,
        LabyMod = 7,       // ★
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

    public static class LoaderDetector
    {
        public static LoaderInfo Detect(string versionJsonPath)
        {
            return DetectInternal(versionJsonPath, 0);
        }

        private static LoaderInfo DetectInternal(string versionJsonPath, int depth)
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

            if (depth > 5) return result;

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

            // ★ 优先级 0：LabyMod（通过 id 前缀识别）
            //   LabyMod 版本 JSON 是 vanilla-like 结构，没有 tweakClass，
            //   靠 id 前缀 "LabyMod-4-" 才能可靠识别。
            string versionId = root.ContainsKey("id") ? Convert.ToString(root["id"]) : "";
            if (versionId.StartsWith("LabyMod-4-", StringComparison.OrdinalIgnoreCase))
            {
                result.Type = LoaderType.LabyMod;
                result.Version = versionId.Substring("LabyMod-4-".Length);
                result.Coordinates = "net.labymod";
                return result;
            }

            // 优先级 1：libraries 指纹
            var info = DetectFromLibraries(root);
            if (info != null)
            {
                info.JsonPath = versionJsonPath;
                return info;
            }

            // 优先级 2：mainClass
            if (!string.IsNullOrEmpty(result.MainClass))
            {
                string mc = result.MainClass;
                if (mc.IndexOf("net.fabricmc", StringComparison.OrdinalIgnoreCase) >= 0)
                { result.Type = LoaderType.Fabric; return result; }
                if (mc.IndexOf("org.quiltmc", StringComparison.OrdinalIgnoreCase) >= 0)
                { result.Type = LoaderType.Quilt; return result; }
                if (mc.IndexOf("net.neoforged", StringComparison.OrdinalIgnoreCase) >= 0)
                { result.Type = LoaderType.NeoForge; return result; }
                if (mc.IndexOf("net.minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0)
                { result.Type = LoaderType.Forge; return result; }
            }

            // 优先级 3：递归 inheritsFrom
            if (root.ContainsKey("inheritsFrom"))
            {
                string parentId = Convert.ToString(root["inheritsFrom"]);
                if (!string.IsNullOrEmpty(parentId))
                {
                    string dir = Path.GetDirectoryName(versionJsonPath);
                    string versionsDir = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(versionsDir))
                    {
                        string parentPath = Path.Combine(
                            versionsDir, parentId, parentId + ".json");
                        if (File.Exists(parentPath))
                        {
                            var p = DetectInternal(parentPath, depth + 1);
                            if (p.Type != LoaderType.Vanilla && p.Type != LoaderType.Unknown)
                            {
                                p.JsonPath = versionJsonPath;
                                return p;
                            }
                        }
                    }
                }
            }

            return result;
        }

        private static LoaderInfo DetectFromLibraries(Dictionary<string, object> root)
        {
            if (!root.ContainsKey("libraries")) return null;
            var libraries = root["libraries"] as ArrayList;
            if (libraries == null) return null;

            string fabricVer = null;
            string quiltVer = null;
            string forgeVer = null;
            string neoVer = null;
            string liteLoaderVer = null;

            bool sawForge = false;
            bool sawNeo = false;
            bool sawLiteLoader = false;

            string versionId = root.ContainsKey("id") ? Convert.ToString(root["id"]) : "";
            bool isLegacyFabric =
                versionId.IndexOf("legacyfabric", StringComparison.OrdinalIgnoreCase) >= 0 ||
                versionId.IndexOf("legacy-fabric", StringComparison.OrdinalIgnoreCase) >= 0;

            foreach (var libObj in libraries)
            {
                var lib = libObj as Dictionary<string, object>;
                if (lib == null) continue;
                string name = lib.ContainsKey("name") ? Convert.ToString(lib["name"]) : null;
                if (string.IsNullOrEmpty(name)) continue;

                var coord = ParseMavenCoord(name);
                if (coord == null) continue;

                if (coord.Group == "net.fabricmc" && coord.Artifact == "fabric-loader")
                {
                    fabricVer = coord.Version;
                }
                else if (coord.Group == "org.quiltmc" && coord.Artifact == "quilt-loader")
                {
                    quiltVer = coord.Version;
                }
                else if (coord.Group.StartsWith("net.neoforged",
                            StringComparison.OrdinalIgnoreCase))
                {
                    sawNeo = true;
                    if (string.IsNullOrEmpty(neoVer) &&
                        (coord.Artifact == "neoforge" || coord.Artifact == "forge"))
                    {
                        neoVer = coord.Version;
                    }
                }
                else if (coord.Group == "net.minecraftforge")
                {
                    if (coord.Artifact == "forge" ||
                        coord.Artifact == "fmlloader" ||
                        coord.Artifact == "fmlearlydisplay" ||
                        coord.Artifact == "forgespi" ||
                        coord.Artifact == "coremods")
                    {
                        sawForge = true;
                        if (string.IsNullOrEmpty(forgeVer) &&
                            (coord.Artifact == "fmlloader" ||
                             coord.Artifact == "fmlearlydisplay" ||
                             coord.Artifact == "forge"))
                        {
                            string v = coord.Version;
                            int dash = v.IndexOf('-');
                            if (dash > 0 && dash < v.Length - 1)
                                v = v.Substring(dash + 1);
                            forgeVer = v;
                        }
                    }
                }
                else if (coord.Group == "com.mumfrey" && coord.Artifact == "liteloader")
                {
                    sawLiteLoader = true;
                    liteLoaderVer = coord.Version;
                }
            }

            if (!string.IsNullOrEmpty(fabricVer))
            {
                if (isLegacyFabric)
                    return new LoaderInfo
                    {
                        Type = LoaderType.LegacyFabric,
                        Version = fabricVer,
                        Coordinates = "net.fabricmc:fabric-loader"
                    };

                return new LoaderInfo
                {
                    Type = LoaderType.Fabric,
                    Version = fabricVer,
                    Coordinates = "net.fabricmc:fabric-loader"
                };
            }
            if (!string.IsNullOrEmpty(quiltVer))
                return new LoaderInfo
                {
                    Type = LoaderType.Quilt,
                    Version = quiltVer,
                    Coordinates = "org.quiltmc:quilt-loader"
                };
            if (sawNeo)
                return new LoaderInfo
                {
                    Type = LoaderType.NeoForge,
                    Version = neoVer,
                    Coordinates = "net.neoforged:neoforge"
                };
            if (sawLiteLoader)
                return new LoaderInfo
                {
                    Type = LoaderType.LiteLoader,
                    Version = liteLoaderVer,
                    Coordinates = "com.mumfrey:liteloader"
                };
            if (sawForge)
                return new LoaderInfo
                {
                    Type = LoaderType.Forge,
                    Version = forgeVer,
                    Coordinates = "net.minecraftforge:forge"
                };

            return null;
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