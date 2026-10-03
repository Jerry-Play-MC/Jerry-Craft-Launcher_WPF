using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class ModInstallTargetWindow : Window
    {
        public LocalVersionInfo SelectedVersion { get; private set; }

        public ModInstallTargetWindow(List<LocalVersionInfo> versions, ModVersion modVersion)
        {
            InitializeComponent();

            TargetList.ItemsSource = versions;
            if (versions != null && versions.Count > 0)
                TargetList.SelectedIndex = 0;

            string gameReq = modVersion != null && modVersion.GameVersions != null
                && modVersion.GameVersions.Count > 0
                ? string.Join(" / ", modVersion.GameVersions.ToArray())
                : LanguageManager.Get("Common.Any");

            string loaderReq = modVersion != null && modVersion.Loaders != null
                && modVersion.Loaders.Count > 0
                ? string.Join(" / ", modVersion.Loaders.ToArray())
                : LanguageManager.Get("Common.Any");

            RequirementText.Text = string.Format(
                LanguageManager.Get("ModInstallTarget.Requirements"), gameReq, loaderReq);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void TargetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Install_Click(sender, e);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            var sel = TargetList.SelectedItem as LocalVersionInfo;
            if (sel == null)
            {
                LanguageManager.ShowInfo("ModInstallTarget.SelectOne");
                return;
            }

            SelectedVersion = sel;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}