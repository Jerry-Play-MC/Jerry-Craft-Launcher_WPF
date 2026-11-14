using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
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

        private void SubNav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            if (MinecraftPanel == null || PlaceholderPanel == null) return;

            string tag = rb.Tag as string;

            MinecraftPanel.Visibility = Visibility.Collapsed;
            ModPanel.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            if (ClientInstallPanel != null)
                ClientInstallPanel.Visibility = Visibility.Collapsed;

            switch (tag)
            {
                case "Minecraft":
                    MinecraftPanel.Visibility = Visibility.Visible;
                    break;
                case "Mod":
                    ModPanel.Visibility = Visibility.Visible;
                    break;
                default:
                    PlaceholderPanel.Visibility = Visibility.Visible;
                    PlaceholderText.Text = rb.Content as string;
                    break;
            }
        }

        private void RefreshManifest_Click(object sender, RoutedEventArgs e)
        {
            RefreshManifest();
        }

        private void RefreshManifest()
        {
            LoadingText.Text = LanguageManager.Get("Download.Loading");
            ManifestVersionList.ItemsSource = null;

            try
            {
                var list = VersionManifestScanner.Fetch();
                _allVersions = list ?? new List<ManifestVersion>();

                if (string.IsNullOrEmpty(_currentCategory))
                    _currentCategory = "Release";

                LoadingText.Text = string.Format(
                    LanguageManager.Get("Download.Count"),
                    _allVersions.Count);
                ApplyCategoryFilter();
            }
            catch (Exception ex)
            {
                LoadingText.Text = LanguageManager.Get("Download.Failure");
                LanguageManager.ShowError("Download.FetchFailed", ex.Message);
            }
        }

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

        // ★ 点击版本条目 -> 显示安装页(不再弹新窗口)
        private void ManifestVersionList_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            var item = ManifestVersionList.SelectedItem as ManifestVersion;
            if (item == null) return;

            ManifestVersionList.SelectedItem = null;

            ShowInstallView(item.Id);
        }

        private void ShowInstallView(string gameVersion)
        {
            if (ClientInstallViewControl == null) return;

            MinecraftPanel.Visibility = Visibility.Collapsed;
            ModPanel.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ClientInstallPanel.Visibility = Visibility.Visible;

            ClientInstallViewControl.SetGameVersion(gameVersion);

            SetSubNavEnabled(false);
        }

        private void BackToVersionList()
        {
            ClientInstallPanel.Visibility = Visibility.Collapsed;
            MinecraftPanel.Visibility = Visibility.Visible;

            SetSubNavEnabled(true);
        }

        private void SetSubNavEnabled(bool enabled)
        {
            if (SubNavMinecraft != null) SubNavMinecraft.IsEnabled = enabled;
            if (SubNavMod != null) SubNavMod.IsEnabled = enabled;
        }

        // ===================== ClientInstallView 事件 =====================
        private void ClientInstallView_BackRequested(object sender, EventArgs e)
        {
            BackToVersionList();
        }

        private void ClientInstallView_InstallStarted(object sender, EventArgs e)
        {
            // 安装中禁用 SubNav,防止用户切走后日志消失
            SetSubNavEnabled(false);
        }

        private void ClientInstallView_InstallCompleted(object sender, EventArgs e)
        {
            // 安装结束(成功或失败都切回来,成功的用户看到日志后点 OK 就能回来)
            // 若失败且用户没点返回,允许用户继续留在安装页看日志。
            // 这里的策略:安装结束后立刻启用 SubNav,但**不自动切回**。
            // 用户点"返回"或 SubNav 中的任意项都可以离开。
            SetSubNavEnabled(true);

            // 若想实现"成功后自动切回",把下面这行注释打开:
            // if (ClientInstallViewControl != null && !ClientInstallViewControl.IsInstalling)
            //     BackToVersionList();
        }
    }
}