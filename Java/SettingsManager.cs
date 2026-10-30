using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// 启动器设置读写：Launcher Setting\Settings.json
    /// JSON 结构：{ "Game": { "IsolationGameData": false } }
    /// </summary>
    public static class SettingsManager
    {
        private static string _path;
        private static Dictionary<string, object> _root = new Dictionary<string, object>();

        public static event Action SettingsChanged;

        public static void Initialize()
        {
            recordLog(LogLevel.Info, "========== 读取启动器设置 ==========");

            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Launcher Setting");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                recordLog(LogLevel.Info, "设置目录不存在，已创建: " + dir);
            }

            _path = Path.Combine(dir, "Settings.json");
            Load();
        }

        // ---------- Game.IsolationGameData ----------

        public static bool GetIsolationGameData()
        {
            return ReadBool("Game", "IsolationGameData", false);
        }

        public static void SetIsolationGameData(bool value)
        {
            WriteBool("Game", "IsolationGameData", value);
        }

        // ---------- 通用读写 ----------

        private static void Load()
        {
            _root = new Dictionary<string, object>();

            if (!File.Exists(_path))
            {
                recordLog(LogLevel.Info, "设置文件不存在，使用默认设置: " + _path);
                return;
            }

            try
            {
                string json = File.ReadAllText(_path, Encoding.UTF8);
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var parsed = ser.Deserialize<Dictionary<string, object>>(json);

                if (parsed != null)
                {
                    _root = parsed;
                    recordLog(LogLevel.Info, "设置加载成功，共 " + _root.Count + " 个配置段: " + _path);
                }
                else
                {
                    recordLog(LogLevel.Warn, "设置文件解析结果为空，使用默认设置: " + _path);
                }
            }
            catch (Exception ex)
            {
                // 解析失败不抛，降级为默认设置，但要留痕
                recordLog(LogLevel.Error, "设置文件读取/解析失败，使用默认设置: " + ex.Message);
            }
        }

        public static void Save()
        {
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = ser.Serialize(_root);
                File.WriteAllText(_path, json, new UTF8Encoding(false));
                recordLog(LogLevel.Info, "设置已保存: " + _path);
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "设置保存失败: " + ex.Message);
            }
        }

        private static bool ReadBool(string section, string key, bool def)
        {
            var sec = JsonHelper.GetObject(_root, section);
            if (sec == null) return def;
            return JsonHelper.GetBool(sec, key, def);
        }

        private static void WriteBool(string section, string key, bool value)
        {
            var sec = JsonHelper.GetObject(_root, section);
            if (sec == null)
            {
                sec = new Dictionary<string, object>();
                _root[section] = sec;
            }

            recordLog(LogLevel.Info, "修改设置: " + section + "." + key + " = " + value);

            sec[key] = value;
            Save();
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            var h = SettingsChanged;
            if (h == null) return;

            try
            {
                h();
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "SettingsChanged 订阅者异常: " + ex.Message);
                throw;
            }
        }

        // ---------- Launcher.Language ----------

        public static string GetLanguage()
        {
            return ReadString("Launcher", "Language", null);
        }

        public static void SetLanguage(string value)
        {
            WriteString("Launcher", "Language", value);
        }

        // ---------- 通用 ----------

        private static string ReadString(string section, string key, string def)
        {
            var sec = JsonHelper.GetObject(_root, section);
            if (sec == null) return def;
            var v = JsonHelper.GetString(sec, key);
            return v ?? def;
        }

        private static void WriteString(string section, string key, string value)
        {
            var sec = JsonHelper.GetObject(_root, section);
            if (sec == null)
            {
                sec = new Dictionary<string, object>();
                _root[section] = sec;
            }

            recordLog(LogLevel.Info, "修改设置: " + section + "." + key + " = " + (value ?? "(null)"));

            sec[key] = value;
            Save();
            RaiseChanged();
        }

        public static string GetFont()
        {
            return ReadString("Launcher", "Font", "Default");
        }

        public static void SetFont(string value)
        {
            WriteString("Launcher", "Font", value);
        }
    }
}