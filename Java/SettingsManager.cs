using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

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
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dir = Path.Combine(baseDir, "Launcher Setting");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

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
            try
            {
                if (File.Exists(_path))
                {
                    string json = File.ReadAllText(_path, Encoding.UTF8);
                    var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var parsed = ser.Deserialize<Dictionary<string, object>>(json);
                    if (parsed != null) _root = parsed;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = ser.Serialize(_root);
                File.WriteAllText(_path, json, new UTF8Encoding(false));
            }
            catch { }
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
            sec[key] = value;
            Save();
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            var h = SettingsChanged;
            if (h != null) h();
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