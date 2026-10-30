using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

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

        public static int LastRemovedInvalidCount { get; private set; }

        public static void Initialize()
        {
            recordLog(LogLevel.Info, "初始化角色管理器，角色目录: " + App.rolePath);
            _roleDir = App.rolePath;

            if (!Directory.Exists(_roleDir))
            {
                Directory.CreateDirectory(_roleDir);
                recordLog(LogLevel.Info, "角色目录不存在，已创建: " + _roleDir);
            }

            Reload();
        }

        public static void Reload()
        {
            recordLog(LogLevel.Info, "开始重新加载角色列表...");
            LastRemovedInvalidCount = 0;
            _roles.Clear();

            int parseFailed = 0;

            try
            {
                if (Directory.Exists(_roleDir))
                {
                    foreach (var f in Directory.GetFiles(_roleDir, "*.json", SearchOption.AllDirectories))
                    {
                        string json = null;

                        try
                        {
                            json = SecureStorage.ReadAllTextWithMigration(f);
                        }
                        catch (Exception ex)
                        {
                            recordLog(LogLevel.Warn, "解密角色文件失败: " + f + "，原因: " + ex.Message);
                            json = null;
                        }

                        // 解密失败 → 换机器 / 换用户 / 文件损坏 → 直接删
                        if (string.IsNullOrEmpty(json))
                        {
                            try
                            {
                                File.Delete(f);
                                LastRemovedInvalidCount++;
                                recordLog(LogLevel.Warn, "解密结果为空，已删除无效角色文件: " + f);
                            }
                            catch (Exception ex)
                            {
                                recordLog(LogLevel.Warn, "删除无效角色文件失败: " + f + "，原因: " + ex.Message);
                            }
                            continue;
                        }

                        try
                        {
                            var role = new JavaScriptSerializer().Deserialize<Role>(json);
                            if (role == null || string.IsNullOrEmpty(role.Uuid))
                            {
                                parseFailed++;
                                recordLog(LogLevel.Warn, "角色文件缺少 Uuid，已跳过: " + f);
                                continue;
                            }

                            if (string.IsNullOrEmpty(role.Type))
                            {
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
                            recordLog(LogLevel.Info, "已加载角色: " + role.Username + " [" + role.Type + "]");
                        }
                        catch (Exception ex)
                        {
                            parseFailed++;
                            recordLog(LogLevel.Warn, "解析角色文件失败: " + f + "，原因: " + ex.Message);
                        }
                    }
                }
                else
                {
                    recordLog(LogLevel.Warn, "角色目录不存在，跳过扫描: " + _roleDir);
                }
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "扫描角色目录异常: " + ex.Message);
            }

            _roles.Sort((a, b) =>
                string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase));

            if (_current == null || !_roles.Any(r => r.Uuid == _current.Uuid))
            {
                _current = _roles.FirstOrDefault();
                RaiseChanged();
            }

            recordLog(LogLevel.Info,
                "========== 角色加载完成 ==========\r\n" +
                "角色加载完成：共 " + _roles.Count + " 个，\r\n" +
                "当前角色: " + (_current != null ? _current.Username : "(无)") + "，\r\n" +
                "清理无效文件 " + LastRemovedInvalidCount + " 个，\r\n" +
                "解析失败 " + parseFailed + " 个\r\n" +
                "=================================");
        }

        public static bool HasAnyRole()
        {
            return _roles.Count > 0;
        }

        public static Role CreateOffline(string username)
        {
            recordLog(LogLevel.Info, "创建离线角色: " + username);

            string uuid = GenerateOfflineUuid(username);

            var existing = _roles.FirstOrDefault(r =>
                string.Equals(r.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                recordLog(LogLevel.Info, "离线角色已存在，切换为当前: " + username);
                SetCurrent(existing);
                return existing;
            }

            var role = new Role { Username = username, Uuid = uuid, Type = "Offline" };
            string path = Path.Combine(_roleDir, uuid + ".json");

            try
            {
                SecureStorage.WriteAllTextEncrypted(path,
                    new JavaScriptSerializer().Serialize(role));
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "保存离线角色失败: " + username + "，原因: " + ex.Message);
                throw;
            }

            _roles.Add(role);
            SetCurrent(role);

            recordLog(LogLevel.Info, "离线角色创建成功: " + username + " (" + uuid + ")");
            return role;
        }

        public static void SetCurrent(Role role)
        {
            if (role == null) return;
            if (_current != null && _current.Uuid == role.Uuid) return;

            recordLog(LogLevel.Info, "切换当前角色: " + (_current != null ? _current.Username : "(无)") + " -> " + role.Username);

            _current = role;
            RaiseChanged();
        }

        public static void SaveRole(Role role)
        {
            if (role == null || string.IsNullOrEmpty(role.Uuid))
            {
                recordLog(LogLevel.Warn, "SaveRole 收到空角色或缺少 Uuid，忽略");
                return;
            }

            if (string.IsNullOrEmpty(_roleDir)) Initialize();

            string path = null;

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
            catch (Exception ex)
            {
                recordLog(LogLevel.Warn, "查找已存在角色文件失败，将按 Type 新建: " + ex.Message);
            }

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

            try
            {
                SecureStorage.WriteAllTextEncrypted(path,
                    new JavaScriptSerializer().Serialize(role));
                recordLog(LogLevel.Info, "保存角色: " + role.Username + " -> " + path);
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "保存角色失败: " + role.Username + "，原因: " + ex.Message);
                throw;
            }
        }

        public static void NotifyCurrentChanged()
        {
            recordLog(LogLevel.Info, "主动通知当前角色刷新");
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
            if (h == null) return;

            try
            {
                h();
            }
            catch (Exception ex)
            {
                // UI 订阅者抛异常不能影响业务，但要留下痕迹
                recordLog(LogLevel.Error, "CurrentRoleChanged 订阅者异常: " + ex.Message);
                throw;
            }
        }

        public static void ClearLastRemovedInvalidCount()
        {
            LastRemovedInvalidCount = 0;
        }
    }
}