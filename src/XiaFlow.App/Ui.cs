using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using XiaFlow.Core;
using XiaFlow.Core.Batch;

namespace XiaFlow.App
{
    /// <summary>UI 工具函数。</summary>
    public static class Ui
    {
        public static string Fmt(long bytes) => Core.Security.PathGuard.FormatBytes(bytes);

        public static string FmtSpeed(double bytesPerSec) =>
            bytesPerSec > 1 ? Fmt((long)bytesPerSec) + "/s" : "";

        public static string FmtEta(TimeSpan? t) =>
            t is { } v && v.TotalSeconds > 1 ? $"{(int)v.TotalHours:00}:{v.Minutes:00}:{v.Seconds:00}" : "";

        public static string? PickFile(string title, string filter = "所有文件 (*.*)|*.*")
        {
            var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = false };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        public static string[]? PickFiles(string title, string filter = "所有文件 (*.*)|*.*")
        {
            var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
            return dlg.ShowDialog() == true ? dlg.FileNames : null;
        }

        public static string? PickFolder(string title, string? initial = null)
        {
            var dlg = new OpenFolderDialog
            {
                Title = title,
                InitialDirectory = initial,
            };
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }

        public static string? PickSaveFile(string title, string fileName, string filter)
        {
            var dlg = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        public static void Info(string title, string msg) =>
            MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);

        public static void WarnBox(string title, string msg) =>
            MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);

        public static bool Confirm(string title, string msg) =>
            MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, MessageBoxOptions.DefaultDesktopOnly) == MessageBoxResult.Yes;

        public static Brush StateBrush(BatchState state)
        {
            var app = Application.Current;
            return state switch
            {
                BatchState.Ready or BatchState.Done => (Brush)app.FindResource("Brush.Success"),
                BatchState.NeedsPassword or BatchState.Split or BatchState.Duplicate or BatchState.ProcessedBefore or BatchState.PendingDetect or BatchState.Detecting => (Brush)app.FindResource("Brush.Warn"),
                BatchState.Corrupt or BatchState.Failed or BatchState.Unsupported => (Brush)app.FindResource("Brush.Danger"),
                BatchState.Extracting or BatchState.Queued => (Brush)app.FindResource("Brush.Accent"),
                _ => (Brush)app.FindResource("Brush.Text2"),
            };
        }

        /// <summary>打开目录（资源管理器）。</summary>
        public static void OpenInExplorer(string path)
        {
            try
            {
                if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
                else if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch { /* ignore */ }
        }

        /// <summary>密码强度标签。</summary>
        public static (string label, Brush brush) Strength(string pwd)
        {
            var (score, label) = PasswordStrength.Evaluate(pwd);
            var app = Application.Current;
            var brush = score switch
            {
                <= 2 => (Brush)app.FindResource("Brush.Danger"),
                3 => (Brush)app.FindResource("Brush.Warn"),
                _ => (Brush)app.FindResource("Brush.Success"),
            };
            return (label, brush);
        }
    }
}
