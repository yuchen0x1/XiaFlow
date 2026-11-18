using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Backup;
using XiaFlow.Core.Engines;

namespace XiaFlow.App.Views
{
    public partial class BackupTaskDialog : Window
    {
        readonly BackupTaskModel? _existing;
        readonly ObservableCollection<string> _sources = new();
        List<PasswordEntry> _vaultEntries = new();
        bool _loading = true;

        public BackupTaskDialog(BackupTaskModel? existing)
        {
            InitializeComponent();
            _existing = existing;
            SourceList.ItemsSource = _sources;
            _vaultEntries = AppServices.Vault.GetAll().ToList();
            PwdBox.ItemsSource = _vaultEntries.Select(v => v.Name).ToList();

            if (existing != null)
            {
                NameBox.Text = existing.Name;
                foreach (var s in existing.SourcePaths) _sources.Add(s);
                DestBox.Text = existing.DestDir;
                Select(FormatBox, existing.Format == ArchiveFormat.Zip ? "zip" : "7z");
                Select(LevelBox, ((int)existing.Level).ToString());
                Select(StrategyBox, existing.Strategy.ToString());
                Select(FullEveryBox, existing.FullBackupEveryN.ToString());
                Select(ScheduleBox, existing.Schedule.ToString());
                IntervalBox.Text = existing.IntervalMinutes.ToString();
                TimeBox.Text = existing.TimeOfDay;
                DayBox.Text = existing.DayOfMonth.ToString();
                D1.IsChecked = (existing.Days & DaysOfWeek.Mon) != 0;
                D2.IsChecked = (existing.Days & DaysOfWeek.Tue) != 0;
                D3.IsChecked = (existing.Days & DaysOfWeek.Wed) != 0;
                D4.IsChecked = (existing.Days & DaysOfWeek.Thu) != 0;
                D5.IsChecked = (existing.Days & DaysOfWeek.Fri) != 0;
                D6.IsChecked = (existing.Days & DaysOfWeek.Sat) != 0;
                D0.IsChecked = (existing.Days & DaysOfWeek.Sun) != 0;
                Select(KeepDaysBox, existing.KeepDays.ToString());
                Select(KeepCountBox, existing.KeepCount.ToString());
                EncBox.IsChecked = existing.Encrypted;
                VerifyBox.IsChecked = existing.VerifyAfterBackup;
                HashBox.IsChecked = existing.HashVerify;
                if (existing.PasswordVaultId != null)
                {
                    var idx = _vaultEntries.FindIndex(v => v.Id == existing.PasswordVaultId);
                    if (idx >= 0) { PwdBox.IsEnabled = true; PwdBox.SelectedIndex = idx; }
                }
                if (existing.NextRun == null)
                    existing.NextRun = ScheduleCalc.NextRun(existing, DateTime.Now);
            }
            else
            {
                DestBox.Text = string.IsNullOrEmpty(AppServices.Settings.DefaultBackupDir)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : AppServices.Settings.DefaultBackupDir;
            }
            Schedule_Changed(null, null!);
            _loading = false;
        }

        static void Select(ComboBox box, string tag)
        {
            foreach (ComboBoxItem i in box.Items)
                if ((string)i.Tag == tag) { box.SelectedItem = i; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        ComboBoxItem Sel(ComboBox box) => (ComboBoxItem)box.SelectedItem;

        void Schedule_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ScheduleBox?.SelectedItem == null || TimePanel == null) return;
            var kind = (ScheduleKind)Enum.Parse(typeof(ScheduleKind), (string)Sel(ScheduleBox).Tag);
            TimePanel.Visibility = kind is ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Monthly ? Visibility.Visible : Visibility.Collapsed;
            WeeklyPanel.Visibility = kind == ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
            MonthlyPanel.Visibility = kind == ScheduleKind.Monthly ? Visibility.Visible : Visibility.Collapsed;
            IntervalPanel.Visibility = kind == ScheduleKind.Interval ? Visibility.Visible : Visibility.Collapsed;
        }

        void Pwd_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
        }

        void AddSource_Click(object sender, RoutedEventArgs e)
        {
            var d = Ui.PickFolder("选择要备份的文件夹");
            if (d != null && !_sources.Contains(d)) _sources.Add(d);
        }

        void RemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (SourceList.SelectedItem is string s) _sources.Remove(s);
        }

        void PickDest_Click(object sender, RoutedEventArgs e)
        {
            var d = Ui.PickFolder("选择备份保存位置", DestBox.Text);
            if (d != null) DestBox.Text = d;
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) { Ui.WarnBox("匣流", "请填写任务名称"); return; }
            if (_sources.Count == 0) { Ui.WarnBox("匣流", "请至少添加一个源文件夹"); return; }
            if (string.IsNullOrWhiteSpace(DestBox.Text)) { Ui.WarnBox("匣流", "请选择备份保存位置"); return; }
            if (!TimeSpan.TryParse(TimeBox.Text, out _)) { Ui.WarnBox("匣流", "执行时间格式应为 HH:mm"); return; }

            var task = _existing ?? new BackupTaskModel();
            task.Name = NameBox.Text.Trim();
            task.SourcePaths = _sources.ToList();
            task.DestDir = DestBox.Text;
            task.Format = (string)Sel(FormatBox).Tag == "zip" ? ArchiveFormat.Zip : ArchiveFormat.SevenZ;
            task.Level = (CompressionLevelOption)int.Parse((string)Sel(LevelBox).Tag);
            task.Strategy = Enum.Parse<BackupKind>((string)Sel(StrategyBox).Tag);
            task.FullBackupEveryN = int.Parse((string)Sel(FullEveryBox).Tag);
            task.Schedule = Enum.Parse<ScheduleKind>((string)Sel(ScheduleBox).Tag);
            task.IntervalMinutes = int.TryParse(IntervalBox.Text, out var im) ? Math.Max(5, im) : 60;
            task.TimeOfDay = TimeBox.Text;
            task.DayOfMonth = int.TryParse(DayBox.Text, out var dom) ? Math.Clamp(dom, 1, 31) : 1;
            DaysOfWeek days = 0;
            if (D1.IsChecked == true) days |= DaysOfWeek.Mon;
            if (D2.IsChecked == true) days |= DaysOfWeek.Tue;
            if (D3.IsChecked == true) days |= DaysOfWeek.Wed;
            if (D4.IsChecked == true) days |= DaysOfWeek.Thu;
            if (D5.IsChecked == true) days |= DaysOfWeek.Fri;
            if (D6.IsChecked == true) days |= DaysOfWeek.Sat;
            if (D0.IsChecked == true) days |= DaysOfWeek.Sun;
            task.Days = days == 0 ? DaysOfWeek.Mon : days;
            task.KeepDays = int.Parse((string)Sel(KeepDaysBox).Tag);
            task.KeepCount = int.Parse((string)Sel(KeepCountBox).Tag);
            task.Encrypted = EncBox.IsChecked == true;
            task.VerifyAfterBackup = VerifyBox.IsChecked == true;
            task.HashVerify = HashBox.IsChecked == true;
            task.PasswordVaultId = null;
            if (task.Encrypted)
            {
                if (PwdBox.SelectedIndex < 0)
                {
                    Ui.WarnBox("匣流", "加密备份需要选择一个已保存的密码（可在设置-安全中添加密码库条目）");
                    return;
                }
                task.PasswordVaultId = _vaultEntries[PwdBox.SelectedIndex].Id;
            }
            task.Enabled = task.Enabled;
            task.NextRun = ScheduleCalc.NextRun(task, DateTime.Now);
            AppServices.BackupStore.SaveTask(task);

            // 同步注册 Windows 计划任务（软件未打开时也可执行）
            var exe = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, Branding.ExecutableName);
            if (task.Schedule != ScheduleKind.Manual && task.Enabled)
            {
                var ok = BackupScheduler.RegisterWindowsTask(task, exe);
                if (!ok) Ui.WarnBox("匣流", "已保存任务，但注册 Windows 计划任务失败——软件打开期间仍会按时执行。");
            }
            DialogResult = true;
        }
    }
}
