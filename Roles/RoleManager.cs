using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
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

        /// <summary>
        /// 最近一次 Reload() 里因为无法解密而删除的角色文件数量。
        /// UI 层可以在 Initialize / Reload 后读这个值，提示用户重新登录。
        /// 每次 Reload 都会重置。
        /// </summary>
        public static int LastRemovedInvalidCount { get; private set; }

        public static void Initialize()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _roleDir = Path.Combine(baseDir, "Launcher Setting", "Roles");
            if (!Directory.Exists(_roleDir)) Directory.CreateDirectory(_roleDir);
            Reload();
        }

        public static void Reload()
        {
            LastRemovedInvalidCount = 0;
            _roles.Clear();

            try
            {
                if (Directory.Exists(_roleDir))
                {
                    // ★ 递归扫描：兼容 Roles\ 根（离线）和 Roles\Microsoft\（正版）
                    foreach (var f in Directory.GetFiles(_roleDir, "*.json",
                                                          SearchOption.AllDirectories))
                    {
                        string json = null;

                        try
                        {
                            // ★ DPAPI 解密读取，兼容旧明文并自动迁移
                            json = SecureStorage.ReadAllTextWithMigration(f);
                        }
                        catch
                        {
                            json = null;
                        }

                        // 解密失败 → 换机器 / 换用户 / 文件损坏 → 直接删
                        if (string.IsNullOrEmpty(json))
                        {
                            try
                            {
                                File.Delete(f);
                                LastRemovedInvalidCount++;
                            }
                            catch { }
                            continue;
                        }

                        // 解密成功，解析 JSON
                        try
                        {
                            var role = new JavaScriptSerializer().Deserialize<Role>(json);
                            if (role == null || string.IsNullOrEmpty(role.Uuid))
                                continue;

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

            // ★ DPAPI 加密写入，与正版账号保持一致
            SecureStorage.WriteAllTextEncrypted(path,
                new JavaScriptSerializer().Serialize(role));

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

        /// <summary>
        /// 把内存里的 Role 覆盖写回磁盘。会优先找已存在的同名 json，
        /// 找不到才按 Type 决定放 Roles\ 还是 Roles\Microsoft\。
        /// </summary>
        public static void SaveRole(Role role)
        {
            if (role == null || string.IsNullOrEmpty(role.Uuid)) return;
            if (string.IsNullOrEmpty(_roleDir)) Initialize();

            string path = null;

            // 1. 优先复用已存在的文件（不改变原有的存储位置）
            try
            {
                if (Directory.Exists(_roleDir))
                {
                    var hits = Directory.GetFiles(
                        _roleDir, role.Uuid + ".json",
                        SearchOption.AllDirectories);
                    if (hits.Length > 0) path = hits[0];
                }
            }
            catch { }

            // 2. 没有已存在文件 → 按 Type 新建
            if (string.IsNullOrEmpty(path))
            {
                if (string.Equals(role.Type, "Microsoft",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string msDir = Path.Combine(_roleDir, "Microsoft");
                    if (!Directory.Exists(msDir)) Directory.CreateDirectory(msDir);
                    path = Path.Combine(msDir, role.Uuid + ".json");
                }
                else
                {
                    path = Path.Combine(_roleDir, role.Uuid + ".json");
                }
            }

            SecureStorage.WriteAllTextEncrypted(path,
                new JavaScriptSerializer().Serialize(role));
        }

        /// <summary>
        /// 主动触发 CurrentRoleChanged，让订阅了该事件的 UI 刷新（比如头像/名字）。
        /// 用于令牌刷新后，Role 对象内容变了但 Uuid 没变，SetCurrent 会直接 return 的情况。
        /// </summary>
        public static void NotifyCurrentChanged()
        {
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
            if (username.Length == 0) throw new ArgumentException(LanguageManager.Get("Role.EmptyUsername"));

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

        /// <summary>清空计数。UI 层弹完对话框后调用，避免重复提示。</summary>
        public static void ClearLastRemovedInvalidCount()
        {
            LastRemovedInvalidCount = 0;
        }
    }
}