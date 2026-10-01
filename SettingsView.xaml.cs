using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Updater;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class SettingsView : UserControl
    {
        private bool _suppressEvents;

        public SettingsView()
        {
            InitializeComponent();
            Loaded += SettingsView_Loaded;
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshIsolationCheck();
            RefreshVersionText();
        }

        // ---------- 子导航切换 ----------

        private void SubNav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            if (LauncherPanel == null || JavaPanel == null
                || GamePanel == null || AboutPanel == null) return;

            string tag = rb.Tag as string;

            LauncherPanel.Visibility = Visibility.Collapsed;
            JavaPanel.Visibility = Visibility.Collapsed;
            GamePanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;

            switch (tag)
            {
                case "Launcher":
                    LauncherPanel.Visibility = Visibility.Visible;
                    RefreshVersionText();
                    break;
                case "Java":
                    JavaPanel.Visibility = Visibility.Visible;
                    break;
                case "Game":
                    GamePanel.Visibility = Visibility.Visible;
                    RefreshIsolationCheck();
                    break;
                case "About":
                    AboutPanel.Visibility = Visibility.Visible;
                    break;
            }
        }

        // ---------- 版本隔离 ----------

        private void RefreshIsolationCheck()
        {
            if (IsolationCheck == null) return;

            _suppressEvents = true;
            try
            {
                IsolationCheck.IsChecked = SettingsManager.GetIsolationGameData();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void IsolationCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (IsolationCheck == null) return;

            bool value = IsolationCheck.IsChecked == true;

            SettingsManager.SetIsolationGameData(value);
            App.Config.Isolated = value;
        }

        // ---------- 启动器：版本号显示 ----------

        private void RefreshVersionText()
        {
            if (CurrentVersionText == null) return;
            CurrentVersionText.Text = "当前版本：" + UpdateService.CurrentVersion;
        }

        // ---------- 启动器：检查更新 ----------

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (CheckUpdateButton == null) return;

            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = "检查中...";
            SetUpdateStatus("正在检查更新...", "#4F46E5");

            try
            {
                var result = await UpdateService.CheckAsync();

                if (!result.Success)
                {
                    SetUpdateStatus(result.Message, "#E53935");
                    MessageBox.Show(result.Message, "检查更新",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!result.HasUpdate)
                {
                    SetUpdateStatus("已是最新版本 " + result.CurrentVersion, "#10B981");
                    MessageBox.Show(
                        "当前已是最新版本 " + result.CurrentVersion + "。",
                        "检查更新",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                SetUpdateStatus("发现新版本 " + result.LatestVersion, "#4F46E5");

                var win = new UpdateWindow(result.Info)
                {
                    Owner = Window.GetWindow(this)
                };
                win.ShowDialog();

                SetUpdateStatus("", "#4F46E5");
            }
            catch (Exception ex)
            {
                SetUpdateStatus("检查失败：" + ex.Message, "#E53935");
                MessageBox.Show("检查更新失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Content = "检查更新";
            }
        }

        private void SetUpdateStatus(string text, string colorHex)
        {
            if (UpdateStatusText == null) return;
            UpdateStatusText.Text = text;
            try
            {
                UpdateStatusText.Foreground =
                    (Brush)new BrushConverter().ConvertFromString(colorHex);
            }
            catch { }
        }
    }
}