using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>server.properties 读写工具：保留注释与原顺序，只改目标行。</summary>
    public static class ServerProperties
    {
        public static string ReadProperty(string path, string key)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            try
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return "";
        }

        public static void WriteProperty(string path, string key, string value)
        {
            var lines = new List<string>();
            bool found = false;

            if (File.Exists(path))
            {
                try
                {
                    foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (!found && line.Length > 0 && line[0] != '#')
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0)
                            {
                                string k = line.Substring(0, eq).Trim();
                                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                                {
                                    lines.Add(key + "=" + value);
                                    found = true;
                                    continue;
                                }
                            }
                        }
                        lines.Add(raw);
                    }
                }
                catch { }
            }

            if (!found) lines.Add(key + "=" + value);
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }
    }
}