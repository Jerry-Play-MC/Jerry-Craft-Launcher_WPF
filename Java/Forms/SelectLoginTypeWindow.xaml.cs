using System.Windows;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public enum LoginType
    {
        Cancel,
        Offline,
        Microsoft
    }

    public partial class SelectLoginTypeWindow : Window
    {
        public LoginType Selected { get; private set; }

        public SelectLoginTypeWindow()
        {
            InitializeComponent();
            Selected = LoginType.Cancel;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Microsoft_Click(object sender, RoutedEventArgs e)
        {
            Selected = LoginType.Microsoft;
            DialogResult = true;
            Close();
        }

        private void Offline_Click(object sender, RoutedEventArgs e)
        {
            Selected = LoginType.Offline;
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Selected = LoginType.Cancel;
            DialogResult = false;
            Close();
        }
    }
}