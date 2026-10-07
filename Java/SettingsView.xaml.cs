using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Updater;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class SettingsView : UserControl
    {
        private bool _suppressEvents;

        public SettingsView()
        {
            InitializeComponent();
            Loaded += SettingsView_Loaded;
            LanguageManager.LanguageChanged += OnLanguageChanged;
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshIsolationCheck();
            RefreshVersionText();
            RefreshLanguageBox();
        }

        private void OnLanguageChanged()
        {
            Dispatcher.Invoke(() =>
            {
                RefreshVersionText();
                SetUpdateStatus("", "#4F46E5");
            });
        }

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
                    RefreshLanguageBox();
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

        private void RefreshVersionText()
        {
            if (CurrentVersionText == null) return;
            CurrentVersionText.Text = LanguageManager.Get("Settings.CurrentVersion")
                + UpdateService.CurrentVersion;
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (CheckUpdateButton == null) return;

            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = LanguageManager.Get("Settings.Checking");
            SetUpdateStatus(LanguageManager.Get("Settings.Checking"), "#4F46E5");

            try
            {
                var result = await UpdateService.CheckAsync();

                if (!result.Success)
                {
                    SetUpdateStatus(result.Message, "#E53935");
                    LanguageManager.ShowWarning("Settings.CheckFailed", result.Message);
                    return;
                }

                if (!result.HasUpdate)
                {
                    SetUpdateStatus(
                        LanguageManager.Get("Settings.UpToDate") + " "
                        + result.CurrentVersion, "#10B981");
                    LanguageManager.ShowInfo("Settings.UpToDateWithVersion",
                        result.CurrentVersion);
                    return;
                }

                SetUpdateStatus(
                    LanguageManager.Get("Settings.NewVersionFound") + " "
                    + result.LatestVersion, "#4F46E5");

                var win = new UpdateWindow(result.Info)
                {
                    Owner = Window.GetWindow(this)
                };
                win.ShowDialog();

                SetUpdateStatus("", "#4F46E5");
            }
            catch (Exception ex)
            {
                SetUpdateStatus(LanguageManager.Get("Settings.CheckFailedShort")
                    + "：" + ex.Message, "#E53935");
                LanguageManager.ShowError("Settings.CheckError", ex.Message);
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Content = LanguageManager.Get("Settings.CheckUpdate");
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

        // ---------- 语言切换 ----------

        private void RefreshLanguageBox()
        {
            if (LanguageBox == null) return;

            _suppressEvents = true;
            try
            {
                LanguageBox.Items.Clear();

                foreach (var kv in LanguageManager.Supported)
                {
                    LanguageBox.Items.Add(new ComboBoxItem
                    {
                        Content = kv.Value,   // 显示名（用母语写）
                        Tag = kv.Key          // 语言代码
                    });
                }

                // 选中当前语言
                foreach (ComboBoxItem item in LanguageBox.Items)
                {
                    if (string.Equals(item.Tag as string,
                        LanguageManager.Current,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        LanguageBox.SelectedItem = item;
                        break;
                    }
                }
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (LanguageBox == null) return;

            var item = LanguageBox.SelectedItem as ComboBoxItem;
            if (item == null) return;

            string code = item.Tag as string;
            if (string.IsNullOrEmpty(code)) return;

            LanguageManager.Switch(code);

            // 切换后立即刷新版本号文案（因为语言变了）
            RefreshVersionText();
        }
    }
}