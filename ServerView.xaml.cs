using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class ServerView : UserControl
    {
        private ServerScanner _scanner;
        private bool _starting;

        public ServerView()
        {
            InitializeComponent();
            Loaded += ServerView_Loaded;
            Unloaded += ServerView_Unloaded;
        }

        private void ServerView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_scanner == null)
            {
                _scanner = new ServerScanner();
                _scanner.ServersChanged += OnServersChanged;
                _scanner.Start();
            }
        }

        private void ServerView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_scanner != null)
            {
                _scanner.Stop();
                _scanner = null;
            }
        }

        private void OnServersChanged(List<ServerInfo> servers)
        {
            Dispatcher.Invoke(() =>
            {
                ServerListBox.ItemsSource = null;
                ServerListBox.ItemsSource = servers;

                EmptyHint.Visibility = servers.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            });
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _scanner?.Stop();
            _scanner?.Start();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Server");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }

        // ---------- 双击启动 ----------

        private void ServerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var info = ServerListBox.SelectedItem as ServerInfo;
            if (info == null) return;
            _ = StartServerAsync(info);
        }

        private void ServerStart_Click(object sender, RoutedEventArgs e)
        {
            var info = GetContextMenuServer(sender);
            if (info != null) _ = StartServerAsync(info);
        }

        private void ServerSettings_Click(object sender, RoutedEventArgs e)
        {
            var info = GetContextMenuServer(sender);
            if (info == null) return;

            var win = new ServerSettingsWindow(info)
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();

            // 设置改完了，立即刷新一次（尤其是 Name / Motd 会变）
            _scanner?.Stop();
            _scanner?.Start();
        }

        private void ServerOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var info = GetContextMenuServer(sender);
            if (info == null) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = info.FolderPath,
                UseShellExecute = true
            });
        }

        private ServerInfo GetContextMenuServer(object sender)
        {
            var mi = sender as MenuItem;
            if (mi == null) return null;

            var cm = mi.Parent as ContextMenu;
            if (cm == null) return null;

            var grid = cm.PlacementTarget as FrameworkElement;
            if (grid == null) return null;

            return grid.DataContext as ServerInfo;
        }

        // ---------- 启动逻辑 ----------

        private async Task StartServerAsync(ServerInfo info)
        {
            if (_starting)
            {
                MessageBox.Show("已有服务端正在启动中，请稍候...", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _starting = true;
            try
            {
                // 1) 启动前：eula.txt 已存在且 eula=false
                string eulaPath;
                if (Launch_Minecraft.ServerLauncher.NeedsAcceptEula(info.FolderPath, out eulaPath))
                {
                    if (!AskAcceptEula(info.Name)) return;
                    Launch_Minecraft.ServerLauncher.AcceptEula(info.FolderPath);
                }

                // 2) 后台启动
                int result = await Task.Run(() =>
                    Launch_Minecraft.ServerLauncher.StartServer(
                        info.FolderPath, App.Config.JavaBaseDir, null));

                // 3) 服务端秒退，且原因疑似 eula 未同意
                if (result == -1)
                {
                    if (!AskAcceptEula(info.Name)) return;
                    Launch_Minecraft.ServerLauncher.AcceptEula(info.FolderPath);

                    await Task.Run(() =>
                        Launch_Minecraft.ServerLauncher.StartServer(
                            info.FolderPath, App.Config.JavaBaseDir, null));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动服务器失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _starting = false;
            }
        }

        private bool AskAcceptEula(string serverName)
        {
            string msg =
                "服务器【" + serverName + "】需要你先同意 Minecraft EULA 才能启动。\n\n" +
                "EULA 官方地址：\n" +
                "https://aka.ms/MinecraftEULA\n\n" +
                "点击「是」表示你已阅读并同意 EULA，\n" +
                "启动器会自动把 eula.txt 中的 eula 改为 true。";

            var r = MessageBox.Show(msg, "同意 Minecraft EULA",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            return r == MessageBoxResult.Yes;
        }
    }
}