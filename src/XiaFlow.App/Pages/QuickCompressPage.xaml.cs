using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Tasks;

namespace XiaFlow.App.Pages
{
    public partial class QuickCompressPage : UserControl
    {
        readonly ObservableCollection<string> _inputs = new();

        public QuickCompressPage()
        {
            InitializeComponent();
            InputList.ItemsSource = _inputs;
            var threads = Environment.ProcessorCount;
            for (int i = 1; i <= threads; i++)
                ThreadsBox.Items.Add(new ComboBoxItem { Content = i == threads ? $"{i}（全部核心）" : i.ToString(), Tag = i });
            ThreadsBox.SelectedIndex = threads - 1;
            OutDir.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            FormatBox.SelectedIndex = 0;
        }

        // ---------- 导入 ----------

        void Zone_FilesDropped(string[] paths) => AddPaths(paths);

        public void Prefill(string[] paths, ArchiveFormat? format, bool askPassword)
        {
            AddPaths(paths);
            if (format == ArchiveFormat.Zip)
            {
                FormatBox.SelectedIndex = 1;
                Format_Changed(null, null!);
            }
            if (askPassword)
            {
                UsePwd.IsChecked = true;
                Pwd1.Focus();
            }
        }

        void AddPaths(IEnumerable<string> paths)
        {
            foreach (var p in paths)
                if (!_inputs.Contains(p)) _inputs.Add(p);
        }

        void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var files = Ui.PickFiles("选择要压缩的文件");
            if (files != null) AddPaths(files);
        }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dir = Ui.PickFolder("选择要压缩的文件夹");
            if (dir != null) AddPaths(new[] { dir });
        }

        void RemoveInput_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string p) _inputs.Remove(p);
        }

        void ClearList_Click(object sender, RoutedEventArgs e) => _inputs.Clear();

        // ---------- 参数联动 ----------

        string CurrentExt() =>
            (FormatBox.SelectedItem as ComboBoxItem)?.Tag switch
            {
                "zip" => ".zip",
                "tar" => ".tar",
                "gz" => ".gz",
                "bz2" => ".bz2",
                "xz" => ".xz",
                "wim" => ".wim",
                _ => ".7z",
            };

        void Format_Changed(object sender, SelectionChangedEventArgs e)
        {
            OutExt.Text = CurrentExt();
            bool is7z = OutExt.Text == ".7z";
            EncryptNames.IsEnabled = is7z && UsePwd.IsChecked == true;
            SolidBox.IsEnabled = is7z;
            DictBox.IsEnabled = is7z;
            VolumeBox.IsEnabled = is7z || OutExt.Text == ".zip";
            bool singleFileOnly = OutExt.Text is ".gz" or ".bz2" or ".xz";
            if (singleFileOnly)
                Ui.WarnBox("格式提示", "GZIP / BZIP2 / XZ 为单文件流格式：仅压缩第一个选中的文件，或先打包为 TAR/7Z。");
        }

        void ProMode_Changed(object sender, RoutedEventArgs e)
        {
            ProPanel.Visibility = ProMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        void UsePwd_Changed(object sender, RoutedEventArgs e)
        {
            PwdPanel.Visibility = UsePwd.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            EncryptNames.IsEnabled = OutExt.Text == ".7z" && UsePwd.IsChecked == true;
            if (UsePwd.IsChecked != true)
            {
                Pwd1.Clear();
                Pwd2.Clear();
                StrengthText.Text = "";
            }
        }

        void Pwd_Changed(object sender, RoutedEventArgs e)
        {
            var (label, brush) = Ui.Strength(Pwd1.Password);
            StrengthText.Text = pwd2Match() ? $"强度：{label}" : "两次输入不一致";
            StrengthText.Foreground = pwd2Match() ? brush : (Brush)FindResource("Brush.Danger");
        }

        bool pwd2Match() => Pwd1.Password.Length > 0 && Pwd1.Password == Pwd2.Password;

        void SavePwd_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Pwd1.Password)) return;
            var name = Views.InputDialog.Ask(Window.GetWindow(this), "保存到密码库", "为该密码取一个便于辨认的名称：", "我的密码");
            if (!string.IsNullOrWhiteSpace(name))
                AppServices.Vault.Add(name, Pwd1.Password);
        }

        void PickOutDir_Click(object sender, RoutedEventArgs e)
        {
            var d = Ui.PickFolder("选择压缩包保存目录", OutDir.Text);
            if (d != null) OutDir.Text = d;
        }

        // ---------- 开始 ----------

        void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_inputs.Count == 0)
            {
                Ui.WarnBox("匣流", "请先添加要压缩的文件或文件夹");
                return;
            }
            if (string.IsNullOrWhiteSpace(OutName.Text))
            {
                Ui.WarnBox("匣流", "请填写压缩包名称");
                return;
            }
            if (UsePwd.IsChecked == true && !pwd2Match())
            {
                Ui.WarnBox("匣流", "两次输入的密码不一致");
                return;
            }

            var format = OutExt.Text switch
            {
                ".zip" => ArchiveFormat.Zip,
                ".tar" => ArchiveFormat.Tar,
                ".gz" => ArchiveFormat.GZip,
                ".bz2" => ArchiveFormat.BZip2,
                ".xz" => ArchiveFormat.XZ,
                ".wim" => ArchiveFormat.Wim,
                _ => ArchiveFormat.SevenZ,
            };
            if (format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.XZ && _inputs.Count > 1)
            {
                Ui.WarnBox("匣流", "该格式为单文件流格式，请只压缩一个文件");
                return;
            }

            string? password = UsePwd.IsChecked == true ? Pwd1.Password : null;
            var outPath = Path.Combine(OutDir.Text, OutName.Text + OutExt.Text);
            if (File.Exists(outPath) && !Ui.Confirm("匣流", $"文件已存在：\n{outPath}\n是否覆盖？"))
                return;

            var level = (CompressionLevelOption)int.Parse((string)((ComboBoxItem)LevelBox.SelectedItem).Tag);
            var volumes = long.TryParse((string)((ComboBoxItem)VolumeBox.SelectedItem).Tag, out var vmb) && vmb > 0
                ? vmb * 1024 * 1024 : (long?)null;
            var dict = long.TryParse((string)((ComboBoxItem)DictBox.SelectedItem).Tag, out var d) && d > 0 ? d : (long?)null;
            var threads = (int)((ComboBoxItem)ThreadsBox.SelectedItem).Tag;
            var excludes = ExcludeBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            bool openFolder = AppServices.Settings.OpenFolderAfterDone;

            SevenZipEngine? engine = null;
            var task = AppServices.Tasks.Enqueue(TaskKind.Compress, $"压缩: {OutName.Text}{OutExt.Text}",
                $"{Ui.Fmt(InputBytes())} → {outPath}", string.Join("; ", _inputs), outPath,
                ctx =>
                {
                    var listing = InputTotalBytes();
                    ctx.ReportProgress(0, null, null);
                    ArchiveService.Compress(engine!, new CompressOptions
                    {
                        InputPaths = _inputs.ToList(),
                        OutputArchive = outPath,
                        Format = format,
                        Level = level,
                        Threads = threads,
                        DictionaryBytes = dict,
                        Solid = SolidBox.IsChecked == true,
                        VolumeSizeBytes = volumes,
                        Password = password,
                        EncryptFileNames = EncryptNames.IsChecked == true,
                        ExcludePatterns = excludes,
                        TestAfterCreate = TestBox.IsChecked == true,
                    }, default);
                    ctx.ReportProgress(100, listing, null);
                    AppServices.Notifications.Push("压缩完成", $"{Path.GetFileName(outPath)} 已生成", "success");
                    if (openFolder)
                        Dispatcher.BeginInvoke(() => Ui.OpenInExplorer(Path.GetDirectoryName(outPath)!));
                });
            task.BeforeStart = t =>
            {
                engine = new SevenZipEngine();
                t.Pausable = engine;
                AppServices.Tasks.BindCancellation(t, engine.Cancel);
            };

            var win = Window.GetWindow(this) as MainWindow;
            win?.Navigate("tasks");
        }

        long InputBytes() => _inputs.Sum(p =>
        {
            try
            {
                if (File.Exists(p)) return new FileInfo(p).Length;
                return new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch { return 0L; } });
            }
            catch { return 0L; }
        });

        long InputTotalBytes() => InputBytes();
    }
}
