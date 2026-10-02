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
                MessageBox.Show(
                    "角色名不符合要求：\n· 长度必须在 4-16 个字符之间\n· 不能包含中文字符或空格",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show("创建失败：" + ex.Message,
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            if (_isFirstUse)
            {
                var r = MessageBox.Show(
                    "还没有角色，启动游戏前必须先创建角色。\n确定要跳过吗？",
                    "提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
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