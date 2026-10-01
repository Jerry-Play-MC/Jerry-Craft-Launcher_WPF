using System;

namespace Launch_Minecraft
{
    internal class Fabric : BaseLoaderLauncher
    {
        public override string LoaderName { get { return "Fabric"; } }

        public static void LaunchClient(string minecraftDir, string versionName,
                                        bool isolated = false, string javaBaseDir = null,
                                        string username = null, string uuid = null,
                                        string accessToken = null, string userType = null,
                                        Action<LaunchProgress> onProgress = null)
        {
            new Fabric().Launch(
                CreateClientContext(minecraftDir, versionName, isolated, javaBaseDir,
                                    username, uuid, accessToken, userType, onProgress));
        }

        public static void LaunchServer(string serverDir, string versionName,
                                        string javaBaseDir = null)
        {
            new Fabric().Launch(
                CreateServerContext(serverDir, versionName, javaBaseDir));
        }
    }
}