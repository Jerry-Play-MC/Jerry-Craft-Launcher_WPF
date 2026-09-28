using System;
using System.IO;
using System.Net;
using System.Threading;

namespace Install_Minecraft_Versions
{
    internal class Program
    {
        static void Main(string[] args)
        {
            int exitCode = 0;

            try
            {
                // UTF8 编码，容错：某些环境（无控制台/重定向）会抛异常
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

                // 强制启用 TLS 1.2（.NET Framework 4.5 默认不含）
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

                // 网络优化
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;

                // 启动退出监听线程
                StartExitListener();

                // ---- 参数检查 ----
                if (args == null || args.Length < 5)
                {
                    throw new ArgumentException(
                        $"参数数量错误：需要 5 个参数（类型、加载器、版本、路径、manifest 目录），" +
                        $"实际 {(args == null ? 0 : args.Length)} 个");
                }

                string MinecraftType = args[0];         // client / server
                string LoaderType = args[1];            // Vanilla / Forge[xxx] / NeoForge[xxx] / Fabric[xxx] / Quilt[xxx]
                string Version = args[2];               // 如 1.21.11
                string minecraftPath = args[3];         // .minecraft 路径
                string LoaderVersionList = args[4];     // manifest 目录，或 "none"

                // ★ 从 LoaderType 里解析 [版本号]，例如 "Quilt[0.30.0]" → "0.30.0"
                string loaderVersionFromType = null;
                int lb = LoaderType.IndexOf('[');
                int rb = LoaderType.IndexOf(']');
                if (lb >= 0 && rb > lb)
                    loaderVersionFromType = LoaderType.Substring(lb + 1, rb - lb - 1).Trim();

                // ★ 合并：LoaderType 里的版本优先，否则用 args[4]
                //   注意 Vanilla 不用这个（它需要的是 manifest 目录）
                string effectiveLoaderParam = !string.IsNullOrEmpty(loaderVersionFromType)
                    ? loaderVersionFromType
                    : LoaderVersionList;

                // ★ 调试输出（确认参数解析正确）
                Console.WriteLine($"[参数] 类型={MinecraftType} | 加载器={LoaderType} | " +
                                  $"版本={Version} | 路径={minecraftPath} | " +
                                  $"manifest={LoaderVersionList}");
                Console.WriteLine($"[参数] 解析出的加载器版本={loaderVersionFromType ?? "N/A"} | " +
                                  $"最终传参={effectiveLoaderParam}");

                // ---- 先装原版 ----
                if (MinecraftType == "client")
                    Vanilla.InstallClient(Version, minecraftPath, LoaderVersionList);
                else if (MinecraftType == "server")
                    Vanilla.InstallServer(Version, minecraftPath, LoaderVersionList);
                else
                    throw new ArgumentException($"未知的游戏类型：{MinecraftType}");

                // ---- 再装加载器 ----
                // 用 StartsWith，避免 "NeoForge" 被误判为 "Forge"
                bool isNeoForge = LoaderType.StartsWith("NeoForge", StringComparison.OrdinalIgnoreCase);
                bool isForge = !isNeoForge &&
                               LoaderType.StartsWith("Forge", StringComparison.OrdinalIgnoreCase);
                bool isFabric = LoaderType.StartsWith("Fabric", StringComparison.OrdinalIgnoreCase);
                bool isQuilt = LoaderType.StartsWith("Quilt", StringComparison.OrdinalIgnoreCase);
                bool isVanilla = LoaderType.StartsWith("Vanilla", StringComparison.OrdinalIgnoreCase);

                // Vanilla 什么都不用再装（上面 InstallClient 已经装过了）
                if (isVanilla)
                {
                    // 什么都不做
                }
                else if (MinecraftType == "client")
                {
                    if (isForge)
                        Forge.InstallClient(Version, minecraftPath, effectiveLoaderParam);
                    else if (isNeoForge)
                        NeoForge.InstallClient(Version, minecraftPath, effectiveLoaderParam);
                    else if (isFabric)
                        Fabric.InstallClient(Version, minecraftPath, effectiveLoaderParam);
                    else if (isQuilt)
                        Quilt.InstallClient(Version, minecraftPath, effectiveLoaderParam);
                    else
                        throw new ArgumentException($"未知的加载器类型：{LoaderType}");
                }
                else if (MinecraftType == "server")
                {
                    if (isForge)
                        Forge.InstallServer(Version, minecraftPath, effectiveLoaderParam);
                    else if (isNeoForge)
                        NeoForge.InstallServer(Version, minecraftPath, effectiveLoaderParam);
                    else if (isFabric)
                        Fabric.InstallServer(Version, minecraftPath, effectiveLoaderParam);
                    else if (isQuilt)
                        Quilt.InstallServer(Version, minecraftPath, effectiveLoaderParam);
                    else
                        throw new ArgumentException($"未知的加载器类型：{LoaderType}");
                }

                Console.WriteLine("[完成] 全部安装完成");
            }
            catch (Exception ex)
            {
                // ★ 自己打印异常，不让 CLR 去调 Exception.ToString()
                try
                {
                    Console.Error.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}");
                }
                catch { /* 连 Error 都写不出去就算了 */ }

                exitCode = 1;
            }

            // ★ 主动退出，避免后台线程被 CLR 强杀触发 ThreadAbortException
            Environment.Exit(exitCode);
        }

        /// <summary>
        /// 后台线程监听控制台输入，输入 exit 或 close（不区分大小写）时退出程序。
        /// 宿主程序通过标准输入写入也算。
        /// </summary>
        private static void StartExitListener()
        {
            Thread t = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        string line = Console.ReadLine();
                        if (line == null) break;   // 输入流关闭

                        string cmd = line.Trim();
                        if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                            cmd.Equals("close", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                Console.WriteLine($"[退出] 收到指令：{cmd}，正在退出...");
                            }
                            catch { }

                            Environment.Exit(0);
                        }
                    }
                }
                catch
                {
                    // 无控制台或读取失败，直接结束监听线程
                }
            });
            t.IsBackground = true;
            t.Name = "ExitListener";
            t.Start();
        }
    }
}