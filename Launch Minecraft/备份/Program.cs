using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Launch_Minecraft
{
    internal class Program
    {
        static void Main(string[] args)
        {
            int exitCode = 0;

            try
            {
                try { Console.OutputEncoding = Encoding.GetEncoding(936); } catch { }

                // ---- 参数检查（前 3 个必需，第 4、5 个可选） ----
                if (args == null || args.Length < 3)
                {
                    throw new ArgumentException(
                        "参数数量错误：至少需要 3 个参数\n" +
                        "用法: Launch Minecraft.exe <client|server> <文件夹路径> <版本名|none> " +
                        "[isolation|unisolation] [Java基准目录]");
                }

                string LaunchType = args[0];
                string FolderPath = args[1];
                string LaunchVersion = args[2];

                // 第 4 个参数：版本隔离
                bool isolated = false;
                if (args.Length >= 4 && !string.IsNullOrEmpty(args[3]))
                {
                    string isoArg = args[3].Trim();
                    if (isoArg.Equals("isolation", StringComparison.OrdinalIgnoreCase))
                        isolated = true;
                    else if (isoArg.Equals("unisolation", StringComparison.OrdinalIgnoreCase))
                        isolated = false;
                    else
                        throw new ArgumentException(
                            $"未知的隔离开关：{isoArg}（只支持 isolation / unisolation）");
                }

                // ★ 第 5 个参数：Java 基准目录（可选）
                //   两种格式都兼容：
                //     A) 父目录，下面有多个 jdk-xxx 子文件夹
                //     B) Java home 本身，根目录直接有 bin\java.exe
                string javaBaseDir = null;
                if (args.Length >= 5 && !string.IsNullOrEmpty(args[4]))
                {
                    javaBaseDir = args[4].Trim().Trim('"');
                    if (!Directory.Exists(javaBaseDir))
                        throw new DirectoryNotFoundException(
                            $"Java 基准目录不存在：{javaBaseDir}");
                }

                bool isClient = LaunchType.Equals("client", StringComparison.OrdinalIgnoreCase);
                bool isServer = LaunchType.Equals("server", StringComparison.OrdinalIgnoreCase);
                if (!isClient && !isServer)
                    throw new ArgumentException($"未知的启动类型：{LaunchType}");

                if (string.IsNullOrEmpty(FolderPath))
                    throw new ArgumentException("文件夹路径不能为空");
                FolderPath = Path.GetFullPath(FolderPath);
                if (!Directory.Exists(FolderPath))
                    throw new DirectoryNotFoundException($"文件夹不存在：{FolderPath}");

                string jsonPath;
                if (isClient)
                {
                    if (string.IsNullOrEmpty(LaunchVersion) ||
                        LaunchVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("client 模式必须提供版本名");

                    jsonPath = Path.Combine(FolderPath, "versions",
                                            LaunchVersion, LaunchVersion + ".json");
                }
                else
                {
                    if (string.IsNullOrEmpty(LaunchVersion) ||
                        LaunchVersion.Equals("none", StringComparison.OrdinalIgnoreCase))
                        jsonPath = FindServerVersionJson(FolderPath);
                    else
                        jsonPath = Path.Combine(FolderPath, LaunchVersion + ".json");
                }

                Console.WriteLine($"[检测] 启动类型  ：{LaunchType}");
                Console.WriteLine($"[检测] 文件夹路径：{FolderPath}");
                Console.WriteLine($"[检测] 启动版本  ：{LaunchVersion}");
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
                        if (isClient) Vanilla.LaunchClient(FolderPath, LaunchVersion, isolated, javaBaseDir);
                        else Vanilla.LaunchServer(FolderPath, LaunchVersion, javaBaseDir);
                        break;

                    case LoaderType.Forge:
                        if (isClient) Forge.LaunchClient(FolderPath, LaunchVersion, isolated, javaBaseDir);
                        else Forge.LaunchServer(FolderPath, LaunchVersion, javaBaseDir);
                        break;

                    case LoaderType.NeoForge:
                        if (isClient) NeoForge.LaunchClient(FolderPath, LaunchVersion, isolated, javaBaseDir);
                        else NeoForge.LaunchServer(FolderPath, LaunchVersion, javaBaseDir);
                        break;

                    case LoaderType.Fabric:
                        if (isClient) Fabric.LaunchClient(FolderPath, LaunchVersion, isolated, javaBaseDir);
                        else Fabric.LaunchServer(FolderPath, LaunchVersion, javaBaseDir);
                        break;

                    case LoaderType.Quilt:
                        if (isClient) Quilt.LaunchClient(FolderPath, LaunchVersion, isolated, javaBaseDir);
                        else Quilt.LaunchServer(FolderPath, LaunchVersion, javaBaseDir);
                        break;

                    case LoaderType.Unknown:
                        throw new Exception($"无法识别加载器，版本 JSON：{info.JsonPath}");
                }

                Console.WriteLine("[完成] 全部处理完成");
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
                exitCode = 1;
            }

            Environment.Exit(exitCode);
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

            var libraries = root.ContainsKey("libraries")
                ? root["libraries"] as ArrayList : null;

            if (libraries != null)
            {
                foreach (var libObj in libraries)
                {
                    var lib = libObj as Dictionary<string, object>;
                    if (lib == null) continue;
                    string name = lib.ContainsKey("name") ? lib["name"] as string : null;
                    if (string.IsNullOrEmpty(name)) continue;

                    MavenCoord coord = ParseMavenCoord(name);
                    if (coord == null) continue;

                    if (coord.Group == "org.quiltmc" && coord.Artifact == "quilt-loader")
                    {
                        result.Type = LoaderType.Quilt;
                        result.Version = coord.Version;
                        result.Coordinates = coord.Group + ":" + coord.Artifact;
                        return result;
                    }
                    if (coord.Group == "net.fabricmc" && coord.Artifact == "fabric-loader")
                    {
                        result.Type = LoaderType.Fabric;
                        result.Version = coord.Version;
                        result.Coordinates = coord.Group + ":" + coord.Artifact;
                        return result;
                    }
                    if (coord.Group == "net.neoforged" &&
                        (coord.Artifact == "neoforge" || coord.Artifact == "forge"))
                    {
                        if (result.Type != LoaderType.NeoForge)
                        {
                            result.Type = LoaderType.NeoForge;
                            result.Version = coord.Version;
                            result.Coordinates = coord.Group + ":" + coord.Artifact;
                        }
                        continue;
                    }
                    if (coord.Group == "net.minecraftforge" && coord.Artifact == "forge")
                    {
                        if (result.Type != LoaderType.NeoForge)
                        {
                            result.Type = LoaderType.Forge;
                            result.Version = coord.Version;
                            result.Coordinates = coord.Group + ":" + coord.Artifact;
                        }
                        continue;
                    }
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