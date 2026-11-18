using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XiaFlow.App.Services;
using XiaFlow.Core.Integration;
using XiaFlow.Core.Tasks;

namespace XiaFlow.App.Pages
{
    public partial class HistoryPage : UserControl
    {
        public HistoryPage()
        {
            InitializeComponent();
            IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Refresh(); };
        }

        void Refresh()
        {
            TaskHistoryList.ItemsSource = AppServices.Tasks.History()
                .OrderByDescending(t => t.EndTime ?? t.StartTime)
                .Select(t => new
                {
                    TimeText = (t.EndTime ?? t.StartTime)?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    KindText = TaskKindText.Of(t.Kind),
                    t.Title,
                    t.StatusText,
                    SizeText = t.TotalBytes > 0 ? Ui.Fmt(t.TotalBytes) : (t.ProcessedBytes > 0 ? Ui.Fmt(t.ProcessedBytes) : ""),
                    ErrorText = t.Error ?? "",
                }).ToList();

            RecentList.ItemsSource = RecentArchives.Load()
                .Select(r => new
                {
                    r.Path,
                    TimeText = r.OpenedAt.ToString("yyyy-MM-dd HH:mm"),
                    PinText = r.Pinned ? "★" : "",
                }).ToList();
        }

        void Recent_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var win = Window.GetWindow(this) as MainWindow;
            if ((RecentList.SelectedItem as dynamic)?.Path is string path)
                win?.OpenArchive(path);
        }

        void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!Ui.Confirm("匣流", "确定清空全部任务历史吗？")) return;
            AppServices.Tasks.ClearHistory();
            Refresh();
        }
    }
}
