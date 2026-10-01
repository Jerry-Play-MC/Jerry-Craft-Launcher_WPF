using System;
using System.Windows;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class App : Application
    {
        public static AppConfig Config { get; } = new AppConfig();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ★ 只加载 x86 版 WebP 原生库
            TryLoadWebPDll();

            RoleManager.Initialize();
            SettingsManager.Initialize();

            var main = new MainWindow();
            MainWindow = main;
            main.Show();

            // ★ 后台静默检查更新，不阻塞启动流程
            _ = CheckUpdateAtStartupAsync(main);

            if (!RoleManager.HasAnyRole())
                PromptCreateRole(main, isFirstUse: true);
        }

        /// <summary>
        /// 启动后延迟一小段时间再检查更新，避免和启动动画抢占 UI 线程。
        /// 检查失败静默忽略，不打扰用户。
        /// </summary>
        private async System.Threading.Tasks.Task CheckUpdateAtStartupAsync(Window owner)
        {
            try
            {
                // 等启动动画走完
                await System.Threading.Tasks.Task.Delay(1500);

                var result = await Updater.UpdateService.CheckAsync();

                if (!result.Success || !result.HasUpdate) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    var win = new Updater.UpdateWindow(result.Info)
                    {
                        Owner = owner
                    };
                    win.ShowDialog();
                });
            }
            catch
            {
                // 静默失败：断网 / GitHub 不可达等情况不打扰用户
            }
        }

        private static void TryLoadWebPDll()
        {
            string root = typeof(App).Namespace;
            try
            {
                NativeLibraryLoader.LoadEmbeddedDll(
                    root + ".libwebp_x86.dll", "libwebp_x86.dll");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WebP] x86 加载失败：" + ex.Message);
            }
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
                dlg.ShowDialog();
            }
        }
    }
}