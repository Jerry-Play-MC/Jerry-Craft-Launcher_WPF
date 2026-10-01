using System;
using System.Windows;
using System.Windows.Controls;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class SettingsView : UserControl
    {
        private bool _suppressEvents;

        public SettingsView()
        {
            InitializeComponent();
            Loaded += SettingsView_Loaded;
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshIsolationCheck();
        }

        // ---------- 子导航切换 ----------

        private void SubNav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            if (LauncherPanel == null || JavaPanel == null
                || GamePanel == null || AboutPanel == null) return;

            string tag = rb.Tag as string;

            LauncherPanel.Visibility = Visibility.Collapsed;
            JavaPanel.Visibility = Visibility.Collapsed;
            GamePanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;

            switch (tag)
            {
                case "Launcher":
                    LauncherPanel.Visibility = Visibility.Visible;
                    break;
                case "Java":
                    JavaPanel.Visibility = Visibility.Visible;
                    break;
                case "Game":
                    GamePanel.Visibility = Visibility.Visible;
                    RefreshIsolationCheck();
                    break;
                case "About":
                    AboutPanel.Visibility = Visibility.Visible;
                    break;
            }
        }

        // ---------- 版本隔离 ----------

        private void RefreshIsolationCheck()
        {
            if (IsolationCheck == null) return;

            _suppressEvents = true;
            try
            {
                IsolationCheck.IsChecked = SettingsManager.GetIsolationGameData();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void IsolationCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (IsolationCheck == null) return;

            bool value = IsolationCheck.IsChecked == true;

            SettingsManager.SetIsolationGameData(value);
            App.Config.Isolated = value;
        }
    }
}