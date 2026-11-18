using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Tasks;

namespace XiaFlow.App.Pages
{
    public partial class QuickExtractPage : UserControl
    {
        public sealed class ArchiveRow
        {
            public string Path { get; set; } = "";
            public string Name => System.IO.Path.GetFileName(Path);
            public string Info { get; set; } = "待检测";
            public bool NeedsPassword { get; set; }
            public string? DetectedPassword { get; set; }
        }

        readonly ObservableCollection<ArchiveRow> _rows = new();
        readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

        public QuickExtractPage()
        {
            InitializeComponent();
            ArchiveList.ItemsSource = _rows;
            OutDirBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            ExtractZone.FilesDropped += Zone_FilesDropped;
            _scanTimer.Tick += (_, _) => ProbePending();
        }

        void Zone_FilesDropped(string[] paths)
        {
            foreach (var p in paths.Where(p => File.Exists(p) && FormatRegistry.FindByExtension(p) != null))
            {
                if (_rows.All(r => !string.Equals(r.Path, p, StringComparison.OrdinalIgnoreCase)))
                    _rows.Add(new ArchiveRow { Path = p });
            }
            _scanTimer.Start();
        }

        void ProbePending()
        {
            var pending = _rows.FirstOrDefault(r => r.Info == "待检测");
            if (pending == null) { _scanTimer.Stop(); return; }
            ProbeOne(pending);
        }

        void ProbeOne(ArchiveRow row)
        {
            Task.Run(() =>
            {
                try
                {
                    var archive = VolumeHelper.GetFirstVolume(row.Path);
                    var listing = new SevenZipEngine().List(archive, null);
                    var info = $"{listing.FormatName} · {listing.FileCount} 个文件 · {Ui.Fmt(listing.TotalUncompressed)}";
                    bool encrypted = listing.HasEncryptedEntries;
                    string? pwd = null;
                    if (encrypted)
                    {
                        var match = AppServices.Vault.TryMatch(archive);
                        if (match != null) { pwd = match.Password; info += " · 已从密码库匹配"; }
                    }
                    Dispatcher.BeginInvoke(() =>
                    {
                        row.Info = info;
                        row.NeedsPassword = encrypted && pwd == null;
                        row.DetectedPassword = pwd;
                        PwdHint.Text = _rows.Any(r => r.NeedsPassword) ? "检测到加密压缩包，请输入密码" : "";
                        ArchiveList.Items.Refresh();
                    });
                }
                catch (ArchiveException aex) when (aex.Kind == ArchiveErrorKind.WrongPassword)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        row.Info = "加密压缩包（需要密码）";
                        row.NeedsPassword = true;
                        PwdHint.Text = "检测到加密压缩包，请输入密码";
                        ArchiveList.Items.Refresh();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        row.Info = "无法读取: " + ex.Message;
                        ArchiveList.Items.Refresh();
                    });
                }
            });
        }

        public void ImportPaths(string[] paths) => Zone_FilesDropped(paths);

        void PickArchive_Click(object sender, RoutedEventArgs e)
        {
            var files = Ui.PickFiles("选择压缩包", "压缩包|*.zip;*.7z;*.rar;*.tar;*.gz;*.bz2;*.xz;*.zst;*.lz4;*.iso;*.wim;*.cab;*.cpio;*.rpm;*.deb;*.001;*.part1.rar|所有文件|*.*");
            if (files != null) Zone_FilesDropped(files);
        }

        void RemoveArchive_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ArchiveRow row) _rows.Remove(row);
        }

        void ClearList_Click(object sender, RoutedEventArgs e) => _rows.Clear();

        void Rescan_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.Info = "待检测";
            _scanTimer.Start();
        }

        void PickOutDir_Click(object sender, RoutedEventArgs e)
        {
            var d = Ui.PickFolder("选择解压目录", OutDirBox.Text);
            if (d != null) OutDirBox.Text = d;
        }

        void Pwd_Changed(object sender, RoutedEventArgs e)
        {
            // 输入密码后清除提示
            if (PwdBox.Password.Length > 0) PwdHint.Text = "";
        }

        void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0)
            {
                Ui.WarnBox("匣流", "请先添加压缩包");
                return;
            }
            if (string.IsNullOrWhiteSpace(OutDirBox.Text))
            {
                Ui.WarnBox("匣流", "请选择解压目录");
                return;
            }
            var conflict = ((ComboBoxItem)ConflictBox.SelectedItem).Tag switch
            {
                "Overwrite" => ConflictPolicy.Overwrite,
                "Skip" => ConflictPolicy.Skip,
                _ => ConflictPolicy.AutoRename,
            };
            bool openDir = OpenDir.IsChecked == true;
            bool createFolder = ModeFolder.IsChecked == true;
            var outRoot = OutDirBox.Text;
            var userPwd = string.IsNullOrEmpty(PwdBox.Password) ? null : PwdBox.Password;

            foreach (var row in _rows)
            {
                var outDir = createFolder
                    ? Path.Combine(outRoot, Path.GetFileNameWithoutExtension(VolumeHelper.GetFirstVolume(row.Path)))
                    : outRoot;
                var password = userPwd ?? row.DetectedPassword;
                SevenZipEngine? engine = null;
                var archive = row.Path;
                var task = AppServices.Tasks.Enqueue(TaskKind.Extract, $"解压: {row.Name}", "解压到 " + outDir, archive, outDir,
                    ctx =>
                    {
                        using var e = engine!;
                        var plan = ArchiveService.Extract(e, new ExtractOptions
                        {
                            ArchivePath = archive,
                            OutputDir = outDir,
                            Password = password,
                            Conflict = conflict,
                            Limits = AppServices.Settings.ToSafetyLimits(),
                        }, default);
                        ctx.ReportProgress(100, plan.Listing!.TotalUncompressed, null);
                        AppServices.Notifications.Push("解压完成", $"{Path.GetFileName(archive)} → {outDir}", "success");
                        if (openDir) Dispatcher.BeginInvoke(() => Ui.OpenInExplorer(outDir));
                    });
                task.BeforeStart = t =>
                {
                    engine = new SevenZipEngine();
                    t.Pausable = engine;
                    AppServices.Tasks.BindCancellation(t, engine.Cancel);
                };
            }

            var win = Window.GetWindow(this) as MainWindow;
            win?.Navigate("tasks");
        }
    }
}
