using System;
using System.IO;
using System.Threading.Tasks;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// 启动器内置 Java 运行时管理。
    /// 路径约定：exe目录\Launcher Setting\Java\{major}\bin\java.exe
    /// 缺失时调用 Download_Java 项目下载。
    /// </summary>
    public static class JavaRuntimeHelper
    {
        public static string GetBuiltInJavaRoot()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "Launcher Setting", "Java");
        }

        public static string GetJavaExePath(int javaMajor)
        {
            return Path.Combine(GetBuiltInJavaRoot(),
                                javaMajor.ToString(),
                                "bin", "java.exe");
        }

        public static bool HasBuiltInJava(int javaMajor)
        {
            try
            {
                string p = GetJavaExePath(javaMajor);
                return File.Exists(p) && new FileInfo(p).Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 同步下载 Java（阻塞）。调用方负责放到后台线程。
        /// </summary>
        public static bool DownloadJava(int javaMajor)
        {
            if (javaMajor <= 0) return false;

            string root = GetBuiltInJavaRoot();
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);

            int rc = Download_Java.JavaDownloader.Run(javaMajor, root);
            return rc == 0 && HasBuiltInJava(javaMajor);
        }

        public static Task<bool> DownloadJavaAsync(int javaMajor)
        {
            return Task.Run(() => DownloadJava(javaMajor));
        }
    }
}