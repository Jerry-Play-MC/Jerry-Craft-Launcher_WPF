using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Launch_Bedrock
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            LaunchOptions options;
            string error;
            if (!CommandLine.TryParse(args, out options, out error))
            {
                if (error == "help")
                {
                    Console.WriteLine(CommandLine.Usage());
                    return 0;
                }
                Console.Error.WriteLine("参数错误：" + error);
                Console.Error.WriteLine(CommandLine.Usage());
                return 2;
            }

            var verbose = options.Verbose;
            Action<string> info = msg => Console.WriteLine("[Launcher] " + msg);
            Action<string> trace = msg => { if (verbose) Console.WriteLine("[Launcher][v] " + msg); };

            try
            {
                trace("启动器架构：" + (Environment.Is64BitProcess ? "x64" : "x86"));
                return Run(options, info, trace);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Launcher] 启动失败：" + ex.Message);
                if (verbose) Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static int Run(LaunchOptions options, Action<string> info, Action<string> trace)
        {
            var gameDir = Path.GetFullPath(options.GameDirectory);
            var executable = Path.Combine(gameDir, "Minecraft.Windows.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("找不到 Minecraft.Windows.exe", executable);

            // 1) 把 Bootstrap / Preload 部署到实例根目录
            var bootstrapTarget = Path.Combine(gameDir, PortalPaths.BootstrapDeployedName);
            var preloadTarget = Path.Combine(gameDir, PortalPaths.PreloadDeployedName);

            DeployFile(PortalPaths.BootstrapSource, bootstrapTarget, info);
            if (File.Exists(PortalPaths.PreloadSource))
                DeployFile(PortalPaths.PreloadSource, preloadTarget, info);
            else
                info("警告：Native\\Preload.dll 不存在，Bootstrap 会跳过 Preload 加载。");

            // 2) 以挂起状态启动
            info("启动：" + executable);
            using (var suspended = SuspendedProcess.Start(executable, options.Arguments, gameDir, false))
            {
                trace("已挂起，PID=" + suspended.ProcessId);

                // 3) 注入 Bootstrap
                info("注入 Bootstrap.dll");
                RemoteInjector.Inject(suspended.ProcessId, bootstrapTarget);
                trace("注入完成");

                // 4) 恢复主线程
                info("恢复主线程");
                suspended.Resume();

                // 5) 可选：等待
                if (options.Wait)
                {
                    info("等待游戏退出...");
                    WaitForExit(suspended.ProcessId);
                }
            }

            info("完成。");
            return 0;
        }

        private static void DeployFile(string source, string target, Action<string> info)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("缺少 Native 依赖：" + source, source);

            var srcInfo = new FileInfo(source);
            if (File.Exists(target))
            {
                var dstInfo = new FileInfo(target);
                if (dstInfo.Length == srcInfo.Length &&
                    dstInfo.LastWriteTimeUtc == srcInfo.LastWriteTimeUtc)
                {
                    return;
                }
            }

            try
            {
                File.Copy(source, target, true);
                info("已部署：" + Path.GetFileName(target));
            }
            catch (IOException ex)
            {
                throw new IOException(
                    "无法覆盖 " + target + "，可能仍有 Minecraft 进程在运行：" + ex.Message, ex);
            }
        }

        private static void WaitForExit(uint pid)
        {
            try
            {
                var process = Process.GetProcessById((int)pid);
                process.WaitForExit();
            }
            catch (ArgumentException)
            {
                // 已退出
            }
        }
    }
}