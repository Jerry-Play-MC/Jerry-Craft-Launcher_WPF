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
                var r = MessageBox.Show(
                    "检测到已缓存的 Microsoft 账号凭证。\n\n" +
                    "是否尝试使用缓存直接登录（无需打开浏览器）？\n\n" +
                    "· 是 → 尝试用缓存登录\n" +
                    "· 否 → 重新进行设备码登录",
                    "缓存登录", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (r == MessageBoxResult.Yes)
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

        // ---------- 设备码回调 ----------

        private void OnUserCodeReceived(string userCode, string verificationUri)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                StatusText.Text = "请在浏览器中输入下方代码完成登录";
                UserCodeText.Text = userCode;
                UserCodePanel.Visibility = Visibility.Visible;

                try { Clipboard.SetText(userCode); } catch { }
            }));
        }

        // ---------- 常规登录 ----------

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            if (_worker != null && _worker.IsBusy) return;

            SetBusy(true, "正在准备登录，请稍候...");
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
            SetBusy(true, "正在使用缓存登录...");

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
            LoginButton.Content = busy ? "登录中..." : "开始登录";
            StatusText.Text = status;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoginCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetBusy(false, StatusText.Text);

            if (e.Result is Exception ex)
            {
                StatusText.Text = "登录失败：" + ex.Message;
                MessageBox.Show("登录失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string path = e.Result as string;
            if (string.IsNullOrEmpty(path))
            {
                StatusText.Text = "登录未返回结果";
                return;
            }

            // 判断是"仅缓存"还是"正式账号"
            if (path.IndexOf("AzureCache", StringComparison.OrdinalIgnoreCase) >= 0 &&
                Path.GetFileName(path).Equals("pending_refresh_token.cache",
                    StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Azure 凭证已缓存，待 Mojang 审核通过后可自动登录";
                MessageBox.Show(
                    "已成功缓存您的 Microsoft 账户凭证。\n\n" +
                    "待 Mojang 服务审核通过后，再次打开本窗口选择「使用缓存登录」即可，" +
                    "无需再次打开浏览器。",
                    "缓存成功", MessageBoxButton.OK, MessageBoxImage.Information);

                _loggedIn = true;
                DialogResult = true;
                Close();
                return;
            }

            _loggedIn = true;
            StatusText.Text = "登录成功";
            MessageBox.Show("登录成功！\n\n账号已保存。", "成功",
                MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }

        // ---------- 关闭 ----------

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_worker != null && _worker.IsBusy)
            {
                var r = MessageBox.Show(
                    "正在登录中，确定要退出吗？",
                    "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }

            DialogResult = _loggedIn;
            Close();
        }
    }
}