using System;
using System.IO;

namespace Launch_Bedrock
{
    internal static class PortalPaths
    {
        public static string LauncherDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string NativeDirectory
        {
            get { return Path.Combine(LauncherDirectory, "Native"); }
        }

        public static string BootstrapSource
        {
            get { return Path.Combine(NativeDirectory, "Bootstrap.dll"); }
        }

        public static string PreloadSource
        {
            get { return Path.Combine(NativeDirectory, "MyPreload.dll"); }
        }

        /// <summary>
        /// Portal 使用的标准 DLL 文件名，游戏进程会按这个名找。
        /// </summary>
        public const string BootstrapDeployedName = "Bootstrap.dll";
        public const string PreloadDeployedName = "MyPreload.dll";
    }
}