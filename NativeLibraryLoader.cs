using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public static class NativeLibraryLoader
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        public static void LoadEmbeddedDll(string resourceName, string outputFileName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string targetDir = Path.Combine(Path.Combine(baseDir, "Launcher Setting"), "Natives");

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    var allResources = string.Join("\n", assembly.GetManifestResourceNames());
                    throw new FileNotFoundException(
                        "无法找到嵌入资源 '" + resourceName + "'。\n可用资源：\n" + allResources);
                }

                string dllPath = Path.Combine(targetDir, outputFileName);

                if (!File.Exists(dllPath))
                {
                    using (var fs = File.Create(dllPath))
                    {
                        byte[] buf = new byte[4096];
                        int read;
                        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                            fs.Write(buf, 0, read);
                    }
                }

                SetDllDirectory(targetDir);
                IntPtr handle = LoadLibrary(dllPath);
                if (handle == IntPtr.Zero)
                    throw new Exception("加载 " + outputFileName + " 失败，错误码: "
                        + Marshal.GetLastWin32Error());
            }
        }
    }
}