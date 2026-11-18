using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XiaFlow.App.Services;
using XiaFlow.App.Views;
using XiaFlow.Core.Backup;
using XiaFlow.Core.Tasks;

namespace XiaFlow.App.Pages
{
    public partial class BackupPage : UserControl
    {
        public BackupPage()
        {
            InitializeComponent();
            IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Refresh(); };
        }

        void Refresh()
        {
            var tasks = AppServices.BackupStore.Tasks;
            TaskCards.ItemsSource = tasks.Select(t => new TaskCardVm(t)).ToList();
            EmptyHint.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SchedHint.Text = $"共 {tasks.Count(t => t.Enabled)} 个启用任务 · 后台调度通过 Windows 任务计划程序执行（软件未打开也能备份）";
        }

        sealed class TaskCardVm
        {
            public readonly BackupTaskModel Model;
            public BackupTaskModel _t => Model;
            public TaskCardVm(BackupTaskModel t) => Model = t;
            public string Name => _t.Name;
            public string StrategyText => _t.StrategyText;
            public string ScheduleText => _t.ScheduleText;
            public string SourcesText => "源: " + string.Join("；", _t.SourcePaths);
            public string DestDir => _t.DestDir;
            public string LastRunText => _t.LastRun?.ToString("yyyy-MM-dd HH:mm") ?? "从未";
            public string NextRunText => _t.NextRun?.ToString("yyyy-MM-dd HH:mm") ?? "—";
            public string KeepText => _t.KeepDays > 0 ? $"{_t.KeepDays} 天" : (_t.KeepCount > 0 ? $"{_t.KeepCount} 个" : "永久");
            public string LastError => _t.LastError ?? "";
            public Visibility ErrorVisibility => string.IsNullOrEmpty(_t.LastError) ? Visibility.Collapsed : Visibility.Visible;
            public string ToggleText => _t.Enabled ? "停用" : "启用";
            public Visibility DisabledVisibility => _t.Enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        void NewTask_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new BackupTaskDialog(null) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                Refresh();
                Ui.Info("匣流", "备份任务已保存。计划任务已同步注册到 Windows 任务计划程序。");
            }
        }

        void Edit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TaskCardVm vm) return;
            var dlg = new BackupTaskDialog(vm.Model) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true) Refresh();
        }

        void RunNow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TaskCardVm vm) return;
            AppServices.EnqueueBackupTask(vm.Model);
            Ui.Info("匣流", $"备份任务「{vm.Name}」已加入任务队列，可在任务中心查看进度。");
        }

        void RunAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in AppServices.BackupStore.Tasks.Where(t => t.Enabled))
                AppServices.EnqueueBackupTask(t);
        }

        void History_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TaskCardVm vm) return;
            var dlg = new BackupHistoryDialog(vm.Model) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            Refresh();
        }

        void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TaskCardVm vm) return;
            Ui.OpenInExplorer(vm.Model.DestDir);
        }

        void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TaskCardVm vm) return;
            vm.Model.Enabled = !vm.Model.Enabled;
            if (vm.Model.Enabled)
                BackupScheduler.RegisterWindowsTask(vm._t, Environment.ProcessPath ?? "");
            else
                BackupScheduler.DeleteWindowsTask(vm.Model.Id);
            AppServices.BackupStore.SaveTask(vm.Model);
            Refresh();
        }

        void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TaskCardVm vm) return;
            if (!Ui.Confirm("匣流", $"确定删除备份任务「{vm.Name}」吗？\n\n备份文件不会被删除，可手动清理；任务清单与计划将被移除。")) return;
            BackupScheduler.DeleteWindowsTask(vm.Model.Id);
            AppServices.BackupStore.DeleteTask(vm.Model.Id);
            Refresh();
        }
    }
}
