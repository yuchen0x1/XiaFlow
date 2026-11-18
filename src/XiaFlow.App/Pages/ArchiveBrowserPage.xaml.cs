using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XiaFlow.App.Services;
using XiaFlow.App.Views;
using XiaFlow.Core;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Tasks;

namespace XiaFlow.App.Pages
{
    public partial class ArchiveBrowserPage : UserControl
    {
        /// <summary>列表行。</summary>
        public sealed class Row
        {
            public required string FullEntryPath { get; init; }  // 归档内完整路径（/ 分隔）
            public required string Name { get; init; }
            public required bool IsDir { get; init; }
            public required long Size { get; init; }
            public required long Packed { get; init; }
            public required DateTime? Mtime { get; init; }
            public required bool Encrypted { get; init; }

            public string TypeText => IsDir ? "文件夹" :
                (Path.GetExtension(FullEntryPath).TrimStart('.').ToUpperInvariant() is { Length: > 0 } e ? e + " 文件" : "文件");
            public string SizeText => IsDir ? "—" : Ui.Fmt(Size);
            public string PackedText => IsDir ? "—" : Ui.Fmt(Packed);
            public string RatioText => (!IsDir && Size > 0) ? Math.Round((1.0 - (double)Packed / Size) * 100, 1) + "%" : "—";
            public string MtimeText => Mtime is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "—";
            public string EncText => Encrypted ? "🔒" : "";
        }

        ArchiveListing? _listing;
        string _archivePath = "";
        string _password = "";
        string _currentDir = "";      // 归档内当前目录（/ 分隔，"" = 根）
        string _search = "";
        readonly ObservableCollection<Row> _rows = new();

        public ArchiveBrowserPage()
        {
            InitializeComponent();
            FileList.ItemsSource = _rows;
        }

        public void LoadArchive(string path)
        {
            _archivePath = path;
            _currentDir = "";
            _password = "";
            OpenInternal();
        }

        void Open_Click(object sender, RoutedEventArgs e)
        {
            var f = Ui.PickFiles("选择压缩包", "压缩包|*.zip;*.7z;*.rar;*.tar;*.gz;*.bz2;*.xz;*.zst;*.iso;*.wim;*.cab;*.cpio;*.rpm;*.deb;*.001;*.part1.rar|所有文件|*.*");
            if (f is { Length: > 0 }) LoadArchive(f[0]);
        }

        void OpenInternal()
        {
            if (string.IsNullOrEmpty(_archivePath) || !File.Exists(_archivePath))
            {
                StatusText.Text = "未打开压缩包";
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    var engine = new SevenZipEngine();
                    var listing = engine.List(VolumeHelper.GetFirstVolume(_archivePath), string.IsNullOrEmpty(_password) ? null : _password);
                    Dispatcher.BeginInvoke(() =>
                    {
                        _listing = listing;
                        StatusText.Text = $"已打开：{Path.GetFileName(_archivePath)}";
                        StatsText.Text = $"{listing.FileCount} 个文件 · {Ui.Fmt(listing.TotalPacked)} → {Ui.Fmt(listing.TotalUncompressed)} · 压缩率 {Math.Round((1.0 - (double)listing.TotalPacked / Math.Max(1, listing.TotalUncompressed)) * 100, 1)}%";
                        RebuildRows();
                    });
                }
                catch (ArchiveException aex) when (aex.Kind == ArchiveErrorKind.WrongPassword)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var pwd = InputDialog.Ask(Window.GetWindow(this), "需要密码",
                            $"「{Path.GetFileName(_archivePath)}」已加密，请输入密码：", "");
                        if (!string.IsNullOrEmpty(pwd))
                        {
                            _password = pwd;
                            OpenInternal();
                        }
                        else StatusText.Text = "需要密码，未打开";
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        StatusText.Text = "打开失败：" + ex.Message;
                    });
                }
            });
        }

        void RebuildRows()
        {
            if (_listing == null) return;
            _rows.Clear();
            var prefix = _currentDir.Length == 0 ? "" : _currentDir + "/";
            var seenDirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in _listing.Entries)
            {
                var name = e.Name.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = name[prefix.Length..];
                if (rest.Length == 0) continue;

                // 显式目录条目（带尾斜杠）
                if (rest.EndsWith("/"))
                {
                    var explicitDir = rest.TrimEnd('/');
                    if (explicitDir.Length > 0 && seenDirs.Add(explicitDir))
                    {
                        _rows.Add(new Row
                        {
                            FullEntryPath = prefix + explicitDir,
                            Name = explicitDir + "/",
                            IsDir = true,
                            Size = 0,
                            Packed = 0,
                            Mtime = e.Modified,
                            Encrypted = false,
                        });
                    }
                    continue;
                }

                var segs = rest.Split('/');
                if (segs.Length > 1)
                {
                    // 子目录聚合
                    var dirName = segs[0];
                    if (seenDirs.Add(dirName))
                    {
                        var dirFull = prefix + dirName;
                        var size = _listing.Entries.Where(x => x.Name.Replace('\\', '/').StartsWith(dirFull + "/", StringComparison.OrdinalIgnoreCase))
                            .Sum(x => x.Size);
                        _rows.Add(new Row
                        {
                            FullEntryPath = dirFull,
                            Name = dirName + "/",
                            IsDir = true,
                            Size = size,
                            Packed = 0,
                            Mtime = e.Modified,
                            Encrypted = false,
                        });
                    }
                    continue;
                }
                if (_search.Length > 0 && !rest.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
                _rows.Add(new Row
                {
                    FullEntryPath = name,
                    Name = rest,
                    IsDir = e.IsDirectory,
                    Size = e.Size,
                    Packed = e.PackedSize,
                    Mtime = e.Modified,
                    Encrypted = e.Encrypted,
                });
            }
            // 目录置顶排序
            var sorted = _rows.OrderByDescending(r => r.IsDir).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _rows.Clear();
            foreach (var r in sorted) _rows.Add(r);
            BreadcrumbText.Text = Path.GetFileName(_archivePath) + (_currentDir.Length == 0 ? "" : "  /  " + _currentDir.Replace("/", " / "));
        }

        // ---------- 导航 ----------

        void Up_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDir.Length == 0) return;
            var idx = _currentDir.LastIndexOf('/');
            _currentDir = idx >= 0 ? _currentDir[..idx] : "";
            RebuildRows();
        }

        void Refresh_Click(object sender, RoutedEventArgs e) => OpenInternal();

        void Search_Changed(object sender, TextChangedEventArgs e)
        {
            _search = SearchBox.Text.Trim();
            RebuildRows();
        }

        void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is Row r && r.IsDir)
            {
                _currentDir = r.FullEntryPath;
                RebuildRows();
            }
        }

        // ---------- 提取 ----------

        List<string> SelectedEntries() =>
            FileList.SelectedItems.OfType<Row>().SelectMany(r =>
            {
                if (r.IsDir)
                    return _listing!.Entries.Where(x => x.Name.Replace('\\', '/').StartsWith(r.FullEntryPath + "/", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Name).ToList();
                return new List<string> { r.FullEntryPath };
            }).ToList();

        void ExtractAll_Click(object sender, RoutedEventArgs e)
        {
            if (_listing == null) return;
            ExtractTo(_listing.Entries.Where(x => !x.IsDirectory).Select(x => x.Name).ToList());
        }

        void ExtractSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_listing == null) return;
            var entries = SelectedEntries();
            if (entries.Count == 0) { Ui.WarnBox("匣流", "请先选择要提取的文件"); return; }
            ExtractTo(entries);
        }

        void ExtractTo(List<string> entries)
        {
            var outDir = Ui.PickFolder("选择提取位置");
            if (outDir == null) return;
            var archive = VolumeHelper.GetFirstVolume(_archivePath);
            var pwd = _password;
            SevenZipEngine? engine = null;
            var task = AppServices.Tasks.Enqueue(TaskKind.Extract, $"提取: {Path.GetFileName(_archivePath)}",
                $"提取 {entries.Count} 个文件 → {outDir}", _archivePath, outDir,
                ctx =>
                {
                    using var en = engine!;
                    en.Extract(new SevenZipEngine.ExtractRequest
                    {
                        ArchivePath = archive,
                        OutputDir = outDir,
                        Password = string.IsNullOrEmpty(pwd) ? null : pwd,
                        SelectedEntries = entries,
                        Conflict = ConflictPolicy.AutoRename,
                    }, default);
                    ctx.ReportProgress(100, null, null);
                    AppServices.Notifications.Push("提取完成", $"{entries.Count} 个文件已提取到 {outDir}", "success");
                });
            task.BeforeStart = t =>
            {
                engine = new SevenZipEngine();
                t.Pausable = engine;
                AppServices.Tasks.BindCancellation(t, engine.Cancel);
            };
        }

        // ---------- 添加 / 删除 / 重命名 ----------

        void AddFile_Click(object sender, RoutedEventArgs e)
        {
            if (_listing == null) { Ui.WarnBox("匣流", "请先打开压缩包"); return; }
            if (!CanModify(out var engine)) return;
            var files = Ui.PickFiles("选择要添加的文件");
            if (files == null || files.Length == 0) return;
            try
            {
                var items = files.Select(f => (f, _currentDir.Length == 0
                    ? Path.GetFileName(f)
                    : _currentDir + "/" + Path.GetFileName(f))).ToList();
                engine.AddToArchive(VolumeHelper.GetFirstVolume(_archivePath), items,
                    string.IsNullOrEmpty(_password) ? null : _password);
                OpenInternal();
            }
            catch (ArchiveException aex)
            {
                Ui.WarnBox("匣流", "添加失败：" + aex.Message);
            }
        }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_listing == null) { Ui.WarnBox("匣流", "请先打开压缩包"); return; }
            if (!CanModify(out var engine)) return;
            var dir = Ui.PickFolder("选择要添加的文件夹");
            if (dir == null) return;
            try
            {
                var folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
                var items = files.Select(f => (_currentDir.Length == 0
                    ? folderName + "/" + Path.GetRelativePath(dir, f).Replace('\\', '/')
                    : _currentDir + "/" + folderName + "/" + Path.GetRelativePath(dir, f).Replace('\\', '/'), f)).ToList();
                engine.AddToArchive(VolumeHelper.GetFirstVolume(_archivePath), items,
                    string.IsNullOrEmpty(_password) ? null : _password);
                OpenInternal();
            }
            catch (ArchiveException aex)
            {
                Ui.WarnBox("匣流", "添加失败：" + aex.Message);
            }
        }

        bool CanModify(out SevenZipEngine engine)
        {
            engine = new SevenZipEngine();
            var fi = FormatRegistry.FindByExtension(_archivePath);
            if (fi == null || (fi.Capabilities & ArchiveCapabilities.Modify) == 0)
            {
                Ui.WarnBox("匣流", "该格式不支持直接修改（仅 7Z / ZIP / TAR 支持添加、删除与重命名）");
                return false;
            }
            return true;
        }

        void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_listing == null) return;
            if (!CanModify(out var engine)) return;
            var rows = FileList.SelectedItems.OfType<Row>().ToList();
            if (rows.Count == 0) return;
            if (!Ui.Confirm("匣流", $"确定从压缩包中删除选中的 {rows.Count} 项吗？此操作不可撤销。")) return;
            try
            {
                var paths = rows.SelectMany(r => r.IsDir
                    ? _listing.Entries.Where(x => x.Name.Replace('\\', '/').StartsWith(r.FullEntryPath + "/", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Name)
                    : new[] { r.FullEntryPath }).ToList();
                engine.DeleteEntries(VolumeHelper.GetFirstVolume(_archivePath), paths,
                    string.IsNullOrEmpty(_password) ? null : _password);
                OpenInternal();
            }
            catch (ArchiveException aex)
            {
                Ui.WarnBox("匣流", "删除失败：" + aex.Message);
            }
        }

        void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_listing == null) return;
            if (!CanModify(out var engine)) return;
            if (FileList.SelectedItem is not Row r) { Ui.WarnBox("匣流", "请选择一个文件"); return; }
            var newName = InputDialog.Ask(Window.GetWindow(this), "重命名",
                $"将「{r.Name}」重命名为（含归档内路径）：", r.FullEntryPath);
            if (string.IsNullOrWhiteSpace(newName) || newName == r.FullEntryPath) return;
            try
            {
                engine.RenameEntries(VolumeHelper.GetFirstVolume(_archivePath),
                    new List<(string, string)> { (r.FullEntryPath, newName.Replace('\\', '/')) },
                    string.IsNullOrEmpty(_password) ? null : _password);
                OpenInternal();
            }
            catch (ArchiveException aex)
            {
                Ui.WarnBox("匣流", "重命名失败：" + aex.Message);
            }
        }

        // ---------- 预览 ----------

        void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (_listing == null) return;
            if (FileList.SelectedItem is not Row r || r.IsDir) { Ui.WarnBox("匣流", "请选择一个文件"); return; }
            if (r.Size > 8 * 1024 * 1024)
            {
                Ui.WarnBox("匣流", "文件超过预览大小限制（8MB）");
                return;
            }
            try
            {
                var tmpDir = Path.Combine(AppPaths.TempDir, "preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);
                using var engine = new SevenZipEngine();
                engine.Extract(new SevenZipEngine.ExtractRequest
                {
                    ArchivePath = VolumeHelper.GetFirstVolume(_archivePath),
                    OutputDir = tmpDir,
                    Password = string.IsNullOrEmpty(_password) ? null : _password,
                    SelectedEntries = { r.FullEntryPath },
                });
                var file = Path.Combine(tmpDir, r.FullEntryPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file))
                {
                    var all = Directory.EnumerateFiles(tmpDir, "*", SearchOption.AllDirectories).ToList();
                    file = all.FirstOrDefault() ?? "";
                }
                if (string.IsNullOrEmpty(file) || !File.Exists(file))
                {
                    Ui.WarnBox("匣流", "无法提取预览文件");
                    return;
                }
                ShowPreview(file);
            }
            catch (Exception ex)
            {
                Ui.WarnBox("匣流", "预览失败：" + ex.Message);
            }
        }

        void ShowPreview(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var textExts = new[] { ".txt", ".json", ".xml", ".ini", ".cfg", ".conf", ".log", ".md", ".csv", ".yml", ".yaml", ".cs", ".py", ".js", ".html", ".css", ".bat", ".ps1", ".h", ".cpp" };
            if (textExts.Contains(ext))
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                if (content.Length > 200_000) content = content[..200_000] + "\n\n…（内容过长已截断）";
                var dlg = new PreviewDialog(Path.GetFileName(file), content, isText: true)
                { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
            }
            else if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp")
            {
                var dlg = new PreviewDialog(Path.GetFileName(file), "", isText: false, imagePath: file)
                { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
            }
            else
            {
                Ui.Info("匣流", "该类型不支持内置预览，已调用系统关联程序打开（只读查看）");
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); }
                catch { }
            }
            try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { }
        }

        void CopySha_Click(object sender, RoutedEventArgs e)
        {
            var entries = SelectedEntries();
            if (entries.Count != 1) { Ui.WarnBox("匣流", "请选择单个文件"); return; }
            try
            {
                var tmpDir = Path.Combine(AppPaths.TempDir, "sha-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);
                using var engine = new SevenZipEngine();
                engine.Extract(new SevenZipEngine.ExtractRequest
                {
                    ArchivePath = VolumeHelper.GetFirstVolume(_archivePath),
                    OutputDir = tmpDir,
                    Password = string.IsNullOrEmpty(_password) ? null : _password,
                    SelectedEntries = { entries[0] },
                });
                var file = Path.Combine(tmpDir, entries[0].Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file))
                    file = Directory.EnumerateFiles(tmpDir, "*", SearchOption.AllDirectories).FirstOrDefault() ?? "";
                if (file == "") return;
                var sha = ArchiveService.Sha256(file);
                Clipboard.SetText(sha);
                StatusText.Text = $"SHA-256 已复制：{sha[..16]}…";
                try { Directory.Delete(tmpDir, true); } catch { }
            }
            catch (Exception ex)
            {
                Ui.WarnBox("匣流", "计算失败：" + ex.Message);
            }
        }

        // ---------- 测试 / 拖放 ----------

        void Test_Click(object sender, RoutedEventArgs e)
        {
            if (_listing == null) { Ui.WarnBox("匣流", "请先打开压缩包"); return; }
            var archive = VolumeHelper.GetFirstVolume(_archivePath);
            SevenZipEngine? engine = null;
            var task = AppServices.Tasks.Enqueue(TaskKind.Test, $"校验: {Path.GetFileName(archive)}", "完整性测试", archive, "",
                ctx =>
                {
                    using var en = engine!;
                    en.Test(archive, string.IsNullOrEmpty(_password) ? null : _password);
                    ctx.ReportProgress(100, null, null);
                    AppServices.Notifications.Push("完整性测试通过", Path.GetFileName(archive) + " 未发现损坏", "success");
                });
            task.BeforeStart = t =>
            {
                engine = new SevenZipEngine();
                t.Pausable = engine;
                AppServices.Tasks.BindCancellation(t, engine.Cancel);
            };
        }

        void List_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (paths.Length > 0) LoadArchive(paths[0]);
        }
    }
}
