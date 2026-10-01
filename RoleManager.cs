using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public static class RoleManager
    {
        private static string _roleDir;
        private static readonly List<Role> _roles = new List<Role>();
        private static Role _current;

        public static event Action CurrentRoleChanged;

        public static IList<Role> Roles { get { return _roles; } }
        public static Role Current { get { return _current; } }

        public static void Initialize()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _roleDir = Path.Combine(baseDir, "Launcher Setting", "Roles");
            if (!Directory.Exists(_roleDir)) Directory.CreateDirectory(_roleDir);
            Reload();
        }

        public static void Reload()
        {
            _roles.Clear();
            try
            {
                if (Directory.Exists(_roleDir))
                {
                    // ★ 递归扫描：兼容 Roles\ 根（离线）和 Roles\Microsoft\（正版）
                    foreach (var f in Directory.GetFiles(_roleDir, "*.json",
                                                          SearchOption.AllDirectories))
                    {
                        try
                        {
                            string json = File.ReadAllText(f);
                            var role = new JavaScriptSerializer().Deserialize<Role>(json);
                            if (role != null && !string.IsNullOrEmpty(role.Uuid))
                            {
                                if (string.IsNullOrEmpty(role.Type))
                                {
                                    // 兜底：JSON 里有 accessToken/refreshToken 字段 → 正版
                                    if (json.IndexOf("\"accessToken\"",
                                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        json.IndexOf("\"refreshToken\"",
                                            StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        role.Type = "Microsoft";
                                    }
                                    else
                                    {
                                        role.Type = "Offline";
                                    }
                                }
                                _roles.Add(role);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            _roles.Sort((a, b) =>
                string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase));

            if (_current == null || !_roles.Any(r => r.Uuid == _current.Uuid))
            {
                _current = _roles.FirstOrDefault();
                RaiseChanged();
            }
        }

        public static bool HasAnyRole()
        {
            return _roles.Count > 0;
        }

        public static Role CreateOffline(string username)
        {
            string uuid = GenerateOfflineUuid(username);

            var existing = _roles.FirstOrDefault(r =>
                string.Equals(r.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SetCurrent(existing);
                return existing;
            }

            var role = new Role { Username = username, Uuid = uuid, Type = "Offline" };
            string path = Path.Combine(_roleDir, uuid + ".json");
            File.WriteAllText(path,
                new JavaScriptSerializer().Serialize(role), Encoding.UTF8);

            _roles.Add(role);
            SetCurrent(role);
            return role;
        }

        public static void SetCurrent(Role role)
        {
            if (role == null) return;
            if (_current != null && _current.Uuid == role.Uuid) return;
            _current = role;
            RaiseChanged();
        }

        public static bool IsValidUsername(string username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            if (username.Length < 4 || username.Length > 16) return false;

            foreach (char c in username)
            {
                if (c == ' ') return false;
                if (c >= 0x4E00 && c <= 0x9FFF) return false;
            }
            return true;
        }

        public static string GenerateOfflineUuid(string username)
        {
            if (username == null) throw new ArgumentNullException("username");
            if (username.Length == 0) throw new ArgumentException("用户名不能为空");

            string input = "OfflinePlayer:" + username;
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);

            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(inputBytes);
                hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
                hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
                string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return string.Format("{0}-{1}-{2}-{3}-{4}",
                    hex.Substring(0, 8),
                    hex.Substring(8, 4),
                    hex.Substring(12, 4),
                    hex.Substring(16, 4),
                    hex.Substring(20, 12));
            }
        }

        private static void RaiseChanged()
        {
            var h = CurrentRoleChanged;
            if (h != null) h();
        }
    }
}