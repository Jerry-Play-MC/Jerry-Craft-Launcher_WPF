using System;
using System.Collections.Generic;

namespace Launch_Minecraft
{
    internal class Quilt : BaseLoaderLauncher
    {
        public override string LoaderName { get { return "Quilt"; } }

        /// <summary>启动 Quilt 客户端</summary>
        /// <param name="minecraftDir">.minecraft 根目录</param>
        /// <param name="versionName">版本目录名，如 "quilt-loader-0.23.0-1.20.1"</param>
        /// <param name="isolated">是否版本隔离</param>
        /// <param name="javaBaseDir">Java 基准目录（可选）</param>
        public static void LaunchClient(string minecraftDir, string versionName,
                                        bool isolated = false, string javaBaseDir = null)
        {
            new Quilt().Launch(
                CreateClientContext(minecraftDir, versionName, isolated, javaBaseDir));
        }

        /// <summary>启动 Quilt 服务端</summary>
        /// <param name="serverDir">server.jar 所在目录</param>
        /// <param name="versionName">版本 JSON 名</param>
        /// <param name="javaBaseDir">Java 基准目录（可选）</param>
        public static void LaunchServer(string serverDir, string versionName,
                                        string javaBaseDir = null)
        {
            new Quilt().Launch(
                CreateServerContext(serverDir, versionName, javaBaseDir));
        }

        /// <summary>Quilt 专属：关闭调试日志输出</summary>
        protected override void AppendLoaderJvmArgs(
            List<string> cmd, LaunchContext context, Dictionary<string, object> root)
        {
            cmd.Add("-Dquilt.loader.debug=false");
        }
    }
}