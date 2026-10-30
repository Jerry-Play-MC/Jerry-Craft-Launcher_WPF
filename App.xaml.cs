using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Updater;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using static Jerry_Craft_Launcher.NET_Framework_4._5_WPF.App.AppLog;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class App : Application
    {
        public static AppConfig Config { get; } = new AppConfig();

        // --------------------------------  常量区  --------------------------------

        // =====程序版本信息=====
        public static readonly string Version = "1.1.0"; // 主程序版本号
        // =====目录路径=====
        public static readonly string exePath = AppDomain.CurrentDomain.BaseDirectory; // 程序根目录
        public static readonly string settingPath = Path.Combine(exePath, "Launcher Setting"); // 设置目录
        public static readonly string logPath = Path.Combine(settingPath, "Logs"); // 日志目录
        public static readonly string rolePath = Path.Combine(settingPath, "Roles"); // 角色目录
        public static readonly string javaPath = Path.Combine(settingPath, "Java"); // Java 运行时目录
        // -------------------------------------------------------------------------

        /// <summary>
        /// 程序运行时方法
        /// </summary>
        /// <param name="e"></param>
        protected override void OnStartup(StartupEventArgs e)
        {


            // 创建并占用日志文件，防止多个实例同时写入或外部移动、修改
            CreateLogFile(logPath);
            recordLog(LogLevel.Info, $"Jerry Craft Launcher {Version} 启动！");

            base.OnStartup(e);

            // 1. 原生库
            TryLoadWebPDll();

            // 2. 角色 / 设置
            RoleManager.Initialize();
            SettingsManager.Initialize();

            // 3. 语言和字体必须在创建任何窗口之前初始化
            LanguageManager.Initialize();
            FontManager.Initialize();

            // 4. 主窗口
            var main = new MainWindow();
            MainWindow = main;
            main.Show();

            // 5. 后台静默检查更新
            _ = CheckUpdateAtStartupAsync(main);

            // 6. 后台刷新过期令牌
            _ = RefreshExpiredTokensAsync();

            // 7. 首次启动提示创建角色
            if (!RoleManager.HasAnyRole())
                PromptCreateRole(main, isFirstUse: true);
        }

        /// <summary>
        /// 程序退出时方法
        /// </summary>
        /// <param name="e"></param>
        protected override void OnExit(ExitEventArgs e)
        {
            recordLog(LogLevel.Info, $"程序退出！");

            // 关闭日志句柄：Flush 残余 → Dispose → 用户才能删日志文件
            AppLog.CloseLogFile();

            base.OnExit(e);
        }

        private static void TryLoadWebPDll()
        {
            recordLog(LogLevel.Info, "开始加载运行库");
            string root = typeof(App).Namespace;
            try
            {
                recordLog(LogLevel.Info, "加载libwebp_x86.dll");
                NativeLibraryLoader.LoadEmbeddedDll(
                    root + ".Java.libwebp_x86.dll", "libwebp_x86.dll");
            }
            catch (Exception ex)
            {
                recordLog(LogLevel.Error, "[libwebp_x86.dll] x86 加载失败：" + ex.Message);
            }
            recordLog(LogLevel.Info, "加载运行库完成");
        }

        /// <summary>
        /// 启动后延迟检查更新。失败静默，不打扰用户。
        /// </summary>
        private async System.Threading.Tasks.Task CheckUpdateAtStartupAsync(Window owner)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(1500);

                var result = await UpdateService.CheckAsync();

                if (!result.Success || !result.HasUpdate) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    var win = new UpdateWindow(result.Info)
                    {
                        Owner = owner
                    };
                    win.ShowDialog();
                });
            }
            catch
            {
                // 断网 / GitHub 不可达等情况静默忽略
            }
        }

        /// <summary>
        /// 启动后延迟刷新过期的微软账号令牌。全部在后台线程执行，不阻塞 UI。
        /// 单个账号刷新失败不抛异常，其它账号继续。刷新完主动通知 UI 更新。
        /// </summary>
        private async System.Threading.Tasks.Task RefreshExpiredTokensAsync()
        {
            try
            {
                // 延迟一点，让主界面先渲染出来，刷新流程悄悄跑
                await System.Threading.Tasks.Task.Delay(2000);

                await System.Threading.Tasks.Task.Run(() =>
                {
                    // ★ 用快照避免和 UI 线程并发遍历
                    var snapshot = RoleManager.Roles.ToList();
                    foreach (var role in snapshot)
                    {
                        try
                        {
                            if (role.Type != "Microsoft") continue;
                            if (string.IsNullOrEmpty(role.AccessToken)) continue;

                            // 还有 5 分钟以上才过期 → 不刷
                            if (!MinecraftTokenHelper.IsExpired(role.AccessToken, 300))
                                continue;

                            RefreshRoleToken(role);
                        }
                        catch
                        {
                            // 单个账号失败（网络/refresh_token 被撤销）静默跳过
                        }
                    }
                });

                // 通知 UI 刷新头像 / 名字（如果被刷的正好是当前角色）
                await Dispatcher.InvokeAsync(() =>
                {
                    RoleManager.NotifyCurrentChanged();
                });
            }
            catch
            {
                // 整体静默
            }
        }

        /// <summary>
        /// 用 refresh_token 把一个已过期的微软账号刷成新的。
        /// 同步方法，调用方负责放到后台线程执行。
        /// 成功返回 true，并把新令牌写回内存对象 + 磁盘。
        /// 失败返回 false，账号文件保持原样（不删）。
        /// </summary>
        public static bool RefreshRoleToken(Role role)
        {
            if (role == null) return false;
            if (!string.Equals(role.Type, "Microsoft", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrEmpty(role.RefreshToken))
                return false;

            var auth = new MinecraftAuthenticator();

            // 1. Azure refresh_token → 新 Azure access_token
            auth.RefreshAzureToken(
                role.RefreshToken,
                out string newAzureToken,
                out string newRefreshToken);

            // 2. 完整走一遍 XBL → XSTS → Minecraft
            var mcResult = auth.RefreshToMinecraft(newAzureToken);

            // 3. 更新内存对象
            role.AccessToken = mcResult.AccessToken;
            role.RefreshToken = newRefreshToken;
            role.Uuid = mcResult.Uuid;
            role.Username = mcResult.Username;
            role.Xuid = mcResult.Xuid;

            // 4. 写回磁盘
            RoleManager.SaveRole(role);

            return true;
        }

        public static void PromptCreateRole(Window owner, bool isFirstUse)
        {
            var sel = new SelectLoginTypeWindow { Owner = owner };
            if (sel.ShowDialog() != true) return;

            if (sel.Selected == LoginType.Offline)
            {
                var dlg = new CreateRoleWindow(isFirstUse) { Owner = owner };
                dlg.ShowDialog();
            }
            else if (sel.Selected == LoginType.Microsoft)
            {
                var dlg = new MicrosoftLoginWindow { Owner = owner };
                if (dlg.ShowDialog() == true)
                {
                    // ★ 登录成功，让账号列表立即刷新
                    RoleManager.Reload();
                }
            }
        }

        /// <summary>
        /// 全局日志写入器。OnStartup 调一次 CreateLogFile 打开文件，
        /// 之后任何地方调 WriteFile 直接追加到同一句柄。
        /// </summary>
        public static class AppLog
        {
            // ===== 状态藏在类内部，调用方完全感知不到 =====
            private static FileStream _fs;
            private static string _fsPath;
            private static readonly object _fsLock = new object();
            private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

            /// <summary>
            /// 在 OnStartup 调用一次。创建日志目录与文件，句柄常开。
            /// 重复调用是幂等的（已打开则直接返回）。
            /// 失败抛异常（UnauthorizedAccess / IOException 等），由启动流程处理。
            /// </summary>
            public static void CreateLogFile(string filePath)
            {
                System.Diagnostics.Debug.WriteLine("[AppLog] CreateLogFile 被调用: " + filePath);
                lock (_fsLock)
                {
                    if (_fs != null) { System.Diagnostics.Debug.WriteLine("[AppLog] 已打开，跳过"); return; }

                    if (!Directory.Exists(filePath))
                        Directory.CreateDirectory(filePath);

                    string fileName = "Log-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    const string ext = ".log";

                    int i = 1;
                    while (true)
                    {
                        string name = i == 1 ? fileName + ext : fileName + "-" + i + ext;
                        string path = Path.Combine(filePath, name);
                        try
                        {
                            // CreateNew 原子占坑；FileShare.Read 允许别人只读，禁写/删
                            _fs = new FileStream(path, FileMode.CreateNew,
                                                 FileAccess.Write, FileShare.Read);
                            System.Diagnostics.Debug.WriteLine("[AppLog] 文件已创建: " + path);
                            _fsPath = path;
                            return;
                        }
                        catch (IOException)
                        {
                            i++;   // 文件名被占，换下一个序号
                        }
                    }
                }
            }

            /// <summary>
            /// 追加一行日志。CreateLogFile 之前调用则静默丢弃。
            /// </summary>
            public static void WriteFile(string content)
            {
                if (string.IsNullOrEmpty(content)) return;
                byte[] bytes = Utf8NoBom.GetBytes(content);

                lock (_fsLock)
                {
                    if (_fs == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[AppLog] WriteFile 时 _fs 为 null，丢弃: " + content);
                        return;
                    }
                    _fs.Write(bytes, 0, bytes.Length);
                    _fs.Flush();   // 进 OS page cache，进程崩溃不丢
                }
            }

            /// <summary>
            /// 退出/崩溃兜底时调用。释放句柄后用户才能删文件。
            /// </summary>
            public static void CloseLogFile()
            {
                lock (_fsLock)
                {
                    if (_fs == null) return;
                    try { _fs.Flush(); _fs.Dispose(); } catch { }
                    _fs = null;
                    _fsPath = null;
                }
            }

            public enum LogLevel { Info, Warn, Error, Fatal }

            public static string Log(LogLevel level, string message)
            {
                string tag;
                switch (level)
                {
                    case LogLevel.Warn: tag = "[WARN] "; break;
                    case LogLevel.Error: tag = "[ERROR] "; break;
                    case LogLevel.Fatal: tag = "[FATAL] "; break;
                    default: tag = "[INFO] "; break;
                }

                string logMessage = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + tag + message;

                return logMessage;
            }

            /// <summary>
            /// 写入日志文件<br/>
            /// 调用：<code>App.AppLog.recordLog(LogLevel.等级, "日志内容");</code><br/>
            /// logLevel.Info(默认)正常 Warn警告 Error错误 Fatal错误（致命）
            /// </summary>
            /// <param name="level"></param>
            /// <param name="message"></param>
            public static void recordLog(LogLevel level, string message)
            {
                string logMessage = Log(level, message);
                Console.WriteLine(logMessage);
                WriteFile(logMessage + "\r\n");
            }
        }
    }
}