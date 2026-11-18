using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XiaFlow.App.Services;
using XiaFlow.App.Views;
using XiaFlow.Core.Backup;
using XiaFlow.Core.Engines;

namespace XiaFlow.App.Pages
{
    public partial class RestorePage : UserControl
    {
        public sealed class PointRow
        {
            readonly RestorePoint _p;
            public PointRow(RestorePoint p, bool chainOk) { _p = p; ChainOk = chainOk; }
            public RestorePoint Point => _p;
            public bool ChainOk { get; }
            public string Title => $"{_p.TypeText}备份 · {_p.Time:yyyy-MM-dd HH:mm:ss}" + (ChainOk ? "" : "  ⚠ 链不完整");
            public string Sub => $"{Path.GetFileName(_p.FileName)} · {Ui.Fmt(_p.Size)}";
        }

        public sealed class EntryRow
        {
            public string Rel { get; set; } = "";
            public long Size { get; set; }
            public DateTime? Mtime { get; set; }
            public string SizeText => Ui.Fmt(Size);
            public string MtimeText => Mtime is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "";
        }

        public RestorePage()
        {
            InitializeComponent();
            IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Refresh(); };
        }

        void Refresh()
        {
            var tasks = AppServices.BackupStore.Tasks;
            TaskBox.ItemsSource = tasks.Select(t => t.Name).ToList();
            if (tasks.Count > 0 && TaskBox.SelectedIndex < 0) TaskBox.SelectedIndex = 0;
        }

        BackupTaskModel? CurrentTask() =>
            TaskBox.SelectedIndex >= 0 ? AppServices.BackupStore.Tasks[TaskBox.SelectedIndex] : null;

        void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        void Task_Changed(object sender, SelectionChangedEventArgs e)
        {
            var task = CurrentTask();
            PointList.ItemsSource = null;
            EntryList.ItemsSource = null;
            BtnRestore.IsEnabled = false;
            if (task == null) return;
            Task.Run(() =>
            {
                var service = new RestoreService(AppServices.BackupStore, AppServices.Vault);
                var points = service.GetRestorePoints(task);
                // 链完整性检查：每个恢复点的依赖链文件是否齐全
                var rows = points.Select(p => new PointRow(p, service.RequiredChainFiles(task, p)
                    .All(f => File.Exists(Path.Combine(task.DestDir, f))))).ToList();
                Dispatcher.BeginInvoke(() =>
                {
                    PointList.ItemsSource = rows;
                    if (rows.Count > 0) PointList.SelectedIndex = 0;
                });
            });
        }

        void Point_Changed(object sender, SelectionChangedEventArgs e)
        {
            BtnRestore.IsEnabled = false;
            if (PointList.SelectedItem is not PointRow row) return;
            var task = CurrentTask();
            if (task == null) return;
            BtnRestore.IsEnabled = row.ChainOk;
            Task.Run(() =>
            {
                try
                {
                    var service = new RestoreService(AppServices.BackupStore, AppServices.Vault);
                    var entries = service.ListPointEntries(task, row.Point);
                    var rows = entries.Select(en => new EntryRow
                    {
                        Rel = en.Name,
                        Size = en.Size,
                        Mtime = en.Modified,
                    }).ToList();
                    Dispatcher.BeginInvoke(() => EntryList.ItemsSource = rows);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() => Ui.WarnBox("匣流", "读取备份内容失败：" + ex.Message));
                }
            });
        }

        void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            var task = CurrentTask();
            if (task != null) Ui.OpenInExplorer(task.DestDir);
        }

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (PointList.SelectedItem is not PointRow row) return;
            var task = CurrentTask();
            if (task == null) return;
            var dlg = new RestoreDialog(task, row.Point.FileName) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }
    }
}
