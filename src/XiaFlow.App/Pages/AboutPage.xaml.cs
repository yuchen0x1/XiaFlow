using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Update;

namespace XiaFlow.App.Pages
{
    public partial class AboutPage : UserControl
    {
        UpdateInfo? _pendingUpdate;
        CancellationTokenSource? _downloadCancellation;

        public AboutPage()
        {
            InitializeComponent();
            ChannelText.Text = AppServices.Settings.UpdateChannel == "beta" ? "测试版" : "稳定版";
            UpdateStatus.Text = UpdateService.IsInstalled
                ? "安装官方签名更新前，请先等待所有任务完成。"
                : "便携版请从发布页下载并解压新版 ZIP。";
        }

        async void Check_Click(object sender, RoutedEventArgs e)
        {
            BtnCheck.IsEnabled = false;
            SetPending(null);
            UpdateStatus.Text = "正在连接更新源…";
            try
            {
                var info = await AppServices.Updater.CheckAsync(AppServices.Settings.UpdateChannel);
                if (info == null)
                    UpdateStatus.Text = "更新源上没有可用版本。";
                else if (!UpdateService.IsNewer(info.Version, Branding.AppVersion.ToString(3)))
                    UpdateStatus.Text = $"当前已是最新版本（远端 {info.Version}）";
                else
                {
                    SetPending(info);
                    var instructions = BtnDownload.Visibility == Visibility.Visible
                        ? "安装前将校验发布签名、包大小及 SHA-256。"
                        : "请在发布页选择对应安装包或便携版。";
                    UpdateStatus.Text = $"发现新版本 {info.Version}（{info.PublishedAt:yyyy-MM-dd}）\n" +
                        $"安装包大小：{(info.DownloadSize > 0 ? Ui.Fmt(info.DownloadSize) : "未知")}\n{instructions}";
                }
            }
            catch (Exception ex)
            {
                UpdateStatus.Text = "检查更新失败：" + ex.Message;
            }
            finally
            {
                BtnCheck.IsEnabled = true;
            }
        }

        void SetPending(UpdateInfo? info)
        {
            _pendingUpdate = info;
            NotesCard.Visibility = BtnSkip.Visibility = BtnDetails.Visibility = BtnLater.Visibility =
                info == null ? Visibility.Collapsed : Visibility.Visible;
            NotesBox.Text = info?.Notes ?? "";
            BtnDownload.Visibility = info?.CanInstall == true && UpdateService.IsInstalled && UpdateService.IsSupportedPlatform
                ? Visibility.Visible : Visibility.Collapsed;
        }

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate is not { } info || _downloadCancellation != null) return;
            if (AppServices.Tasks.ActiveCount != 0)
            {
                UpdateStatus.Text = "请先等待所有任务完成。";
                return;
            }

            using var cancellation = new CancellationTokenSource();
            _downloadCancellation = cancellation;
            BtnCheck.IsEnabled = BtnDownload.IsEnabled = BtnSkip.IsEnabled = BtnLater.IsEnabled = false;
            BtnCancel.Visibility = UpdateProgress.Visibility = Visibility.Visible;
            UpdateProgress.Value = 0;
            UpdateStatus.Text = "正在验证发布签名…";
            var elapsed = Stopwatch.StartNew();
            try
            {
                var progress = new Progress<(long Downloaded, long Total)>(value =>
                {
                    if (_downloadCancellation != cancellation) return;
                    UpdateProgress.Value = value.Downloaded * 100.0 / value.Total;
                    var speed = (long)(value.Downloaded / Math.Max(1, elapsed.Elapsed.TotalSeconds));
                    UpdateStatus.Text = $"下载 {Ui.Fmt(value.Downloaded)} / {Ui.Fmt(value.Total)} · {Ui.Fmt(speed)}/秒";
                });
                await AppServices.Updater.InstallAsync(info, progress, cancellation.Token, () => AppServices.Tasks.ActiveCount == 0);
                ((App)Application.Current).ExitApplication();
            }
            catch (OperationCanceledException)
            {
                UpdateStatus.Text = cancellation.IsCancellationRequested ? "已取消更新。" : "下载超时，请重试。";
            }
            catch (Exception ex)
            {
                UpdateStatus.Text = "更新失败：" + ex.Message;
            }
            finally
            {
                _downloadCancellation = null;
                BtnCheck.IsEnabled = BtnDownload.IsEnabled = BtnSkip.IsEnabled = BtnLater.IsEnabled = true;
                BtnCancel.Visibility = UpdateProgress.Visibility = Visibility.Collapsed;
            }
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();

        void Later_Click(object sender, RoutedEventArgs e)
        {
            SetPending(null);
            UpdateStatus.Text = "可以在此随时重新检查更新。";
            if (Window.GetWindow(this) is MainWindow main)
                main.DismissUpdate();
        }

        void Skip_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate == null) return;
            AppServices.Settings.SkippedVersion = _pendingUpdate.Version;
            AppServices.Settings.Save();
            UpdateStatus.Text = $"已跳过版本 {_pendingUpdate.Version}。";
            SetPending(null);
            if (Window.GetWindow(this) is MainWindow main)
                main.DismissUpdate();
        }

        void Details_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate != null) OpenUrl(_pendingUpdate.ReleaseUrl);
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.WarnBox("匣流", "无法打开发行页：" + ex.Message); }
        }

        void OpenHome_Click(object sender, RoutedEventArgs e) => OpenUrl(Branding.HomepageUrl);

        void OpenLicense_Click(object sender, RoutedEventArgs e)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "licenses");
            Ui.OpenInExplorer(Directory.Exists(dir) ? dir : AppContext.BaseDirectory);
        }

        void OpenData_Click(object sender, RoutedEventArgs e) => Ui.OpenInExplorer(AppPaths.RootDir);
    }
}
