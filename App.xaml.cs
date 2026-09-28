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
            {
                var dlg = new CreateRoleWindow(true) { Owner = main };
                dlg.ShowDialog();
            }
        }
    }
}