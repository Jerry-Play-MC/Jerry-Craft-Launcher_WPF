using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
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

            _scanner.ServersChanged -= OnServersChanged;
            _scanner.ServersChanged += OnServersChanged;

            OnServersChanged(_scanner.Current);
        }

        private void ServerView_Unloaded(object sender, RoutedEventArgs e)
        {
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

        private void InstallServer_Click(object sender, RoutedEventArgs e)
        {
            var win = new ServerInstallWindow
            {
                Owner = Window.GetWindow(this)
            };

            if (win.ShowDialog() == true)
            {
                _scanner?.Stop();
                _scanner?.Start();
            }
        }

        private ServerInfo GetServerFromButton(object sender)
        {
            var btn = sender as Button;
            if (btn == null) return null;
            return btn.DataContext as ServerInfo;
        }

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
                LanguageManager.ShowInfo("Server.Running", info.Name);
                return;
            }

            try
            {
                var status = Launch_Minecraft.ServerLauncher.CheckEula(info.FolderPath);
                if (status == Launch_Minecraft.ServerEulaStatus.NeedAccept)
                {
                    if (!AskAcceptEula(info.Name)) return;
                    Launch_Minecraft.ServerLauncher.WriteEulaTrue(info.FolderPath);
                }

                var result = await Task.Run(() =>
                    Launch_Minecraft.ServerLauncher.StartServer(
                        info.FolderPath, App.Config.JavaBaseDir,
                        progress => ReportProgress(info, progress)));

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
                LanguageManager.ShowError("Server.StartFailed", ex.Message);
            }
        }

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

        private void ServerSettings_Click(object sender, RoutedEventArgs e)
        {
            var info = GetServerFromButton(sender);
            if (info == null) return;

            var win = new ServerSettingsWindow(info)
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();

            _scanner?.Stop();
            _scanner?.Start();
        }

        private void ServerDelete_Click(object sender, RoutedEventArgs e)
        {
            var info = GetServerFromButton(sender);
            if (info == null) return;

            if (info.IsRunning)
            {
                LanguageManager.ShowWarning("Server.DeleteRunning", info.Name);
                return;
            }

            if (!LanguageManager.ConfirmWarning("Server.DeleteConfirm",
                    info.Name, info.FolderPath))
                return;

            try
            {
                Directory.Delete(info.FolderPath, true);
                _scanner?.Stop();
                _scanner?.Start();
            }
            catch (Exception ex)
            {
                LanguageManager.ShowError("Server.DeleteFailed", ex.Message);
            }
        }

        private bool AskAcceptEula(string serverName)
        {
            return LanguageManager.ConfirmWarning("Server.EulaPrompt", serverName);
        }
    }
}