using System;

namespace Launch_Minecraft
{
    internal class Vanilla : BaseLoaderLauncher
    {
        public override string LoaderName { get { return "Vanilla"; } }

        /// <summary>启动原版客户端</summary>
        /// <param name="minecraftDir">.minecraft 根目录</param>
        /// <param name="versionName">版本目录名，如 "1.20.1"</param>
        /// <param name="isolated">是否版本隔离</param>
        /// <param name="javaBaseDir">Java 基准目录（可选）。父目录或 Java home 均可</param>
        public static void LaunchClient(string minecraftDir, string versionName,
                                        bool isolated = false, string javaBaseDir = null)
        {
            new Vanilla().Launch(
                CreateClientContext(minecraftDir, versionName, isolated, javaBaseDir));
        }

        /// <summary>启动原版服务端</summary>
        /// <param name="serverDir">server.jar 所在目录</param>
        /// <param name="versionName">版本 JSON 名（通常为 "none" 时自动探测）</param>
        /// <param name="javaBaseDir">Java 基准目录（可选）</param>
        public static void LaunchServer(string serverDir, string versionName,
                                        string javaBaseDir = null)
        {
            new Vanilla().Launch(
                CreateServerContext(serverDir, versionName, javaBaseDir));
        }
    }
}