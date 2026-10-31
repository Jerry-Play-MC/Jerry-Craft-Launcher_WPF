using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Java.Install_Java
{
    public static class JavaRegistryRegistrar
    {
        /// <summary>
        /// 将指定 Java 路径注册到标准 JavaSoft 注册表位置。
        /// 自动区分 JDK / JRE、Java 8- / Java 9+、32 位 / 64 位视图。
        /// </summary>
        /// <param name="javaHome">Java 根目录（也容忍传入 bin 目录），例如 D:\Java\jdk-17</param>
        /// <returns>写入的注册表项在 regedit 中的真实可读路径</returns>
        public static string Register(string javaHome, bool forCurrentUser = false)
        {
            if (string.IsNullOrEmpty(javaHome) || !Directory.Exists(javaHome))
                throw new DirectoryNotFoundException($"Java目录不存在: {javaHome}");

            javaHome = Path.GetFullPath(javaHome).TrimEnd('\\');

            if (Path.GetFileName(javaHome).Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(javaHome);
                if (!string.IsNullOrEmpty(parent) &&
                    File.Exists(Path.Combine(parent, "bin", "java.exe")))
                    javaHome = parent;
            }

            bool isJdk = File.Exists(Path.Combine(javaHome, "bin", "javac.exe"));

            string version;
            bool is64Bit;
            ParseJavaInfo(javaHome, out version, out is64Bit);
            if (string.IsNullOrEmpty(version))
                throw new Exception($"无法从 {javaHome} 解析 Java 版本。");

            int major = GetMajorVersion(version);
            string baseKeyPath = GetBaseKeyPath(major, isJdk);
            string fullKeyPath = $@"{baseKeyPath}\{version}";
            string currentVersionValue = GetCurrentVersionValue(version, major);

            // ★ 选 Hive 和 View
            RegistryHive hive = forCurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            // HKCU\Software 下除 Classes 外不做 WOW64 重定向，直接用默认视图即可
            RegistryView view = forCurrentUser
                ? RegistryView.Default
                : (is64Bit ? RegistryView.Registry64 : RegistryView.Registry32);

            WriteVersionKey(hive, view, fullKeyPath, javaHome, major);
            WriteCurrentVersion(hive, view, baseKeyPath, currentVersionValue);

            // 返回真实可读路径
            if (forCurrentUser)
                return $@"HKEY_CURRENT_USER\{fullKeyPath}";

            return is64Bit
                ? $@"HKEY_LOCAL_MACHINE\{fullKeyPath}"
                : $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\{StripSoftwarePrefix(fullKeyPath)}";
        }

        private static void WriteVersionKey(
            RegistryHive hive, RegistryView view, string keyPath, string javaHome, int major)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
            using (var key = baseKey.CreateSubKey(keyPath))
            {
                if (key == null) throw new Exception($"无法创建注册表键: {keyPath}");

                key.SetValue("JavaHome", javaHome, RegistryValueKind.String);
                if (major < 9)
                    key.SetValue("MicroVersion", "0", RegistryValueKind.String);

                string runtimeLib = FindJvmDll(javaHome);
                if (!string.IsNullOrEmpty(runtimeLib))
                    key.SetValue("RuntimeLib", runtimeLib, RegistryValueKind.String);
            }
        }

        private static void WriteCurrentVersion(
            RegistryHive hive, RegistryView view, string baseKeyPath, string currentVersion)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
            using (var key = baseKey.CreateSubKey(baseKeyPath))
            {
                key?.SetValue("CurrentVersion", currentVersion, RegistryValueKind.String);
            }
        }

        private static void WriteVersionKey(
            RegistryView view, string keyPath, string javaHome, int major)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var key = baseKey.CreateSubKey(keyPath))
            {
                if (key == null)
                    throw new Exception($"无法创建注册表键: {keyPath}");

                key.SetValue("JavaHome", javaHome, RegistryValueKind.String);

                // MicroVersion 仅 Java 8 及更早的标准结构里有
                if (major < 9)
                    key.SetValue("MicroVersion", "0", RegistryValueKind.String);

                // RuntimeLib：指向真实存在的 jvm.dll
                string runtimeLib = FindJvmDll(javaHome);
                if (!string.IsNullOrEmpty(runtimeLib))
                    key.SetValue("RuntimeLib", runtimeLib, RegistryValueKind.String);
            }
        }

        private static void WriteCurrentVersion(
            RegistryView view, string baseKeyPath, string currentVersion)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var key = baseKey.CreateSubKey(baseKeyPath))
            {
                key?.SetValue("CurrentVersion", currentVersion, RegistryValueKind.String);
            }
        }

        private static string GetBaseKeyPath(int major, bool isJdk)
        {
            // Java 9+ 使用简短键名 JDK / JRE；Java 8 及以下使用完整键名
            if (major >= 9)
                return isJdk ? @"SOFTWARE\JavaSoft\JDK" : @"SOFTWARE\JavaSoft\JRE";

            return isJdk
                ? @"SOFTWARE\JavaSoft\Java Development Kit"
                : @"SOFTWARE\JavaSoft\Java Runtime Environment";
        }

        private static string GetCurrentVersionValue(string version, int major)
        {
            if (major >= 9) return major.ToString();

            // 1.8.0_xxx → "1.8"；1.7.0_80 → "1.7"
            var m = Regex.Match(version, @"^1\.(\d+)");
            return m.Success ? $"1.{m.Groups[1].Value}" : version;
        }

        /// <summary>
        /// 按优先级查找真实存在的 jvm.dll。
        /// 覆盖：64位 JDK8 内置 JRE、32位 JDK8 内置 JRE、64位 JRE8/Java9+、32位 JRE8。
        /// </summary>
        private static string FindJvmDll(string javaHome)
        {
            var candidates = new[]
            {
                Path.Combine(javaHome, "jre", "bin", "server", "jvm.dll"), // 64位 JDK 8 内置 JRE
                Path.Combine(javaHome, "jre", "bin", "client", "jvm.dll"), // 32位 JDK 8 内置 JRE
                Path.Combine(javaHome, "bin", "server", "jvm.dll"),        // 64位 JRE 8 / Java 9+
                Path.Combine(javaHome, "bin", "client", "jvm.dll"),        // 32位 JRE 8
            };

            foreach (var p in candidates)
                if (File.Exists(p)) return p;

            return null;
        }

        private static void ParseJavaInfo(string javaHome, out string version, out bool is64Bit)
        {
            string releaseFile = Path.Combine(javaHome, "release");
            version = null;
            is64Bit = Environment.Is64BitOperatingSystem; // 默认按系统架构

            // 1. 优先读 release 文件
            if (File.Exists(releaseFile))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(releaseFile))
                    {
                        if (line.StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase))
                            version = line.Substring("JAVA_VERSION=".Length).Trim('"', ' ');

                        if (line.StartsWith("OS_ARCH=", StringComparison.OrdinalIgnoreCase))
                        {
                            string arch = line.Substring("OS_ARCH=".Length).Trim('"', ' ');
                            if (arch.Equals("amd64", StringComparison.OrdinalIgnoreCase) ||
                                arch.Equals("x86_64", StringComparison.OrdinalIgnoreCase) ||
                                arch.Equals("aarch64", StringComparison.OrdinalIgnoreCase))
                                is64Bit = true;
                            else if (arch.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
                                     arch.Equals("i386", StringComparison.OrdinalIgnoreCase) ||
                                     arch.Equals("arm", StringComparison.OrdinalIgnoreCase))
                                is64Bit = false;
                        }
                    }
                }
                catch { }
            }

            // 2. release 缺失或没读到版本时，从目录名推断
            //    支持 jdk1.8.0_401 / jdk-17.0.2 / jdk-21 / jre8u401 等常见绿色版命名
            if (string.IsNullOrEmpty(version))
            {
                string dirName = Path.GetFileName(javaHome);
                var m = Regex.Match(dirName,
                    @"(?:jdk|jre)[-_]?(\d+(?:\.\d+)*(?:_\d+)?)",
                    RegexOptions.IgnoreCase);
                if (m.Success) version = m.Groups[1].Value;
            }
        }

        private static int GetMajorVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;

            var m = Regex.Match(version, @"^(\d+)");
            if (!m.Success) return 0;

            int major = int.Parse(m.Groups[1].Value);

            // 1.8.0_xxx → major = 8
            if (major == 1 && version.StartsWith("1."))
            {
                var m2 = Regex.Match(version, @"^1\.(\d+)");
                if (m2.Success) return int.Parse(m2.Groups[1].Value);
            }

            return major;
        }

        private static string StripSoftwarePrefix(string path)
        {
            const string prefix = @"SOFTWARE\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(prefix.Length)
                : path;
        }
    }
}