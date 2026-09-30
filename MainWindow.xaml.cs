using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class MainWindow : Window
    {
        private bool _wasMinimized = false;
        private bool _firstShown = false;

        private const double CornerRadiusValue = 10;

        private readonly LaunchView _launchView = new LaunchView();
        private readonly DownloadView _downloadView = new DownloadView();
        private readonly ServerView _serverView = new ServerView();
        private readonly SettingsView _settingsView = new SettingsView();

        public MainWindow()
        {
            InitializeComponent();

            MainContent.Content = _launchView;

            ContentRendered += MainWindow_ContentRendered;
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RootGrid.Clip = new RectangleGeometry(
                new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight),
                CornerRadiusValue, CornerRadiusValue);
        }

        private void MainWindow_ContentRendered(object sender, EventArgs e)
        {
            if (_firstShown) return;
            _firstShown = true;

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                PlayFadeIn();
            };
            timer.Start();
        }

        private void PlayFadeIn()
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            RootGrid.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void TopNav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null || MainContent == null) return;

            switch (rb.Tag as string)
            {
                case "Launch": MainContent.Content = _launchView; break;
                case "Download": MainContent.Content = _downloadView; break;
                case "Server": MainContent.Content = _serverView; break;
                case "Settings": MainContent.Content = _settingsView; break;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            RootGrid.RenderTransformOrigin = new Point(0.5, 1);
            var scale = new ScaleTransform(1, 1);
            RootGrid.RenderTransform = scale;

            var duration = TimeSpan.FromMilliseconds(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

            var scaleX = new DoubleAnimation(1, 0.05, duration) { EasingFunction = ease };
            var scaleY = new DoubleAnimation(1, 0.05, duration) { EasingFunction = ease };
            var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };

            scaleY.Completed += (s, args) =>
            {
                RootGrid.RenderTransform = null;
                RootGrid.Opacity = 1;
                WindowState = WindowState.Minimized;
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            RootGrid.BeginAnimation(OpacityProperty, fade);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (WindowState == WindowState.Minimized)
            {
                _wasMinimized = true;
            }
            else if (WindowState == WindowState.Normal && _wasMinimized)
            {
                _wasMinimized = false;

                RootGrid.RenderTransformOrigin = new Point(0.5, 1);
                var scale = new ScaleTransform(0.05, 0.05);
                RootGrid.RenderTransform = scale;
                RootGrid.Opacity = 0;

                var duration = TimeSpan.FromMilliseconds(320);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                var scaleX = new DoubleAnimation(0.05, 1, duration) { EasingFunction = ease };
                var scaleY = new DoubleAnimation(0.05, 1, duration) { EasingFunction = ease };
                var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };

                scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
                RootGrid.BeginAnimation(OpacityProperty, fade);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (s, args) => Close();
            RootGrid.BeginAnimation(OpacityProperty, fadeOut);
        }
    }
}