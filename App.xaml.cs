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

            // 启动时提示被清理的账号缓存
            NotifyRemovedInvalidRoles(main);

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
                // 离线登录走 RoleManager.CreateOffline，直接操作内存列表，无需 Reload
            }
            else if (sel.Selected == LoginType.Microsoft)
            {
                var dlg = new MicrosoftLoginWindow { Owner = owner };
                dlg.ShowDialog();
                // 微软登录只写文件、不经过 RoleManager，必须重新加载
                RoleManager.Reload();

                // 登录流程里一般不会产生无效文件，但保险起见也检查一下
                NotifyRemovedInvalidRoles(owner);
            }
        }

        /// <summary>
        /// 如果 RoleManager 上次 Reload 删掉了无法解密的角色文件，
        /// 弹一次对话框提示用户。没有删除就什么都不做。
        /// </summary>
        private static void NotifyRemovedInvalidRoles(Window owner)
        {
            int n = RoleManager.LastRemovedInvalidCount;
            if (n <= 0) return;

            // 消费掉计数，避免重复弹
            RoleManager.ClearLastRemovedInvalidCount();

            MessageBox.Show(
                "检测到 " + n + " 个角色缓存文件无法在当前 Windows 用户下解密" +
                "（可能来自其他电脑或其他系统用户），已自动清理。\n\n" +
                "如果这些是你的账号，请重新登录以恢复。",
                "账号缓存已清理",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}