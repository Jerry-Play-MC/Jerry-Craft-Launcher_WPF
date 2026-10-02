using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Updater
{
    public partial class UpdateWindow : Window
    {
        private readonly UpdateInfo _info;
        private CancellationTokenSource _cts;
        private bool _updating;

        public UpdateWindow(UpdateInfo info)
        {
            InitializeComponent();
            _info = info;

            VersionText.Text = "当前版本：" + UpdateService.CurrentVersion
                + "  →  最新版本：" + info.version;

            NotesText.Text = string.IsNullOrEmpty(info.notes)
                ? "（无更新说明）" : info.notes;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_updating) DragMove();
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            _updating = true;

            UpdateButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;

            _cts = new CancellationTokenSource();

            try
            {
                StatusText.Text = "正在下载更新包...";

                var progress = new Progress<double>(p =>
                {
                    Progress.Value = p * 100;
                    StatusText.Text = "正在下载更新包：" + (int)(p * 100) + "%";
                });

                string zipPath = await UpdateService.DownloadAsync(
                    _info, progress, _cts.Token);

                StatusText.Text = "下载完成，正在准备应用更新...";
                Progress.Value = 100;

                string targetDir = AppDomain.CurrentDomain.BaseDirectory;
                UpdateService.ApplyUpdateAndRestart(zipPath, targetDir);

                StatusText.Text = "即将重启应用...";

                await Task.Delay(500);

                Application.Current.Shutdown();
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "已取消";
                ResetButtons();
            }
            catch (Exception ex)
            {
                StatusText.Text = "更新失败：" + ex.Message;
                MessageBox.Show("更新失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ResetButtons();
            }
        }

        private void ResetButtons()
        {
            _updating = false;
            UpdateButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Progress.Value = 0;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_updating)
            {
                if (_cts != null) _cts.Cancel();
                return;
            }
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            Close();
        }
    }
}