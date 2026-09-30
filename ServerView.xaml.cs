using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class ServerView : UserControl
    {
        // ★ scanner 提为 static，跨页面切换保持存活，保留 ServerInfo 的运行状态
        private static ServerScanner _scanner;

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
                _scanner.Start();
            }

            // 防止重复订阅
            _scanner.ServersChanged -= OnServersChanged;
            _scanner.ServersChanged += OnServersChanged;

            // 立即回填当前列表（切页回来不用等下一次 Tick）
            OnServersChanged(_scanner.Current);
        }

        private void ServerView_Unloaded(object sender, RoutedEventArgs e)
        {
            // ★ 不再 Stop / 不再清空 scanner：状态由 scanner 全局持有，
            //    页面再回来时复用同一批 ServerInfo 对象，IsRunning / Status 不丢。
            if (_scanner != null)
                _scanner.ServersChanged -= OnServersChanged;
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

        // ---------- 从按钮拿到绑定的 ServerInfo ----------

        private ServerInfo GetServerFromButton(object sender)
        {
            var btn = sender as Button;
            if (btn == null) return null;
            return btn.DataContext as ServerInfo;
        }

        // ---------- 启动 ----------

        private void ServerStart_Click(object sender, RoutedEventArgs e)
        {
            var info = GetServerFromButton(sender);
            if (info == null) return;
            _ = StartServerAsync(info);
        }

        private async Task StartServerAsync(ServerInfo info)
        {
            if (info.IsRunning)
            {
                MessageBox.Show($"服务器【{info.Name}】已在运行中。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // 1) 前置 eula 检查：只在「已存在 eula=false」时弹窗
                var status = Launch_Minecraft.ServerLauncher.CheckEula(info.FolderPath);
                if (status == Launch_Minecraft.ServerEulaStatus.NeedAccept)
                {
                    if (!AskAcceptEula(info.Name)) return;
                    Launch_Minecraft.ServerLauncher.WriteEulaTrue(info.FolderPath);
                }

                // 2) 启动（带进度回调）
                var result = await Task.Run(() =>
                    Launch_Minecraft.ServerLauncher.StartServer(
                        info.FolderPath, App.Config.JavaBaseDir,
                        progress => ReportProgress(info, progress)));

                // 3) 服务端秒退且报 eula 未同意 → 弹窗 + 写 eula + 重启
                if (result == Launch_Minecraft.ServerStartResult.NeedEula)
                {
                    if (!AskAcceptEula(info.Name)) return;
                    Launch_Minecraft.ServerLauncher.WriteEulaTrue(info.FolderPath);

                    await Task.Run(() =>
                        Launch_Minecraft.ServerLauncher.StartServer(
                            info.FolderPath, App.Config.JavaBaseDir,
                            progress => ReportProgress(info, progress)));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动服务器失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>把 ServerStartProgress 报告到 UI（从任意线程安全调度）</summary>
        private void ReportProgress(ServerInfo info, Launch_Minecraft.ServerStartProgress progress)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                info.Status = progress.Message;

                switch (progress.Phase)
                {
                    case Launch_Minecraft.ServerStartPhase.Detecting:
                    case Launch_Minecraft.ServerStartPhase.Starting:
                    case Launch_Minecraft.ServerStartPhase.Running:
                        info.IsRunning = true;
                        break;

                    case Launch_Minecraft.ServerStartPhase.Stopped:
                    case Launch_Minecraft.ServerStartPhase.Failed:
                        info.IsRunning = false;

                        // 3 秒后清空 Status → 恢复显示 Motd
                        var timer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(3)
                        };
                        timer.Tick += (s, e) =>
                        {
                            timer.Stop();
                            if (!info.IsRunning) info.Status = "";
                        };
                        timer.Start();
                        break;
                }
            }));
        }

        // ---------- 设置 ----------

        private void ServerSettings_Click(object sender, RoutedEventArgs e)
        {
            var info = GetServerFromButton(sender);
            if (info == null) return;

            var win = new ServerSettingsWindow(info)
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();

            // 名称/Motd 可能变了，重新扫一遍
            _scanner?.Stop();
            _scanner?.Start();
        }

        // ---------- 删除 ----------

        private void ServerDelete_Click(object sender, RoutedEventArgs e)
        {
            var info = GetServerFromButton(sender);
            if (info == null) return;

            if (info.IsRunning)
            {
                MessageBox.Show(
                    $"服务器【{info.Name}】正在运行中，请先关闭服务端后再删除。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var r = MessageBox.Show(
                $"确定要删除服务器【{info.Name}】吗？\n\n" +
                $"此操作会删除整个文件夹：\n{info.FolderPath}\n\n" +
                "删除后无法恢复！",
                "删除服务器",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (r != MessageBoxResult.Yes) return;

            try
            {
                Directory.Delete(info.FolderPath, true);
                _scanner?.Stop();
                _scanner?.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- EULA ----------

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