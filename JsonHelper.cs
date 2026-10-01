using System;
using System.Collections;
using System.Collections.Generic;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// JavaScriptSerializer 反序列化结果的访问辅助。
    /// 数字默认是 int / long / decimal，字典是 Dictionary&lt;string, object&gt;，
    /// 数组是 ArrayList。
    /// </summary>
    public static class JsonHelper
    {
        public static Dictionary<string, object> AsObject(object node)
        {
            return node as Dictionary<string, object>;
        }

        public static ArrayList AsArray(object node)
        {
            return node as ArrayList;
        }

        public static Dictionary<string, object> GetObject(object node, string key)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey(key)) return null;
            return dict[key] as Dictionary<string, object>;
        }

        public static ArrayList GetArray(object node, string key)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey(key)) return null;
            return dict[key] as ArrayList;
        }

        public static string GetString(object node, string key)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey(key)) return null;
            var v = dict[key];
            if (v == null) return null;
            return Convert.ToString(v);
        }

        public static int GetInt(object node, string key, int def = 0)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey(key)) return def;
            var v = dict[key];
            if (v == null) return def;
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is decimal) return (int)(decimal)v;
            int r;
            if (int.TryParse(Convert.ToString(v), out r)) return r;
            return def;
        }

        public static long GetLong(object node, string key, long def = 0)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey(key)) return def;
            var v = dict[key];
            if (v == null) return def;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is decimal) return (long)(decimal)v;
            long r;
            if (long.TryParse(Convert.ToString(v), out r)) return r;
            return def;
        }

        public static bool GetBool(object node, string key, bool def = false)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey(key)) return def;
            var v = dict[key];
            if (v == null) return def;
            if (v is bool) return (bool)v;
            bool r;
            if (bool.TryParse(Convert.ToString(v), out r)) return r;
            return def;
        }

        public static object GetValue(object node, string key)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey(key)) return null;
            return dict[key];
        }

        public static string GetStringFromArray(ArrayList arr)
        {
            if (arr == null || arr.Count == 0) return "";
            var parts = new List<string>();
            foreach (var item in arr)
                parts.Add(Convert.ToString(item));
            return string.Join(", ", parts.ToArray());
        }
    }
}