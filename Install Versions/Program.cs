using System;
using System.IO;
using System.Linq;

namespace Install_Versions
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
                return InteractiveMode();

            string command = args[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "install":
                        return HandleInstall(args);
                    case "list":
                        return HandleList(args);
                    case "launch":
                        return HandleLaunch(args);
                    case "help":
                    case "-h":
                    case "--help":
                        PrintUsage();
                        return 0;
                    default:
                        Console.Error.WriteLine("未知命令: " + command);
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        static int HandleInstall(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法: install <版本号> <安装路径> [--preview]");
                return 1;
            }

            string version = args[1];
            string targetDir = Path.GetFullPath(args[2]);
            bool includePreview = args.Any(a =>
                a.Equals("--preview", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-p", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine("版本号: " + version);
            Console.WriteLine("安装路径: " + targetDir);
            Console.WriteLine("包含预览版: " + includePreview);
            Console.WriteLine();

            if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            {
                Console.Error.WriteLine("目标目录非空: " + targetDir);
                return 1;
            }

            Console.WriteLine("正在获取版本列表...");
            var versions = BedrockInstaller.GetVersions(includePreview);
            if (versions.Count == 0)
            {
                Console.Error.WriteLine("无法获取版本列表");
                return 1;
            }

            var target = versions.FirstOrDefault(v =>
                string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                var matches = versions.Where(v =>
                    v.Version.StartsWith(version, StringComparison.OrdinalIgnoreCase)).ToList();

                if (matches.Count == 0)
                {
                    Console.Error.WriteLine("找不到版本: " + version);
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("最近 10 个可用版本:");
                    foreach (var v in versions.Take(10))
                        Console.Error.WriteLine("  " + v.Version + "  [" + v.BuildType + "]  " + v.Date);
                    return 1;
                }

                if (matches.Count > 1)
                {
                    Console.Error.WriteLine("版本前缀 '" + version + "' 匹配到多个结果:");
                    foreach (var v in matches.Take(20))
                        Console.Error.WriteLine("  " + v.Version + "  [" + v.BuildType + "]  " + v.Date);
                    return 1;
                }

                target = matches[0];
            }

            Console.WriteLine("已选中: " + target.Version + "  [" + target.BuildType + "]  " + target.Date);
            Console.WriteLine();

            var startTime = DateTime.Now;
            BedrockInstaller.Install(target, targetDir, log => Console.WriteLine(log));
            var elapsed = DateTime.Now - startTime;

            Console.WriteLine();
            Console.WriteLine("安装完成! 耗时 " + elapsed.ToString(@"mm\:ss"));
            Console.WriteLine("安装位置: " + targetDir);
            Console.WriteLine();
            Console.WriteLine("启动命令: BedrockInstaller.exe launch \"" + targetDir + "\"");

            return 0;
        }

        static int HandleList(string[] args)
        {
            bool includePreview = args.Any(a =>
                a.Equals("--preview", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-p", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine("正在获取版本列表...");
            var versions = BedrockInstaller.GetVersions(includePreview);

            Console.WriteLine("共 " + versions.Count + " 个版本:");
            Console.WriteLine();
            Console.WriteLine("{0,-20} {1,-8} {2,-10} {3}", "版本", "类型", "构建", "日期");
            Console.WriteLine(new string('-', 60));

            foreach (var v in versions)
            {
                Console.WriteLine("{0,-20} {1,-8} {2,-10} {3}",
                    v.Version, v.Type, v.BuildType, v.Date);
            }

            return 0;
        }

        static int HandleLaunch(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("用法: launch <安装路径>");
                return 1;
            }

            string targetDir = Path.GetFullPath(args[1]);

            if (!Directory.Exists(targetDir))
            {
                Console.Error.WriteLine("目录不存在: " + targetDir);
                return 1;
            }

            Console.WriteLine("启动 GDK 版: " + targetDir);
            BedrockInstaller.LaunchGDK(targetDir);
            return 0;
        }

        static int InteractiveMode()
        {
            Console.WriteLine("=== Bedrock Installer ===");
            Console.WriteLine();
            Console.WriteLine("1. 列出所有版本");
            Console.WriteLine("2. 安装指定版本");
            Console.WriteLine("3. 启动已安装版本");
            Console.WriteLine("4. 退出");
            Console.WriteLine();
            Console.Write("选择: ");

            string choice = Console.ReadLine();
            switch (choice)
            {
                case "1":
                    return HandleList(new[] { "list" });
                case "2":
                    Console.Write("版本号: ");
                    string version = Console.ReadLine();
                    Console.Write("安装路径: ");
                    string path = Console.ReadLine();
                    return HandleInstall(new[] { "install", version, path });
                case "3":
                    Console.Write("安装路径: ");
                    string launchPath = Console.ReadLine();
                    return HandleLaunch(new[] { "launch", launchPath });
                default:
                    return 0;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  BedrockInstaller.exe install <版本号> <安装路径> [--preview]");
            Console.WriteLine("  BedrockInstaller.exe list [--preview]");
            Console.WriteLine("  BedrockInstaller.exe launch <安装路径>");
            Console.WriteLine("  BedrockInstaller.exe                            (交互式菜单)");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  BedrockInstaller.exe install 1.21.120.20 \"D:\\MC\\1.21.120.20\"");
            Console.WriteLine("  BedrockInstaller.exe install 1.21.120 \"D:\\MC\\test\" -p");
            Console.WriteLine("  BedrockInstaller.exe list");
            Console.WriteLine("  BedrockInstaller.exe launch \"D:\\MC\\1.21.120.20\"");
        }
    }
}