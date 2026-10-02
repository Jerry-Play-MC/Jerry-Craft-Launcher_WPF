using System.ComponentModel;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class ServerInfo : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string FolderPath { get; set; }
        public string JarPath { get; set; }
        public string PropertiesPath { get; set; }

        private string _motd = "";
        public string Motd
        {
            get { return _motd; }
            set
            {
                _motd = value;
                OnPropertyChanged("Motd");
                OnPropertyChanged("DisplayText");
            }
        }

        private string _status = "";
        /// <summary>启动过程中的状态文字。为空时 UI 显示 Motd</summary>
        public string Status
        {
            get { return _status; }
            set
            {
                _status = value;
                OnPropertyChanged("Status");
                OnPropertyChanged("DisplayText");
            }
        }

        private bool _isRunning;
        /// <summary>该服务器当前是否有活跃进程</summary>
        public bool IsRunning
        {
            get { return _isRunning; }
            set
            {
                _isRunning = value;
                OnPropertyChanged("IsRunning");
            }
        }

        /// <summary>UI 显示文字：状态优先，否则 Motd</summary>
        public string DisplayText
        {
            get
            {
                if (!string.IsNullOrEmpty(_status)) return _status;
                return _motd ?? "";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        public override string ToString() { return Name; }
    }
}