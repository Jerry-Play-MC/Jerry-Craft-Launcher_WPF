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

                    case LoaderType.Quilt:
                        if (isClient) Quilt.LaunchClient(folderPath, launchVersion, isolated, javaBaseDir,
                                                          username, uuid, accessToken, userType, onProgress);
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

            if (depth > 5) return result;   // 防环

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

            // ============================================================
            // 优先级 1：libraries 里的加载器指纹坐标（最可靠）
            // ============================================================
            var info = DetectFromLibraries(root);
            if (info != null)
            {
                info.JsonPath = versionJsonPath;
                return info;
            }

            // ============================================================
            // 优先级 2：从 mainClass 判断（区分大类，Forge/NeoForge 可能混淆）
            // ============================================================
            if (!string.IsNullOrEmpty(result.MainClass))
            {
                string mc = result.MainClass;
                if (mc.IndexOf("net.fabricmc", StringComparison.OrdinalIgnoreCase) >= 0)
                { result.Type = LoaderType.Fabric; return result; }
                if (mc.IndexOf("org.quiltmc", StringComparison.OrdinalIgnoreCase) >= 0)
                { result.Type = LoaderType.Quilt; return result; }
                // 注意：新版 Forge 和 NeoForge 都用 BootstrapLauncher，mainClass 无法区分，
                // 只能靠 inheritsFrom / 目录名兜底。这里暂标 Forge，交给下面递归修正。
                if (mc.IndexOf("net.neoforged", StringComparison.OrdinalIgnoreCase) >= 0)
                { result.Type = LoaderType.NeoForge; return result; }
                if (mc.IndexOf("net.minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0)
                { result.Type = LoaderType.Forge; return result; }
            }

            // ============================================================
            // 优先级 3：递归 inheritsFrom（覆盖库散在父版本 JSON 的情况）
            // ============================================================
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

        // ================================================================
        //  从 libraries 提取加载器类型和版本
        // ================================================================
        private static LoaderInfo DetectFromLibraries(Dictionary<string, object> root)
        {
            if (!root.ContainsKey("libraries")) return null;
            var libraries = root["libraries"] as ArrayList;
            if (libraries == null) return null;

            string fabricVer = null;
            string quiltVer = null;
            string forgeVer = null;      // 已去掉 MC 前缀
            string neoVer = null;

            bool sawForge = false;
            bool sawNeo = false;

            foreach (var libObj in libraries)
            {
                var lib = libObj as Dictionary<string, object>;
                if (lib == null) continue;
                string name = lib.ContainsKey("name") ? Convert.ToString(lib["name"]) : null;
                if (string.IsNullOrEmpty(name)) continue;

                var coord = ParseMavenCoord(name);
                if (coord == null) continue;

                // ---- Fabric ----
                if (coord.Group == "net.fabricmc" && coord.Artifact == "fabric-loader")
                {
                    fabricVer = coord.Version;
                }
                // ---- Quilt ----
                else if (coord.Group == "org.quiltmc" && coord.Artifact == "quilt-loader")
                {
                    quiltVer = coord.Version;
                }
                // ---- NeoForge：可能出现在多个 group 下 ----
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
                // ---- Forge ----
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
                            // forge 库版本形如 "1.20.1-47.2.0"，取 - 后的部分
                            string v = coord.Version;
                            int dash = v.IndexOf('-');
                            if (dash > 0 && dash < v.Length - 1)
                                v = v.Substring(dash + 1);
                            forgeVer = v;
                        }
                    }
                }
            }

            // 优先级：Fabric > Quilt > NeoForge > Forge（同时出现两个加载器的可能性极小）
            if (!string.IsNullOrEmpty(fabricVer))
                return new LoaderInfo
                {
                    Type = LoaderType.Fabric,
                    Version = fabricVer,
                    Coordinates = "net.fabricmc:fabric-loader"
                };
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