using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XiaFlow.App.Services;
using XiaFlow.Core.Tasks;
using TaskStatus = XiaFlow.Core.Tasks.TaskStatus;

namespace XiaFlow.App.Pages
{
    public partial class TaskCenterPage : UserControl
    {
        /// <summary>任务行 VM（桥接 Core 的 ArchiveTask 到 UI）。</summary>
        public sealed class TaskRow : INotifyPropertyChanged
        {
            readonly ArchiveTask _t;
            public TaskRow(ArchiveTask t) => _t = t;

            public ArchiveTask Task => _t;
            public string Title => _t.Title;
            public string Detail => _t.Detail;
            public string KindText => TaskKindText.Of(_t.Kind);
            public string StatusText => _t.StatusText;
            public double ProgressValue => _t.Percent ?? (_t.TotalBytes > 0 ? Math.Min(100, _t.ProcessedBytes * 100.0 / _t.TotalBytes) : 0);
            public Visibility ProgressVisibility => _t.Status is TaskStatus.Running or TaskStatus.Paused or TaskStatus.Completed or TaskStatus.CompletedWithWarnings
                ? Visibility.Visible : Visibility.Collapsed;
            public string ProgressInfo => _t.Status is TaskStatus.Completed or TaskStatus.CompletedWithWarnings
                ? $"完成 · {Ui.Fmt(Math.Max(_t.ProcessedBytes, _t.TotalBytes))}"
                : _t.ProgressText;
            public string SpeedText => _t.Status == TaskStatus.Running ? Ui.FmtSpeed(_t.SpeedBytesPerSec) : "";
            public string EtaText => _t.Status == TaskStatus.Running ? Ui.FmtEta(_t.RemainingTime) : "";
            public Visibility PauseVisibility => _t.Status == TaskStatus.Running ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ResumeVisibility => _t.Status == TaskStatus.Paused ? Visibility.Visible : Visibility.Collapsed;
            public Visibility CancelVisibility => _t.Status is TaskStatus.Queued or TaskStatus.Running or TaskStatus.Paused
                ? Visibility.Visible : Visibility.Collapsed;
            public Visibility RetryVisibility => _t.Status is TaskStatus.Failed or TaskStatus.Cancelled
                ? Visibility.Visible : Visibility.Collapsed;
            public Visibility OpenVisibility =>
                (_t.Status is TaskStatus.Completed or TaskStatus.CompletedWithWarnings) && Directory.Exists(_t.Target) &&
                !_t.Target.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && !_t.Target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
            public string ErrorText => _t.Error ?? "";
            public Visibility ErrorVisibility => string.IsNullOrEmpty(_t.Error) ? Visibility.Collapsed : Visibility.Visible;

            public event PropertyChangedEventHandler? PropertyChanged;
            public void Refresh() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        readonly List<TaskRow> _rows = new();
        readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };

        public TaskCenterPage()
        {
            InitializeComponent();
            TaskList.ItemsSource = _rows;
            _timer.Tick += (_, _) => RefreshRows();
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue) { Reload(); _timer.Start(); }
                else _timer.Stop();
            };
            AppServices.Tasks.TaskAdded += _ => Dispatcher.BeginInvoke(Reload);
            AppServices.Tasks.TaskFinished += _ => Dispatcher.BeginInvoke(Reload);
            ParallelHint.Text = $"并行上限 {AppServices.Settings.MaxParallelTasks}（设置中心可调整）";
        }

        void Reload()
        {
            var tasks = AppServices.Tasks.Tasks;
            _rows.Clear();
            foreach (var t in tasks.AsEnumerable().Reverse()) _rows.Add(new TaskRow(t));
            var view = new System.ComponentModel.BindingList<TaskRow>(_rows);
            TaskList.ItemsSource = view;
            RefreshRows();
        }

        void RefreshRows()
        {
            foreach (var r in _rows) r.Refresh();
        }

        TaskRow? RowOf(object sender)
        {
            if ((sender as FrameworkElement)?.DataContext is TaskRow r) return r;
            return null;
        }

        void Pause_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) AppServices.Tasks.Pause(r.Task); }
        void Resume_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) AppServices.Tasks.Resume(r.Task); }
        void Cancel_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) AppServices.Tasks.Cancel(r.Task); }
        void Retry_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) AppServices.Tasks.Retry(r.Task); }
        void Open_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) Ui.OpenInExplorer(r.Task.Target); }

        void PauseAll_Click(object sender, RoutedEventArgs e) => AppServices.Tasks.PauseAll();
        void ResumeAll_Click(object sender, RoutedEventArgs e) => AppServices.Tasks.ResumeAll();
        void CancelAll_Click(object sender, RoutedEventArgs e) => AppServices.Tasks.CancelAll();

        void ClearDone_Click(object sender, RoutedEventArgs e)
        {
            var done = AppServices.Tasks.Tasks.Where(t => t.Status is TaskStatus.Completed or TaskStatus.CompletedWithWarnings
                                                          or TaskStatus.Failed or TaskStatus.Cancelled).ToList();
            if (done.Count == 0) return;
            Ui.Info("匣流", $"已完成的任务会自动移入历史记录（当前历史 {AppServices.Tasks.History().Count} 条），可在历史记录页查看。");
        }
    }
}
