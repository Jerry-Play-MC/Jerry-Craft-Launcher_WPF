using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Launch_Minecraft;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class LaunchView : UserControl
    {
        private VersionScanner _scanner;

        public LaunchView()
        {
            InitializeComponent();
            Loaded += LaunchView_Loaded;
            Unloaded += LaunchView_Unloaded;
            RoleManager.CurrentRoleChanged += OnCurrentRoleChanged;
        }

        private void LaunchView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_scanner == null)
            {
                _scanner = new VersionScanner();
                _scanner.VersionsChanged += OnVersionsChanged;
                _scanner.Start();
            }

            UpdateVersionLabel();
            UpdateAvatar();
            RefreshRoleList();
        }

        private void LaunchView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_scanner != null)
            {
                _scanner.Stop();
                _scanner = null;
            }
        }

        private void OnVersionsChanged(List<VersionInfo> versions)
        {
            VersionList.ItemsSource = versions;

            string current = App.Config.CurrentVersion;
            bool exists = !string.IsNullOrEmpty(current)
                          && versions.Any(v => v.Name == current);

            if (!exists)
            {
                App.Config.CurrentVersion = versions.Count > 0
                    ? versions[0].Name
                    : null;
            }

            UpdateVersionLabel();
        }

        private void UpdateVersionLabel()
        {
            string v = App.Config.CurrentVersion;
            CurrentVersionText.Text = string.IsNullOrEmpty(v) ? "无版本" : v;
        }

        private void SubNav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            if (HomePanel == null || RolesPanel == null) return;

            switch (rb.Tag as string)
            {
                case "Home":
                    HomePanel.Visibility = Visibility.Visible;
                    RolesPanel.Visibility = Visibility.Collapsed;
                    break;
                case "Roles":
                    HomePanel.Visibility = Visibility.Collapsed;
                    RolesPanel.Visibility = Visibility.Visible;
                    RefreshRoleList();
                    break;
            }
        }

        private void OnCurrentRoleChanged()
        {
            Dispatcher.Invoke(UpdateAvatar);
        }

        private void UpdateAvatar()
        {
            var role = RoleManager.Current;
            if (role == null)
            {
                AvatarText.Text = "?";
                RoleNameText.Text = "未登录";
                RoleTypeText.Text = "";
                AvatarBorder.Background = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                return;
            }

            AvatarText.Text = role.Initial;
            RoleNameText.Text = role.Username;
            RoleTypeText.Text = role.Type == "Offline" ? "离线账号"
                              : role.Type == "Microsoft" ? "正版账号"
                              : role.Type;
            AvatarBorder.Background = new SolidColorBrush(GetAvatarColor(role.Username));
        }

        private static Color GetAvatarColor(string username)
        {
            if (string.IsNullOrEmpty(username))
                return Color.FromRgb(0x4F, 0x46, 0xE5);

            int hash = 0;
            foreach (char c in username) hash = hash * 31 + c;

            var colors = new[]
            {
                Color.FromRgb(0x4F, 0x46, 0xE5),
                Color.FromRgb(0xE5, 0x46, 0x7A),
                Color.FromRgb(0x10, 0xB9, 0x81),
                Color.FromRgb(0xF5, 0x9E, 0x0B),
                Color.FromRgb(0x8B, 0x5C, 0xF6),
                Color.FromRgb(0x06, 0xB6, 0xD4),
            };
            return colors[Math.Abs(hash) % colors.Length];
        }

        private void RefreshRoleList()
        {
            RoleListBox.ItemsSource = null;
            RoleListBox.ItemsSource = RoleManager.Roles.ToList();

            var current = RoleManager.Current;
            if (current != null)
            {
                var item = RoleManager.Roles.FirstOrDefault(r => r.Uuid == current.Uuid);
                if (item != null) RoleListBox.SelectedItem = item;
            }
        }

        private void RoleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var role = RoleListBox.SelectedItem as Role;
            if (role == null) return;
            RoleManager.SetCurrent(role);
        }

        private void CreateRole_Click(object sender, RoutedEventArgs e)
        {
            App.PromptCreateRole(Window.GetWindow(this), isFirstUse: false);
            RefreshRoleList();
        }

        private void VersionPickerButton_Click(object sender, RoutedEventArgs e)
        {
            VersionPopup.IsOpen = !VersionPopup.IsOpen;
        }

        private void VersionItem_Click(object sender, MouseButtonEventArgs e)
        {
            var item = (sender as ListBoxItem)?.DataContext as VersionInfo;
            if (item == null) return;

            App.Config.CurrentVersion = item.Name;
            UpdateVersionLabel();
            VersionPopup.IsOpen = false;

            e.Handled = true;
        }

        // ---------- 启动 ----------

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            string version = App.Config.CurrentVersion;
            if (string.IsNullOrEmpty(version))
            {
                MessageBox.Show(
                    "尚未选择版本。请先在 .minecraft\\versions 下安装版本。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var role = RoleManager.Current;
            if (role == null)
            {
                var r = MessageBox.Show(
                    "尚未创建角色。是否现在创建？",
                    "提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                {
                    App.PromptCreateRole(Window.GetWindow(this), isFirstUse: false);
                    RefreshRoleList();
                }
                return;
            }

            LaunchButton.IsEnabled = false;
            VersionPickerButton.IsEnabled = false;

            try
            {
                string gameDir = App.Config.GameDir;
                string javaBaseDir = App.Config.JavaBaseDir;
                bool isolated = App.Config.Isolated;

                string userType = role.Type == "Microsoft" ? "msa" : "legacy";
                string accessToken = role.AccessToken;
                if (string.IsNullOrEmpty(accessToken))
                    accessToken = "0";

                var progress = new Progress<LaunchProgress>(p =>
                {
                    Dispatcher.Invoke(() => UpdateLaunchStatus(p));
                });

                int exitCode = await Task.Run(() =>
                    Launch_Minecraft.GameLauncher.Run(
                        "client",
                        gameDir,
                        version,
                        isolated,
                        javaBaseDir,
                        role.Username,
                        role.Uuid,
                        accessToken,
                        userType,
                        p => ((IProgress<LaunchProgress>)progress).Report(p)));

                if (exitCode != 0)
                {
                    MessageBox.Show($"启动失败，退出码：{exitCode}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动出错：{ex.GetType().Name}: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LaunchButton.IsEnabled = true;
                VersionPickerButton.IsEnabled = true;
            }
        }

        // ---------- 启动进度更新 ----------

        private void UpdateLaunchStatus(LaunchProgress p)
        {
            if (p == null) return;

            CurrentVersionText.Text = p.Message;

            if (p.Phase == LaunchPhase.Stopped || p.Phase == LaunchPhase.Failed)
            {
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    UpdateVersionLabel();
                };
                timer.Start();
            }
        }
    }
}