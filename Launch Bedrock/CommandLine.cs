using System;
using System.Collections.Generic;
using System.Text;

namespace Launch_Bedrock
{
    internal sealed class LaunchOptions
    {
        public string GameDirectory;
        public string Arguments;
        public bool Wait;
        public bool Verbose;
    }

    internal static class CommandLine
    {
        public static bool TryParse(string[] args, out LaunchOptions options, out string error)
        {
            options = new LaunchOptions { Wait = false, Verbose = false };
            error = null;

            var passthrough = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--game":
                        if (i + 1 >= args.Length) { error = "--game requires a path"; return false; }
                        options.GameDirectory = args[++i];
                        break;
                    case "--wait":
                        options.Wait = true;
                        break;
                    case "-v":
                    case "--verbose":
                        options.Verbose = true;
                        break;
                    case "--":
                        for (var j = i + 1; j < args.Length; j++) passthrough.Add(args[j]);
                        i = args.Length;
                        break;
                    case "-h":
                    case "--help":
                        error = "help";
                        return false;
                    default:
                        passthrough.Add(a);
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(options.GameDirectory))
            {
                error = "missing --game <directory>";
                return false;
            }

            options.Arguments = passthrough.Count == 0 ? null : JoinArguments(passthrough);
            return true;
        }

        /// <summary>
        /// 简单但正确的命令行拼接：含空格/引号的参数加引号并转义。
        /// </summary>
        private static string JoinArguments(List<string> parts)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                var p = parts[i];
                if (p.Length == 0 || p.IndexOf(' ') >= 0 || p.IndexOf('\t') >= 0 || p.IndexOf('"') >= 0)
                {
                    sb.Append('"');
                    var backslashes = 0;
                    foreach (var c in p)
                    {
                        if (c == '\\') { backslashes++; continue; }
                        if (c == '"')
                        {
                            sb.Append('\\', backslashes * 2 + 1);
                            sb.Append('"');
                            backslashes = 0;
                            continue;
                        }
                        sb.Append('\\', backslashes);
                        backslashes = 0;
                        sb.Append(c);
                    }
                    sb.Append('\\', backslashes * 2);
                    sb.Append('"');
                }
                else
                {
                    sb.Append(p);
                }
            }
            return sb.ToString();
        }

        public static string Usage()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "Launch Bedrock",
                "",
                "  --game <dir>       Minecraft 实例根目录（必需）",
                "  --wait             启动后等待游戏退出",
                "  -v, --verbose      打印详细日志",
                "  -- <args...>       其余参数原样传给 Minecraft",
                ""
            });
        }
    }
}