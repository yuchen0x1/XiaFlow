using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Backup;
using XiaFlow.Core.Engines;

namespace XiaFlow.App.Views
{
    public partial class BackupHistoryDialog : Window
    {
        readonly BackupTaskModel _task;
        readonly ObservableCollection<HistoryRow> _rows = new();
        List<BackupHistoryRecord> _records = new();

        public BackupHistoryDialog(BackupTaskModel task)
        {
            InitializeComponent();
            _task = task;
            TitleText.Text = $"备份历史 — {task.Name}";
            Grid.ItemsSource = _rows;
            Load();
        }

        void Load()
        {
            _records = AppServices.BackupStore.HistoryFor(_task.Id)
                .OrderByDescending(h => h.Time).ToList();
            _rows.Clear();
            foreach (var h in _records)
                _rows.Add(new HistoryRow(h, _task.DestDir));
        }

        public sealed class HistoryRow
        {
            readonly BackupHistoryRecord _r;
            readonly string _destDir;
            public HistoryRow(BackupHistoryRecord r, string destDir) { _r = r; _destDir = destDir; }
            public BackupHistoryRecord Record => _r;
            public string TimeText => _r.Time.ToString("yyyy-MM-dd HH:mm:ss");
            public string TypeText => _r.TypeText;
            public string FileName => _r.ArchiveFile;
            public bool FileExists => File.Exists(_r.ArchiveFile);
            public string SourceText => Ui.Fmt(_r.SourceBytes);
            public string ArchiveText => FileExists ? Ui.Fmt(_r.ArchiveBytes) : "已清理";
            public string RatioText => FileExists ? _r.Ratio + "%" : "—";
            public int FileCount => _r.FileCount;
            public string Status => _r.Status;
            public string VerifiedText => FileExists ? (_r.Verified ? "✓" : "—") : "—";
        }

        void OpenDir_Click(object sender, RoutedEventArgs e) => Ui.OpenInExplorer(_task.DestDir);

        void Export_Click(object sender, RoutedEventArgs e)
        {
            var file = Ui.PickSaveFile("导出备份记录", $"XiaFlow_备份记录_{_task.Name}_{DateTime.Now:yyyyMMdd}.csv",
                "CSV 文件|*.csv");
            if (file == null) return;
            var sb = new System.Text.StringBuilder("时间,类型,文件,源大小,备份大小,压缩率,文件数,状态,校验\n");
            foreach (var r in _records)
            {
                sb.AppendLine(string.Join(",",
                    r.Time, r.TypeText, "\"" + r.ArchiveFile + "\"", r.SourceBytes, r.ArchiveBytes,
                    r.Ratio + "%", r.FileCount, r.Status, r.Verified));
            }
            File.WriteAllText(file, sb.ToString(), System.Text.Encoding.UTF8);
            Ui.OpenInExplorer(Path.GetDirectoryName(file)!);
        }

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (Grid.SelectedItem is not HistoryRow row) { Ui.WarnBox("匣流", "请选择一个备份"); return; }
            if (!row.FileExists) { Ui.WarnBox("匣流", "该备份文件已被清理"); return; }

            var dlg = new RestoreDialog(_task, row.Record.ArchiveFile) { Owner = this };
            dlg.ShowDialog();
        }

        void Verify_Click(object sender, RoutedEventArgs e)
        {
            if (Grid.SelectedItem is not HistoryRow row) { Ui.WarnBox("匣流", "请选择一个备份"); return; }
            if (!row.FileExists) { Ui.WarnBox("匣流", "该备份文件已被清理"); return; }
            var rec = row.Record;
            Task.Run(() =>
            {
                using var engine = new SevenZipEngine();
                var (ok, err) = ArchiveService.Verify(engine, rec.ArchiveFile, null);
                rec.Verified = ok;
                AppServices.BackupStore.UpdateHistory(rec);
                Dispatcher.BeginInvoke(() =>
                {
                    if (ok) Ui.Info("匣流", "校验通过：备份完整无损");
                    else Ui.WarnBox("匣流", "校验失败：" + err);
                    Load();
                });
            });
        }

        void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (Grid.SelectedItem is not HistoryRow row) { Ui.WarnBox("匣流", "请选择一个备份"); return; }
            var manifest = BackupManifest.Load(_task.Id);
            var idx = manifest.Chain.FindIndex(c => c.File == Path.GetFileName(row.Record.ArchiveFile));
            string depWarning = "";
            if (idx >= 0)
            {
                for (int i = idx + 1; i < manifest.Chain.Count; i++)
                {
                    var later = manifest.Chain[i];
                    if (later.Type == BackupKind.Incremental || (later.Type == BackupKind.Differential && later.BaseIndex == idx))
                    {
                        depWarning = $"\n\n⚠ 注意：备份链中「{later.File}」依赖它，删除后该备份将无法恢复。";
                        break;
                    }
                }
            }
            if (!Ui.Confirm("匣流", $"确定删除备份文件吗？{depWarning}\n\n此操作只删除匣流管理的备份文件，不会触碰源文件。")) return;
            try
            {
                if (File.Exists(row.Record.ArchiveFile)) File.Delete(row.Record.ArchiveFile);
                if (idx >= 0) { manifest.Chain.RemoveAt(idx); manifest.Save(_task.Id); }
                Load();
            }
            catch (Exception ex)
            {
                Ui.WarnBox("匣流", "删除失败：" + ex.Message);
            }
        }

        void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
