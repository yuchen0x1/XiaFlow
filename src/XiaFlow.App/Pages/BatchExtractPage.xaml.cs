using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Batch;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Tasks;

namespace XiaFlow.App.Pages
{
    public partial class BatchExtractPage : UserControl
    {
        /// <summary>列表行视图模型（包装 BatchItem）。</summary>
        public sealed class BatchItemVm : INotifyPropertyChanged
        {
            public BatchItem Item { get; }
            public BatchExtractPage Owner { get; }

            public BatchItemVm(BatchItem item, BatchExtractPage owner)
            {
                Item = item;
                Owner = owner;
            }

            public string Path => Item.Path;
            public string DisplayName => Item.DisplayName;
            public string FormatName => string.IsNullOrEmpty(Item.FormatName) ? "—" : Item.FormatName;
            public string SizeText => Ui.Fmt(Item.SizeBytes);
            public string FileCountText => Item.FileCount is int c ? c.ToString() : "—";

            public string StateText => BatchStateText.Of(Item.State);
            public Brush StateBrush => Ui.StateBrush(Item.State);

            public int Percent => Item.Percent ?? (Item.State == BatchState.Done ? 100 : 0);
            public Visibility ProgressVisibility =>
                Item.State is BatchState.Extracting or BatchState.Paused or BatchState.Done ? Visibility.Visible : Visibility.Collapsed;

            public string Detail => Item.State switch
            {
                BatchState.Done => $"{Ui.Fmt(Item.ExtractedBytes)} · {Item.ExtractedFiles} 个文件",
                BatchState.Failed => Item.Error ?? "",
                _ => Item.DetectInfo ?? (Item.SpeedBytesPerSec > 1 ? Ui.FmtSpeed(Item.SpeedBytesPerSec) : ""),
            };

            public Visibility CanExtractVisibility =>
                Item.State is BatchState.Ready or BatchState.ProcessedBefore ? Visibility.Visible : Visibility.Collapsed;
            public Visibility NeedPwdVisibility =>
                Item.State == BatchState.NeedsPassword ? Visibility.Visible : Visibility.Collapsed;
            public Visibility PauseVisibility =>
                Item.State == BatchState.Extracting ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ResumeVisibility =>
                Item.State == BatchState.Paused ? Visibility.Visible : Visibility.Collapsed;
            public Visibility CancelVisibility =>
                Item.State is BatchState.Extracting or BatchState.Queued or BatchState.Paused ? Visibility.Visible : Visibility.Collapsed;
            public Visibility OpenVisibility =>
                Item.State == BatchState.Done && Item.TargetDir != null ? Visibility.Visible : Visibility.Collapsed;

            public event PropertyChangedEventHandler? PropertyChanged;

            public void Refresh()
            {
                foreach (var p in new[]
                {
                    nameof(StateText), nameof(StateBrush), nameof(Percent), nameof(ProgressVisibility),
                    nameof(Detail), nameof(CanExtractVisibility), nameof(NeedPwdVisibility), nameof(PauseVisibility),
                    nameof(ResumeVisibility), nameof(CancelVisibility), nameof(OpenVisibility),
                    nameof(FileCountText), nameof(FormatName),
                })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
            }
        }

        readonly ObservableCollection<BatchItemVm> _vms = new();
        readonly List<BatchItem> _items = new();
        BatchAnalyzer? _analyzer;
        BatchRunner? _runner;
        BatchReport? _lastReport;
        readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        string _filter = "all";

        public BatchExtractPage()
        {
            InitializeComponent();
            ItemsGrid.ItemsSource = _vms;
            OutputRoot.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            _uiTimer.Tick += (_, _) => RefreshRows();
            _uiTimer.Start();
        }

        // ---------- 导入 / 检测 ----------

        void Zone_FilesDropped(string[] paths) => ImportPaths(paths);

        public void ImportPaths(string[] paths)
        {
            var imported = BatchAnalyzer.Import(paths, recursive: false);
            foreach (var it in imported.Where(i => i.IsPrimaryVolume))
                _items.Add(it);
            // 非首卷（分卷后续）也显示但标记跳过
            foreach (var it in imported.Where(i => !i.IsPrimaryVolume))
                _items.Add(it);
            RebuildVms();
            StartAnalyze();
        }

        void ScanFolder_Click(object sender, RoutedEventArgs e)
        {
            var dir = Ui.PickFolder("选择要扫描的文件夹");
            if (dir == null) return;
            var found = BatchAnalyzer.Import(new[] { dir }, recursive: true);
            foreach (var it in found) _items.Add(it);
            RebuildVms();
            StartAnalyze();
        }

        void StartAnalyze()
        {
            _analyzer ??= new BatchAnalyzer(AppServices.Vault);
            _analyzer.ItemUpdated -= OnItemUpdated;
            _analyzer.ItemUpdated += OnItemUpdated;
            BatchRunner.MarkProcessedBefore(_items);
            var toAnalyze = _items.Where(i => i.State is BatchState.PendingDetect or BatchState.Detecting or BatchState.Ready
                                              or BatchState.NeedsPassword or BatchState.Corrupt or BatchState.Unsupported
                                              or BatchState.Duplicate or BatchState.Skipped or BatchState.Failed
                                              or BatchState.ProcessedBefore).ToList();
            foreach (var i in toAnalyze) i.State = BatchState.PendingDetect;
            Task.Run(() => _analyzer.Analyze(toAnalyze));
        }

        void OnItemUpdated(BatchItem item)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var vm = _vms.FirstOrDefault(v => v.Item == item);
                vm?.Refresh();
                UpdateSummary();
            });
        }

        void RebuildVms()
        {
            _vms.Clear();
            foreach (var it in _items) _vms.Add(new BatchItemVm(it, this));
            ApplyFilter();
            UpdateSummary();
        }

        void ApplyFilter()
        {
            var view = CollectionViewSource.GetDefaultView(_vms);
            if (_filter == "all") { view.Filter = null; return; }
            view.Filter = o => o is BatchItemVm vm && vm.Item.State.ToString() == _filter;
        }

        void UpdateSummary()
        {
            var groups = _items.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count());
            var parts = new List<string> { $"共 {_items.Count} 项" };
            foreach (var (state, count) in groups)
                parts.Add($"{BatchStateText.Of(state)} {count}");
            SummaryText.Text = string.Join(" · ", parts);
        }

        void RefreshRows()
        {
            foreach (var vm in _vms) vm.Refresh();
        }

        // ---------- 筛选 / 设置 ----------

        void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            _filter = (string)((ComboBoxItem)FilterBox.SelectedItem).Tag;
            ApplyFilter();
        }

        void Mode_Changed(object sender, SelectionChangedEventArgs e)
        {
            // XAML 的 IsSelected 在 InitializeComponent 期间触发，此时控件尚未就绪
            if (TypeFilterPanel == null || PatternPanel == null || ModeBox.SelectedItem is not ComboBoxItem item) return;
            var tag = (string)item.Tag;
            TypeFilterPanel.Visibility = tag == "OnlyFileTypes" ? Visibility.Visible : Visibility.Collapsed;
            PatternPanel.Visibility = tag == "OnlyNamePattern" ? Visibility.Visible : Visibility.Collapsed;
        }

        void PickOutput_Click(object sender, RoutedEventArgs e)
        {
            var d = Ui.PickFolder("选择输出根目录", OutputRoot.Text);
            if (d != null) OutputRoot.Text = d;
        }

        void Reanalyze_Click(object sender, RoutedEventArgs e) => StartAnalyze();

        void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
            RebuildVms();
        }

        // ---------- 行操作 ----------

        void RowExtract_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BatchItemVm vm) StartBatch(new List<BatchItem> { vm.Item });
        }

        void RowPassword_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BatchItemVm vm) return;
            var pwd = Views.InputDialog.Ask(Window.GetWindow(this), "输入密码",
                $"压缩包「{vm.DisplayName}」需要密码：", "");
            if (string.IsNullOrEmpty(pwd)) return;
            vm.Item.Password = pwd;
            // 对同类密码任务应用
            vm.Item.State = BatchState.Ready;
            vm.Item.DetectInfo = "已输入密码";
            vm.Refresh();
        }

        void RowPause_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BatchItemVm vm) _runner?.Pause(vm.Item);
        }

        void RowResume_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BatchItemVm vm) _runner?.Resume(vm.Item);
        }

        void RowCancel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BatchItemVm vm) _runner?.Cancel(vm.Item);
        }

        void RowOpen_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BatchItemVm vm && vm.Item.TargetDir != null)
                Ui.OpenInExplorer(vm.Item.TargetDir);
        }

        // ---------- 批量控制 ----------

        BatchOptions BuildOptions()
        {
            var mode = (string)((ComboBoxItem)ModeBox.SelectedItem).Tag switch
            {
                "SingleDirectory" => BatchOutputMode.SingleDirectory,
                "PreserveStructure" => BatchOutputMode.PreserveStructure,
                "ByFileType" => BatchOutputMode.ByFileType,
                "OnlyFileTypes" => BatchOutputMode.OnlyFileTypes,
                "OnlyNamePattern" => BatchOutputMode.OnlyNamePattern,
                _ => BatchOutputMode.OwnFolderEach,
            };
            var conflict = ((ComboBoxItem)ConflictBox.SelectedItem).Tag switch
            {
                "Overwrite" => ConflictPolicy.Overwrite,
                "Skip" => ConflictPolicy.Skip,
                _ => ConflictPolicy.AutoRename,
            };
            int.TryParse((string)((ComboBoxItem)ParallelBox.SelectedItem).Tag, out var parallel);

            var types = TypeFilterBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return new BatchOptions
            {
                Mode = mode,
                OutputRoot = OutputRoot.Text,
                Conflict = conflict,
                Password = string.IsNullOrEmpty(GlobalPwd.Password) ? null : GlobalPwd.Password,
                UseVaultPasswords = UseVault.IsChecked == true,
                FileTypeFilter = types,
                NamePattern = PatternBox.Text,
                SkipProcessed = SkipProcessed.IsChecked == true,
                MaxParallel = Math.Max(1, parallel),
                Limits = AppServices.Settings.ToSafetyLimits(),
            };
        }

        void StartAll_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count == 0) { Ui.WarnBox("匣流", "请先导入压缩包"); return; }
            if (string.IsNullOrWhiteSpace(OutputRoot.Text)) { Ui.WarnBox("匣流", "请设置输出根目录"); return; }
            StartBatch(_items);
        }

        void PwdStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(GlobalPwd.Password))
            {
                Ui.WarnBox("匣流", "请先在统一密码框输入密码");
                return;
            }
            // 给等待密码的项应用统一密码并开始
            foreach (var it in _items.Where(i => i.State == BatchState.NeedsPassword && string.IsNullOrEmpty(i.Password)))
            {
                it.Password = GlobalPwd.Password;
                it.State = BatchState.Ready;
            }
            if (_items.Any(i => i.State == BatchState.Ready))
                StartBatch(_items);
        }

        void StartBatch(List<BatchItem> targets)
        {
            if (string.IsNullOrWhiteSpace(OutputRoot.Text)) { Ui.WarnBox("匣流", "请设置输出根目录"); return; }
            var opt = BuildOptions();

            _runner = new BatchRunner(AppServices.Vault);
            _runner.ItemChanged += item => Dispatcher.BeginInvoke(() =>
                _vms.FirstOrDefault(v => v.Item == item)?.Refresh());

            var resolvePassword = new Func<BatchItem, string?>(item =>
            {
                // 密码库 → 已输入密码
                if (opt.UseVaultPasswords)
                {
                    var m = AppServices.Vault.TryMatch(VolumeHelper.GetFirstVolume(item.Path));
                    if (m != null) return m.Password;
                }
                return string.IsNullOrEmpty(item.Password) ? opt.Password : item.Password;
            });

            var archiveCount = targets.Count(i => i.IsPrimaryVolume && !i.IsDuplicateOf);
            AppServices.Tasks.Enqueue(TaskKind.BatchExtract, $"批量解压 {archiveCount} 个压缩包",
                $"模式 {ModeBox.SelectedIndex + 1} → {opt.OutputRoot}", "", opt.OutputRoot,
                ctx =>
                {
                    ctx.ReportProgress(0, null, null);
                    _lastReport = _runner.Run(targets, opt, resolvePassword);
                    var total = targets.Count;
                    ctx.ReportProgress(100, _lastReport.TotalExtractedBytes, null);
                    AppServices.Notifications.Push("批量解压完成",
                        $"成功 {_lastReport.Success} / 失败 {_lastReport.Failed} / 跳过 {_lastReport.Skipped}", "success");
                });

            var win = Window.GetWindow(this) as MainWindow;
            win?.Navigate("tasks");
        }

        void PauseAll_Click(object sender, RoutedEventArgs e) => _runner?.PauseAll();
        void ResumeAll_Click(object sender, RoutedEventArgs e) => _runner?.ResumeAll();
        void CancelAll_Click(object sender, RoutedEventArgs e) => _runner?.CancelAll();

        // ---------- 报告 ----------

        void ExportReport_Click(object sender, RoutedEventArgs e)
        {
            if (_lastReport == null)
            {
                // 生成当前列表状态报告
                _lastReport = new BatchReport { StartedAt = DateTime.Now, FinishedAt = DateTime.Now, OutputRoot = OutputRoot.Text, Items = _items };
            }
            var file = Ui.PickSaveFile("导出批量报告", $"XiaFlow_批量解压报告_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                "CSV 文件|*.csv|TXT 文件|*.txt");
            if (file == null) return;
            File.WriteAllText(file, file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? _lastReport.ToText() : _lastReport.ToCsv(), System.Text.Encoding.UTF8);
            Ui.OpenInExplorer(Path.GetDirectoryName(file)!);
        }
    }
}
