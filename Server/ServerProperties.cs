using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    /// <summary>
    /// server.properties 读写工具：保留注释与原顺序，只改目标行。
    /// 与 Java Properties 的 escape/unescape 保持一致，
    /// 避免 \: \= \! \# 等转义字符直接暴露给用户。
    /// </summary>
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
                        return Unescape(line.Substring(eq + 1).Trim());
                }
            }
            catch { }
            return "";
        }

        public static void WriteProperty(string path, string key, string value)
        {
            var lines = new List<string>();
            bool found = false;

            string escaped = Escape(value ?? "");

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
                                    lines.Add(key + "=" + escaped);
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

            if (!found) lines.Add(key + "=" + escaped);
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }

        // ============================================================
        //       Java Properties 风格 escape / unescape
        // ============================================================

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char n = s[i + 1];
                switch (n)
                {
                    case 't': sb.Append('\t'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 'f': sb.Append('\f'); i++; break;
                    case 'u':
                        if (i + 5 < s.Length)
                        {
                            string hex = s.Substring(i + 2, 4);
                            ushort code;
                            if (ushort.TryParse(hex, NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 5;
                                break;
                            }
                        }
                        // 不是合法 \uXXXX，原样输出
                        sb.Append(c);
                        break;
                    default:
                        // \: \= \! \# \\ \ 空格 等 —— Java 会去掉反斜杠
                        sb.Append(n);
                        i++;
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\f': sb.Append("\\f"); break;
                    case ':':
                    case '=':
                    case '#':
                    case '!':
                        sb.Append('\\').Append(c);
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}