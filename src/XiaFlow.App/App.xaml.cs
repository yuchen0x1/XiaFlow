using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XiaFlow.App.Services;
using XiaFlow.App.Themes;
using XiaFlow.App.Views;
using XiaFlow.Core;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Integration;
using XiaFlow.Core.Logging;
using XiaFlow.Core.Tasks;
using XiaFlow.Core.Update;

namespace XiaFlow.App
{
    public partial class App : Application
    {
        Mutex? _mutex;
        TrayIcon? _tray;
        MainWindow? _main;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, args) =>
            {
                Logger.Error("UI 未处理异常", args.Exception);
                MessageBox.Show("发生未处理的错误：" + args.Exception.Message, "匣流 XiaFlow",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // 单实例：第二个实例把参数转发给第一个
            bool createdNew;
            _mutex = new Mutex(true, SingleInstance.MutexName, out createdNew);
            if (!createdNew)
            {
                SingleInstance.SendArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());
                Shutdown();
                return;
            }

            Logger.Initialize();
            AppServices.Initialize();
            ThemeManager.Apply(AppServices.Settings.Theme);

            _main = new MainWindow();
            _main.Show();
            SetupTray();
            StartPipeServer();
            StartUpdateTimer();

            HandleLaunchArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());
        }

        // ---------- 托盘 ----------

        void SetupTray()
        {
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "XiaFlow.ico");
                _tray = new TrayIcon(iconPath, Branding.DisplayName + " — " + Branding.ProductDescription);
                _tray.Activated += RestoreMainWindow;
                _tray.MenuRequested += ShowTrayMenu;
                // 通知中心 → 系统气泡
                AppServices.Notifications.Added += n =>
                    Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(n.Title, n.Message, n.Level));
            }
            catch (Exception ex)
            {
                Logger.Warn("托盘初始化失败: " + ex.Message);
            }
        }

        void ShowTrayMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu
            {
                Style = null,
            };
            void Add(string label, Action action, bool isDefault = false)
            {
                var item = new System.Windows.Controls.MenuItem { Header = label, FontWeight = isDefault ? FontWeights.SemiBold : FontWeights.Normal };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            Add($"打开 {Branding.DisplayName}", () => { RestoreMainWindow(); }, isDefault: true);
            menu.Items.Add(new System.Windows.Controls.Separator());
            Add("快速压缩…", () => { RestoreMainWindow(); _main?.Navigate("compress"); });
            Add("快速解压…", () => { RestoreMainWindow(); _main?.Navigate("extract"); });
            Add("智能批量解压…", () => { RestoreMainWindow(); _main?.Navigate("batch"); });
            Add("立即运行全部备份", () =>
            {
                foreach (var t in AppServices.BackupStore.Tasks.Where(t => t.Enabled))
                    AppServices.EnqueueBackupTask(t);
            });
            menu.Items.Add(new System.Windows.Controls.Separator());
            Add("退出", ExitApplication);
            menu.IsOpen = true;
        }

        void RestoreMainWindow()
        {
            if (_main == null) return;
            _main.Show();
            _main.WindowState = WindowState.Normal;
            _main.Activate();
        }

        public void ExitApplication()
        {
            try
            {
                AppServices.Scheduler.Dispose();
                AppServices.Tasks.CancelAll();
                _tray?.Dispose();
            }
            catch { /* ignore */ }
            finally
            {
                Shutdown();
            }
        }

        // ---------- 单实例管道 ----------

        void StartPipeServer()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new System.IO.Pipes.NamedPipeServerStream(SingleInstance.PipeName,
                            System.IO.Pipes.PipeDirection.In);
                        await server.WaitForConnectionAsync();
                        using var reader = new StreamReader(server);
                        var line = await reader.ReadToEndAsync();
                        var args = line.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        Dispatcher.BeginInvoke(() => HandleLaunchArgs(args));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("管道服务异常: " + ex.Message);
                        await Task.Delay(1000);
                    }
                }
            });
        }

        // ---------- 启动参数 / 右键菜单动作 ----------

        void HandleLaunchArgs(string[] args)
        {
            if (args.Length == 0) return;
            Logger.Info("启动参数: " + string.Join(' ', args.Select(RedactArg)));

            string RedactArg(string a) => a.StartsWith("--run-backup") ? "--run-backup <id>" : a;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var a = args[i];
                    var rest = args.Skip(i + 1).Where(x => !x.StartsWith("--")).ToArray();
                    switch (a)
                    {
                        case "--minimized":
                            if (_main != null) { _main.HideToTray(); }
                            return;

                        case "--screenshot":
                        {
                            var page = rest.FirstOrDefault() ?? "home";
                            var outArg = args.SkipWhile(x => x != "--out").Skip(1).FirstOrDefault();
                            var loadArg = args.SkipWhile(x => x != "--load").Skip(1).FirstOrDefault();
                            CaptureScreenshot(page, outArg ?? Path.Combine(Path.GetTempPath(), "xiaflow-shot.png"), loadArg);
                            return;
                        }

                        case "--compress":
                            if (rest.Length > 0)
                            {
                                RestoreMainWindow();
                                _main?.CompressPrefill(rest, format: null, askPassword: false);
                            }
                            return;

                        case "--compress-zip":
                            if (rest.Length > 0)
                            {
                                RestoreMainWindow();
                                _main?.CompressPrefill(rest, format: ArchiveFormat.Zip, askPassword: false);
                            }
                            return;

                        case "--compress-pwd":
                            if (rest.Length > 0)
                            {
                                RestoreMainWindow();
                                _main?.CompressPrefill(rest, format: null, askPassword: true);
                            }
                            return;

                        case "--open":
                            if (rest.Length > 0)
                            {
                                RestoreMainWindow();
                                _main?.OpenArchive(rest[0]);
                            }
                            return;

                        case "--extract-here":
                            if (rest.Length > 0) ExtractQuick(rest, here: true);
                            return;

                        case "--extract-to-folder":
                            if (rest.Length > 0) ExtractQuick(rest, here: false);
                            return;

                        case "--batch":
                            if (rest.Length > 0)
                            {
                                RestoreMainWindow();
                                _main?.BatchPrefill(rest);
                            }
                            return;

                        case "--test":
                            if (rest.Length > 0)
                            {
                                foreach (var arc in rest) EnqueueTestTask(arc);
                                RestoreMainWindow();
                                _main?.Navigate("tasks");
                            }
                            return;

                        case "--run-backup":
                            if (rest.Length > 0)
                            {
                                RunBackupById(rest[0]);
                                return;
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("处理启动参数失败", ex);
            }
        }

        void ExtractQuick(string[] archives, bool here)
        {
            foreach (var arc in archives)
            {
                if (!File.Exists(arc)) continue;
                var outDir = here
                    ? Path.GetDirectoryName(Path.GetFullPath(arc)) ?? "."
                    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(arc)) ?? ".",
                        Path.GetFileNameWithoutExtension(VolumeHelper.GetFirstVolume(arc)));
                EnqueueExtractTask(arc, outDir);
            }
            RestoreMainWindow();
            _main?.Navigate("tasks");
        }

        ArchiveTask EnqueueExtractTask(string archive, string outDir)
        {
            var name = Path.GetFileName(archive);
            SevenZipEngine? engine = null;
            var task = AppServices.Tasks.Enqueue(TaskKind.Extract, $"解压: {name}", "解压到 " + outDir, archive, outDir,
                ctx =>
                {
                    using var e = engine!;
                    var listing = e.List(archive);
                    ctx.ReportProgress(0, null, null);
                    var plan = ArchiveService.Extract(e, new ExtractOptions
                    {
                        ArchivePath = archive,
                        OutputDir = outDir,
                        Conflict = AppServices.Settings.DefaultConflictPolicy,
                        Limits = AppServices.Settings.ToSafetyLimits(),
                    }, default);
                    ctx.ReportProgress(100, listing.TotalUncompressed, null);
                    RecentArchives.Add(archive);
                });
            task.BeforeStart = t =>
            {
                engine = new SevenZipEngine();
                t.Pausable = engine;
                AppServices.Tasks.BindCancellation(t, engine.Cancel);
            };
            return task;
        }

        ArchiveTask EnqueueTestTask(string archive)
        {
            SevenZipEngine? engine = null;
            var task = AppServices.Tasks.Enqueue(TaskKind.Test, $"校验: {Path.GetFileName(archive)}", "完整性测试", archive, "", ctx =>
            {
                using var e = engine!;
                var listing = e.Test(archive);
                ctx.ReportProgress(100, listing.TotalUncompressed, null);
            });
            task.BeforeStart = t =>
            {
                engine = new SevenZipEngine();
                t.Pausable = engine;
                AppServices.Tasks.BindCancellation(t, engine.Cancel);
            };
            return task;
        }

        void RunBackupById(string id)
        {
            var task = AppServices.BackupStore.Find(id);
            if (task == null)
            {
                Logger.Warn($"找不到备份任务 {id}");
                Shutdown();
                return;
            }
            var captured = AppServices.EnqueueBackupTask(task);
            // 后台任务：完成或失败后退出（通知已由任务内部推送）
            AppServices.Tasks.TaskFinished += t =>
            {
                if (t == captured)
                {
                    Dispatcher.BeginInvoke(Shutdown);
                }
            };
        }

        // ---------- 更新检查 ----------

        System.Windows.Threading.DispatcherTimer? _updateTimer;
        string? _notifiedUpdate;
        bool _checkingUpdate;

        void StartUpdateTimer()
        {
            if (!AppServices.Settings.AutoCheckUpdate) return;
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromHours(Math.Max(1, AppServices.Settings.UpdateCheckIntervalHours)),
            };
            _updateTimer.Tick += (_, _) => CheckUpdateQuiet();
            _updateTimer.Start();
            Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => Dispatcher.BeginInvoke(CheckUpdateQuiet));
        }

        async void CheckUpdateQuiet()
        {
            if (_checkingUpdate || !AppServices.Settings.AutoCheckUpdate) return;
            _checkingUpdate = true;
            try
            {
                var info = await AppServices.Updater.CheckAsync(AppServices.Settings.UpdateChannel);
                if (info != null && UpdateService.IsNewer(info.Version, Branding.AppVersion.ToString(3)) &&
                    info.Version != AppServices.Settings.SkippedVersion && info.Version != _notifiedUpdate)
                {
                    _notifiedUpdate = info.Version;
                    AppServices.Notifications.Push("发现新版本",
                        $"匣流 {info.Version} 已发布，当前 {Branding.VersionText}。可在“关于”页面查看并更新。", "info");
                    _main?.NotifyUpdateAvailable(info);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("更新检查失败: " + ex.Message);
            }
            finally
            {
                _checkingUpdate = false;
            }
        }

        // ---------- 截图模式（文档用：渲染真实页面后自动退出） ----------

        void CaptureScreenshot(string page, string outFile, string? loadPath = null)
        {
            if (_main == null) return;
            if (!string.IsNullOrEmpty(loadPath))
            {
                if (page == "browser") _main.OpenArchive(loadPath);
                else if (page == "batch") _main.BatchPrefill(new[] { loadPath });
                else if (page == "compress") _main.CompressPrefill(new[] { loadPath }, null, false);
            }
            _main.Width = 1440;
            _main.Height = 900;
            _main.Left = 0;
            _main.Top = 0;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
            int step = 0;
            timer.Tick += (_, _) =>
            {
                step++;
                if (step == 1)
                {
                    _main.Navigate(page);
                }
                else
                {
                    timer.Stop();
                    try
                    {
                        var w = (int)Math.Ceiling(_main.ActualWidth);
                        var h = (int)Math.Ceiling(_main.ActualHeight);
                        var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
                        rtb.Render(_main);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                        using var fs = File.Create(outFile);
                        enc.Save(fs);
                        Logger.Info("截图已保存: " + outFile);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("截图失败: " + ex.Message);
                    }
                    finally
                    {
                        Shutdown();
                    }
                }
            };
            timer.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            _mutex?.ReleaseMutex();
            base.OnExit(e);
        }
    }

    /// <summary>轻量命令绑定。</summary>
    public class RelayCommand : System.Windows.Input.ICommand
    {
        readonly Func<object?, Task> _exec;
        public RelayCommand(Func<object?, Task> exec) => _exec = exec;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _ = _exec(parameter);
    }
}
