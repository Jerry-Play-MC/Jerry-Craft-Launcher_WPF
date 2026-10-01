using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class DownloadView : UserControl
    {
        private List<ManifestVersion> _allVersions = new List<ManifestVersion>();
        private string _currentCategory = "Release";

        public DownloadView()
        {
            InitializeComponent();
            Loaded += DownloadView_Loaded;
        }

        private void DownloadView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_allVersions.Count == 0)
                RefreshManifest();
        }

        // ---------- 子导航切换 ----------

        private void SubNav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            if (MinecraftPanel == null || PlaceholderPanel == null) return;

            switch (rb.Tag as string)
            {
                case "Minecraft":
                    MinecraftPanel.Visibility = Visibility.Visible;
                    PlaceholderPanel.Visibility = Visibility.Collapsed;
                    break;
                default:
                    MinecraftPanel.Visibility = Visibility.Collapsed;
                    PlaceholderPanel.Visibility = Visibility.Visible;
                    PlaceholderText.Text = rb.Content as string;
                    break;
            }
        }

        // ---------- 刷新清单 ----------

        private void RefreshManifest_Click(object sender, RoutedEventArgs e)
        {
            RefreshManifest();
        }

        private void RefreshManifest()
        {
            LoadingText.Text = "加载中...";
            ManifestVersionList.ItemsSource = null;

            try
            {
                var list = VersionManifestScanner.Fetch();
                _allVersions = list ?? new List<ManifestVersion>();

                if (string.IsNullOrEmpty(_currentCategory))
                    _currentCategory = "Release";

                LoadingText.Text = $"共 {_allVersions.Count} 个版本";
                ApplyCategoryFilter();
            }
            catch (Exception ex)
            {
                LoadingText.Text = "加载失败";
                MessageBox.Show("获取版本清单失败：" + ex.Message,
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- 分类切换 ----------

        private void Category_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;

            string tag = rb.Tag as string;

            if (string.IsNullOrEmpty(tag))
            {
                string content = rb.Content as string;
                switch (content)
                {
                    case "正式版": tag = "Release"; break;
                    case "快照版": tag = "Snapshot"; break;
                    case "远古版": tag = "Old"; break;
                    case "愚人节版": tag = "AprilFools"; break;
                    default: tag = "Release"; break;
                }
            }

            _currentCategory = tag;

            if (ManifestVersionList != null)
                ApplyCategoryFilter();
        }

        private void ApplyCategoryFilter()
        {
            if (_allVersions == null) return;

            List<ManifestVersion> filtered;

            switch (_currentCategory)
            {
                case "Release":
                    filtered = _allVersions
                        .Where(v => v.Category == VersionCategory.Release)
                        .ToList();
                    break;
                case "Snapshot":
                    filtered = _allVersions
                        .Where(v => v.Category == VersionCategory.Snapshot)
                        .ToList();
                    break;
                case "Old":
                    filtered = _allVersions
                        .Where(v => v.Category == VersionCategory.OldBeta ||
                                    v.Category == VersionCategory.OldAlpha)
                        .ToList();
                    break;
                case "AprilFools":
                    filtered = _allVersions
                        .Where(v => v.Category == VersionCategory.AprilFools)
                        .ToList();
                    break;
                default:
                    filtered = _allVersions;
                    break;
            }

            filtered = filtered
                .OrderByDescending(v => ParseTime(v.ReleaseTime))
                .ToList();

            ManifestVersionList.ItemsSource = filtered;
        }

        private static DateTime ParseTime(string s)
        {
            try { return DateTime.Parse(s); }
            catch { return DateTime.MinValue; }
        }

        // ---------- 选中版本 → 打开客户端安装窗口 ----------

        private void ManifestVersionList_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            var item = ManifestVersionList.SelectedItem as ManifestVersion;
            if (item == null) return;

            // 清除选中状态，避免重复触发
            ManifestVersionList.SelectedItem = null;

            var win = new ClientInstallWindow(item.Id)
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();
        }
    }
}