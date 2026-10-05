using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Launch_Minecraft
{
    internal class LabyMod : BaseLoaderLauncher
    {
        public override string LoaderName { get { return "LabyMod"; } }

        public static void LaunchClient(string minecraftDir, string versionName,
                                        bool isolated = false, string javaBaseDir = null,
                                        string username = null, string uuid = null,
                                        string accessToken = null, string userType = null,
                                        Action<LaunchProgress> onProgress = null)
        {
            new LabyMod().Launch(
                CreateClientContext(minecraftDir, versionName, isolated, javaBaseDir,
                                    username, uuid, accessToken, userType, onProgress));
        }

        public static void LaunchServer(string serverDir, string versionName,
                                        string javaBaseDir = null)
        {
            throw new NotSupportedException(
                "LabyMod 是纯客户端模组客户端，没有服务端。");
        }

        /// <summary>
        /// LabyMod 需要把原版 1.12.2 客户端 jar 也加进 classpath，
        /// 否则 GameProvider / FartMappingService 会拿到 null 路径崩掉。
        /// </summary>
        protected override List<string> ReorderClasspath(
            List<string> entries, LaunchContext context,
            Dictionary<string, object> root, string mainClass)
        {
            // 1) LabyMod 核心 jar 固定放首位
            string coreJar = Path.Combine(
                Path.Combine(Path.Combine(context.MinecraftDir, "versions"), context.VersionName),
                context.VersionName + ".jar");

            if (File.Exists(coreJar))
            {
                entries.RemoveAll(e => string.Equals(e.Trim(), coreJar, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, coreJar);
            }

            // 2) 找原版 jar。LabyMod 的 JSON 里有 _minecraftVersion，
            //    优先用它；没有就退回 inheritsFrom / minecraftVersion。
            string mcVersion = null;
            if (root.ContainsKey("_minecraftVersion"))
                mcVersion = Convert.ToString(root["_minecraftVersion"]);
            else if (root.ContainsKey("inheritsFrom"))
                mcVersion = Convert.ToString(root["inheritsFrom"]);
            else if (root.ContainsKey("minecraftVersion"))
                mcVersion = Convert.ToString(root["minecraftVersion"]);

            if (!string.IsNullOrEmpty(mcVersion))
            {
                string vanillaJar = Path.Combine(
                    Path.Combine(Path.Combine(context.MinecraftDir, "versions"), mcVersion),
                    mcVersion + ".jar");

                if (File.Exists(vanillaJar))
                {
                    entries.RemoveAll(e => string.Equals(e.Trim(), vanillaJar, StringComparison.OrdinalIgnoreCase));

                    // 插到 LabyMod 核心 jar 之后
                    int idx = entries.IndexOf(coreJar) + 1;
                    if (idx <= 0) idx = 1;
                    if (idx > entries.Count) idx = entries.Count;
                    entries.Insert(idx, vanillaJar);

                    Console.WriteLine($"[LabyMod] 已把原版 jar 加入 classpath: {vanillaJar}");
                }
                else
                {
                    Console.WriteLine($"[LabyMod] 警告：未找到原版 jar: {vanillaJar}");
                }
            }

            return entries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}