using System;
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
        string? _downloadedFile;

        public AboutPage()
        {
            InitializeComponent();
            ChannelText.Text = AppServices.Settings.UpdateChannel == "beta" ? "测试版" : "稳定版";
        }

        async void Check_Click(object sender, RoutedEventArgs e)
        {
            BtnCheck.IsEnabled = false;
            UpdateStatus.Text = "正在连接更新源…";
            try
            {
                var info = await AppServices.Updater.CheckAsync(AppServices.Settings.UpdateChannel);
                if (info == null)
                {
                    UpdateStatus.Text = "更新源上没有可用版本。";
                    return;
                }
                if (!UpdateService.IsNewer(info.Version, Branding.AppVersion.ToString(3)))
                {
                    UpdateStatus.Text = $"当前已是最新版本（远端 {info.Version}）";
                    return;
                }
                _pendingUpdate = info;
                NotesCard.Visibility = Visibility.Visible;
                NotesBox.Text = info.Notes ?? "";
                UpdateStatus.Text = $"发现新版本 {info.Version}（发布于 {info.PublishedAt:yyyy-MM-dd}）\n" +
                    $"下载大小：约 {Ui.Fmt(info.DownloadSize)}\n" +
                    (info.Sha256 != null ? "已提供 SHA-256 摘要，安装前将自动校验。" : "⚠ 该版本未提供 SHA-256 摘要，将下载但不会自动执行安装。");
                BtnDownload.Visibility = Visibility.Visible;
                BtnSkip.Visibility = Visibility.Visible;
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

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate?.DownloadUrl == null)
            {
                Ui.WarnBox("匣流", "该版本没有可用的下载资产");
                return;
            }
            BtnDownload.IsEnabled = false;
            UpdateProgress.Visibility = Visibility.Visible;
            UpdateProgress.Value = 0;
            try
            {
                var progress = new Progress<double>(v => UpdateProgress.Value = v);
                _downloadedFile = await AppServices.Updater.DownloadAsync(_pendingUpdate, progress);
                UpdateStatus.Text = "下载完成，正在校验 SHA-256…";

                if (_pendingUpdate.Sha256 != null)
                {
                    bool ok = UpdateService.VerifyFile(_downloadedFile, _pendingUpdate.Sha256);
                    if (!ok)
                    {
                        File.Delete(_downloadedFile);
                        _downloadedFile = null;
                        UpdateStatus.Text = "SHA-256 校验失败！已删除未经验证的文件。请从官方发布页手动下载。";
                        return;
                    }
                    UpdateStatus.Text = "校验通过，准备安装…";
                }
                else
                {
                    UpdateStatus.Text = "该版本未提供 SHA-256 摘要，出于安全考虑不会自动执行。请手动运行：" + _downloadedFile;
                    Ui.OpenInExplorer(Path.GetDirectoryName(_downloadedFile)!);
                    return;
                }

                AppServices.Updater.Install(_downloadedFile, silent: true);
                UpdateStatus.Text = "安装程序已启动，匣流将退出并完成更新。";
                await Task.Delay(1500);
                ((App)Application.Current).ExitApplication();
            }
            catch (Exception ex)
            {
                UpdateStatus.Text = "下载失败：" + ex.Message;
            }
            finally
            {
                BtnDownload.IsEnabled = true;
            }
        }

        void Skip_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate == null) return;
            AppServices.Settings.SkippedVersion = _pendingUpdate.Version;
            AppServices.Settings.Save();
            UpdateStatus.Text = $"已跳过版本 {_pendingUpdate.Version}。";
            BtnDownload.Visibility = Visibility.Collapsed;
            BtnSkip.Visibility = Visibility.Collapsed;
        }

        void OpenHome_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(Branding.HomepageUrl) { UseShellExecute = true }); } catch { }
        }

        void OpenLicense_Click(object sender, RoutedEventArgs e)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "licenses");
            Ui.OpenInExplorer(Directory.Exists(dir) ? dir : AppContext.BaseDirectory);
        }

        void OpenData_Click(object sender, RoutedEventArgs e) => Ui.OpenInExplorer(AppPaths.RootDir);
    }
}
