using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public class TaskGroup : ObservableObject
    {
        private string _id;
        private string _groupName;
        private string _summaryText;
        private string _currentAction;
        private double _overallProgress;
        private TaskState _state;
        private bool _isExpanded;
        private bool _isIndeterminate;

        public string Id { get => _id; set => Set(ref _id, value); }
        public string GroupName { get => _groupName; set => Set(ref _groupName, value); }
        public string SummaryText { get => _summaryText; set => Set(ref _summaryText, value); }
        public string CurrentAction { get => _currentAction; set => Set(ref _currentAction, value); }
        public double OverallProgress { get => _overallProgress; set => Set(ref _overallProgress, value); }
        public bool IsIndeterminate { get => _isIndeterminate; set => Set(ref _isIndeterminate, value); }
        public TaskState State { get => _state; set => Set(ref _state, value); }

        public Action<TaskGroup> OnExpanded { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!Set(ref _isExpanded, value)) return;
                if (value) OnExpanded?.Invoke(this);
            }
        }

        public ObservableCollection<SubTaskItem> Children { get; } = new ObservableCollection<SubTaskItem>();
        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        // ============ UI 线程辅助 ============
        private static void Dispatch(Action action)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }

        // ============ 阶段管理（线程安全） ============

        public SubTaskItem AddStage(string name, double weight = 1.0)
        {
            var item = new SubTaskItem
            {
                Name = name,
                Weight = weight,
                State = TaskState.Pending,
                Progress = 0,
            };
            Dispatch(() =>
            {
                Children.Add(item);
                RecomputeInternal();
            });
            return item;
        }

        public void UpdateStage(SubTaskItem stage, double progress, string status = null)
        {
            if (stage == null) return;
            Dispatch(() =>
            {
                if (stage.State == TaskState.Pending) stage.State = TaskState.Running;
                stage.Progress = Math.Max(0, Math.Min(1, progress));
                if (status != null) stage.StatusText = status;
                RecomputeInternal();
            });
        }

        public void CompleteStage(SubTaskItem stage)
        {
            if (stage == null) return;
            Dispatch(() =>
            {
                stage.Progress = 1;
                stage.State = TaskState.Success;
                stage.StatusText = "OK";
                RecomputeInternal();
            });
        }

        public void SkipStage(SubTaskItem stage, string reason = "Exists")
        {
            if (stage == null) return;
            Dispatch(() =>
            {
                stage.Progress = 1;
                stage.State = TaskState.Skipped;
                stage.StatusText = reason;
                RecomputeInternal();
            });
        }

        public void FailStage(SubTaskItem stage, string message = "Failed")
        {
            if (stage == null) return;
            Dispatch(() =>
            {
                stage.State = TaskState.Failed;
                stage.StatusText = message;
                RecomputeInternal();
            });
        }

        private void RecomputeInternal()
        {
            if (Children.Count == 0) return;

            double totalWeight = 0, weighted = 0;
            int ok = 0, failed = 0;
            foreach (var c in Children)
            {
                double w = c.Weight > 0 ? c.Weight : 1.0;
                totalWeight += w;
                weighted += c.Progress * w;
                if (c.State == TaskState.Success || c.State == TaskState.Skipped) ok++;
                if (c.State == TaskState.Failed) failed++;
            }

            OverallProgress = totalWeight > 0 ? weighted / totalWeight : 0;
            SummaryText = failed > 0
                ? $"{ok}/{Children.Count} ({failed} failed)"
                : $"{ok}/{Children.Count}";

            if (failed > 0) State = TaskState.Failed;
            else if (ok == Children.Count) State = TaskState.Success;
            else State = TaskState.Running;

            TaskCenter.Instance.NotifyStateChanged();
        }

        public void AppendLogs(System.Collections.Generic.IEnumerable<string> lines)
        {
            if (lines == null) return;
            Dispatch(() =>
            {
                foreach (var l in lines)
                {
                    if (string.IsNullOrEmpty(l)) continue;
                    LogLines.Add(l);
                    CurrentAction = l;
                }
                const int MaxLines = 2000;
                int excess = LogLines.Count - MaxLines;
                for (int i = 0; i < excess; i++) LogLines.RemoveAt(0);
            });
        }
    }
}