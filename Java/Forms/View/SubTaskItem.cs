using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class SubTaskItem : ObservableObject
    {
        private string _name;
        private double _progress;
        private string _statusText;
        private TaskState _state;
        private double _weight = 1.0;

        public string Name { get => _name; set => Set(ref _name, value); }
        public double Progress { get => _progress; set => Set(ref _progress, value); }
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
        public TaskState State { get => _state; set => Set(ref _state, value); }
        public double Weight { get => _weight; set => Set(ref _weight, value); }
    }
}
