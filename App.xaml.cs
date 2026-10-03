using System;
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
            // 测试用：模拟系统语言，测完注释掉
            // System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("ar-SA");

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

            // 6. 首次启动提示创建角色
            if (!RoleManager.HasAnyRole())
                PromptCreateRole(main, isFirstUse: true);
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