using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// 使用 Windows DPAPI 对本地文件进行加解密。
    /// 密钥由 Windows 自动管理，绑定当前用户，代码可公开。
    /// </summary>
    public static class SecureStorage
    {
        // 应用专属 entropy，公开无妨，仅用于区分同一用户下其他程序的密文
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("JerryCraftLauncher.TokenCache.v1");

        /// <summary>加密写入文件（覆盖）。</summary>
        public static void WriteAllTextEncrypted(string path, string plainText)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText ?? "");
            byte[] cipher = ProtectedData.Protect(
                plainBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, cipher);
        }

        /// <summary>解密读取文件，失败返回 null。</summary>
        public static string ReadAllTextDecrypted(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                byte[] cipher = File.ReadAllBytes(path);
                byte[] plainBytes = ProtectedData.Unprotect(
                    cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException)
            {
                // 换机器、换用户、文件损坏、非本程序加密
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>带明文回退的读取：兼容旧版本未加密文件，读到明文后自动重新加密迁移。</summary>
        public static string ReadAllTextWithMigration(string path)
        {
            if (!File.Exists(path)) return null;

            string decrypted = ReadAllTextDecrypted(path);
            if (decrypted != null) return decrypted;

            // 尝试按明文读取（旧版本遗留）
            try
            {
                string plain = File.ReadAllText(path, Encoding.UTF8);
                if (!string.IsNullOrEmpty(plain) && plain.TrimStart().StartsWith("{"))
                {
                    WriteAllTextEncrypted(path, plain); // 迁移到加密格式
                    return plain;
                }
            }
            catch { }

            return null;
        }
    }
}