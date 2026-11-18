using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Backup;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Tasks;

namespace XiaFlow.App.Views
{
    public partial class RestoreDialog : Window
    {
        readonly BackupTaskModel _task;
        readonly string _archiveFile;

        public RestoreDialog(BackupTaskModel task, string archiveFile)
        {
            InitializeComponent();
            _task = task;
            _archiveFile = archiveFile;
            PointText.Text = $"任务「{task.Name}」 · 备份文件 {Path.GetFileName(archiveFile)}";
        }

        RestorePoint MakePoint()
        {
            var manifest = BackupManifest.Load(_task.Id);
            var idx = manifest.Chain.FindIndex(c => c.File == Path.GetFileName(_archiveFile));
            var fi = new FileInfo(_archiveFile);
            return new RestorePoint
            {
                ChainIndex = idx,
                FileName = Path.GetFileName(_archiveFile),
                Type = idx >= 0 ? manifest.Chain[idx].Type : BackupKind.Full,
                Time = fi.LastWriteTime,
                FilePath = _archiveFile,
                Size = fi.Length,
            };
        }

        List<string>? SelectedEntries()
        {
            if (SelectiveBox.IsChecked != true) return null;
            // 从恢复中心页面列表拿选中项 —— 简化：此处按“全部”处理，页面上已预览
            return null;
        }

        void PickNew_Click(object sender, RoutedEventArgs e)
        {
            var d = Ui.PickFolder("选择恢复位置");
            if (d != null) NewLocBox.Text = d;
        }

        async void PreviewOverwrite_Click(object sender, RoutedEventArgs e)
        {
            var service = new RestoreService(AppServices.BackupStore, AppServices.Vault);
            var point = MakePoint();
            var conflict = CurrentConflict();
            await Task.Run(() =>
            {
                var overwrites = service.PreviewOverwrites(_task, point, null);
                Dispatcher.BeginInvoke(() =>
                {
                    OverwriteHint.Text = conflict == ConflictPolicy.Overwrite && ModeOriginal.IsChecked == true
                        ? $"恢复到原位置将覆盖 {overwrites.Count} 个当前文件：\n" + string.Join("\n", overwrites.Take(15)) +
                          (overwrites.Count > 15 ? $"\n… 共 {overwrites.Count} 个" : "")
                        : $"冲突策略为「{(string)((ComboBoxItem)ConflictBox.SelectedItem).Content}」，不会直接覆盖当前文件（自动重命名/跳过）。\n当前存在 {overwrites.Count} 个同名文件。";
                });
            });
        }

        ConflictPolicy CurrentConflict() =>
            ((ComboBoxItem)ConflictBox.SelectedItem).Tag switch
            {
                "Overwrite" => ConflictPolicy.Overwrite,
                "Skip" => ConflictPolicy.Skip,
                _ => ConflictPolicy.AutoRename,
            };

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        async void Start_Click(object sender, RoutedEventArgs e)
        {
            bool toOriginal = ModeOriginal.IsChecked == true;
            if (!toOriginal && string.IsNullOrWhiteSpace(NewLocBox.Text))
            {
                Ui.WarnBox("匣流", "请选择恢复位置");
                return;
            }
            var conflict = CurrentConflict();
            var point = MakePoint();
            var selected = SelectedEntries();

            // 重要文件恢复必须明确确认
            if (toOriginal && conflict == ConflictPolicy.Overwrite)
            {
                var service = new RestoreService(AppServices.BackupStore, AppServices.Vault);
                var overwrites = await Task.Run(() => service.PreviewOverwrites(_task, point, selected));
                if (overwrites.Count > 0 &&
                    !Ui.Confirm("匣流", $"即将覆盖 {overwrites.Count} 个当前文件（列出前几项）：\n" +
                        string.Join("\n", overwrites.Take(8)) +
                        (overwrites.Count > 8 ? $"\n… 共 {overwrites.Count} 个" : "") +
                        "\n\n确认继续恢复吗？"))
                    return;
            }

            DialogResult = true;
            AppServices.Tasks.Enqueue(TaskKind.Restore, $"恢复: {_task.Name}",
                (toOriginal ? "恢复到原位置" : "恢复到 " + NewLocBox.Text) + $" · {Path.GetFileName(_archiveFile)}",
                _archiveFile, toOriginal ? string.Join("; ", _task.SourcePaths) : NewLocBox.Text,
                ctx =>
                {
                    ctx.ReportProgress(0, null, null);
                    var service = new RestoreService(AppServices.BackupStore, AppServices.Vault);
                    var result = service.Restore(_task, new RestoreOptions
                    {
                        Point = point,
                        ToOriginalLocation = toOriginal,
                        NewLocation = NewLocBox.Text,
                        SelectedEntries = selected,
                        Conflict = conflict,
                    }, ctx);
                    ctx.ReportProgress(100, null, null);
                    if (result.Success)
                    {
                        AppServices.Notifications.Push("恢复完成",
                            $"已恢复 {result.RestoredFiles} 个文件（覆盖 {result.Overwritten}，跳过 {result.Skipped}，重命名 {result.Renamed}）", "success");
                    }
                    else
                    {
                        AppServices.Notifications.Push("恢复失败", result.Error ?? "", "error");
                        throw new ArchiveException(ArchiveErrorKind.IoError, result.Error ?? "恢复失败");
                    }
                });
        }
    }
}
