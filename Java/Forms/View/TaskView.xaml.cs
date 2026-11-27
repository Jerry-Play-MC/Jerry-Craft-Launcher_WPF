using System.Windows;
using System.Windows.Controls;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public partial class TaskView : UserControl
    {
        public TaskView()
        {
            InitializeComponent();
        }

        private void ClearFinished_Click(object sender, RoutedEventArgs e)
        {
            TaskCenter.Instance.ClearFinished();
        }
    }
}