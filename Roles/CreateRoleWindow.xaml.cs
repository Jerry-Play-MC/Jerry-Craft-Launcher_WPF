using Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Languages;
using System;
using System.Windows;
using System.Windows.Input;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class CreateRoleWindow : Window
    {
        private readonly bool _isFirstUse;

        public Role CreatedRole { get; private set; }

        public CreateRoleWindow(bool isFirstUse = false)
        {
            InitializeComponent();
            _isFirstUse = isFirstUse;
            Loaded += (s, e) => RoleNameBox.Focus();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void RoleNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Create_Click(sender, e);
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            string username = (RoleNameBox.Text ?? "").Trim();

            if (!RoleManager.IsValidUsername(username))
            {
                LanguageManager.ShowWarning("Role.Invalid");
                return;
            }

            try
            {
                CreatedRole = RoleManager.CreateOffline(username);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                LanguageManager.ShowError("Role.CreateFailed", ex.Message);
            }
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstUse)
            {
                if (!LanguageManager.Confirm("Role.NeedFirst")) return;
            }
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}