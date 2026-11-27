using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class TaskCenter : ObservableObject
    {
        public static TaskCenter Instance { get; } = new TaskCenter();
        private TaskCenter() { }

        public ObservableCollection<TaskGroup> Groups { get; } = new ObservableCollection<TaskGroup>();

        private bool _isPanelOpen;
        public bool IsPanelOpen
        {
            get => _isPanelOpen;
            set => Set(ref _isPanelOpen, value);
        }

        public bool IsBusy => Groups.Any(g =>
            g.State == TaskState.Running || g.State == TaskState.Pending);

        public bool HasFailure => Groups.Any(g => g.State == TaskState.Failed);

        public TaskGroup CreateGroup(string name)
        {
            var g = new TaskGroup
            {
                Id = Guid.NewGuid().ToString("N"),
                GroupName = name,
                State = TaskState.Pending,
                OnExpanded = CollapseOthers,
            };
            Groups.Insert(0, g);
            g.IsExpanded = true;
            NotifyStateChanged();
            return g;
        }

        public void RemoveGroup(TaskGroup g)
        {
            if (g != null && Groups.Remove(g))
                NotifyStateChanged();
        }

        public void ClearFinished()
        {
            foreach (var g in Groups
                .Where(x => x.State == TaskState.Success || x.State == TaskState.Failed)
                .ToList())
                Groups.Remove(g);
            NotifyStateChanged();
        }

        private void CollapseOthers(TaskGroup target)
        {
            foreach (var g in Groups)
                if (!ReferenceEquals(g, target)) g.IsExpanded = false;
        }

        public void NotifyStateChanged()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(HasFailure));
        }
    }
}