using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Launch_Minecraft
{
    /// <summary>
    /// 服务端启动器：
    /// · 优先运行 Forge/NeoForge 安装器生成的 run.bat（无窗口）
    /// · 否则 java -jar server.jar nogui
    /// · 处理 eula.txt 前置检查与「服务端因未同意 eula 秒退」的后置检查
    /// </summary>
    public static class ServerLauncher
    {
        /// <summary>
        /// 检测是否需要弹窗询问 EULA。
        /// 返回 true：eula.txt 存在且 eula=false。
        /// 返回 false：eula.txt 不存在，或 eula=true（直接启动）。
        /// </summary>
        public static bool NeedsAcceptEula(string serverDir, out string eulaPath)
        {
            eulaPath = Path.Combine(serverDir, "eula.txt");
            if (!File.Exists(eulaPath)) return false;

            try
            {
                string content = File.ReadAllText(eulaPath);
                var m = Regex.Match(content, @"^\s*eula\s*=\s*(true|false)\s*$",
                                    RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (!m.Success) return false;
                return m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把 eula.txt 里的 eula 改成 true；文件不存在则创建</summary>
        public static void AcceptEula(string serverDir)
        {
            string eulaPath = Path.Combine(serverDir, "eula.txt");
            var sb = new StringBuilder();
            bool found = false;

            if (File.Exists(eulaPath))
            {
                foreach (var raw in File.ReadAllLines(eulaPath))
                {
                    if (Regex.IsMatch(raw, @"^\s*eula\s*=", RegexOptions.IgnoreCase))
                    {
                        sb.AppendLine("eula=true");
                        found = true;
                    }
                    else
                    {
                        sb.AppendLine(raw);
                    }
                }
            }

            if (!found) sb.AppendLine("eula=true");
            File.WriteAllText(eulaPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// 无窗口启动服务端。
        /// 返回值：
        ///   0  = 启动成功（8 秒内未退出，视为已开始运行）
        ///  -1  = 检测到 eula 未同意
        ///   其它 = 服务端秒退的退出码
        /// </summary>
        public static int StartServer(string serverDir,
                                      string javaBaseDir = null,
                                      Action<string> onOutput = null)
        {
            string batPath = Path.Combine(serverDir, "run.bat");
            ProcessStartInfo psi;

            if (File.Exists(batPath) && IsForgeOrNeoForgeRunBat(batPath))
            {
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + batPath + "\"",
                    WorkingDirectory = serverDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.GetEncoding(936),
                    StandardErrorEncoding = Encoding.GetEncoding(936),
                };
            }
            else
            {
                string jar = Path.Combine(serverDir, "server.jar");
                if (!File.Exists(jar))
                    throw new FileNotFoundException("找不到 server.jar：" + jar);

                string java = JavaLocator.Find(17, javaBaseDir);

                psi = new ProcessStartInfo
                {
                    FileName = java,
                    Arguments = "-Xmx2048M -jar \"" + jar + "\"",
                    WorkingDirectory = serverDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.GetEncoding(936),
                    StandardErrorEncoding = Encoding.GetEncoding(936),
                };
            }

            var proc = new Process { StartInfo = psi };
            bool eulaDetected = false;

            DataReceivedEventHandler onData = (s, e) =>
            {
                if (e.Data == null) return;
                if (onOutput != null) onOutput(e.Data);

                // 服务端报错：「You need to agree to the EULA ...」
                if (e.Data.IndexOf("agree to the EULA",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Data.IndexOf("You need to agree to the EULA",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    eulaDetected = true;
                }
            };

            proc.OutputDataReceived += onData;
            proc.ErrorDataReceived += onData;

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 只等 8 秒判断是否秒退；超过则视为已正常启动
            if (proc.WaitForExit(8000))
            {
                if (eulaDetected || NeedsAcceptEula(serverDir, out _))
                    return -1;
                return proc.ExitCode;
            }

            // 未退出 → 让它在后台继续跑，不等待
            return 0;
        }

        private static bool IsForgeOrNeoForgeRunBat(string batPath)
        {
            if (string.IsNullOrEmpty(batPath) || !File.Exists(batPath))
                return false;
            try
            {
                string content = File.ReadAllText(batPath);
                bool hasUserJvm = content.IndexOf("user_jvm_args.txt",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasForgeOrNeo =
                    content.IndexOf("minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    content.IndexOf("neoforged", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    content.IndexOf("forge-", StringComparison.OrdinalIgnoreCase) >= 0;
                return hasUserJvm && hasForgeOrNeo;
            }
            catch { return false; }
        }
    }
}