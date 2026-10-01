using System.Windows;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class App : Application
    {
        public static AppConfig Config { get; } = new AppConfig();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            RoleManager.Initialize();

            var main = new MainWindow();
            MainWindow = main;
            main.Show();

            if (!RoleManager.HasAnyRole())
                PromptCreateRole(main, isFirstUse: true);
        }

        /// <summary>
        /// 统一的"创建角色"入口：先选登录方式，再打开对应窗口。
        /// </summary>
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