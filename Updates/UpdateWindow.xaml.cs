using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
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

            VersionText.Text = LanguageManager.Get("Settings.CurrentVersion")
                + UpdateService.CurrentVersion + "  →  " + info.version;

            NotesText.Text = string.IsNullOrEmpty(info.notes) ? "—" : info.notes;
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
                StatusText.Text = LanguageManager.Get("Update.Downloading");

                var progress = new Progress<double>(p =>
                {
                    Progress.Value = p * 100;
                    StatusText.Text = string.Format(
                        LanguageManager.Get("ModVersions.Downloading"),
                        _info.version, (int)(p * 100), "", "");
                });

                string zipPath = await UpdateService.DownloadAsync(
                    _info, progress, _cts.Token);

                StatusText.Text = LanguageManager.Get("Update.Preparing");
                Progress.Value = 100;

                string targetDir = AppDomain.CurrentDomain.BaseDirectory;
                UpdateService.ApplyUpdateAndRestart(zipPath, targetDir);

                StatusText.Text = LanguageManager.Get("Update.Restarting");

                await Task.Delay(500);
                Application.Current.Shutdown();
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = LanguageManager.Get("Update.Cancelled");
                ResetButtons();
            }
            catch (Exception ex)
            {
                StatusText.Text = LanguageManager.Get("Update.Failed");
                MessageBox.Show(
                    LanguageManager.Get("Update.Failed") + "\n" + ex.Message,
                    LanguageManager.Get("Dialog.Error"),
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