using System.Windows.Controls;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF.Java.Forms.View
{
    public partial class TaskProgressView : UserControl
    {
        public TaskProgressView()
        {
            InitializeComponent();
            DataContext = TaskCenter.Instance;
        }
    }
}