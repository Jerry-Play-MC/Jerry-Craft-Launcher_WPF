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

            var main = new MainWindow();
            MainWindow = main;
            main.Show();

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