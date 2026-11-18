using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XiaFlow.App.Pages;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Update;

namespace XiaFlow.App
{
    public partial class MainWindow : Window
    {
        readonly Dictionary<string, Func<UserControl>> _pages;
        readonly Dictionary<string, UserControl> _cache = new();

        public MainWindow()
        {
            InitializeComponent();
            _pages = new Dictionary<string, Func<UserControl>>
            {
                ["home"] = () => new HomePage(),
                ["browser"] = () => new ArchiveBrowserPage(),
                ["compress"] = () => new QuickCompressPage(),
                ["extract"] = () => new QuickExtractPage(),
                ["batch"] = () => new BatchExtractPage(),
                ["backup"] = () => new BackupPage(),
                ["restore"] = () => new RestorePage(),
                ["tasks"] = () => new TaskCenterPage(),
                ["history"] = () => new HistoryPage(),
                ["settings"] = () => new SettingsPage(),
                ["about"] = () => new AboutPage(),
            };

            AppServices.Tasks.TaskAdded += _ => Dispatcher.BeginInvoke(UpdateStatusBar);
            AppServices.Tasks.TaskFinished += _ => Dispatcher.BeginInvoke(UpdateStatusBar);
            AppServices.Notifications.Added += _ => Dispatcher.BeginInvoke(UpdateStatusBar);
            Loaded += (_, _) => { Navigate("home"); UpdateStatusBar(); };
        }

        // ---------- 导航 ----------

        public void Navigate(string key)
        {
            if (_pages == null || !_pages.TryGetValue(key, out var factory)) return;
            if (!_cache.TryGetValue(key, out var page))
            {
                page = factory();
                _cache[key] = page;
            }
            // 同步选中态
            foreach (var rb in FindNavRadios())
            {
                if ((string)rb.Tag == key) rb.IsChecked = true;
            }
            PageHost.Content = page;
            // 页面淡入过渡
            if (AppServices.Settings.EnableAnimations)
            {
                var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                page.BeginAnimation(OpacityProperty, anim);
                var slide = new ThicknessAnimation(new Thickness(0, 14, 0, 0), new Thickness(0), TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                page.BeginAnimation(MarginProperty, slide);
            }
        }

        IEnumerable<RadioButton> FindNavRadios() =>
            from rb in this.FindChildren<RadioButton>() where rb.GroupName == "nav" select rb;

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // XAML 的 IsChecked="True" 在构造函数完成前触发，此时页面表尚未就绪
            if (_pages == null) return;
            if (sender is RadioButton rb && rb.Tag is string tag)
                Navigate(tag);
        }

        // ---------- 外部调用入口（CLI / 托盘） ----------

        public void OpenArchive(string path)
        {
            Navigate("browser");
            if (_cache.TryGetValue("browser", out var p) && p is ArchiveBrowserPage b)
                b.LoadArchive(path);
        }

        public void CompressPrefill(string[] paths, Core.ArchiveFormat? format, bool askPassword)
        {
            Navigate("compress");
            if (_cache.TryGetValue("compress", out var p) && p is QuickCompressPage c)
                c.Prefill(paths, format, askPassword);
        }

        public void BatchPrefill(string[] paths)
        {
            Navigate("batch");
            if (_cache.TryGetValue("batch", out var p) && p is BatchExtractPage b)
                b.ImportPaths(paths);
        }

        public void NotifyUpdateAvailable(UpdateInfo info)
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateBanner.Visibility = Visibility.Visible;
                UpdateBannerText.Text = $"发现新版本 {info.Version}";
                UpdateBanner.Tag = info;
            });
        }

        public void DismissUpdate() => UpdateBanner.Visibility = Visibility.Collapsed;

        // ---------- 标题栏 ----------

        void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void BtnMax_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (AppServices.Settings.CloseToTray) HideToTray();
            else ((App)Application.Current).ExitApplication();
        }

        public void HideToTray() => Hide();

        void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                BtnMax_Click(sender, e);
                return;
            }
            try { DragMove(); } catch { /* 窗口已最大化 */ }
        }

        void UpdateBanner_Click(object sender, MouseButtonEventArgs e)
        {
            Navigate("about");
        }

        void Notification_Click(object sender, MouseButtonEventArgs e)
        {
            AppServices.Notifications.MarkAllRead();
            UpdateStatusBar();
            Navigate("tasks");
        }

        // ---------- 状态栏 ----------

        void UpdateStatusBar()
        {
            var active = AppServices.Tasks.ActiveCount;
            TaskCountBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
            TaskCountText.Text = active.ToString();
            StatusText.Text = active > 0 ? $"{active} 个任务执行中" : "就绪";
            StatusDot.Fill = active > 0
                ? FindResource("Brush.Accent") as System.Windows.Media.Brush
                : FindResource("Brush.Success") as System.Windows.Media.Brush;
            var unread = AppServices.Notifications.UnreadCount;
            NotificationText.Text = unread > 0 ? $"通知 {unread}" : "通知";
        }
    }

    public static class WpfExtensions
    {
        public static IEnumerable<T> FindChildren<T>(this DependencyObject? start) where T : DependencyObject
        {
            if (start == null) yield break;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(current, i);
                    if (child is T match) yield return match;
                    queue.Enqueue(child);
                }
            }
        }
    }

}
