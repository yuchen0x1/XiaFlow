using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Tasks;
using TaskStatus = XiaFlow.Core.Tasks.TaskStatus;

namespace XiaFlow.App.Pages
{
    public partial class HomePage : UserControl
    {
        readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

        public HomePage()
        {
            InitializeComponent();
            _timer.Tick += (_, _) => Refresh();
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue) { Refresh(); _timer.Start(); }
                else _timer.Stop();
            };
        }

        void Refresh()
        {
            // 备份统计（真实数据）
            var st = AppServices.BackupStore.Stats();
            StatBackupToday.Text = st.today.ToString();
            StatBackupWeek.Text = $"本周 {st.week} 次";
            StatBackupTotal.Text = st.total.ToString();
            StatBackupFailed.Text = $"失败 {st.failedCount} 次";
            StatBackupBytes.Text = Ui.Fmt(st.totalBytes);
            StatLastBackup.Text = st.lastTime is { } t ? $"最近 {t:MM-dd HH:mm}" : "暂无备份";

            // 任务统计
            var hist = AppServices.Tasks.History();
            StatTaskDone.Text = hist.Count(h => h.Status is TaskStatus.Completed or TaskStatus.CompletedWithWarnings).ToString();
            StatTaskSub.Text = $"历史任务 {hist.Count} 个";

            // 运行中任务
            var running = AppServices.Tasks.Tasks.Where(t => t.Status is TaskStatus.Running or TaskStatus.Paused or TaskStatus.Queued).ToList();
            RunningTasksHint.Text = running.Count == 0 ? "暂无执行中的任务" : $"{running.Count} 个任务";
            RunningTasksList.ItemsSource = running;

            // 最近备份
            RecentBackups.ItemsSource = AppServices.BackupStore.History
                .OrderByDescending(h => h.Time).Take(6)
                .Select(h => new
                {
                    h.TaskName,
                    h.TypeText,
                    h.Status,
                    h.ArchiveBytes,
                    SizeText = Ui.Fmt(h.ArchiveBytes),
                    TimeText = h.Time.ToString("MM-dd HH:mm"),
                }).ToList();
        }

        void GoCompress_Click(object sender, RoutedEventArgs e) => Navigate("compress");
        void GoExtract_Click(object sender, RoutedEventArgs e) => Navigate("extract");
        void GoBatch_Click(object sender, RoutedEventArgs e) => Navigate("batch");
        void GoBackup_Click(object sender, RoutedEventArgs e) => Navigate("backup");
        void GoRestore_Click(object sender, RoutedEventArgs e) => Navigate("restore");
        void GoBrowser_Click(object sender, RoutedEventArgs e) => Navigate("browser");
        void GoTasks_Click(object sender, RoutedEventArgs e) => Navigate("tasks");
        void GoSettings_Click(object sender, RoutedEventArgs e) => Navigate("settings");

        void CheckUpdate_Click(object sender, RoutedEventArgs e) => Navigate("about");

        void Navigate(string key)
        {
            var win = Window.GetWindow(this) as MainWindow;
            win?.Navigate(key);
        }
    }
}
