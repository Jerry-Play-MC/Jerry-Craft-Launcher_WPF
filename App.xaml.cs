using System;
using System.Linq;
using System.Windows;
using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Updater;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class App : Application
    {
        public static AppConfig Config { get; } = new AppConfig();

        protected override void OnStartup(StartupEventArgs e)
        {
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

            // 6. ★ 后台刷新过期令牌
            _ = RefreshExpiredTokensAsync();

            // 7. 首次启动提示创建角色
            if (!RoleManager.HasAnyRole())
                PromptCreateRole(main, isFirstUse: true);
        }

        private static void TryLoadWebPDll()
        {
            string root = typeof(App).Namespace;
            try
            {
                NativeLibraryLoader.LoadEmbeddedDll(
                    root + ".Java.libwebp_x86.dll", "libwebp_x86.dll");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WebP] x86 加载失败：" + ex.Message);
            }
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
    }
}