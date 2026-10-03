using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class MicrosoftLoginWindow : Window
    {
        private readonly MinecraftAuthenticator _auth;
        private BackgroundWorker _worker;
        private bool _loggedIn;

        public MicrosoftLoginWindow()
        {
            InitializeComponent();
            _auth = new MinecraftAuthenticator();
            _auth.OnUserCodeReceived += OnUserCodeReceived;
            Loaded += MicrosoftLoginWindow_Loaded;
        }

        private void MicrosoftLoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string cacheFile = GetCacheFilePath();
            if (File.Exists(cacheFile))
            {
                if (LanguageManager.Confirm("MSLogin.CacheDetected"))
                    TryCacheLogin(cacheFile);
            }
        }

        private static string GetCacheFilePath()
        {
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Launcher Setting", "AzureCache",
                "pending_refresh_token.cache");
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_worker == null || !_worker.IsBusy)
                DragMove();
        }

        private void OnUserCodeReceived(string userCode, string verificationUri)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                StatusText.Text = LanguageManager.Get("MSLogin.CodeHint");
                UserCodeText.Text = userCode;
                UserCodePanel.Visibility = Visibility.Visible;
                try { Clipboard.SetText(userCode); } catch { }
            }));
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            if (_worker != null && _worker.IsBusy) return;

            SetBusy(true, LanguageManager.Get("MSLogin.Preparing"));
            UserCodePanel.Visibility = Visibility.Collapsed;

            _worker = new BackgroundWorker();
            _worker.DoWork += (s, args) =>
            {
                try { args.Result = _auth.AuthenticateAndSave(); }
                catch (Exception ex) { args.Result = ex; }
            };
            _worker.RunWorkerCompleted += LoginCompleted;
            _worker.RunWorkerAsync();
        }

        private void TryCacheLogin(string cacheFile)
        {
            SetBusy(true, LanguageManager.Get("MSLogin.CacheLogin"));

            _worker = new BackgroundWorker();
            _worker.DoWork += (s, args) =>
            {
                try { args.Result = _auth.CompleteLoginFromCache(cacheFile); }
                catch (Exception ex) { args.Result = ex; }
            };
            _worker.RunWorkerCompleted += LoginCompleted;
            _worker.RunWorkerAsync();
        }

        private void SetBusy(bool busy, string status)
        {
            LoginButton.IsEnabled = !busy;
            LoginButton.Content = busy
                ? LanguageManager.Get("MSLogin.LoginBusy")
                : LanguageManager.Get("MSLogin.Login");
            StatusText.Text = status;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoginCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetBusy(false, StatusText.Text);

            if (e.Result is Exception ex)
            {
                StatusText.Text = LanguageManager.Get("MSLogin.LoginFailed");
                LanguageManager.ShowError("MSLogin.LoginFailed", ex.Message);
                return;
            }

            string path = e.Result as string;
            if (string.IsNullOrEmpty(path))
            {
                StatusText.Text = LanguageManager.Get("MSLogin.NoResult");
                return;
            }

            if (path.IndexOf("AzureCache", StringComparison.OrdinalIgnoreCase) >= 0 &&
                Path.GetFileName(path).Equals("pending_refresh_token.cache",
                    StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = LanguageManager.Get("MSLogin.CacheSaved");
                LanguageManager.ShowInfo("MSLogin.CacheSuccess");

                _loggedIn = true;
                DialogResult = true;
                Close();
                return;
            }

            _loggedIn = true;
            StatusText.Text = LanguageManager.Get("MSLogin.Success");
            LanguageManager.ShowInfo("MSLogin.LoginSuccess");

            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_worker != null && _worker.IsBusy)
            {
                if (!LanguageManager.Confirm("MSLogin.CancelConfirm")) return;
            }

            DialogResult = _loggedIn;
            Close();
        }
    }
}