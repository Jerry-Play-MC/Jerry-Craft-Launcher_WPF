using System;
using System.Collections.Generic;

namespace Launch_Minecraft
{
    internal class Quilt : BaseLoaderLauncher
    {
        public override string LoaderName { get { return "Quilt"; } }

        public static void LaunchClient(string minecraftDir, string versionName,
                                        bool isolated = false, string javaBaseDir = null,
                                        string username = null, string uuid = null,
                                        string accessToken = null, string userType = null,
                                        Action<LaunchProgress> onProgress = null)
        {
            new Quilt().Launch(
                CreateClientContext(minecraftDir, versionName, isolated, javaBaseDir,
                                    username, uuid, accessToken, userType, onProgress));
        }

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