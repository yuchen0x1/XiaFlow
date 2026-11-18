using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XiaFlow.App.Services;
using XiaFlow.Core;
using XiaFlow.Core.Integration;
using ThemeMode = XiaFlow.Core.ThemeMode;
using ContextMenu = XiaFlow.Core.Integration.ContextMenu;

namespace XiaFlow.App.Pages
{
    public partial class SettingsPage : UserControl
    {
        readonly ObservableCollection<PasswordEntry> _vaultRows = new();
        bool _loading = true;

        public SettingsPage()
        {
            InitializeComponent();
            IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Load(); };
        }

        void Load()
        {
            _loading = true;
            var s = AppServices.Settings;

            StartWithWindows.IsChecked = s.StartWithWindows;
            MinimizeToTray.IsChecked = s.MinimizeToTray;
            CloseToTray.IsChecked = s.CloseToTray;
            OpenFolderAfterDone.IsChecked = s.OpenFolderAfterDone;
            Select(ThemeBox, s.Theme.ToString());
            EnableAnimations.IsChecked = s.EnableAnimations;
            Select(DefaultFormatBox, s.DefaultFormat == Core.ArchiveFormat.Zip ? "zip" : "7z");
            TestAfterCompress.IsChecked = s.TestAfterCompress;
            DefaultExtractDir.Text = s.DefaultExtractDir;
            Select(ConflictBox, s.DefaultConflictPolicy.ToString());
            ExtractToFolderByName.IsChecked = s.ExtractToFolderByName;
            Select(MaxParallelBox, s.MaxParallelTasks.ToString());
            BatchSkipProcessed.IsChecked = s.BatchSkipProcessed;
            DefaultBackupDir.Text = s.DefaultBackupDir;
            RunMissedBackup.IsChecked = s.RunMissedBackupOnStartup;
            NotifyOnBackupDone.IsChecked = s.NotifyOnBackupDone;
            MaxTotalGB.Text = s.MaxExtractTotalGB.ToString();
            MaxSingleGB.Text = s.MaxExtractSingleFileGB.ToString();
            MaxFileCount.Text = s.MaxExtractFileCount.ToString();
            WarnBeforeOverwrite.IsChecked = s.WarnBeforeOverwrite;
            ContextMenuCompress.IsChecked = s.ContextMenuCompress;
            ContextMenuExtract.IsChecked = s.ContextMenuExtract;
            ContextMenuOpen.IsChecked = s.ContextMenuOpen;
            AutoCheckUpdate.IsChecked = s.AutoCheckUpdate;
            Select(UpdateIntervalBox, s.UpdateCheckIntervalHours.ToString());
            Select(ChannelBox, s.UpdateChannel);
            UpdateSourceBox.Text = s.UpdateSource;

            VaultList.ItemsSource = _vaultRows;
            _vaultRows.Clear();
            foreach (var v in AppServices.Vault.GetAll()) _vaultRows.Add(v);
            _loading = false;
        }

        static void Select(ComboBox box, string tag)
        {
            foreach (ComboBoxItem i in box.Items)
                if ((string)i.Tag == tag) { box.SelectedItem = i; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        void Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || ThemeBox == null) return;
            var s = AppServices.Settings;

            s.StartWithWindows = StartWithWindows.IsChecked == true;
            s.MinimizeToTray = MinimizeToTray.IsChecked == true;
            s.CloseToTray = CloseToTray.IsChecked == true;
            s.OpenFolderAfterDone = OpenFolderAfterDone.IsChecked == true;
            s.Theme = Enum.Parse<ThemeMode>((string)((ComboBoxItem)ThemeBox.SelectedItem).Tag);
            s.EnableAnimations = EnableAnimations.IsChecked == true;
            s.DefaultFormat = (string)((ComboBoxItem)DefaultFormatBox.SelectedItem).Tag == "zip"
                ? Core.ArchiveFormat.Zip : Core.ArchiveFormat.SevenZ;
            s.TestAfterCompress = TestAfterCompress.IsChecked == true;
            s.DefaultExtractDir = DefaultExtractDir.Text;
            s.DefaultConflictPolicy = Enum.Parse<Core.Engines.ConflictPolicy>((string)((ComboBoxItem)ConflictBox.SelectedItem).Tag);
            s.ExtractToFolderByName = ExtractToFolderByName.IsChecked == true;
            s.MaxParallelTasks = int.Parse((string)((ComboBoxItem)MaxParallelBox.SelectedItem).Tag);
            s.BatchSkipProcessed = BatchSkipProcessed.IsChecked == true;
            s.DefaultBackupDir = DefaultBackupDir.Text;
            s.RunMissedBackupOnStartup = RunMissedBackup.IsChecked == true;
            s.NotifyOnBackupDone = NotifyOnBackupDone.IsChecked == true;
            s.MaxExtractTotalGB = long.TryParse(MaxTotalGB.Text, out var a) ? Math.Max(1, a) : 20;
            s.MaxExtractSingleFileGB = long.TryParse(MaxSingleGB.Text, out var b) ? Math.Max(1, b) : 8;
            s.MaxExtractFileCount = int.TryParse(MaxFileCount.Text, out var c) ? Math.Max(100, c) : 200000;
            s.WarnBeforeOverwrite = WarnBeforeOverwrite.IsChecked == true;
            s.AutoCheckUpdate = AutoCheckUpdate.IsChecked == true;
            s.UpdateCheckIntervalHours = int.Parse((string)((ComboBoxItem)UpdateIntervalBox.SelectedItem).Tag);
            s.UpdateChannel = (string)((ComboBoxItem)ChannelBox.SelectedItem).Tag;
            s.UpdateSource = UpdateSourceBox.Text.Trim();

            AppServices.ApplyGeneralSettings();
        }

        void Integration_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var s = AppServices.Settings;
            s.ContextMenuCompress = ContextMenuCompress.IsChecked == true;
            s.ContextMenuExtract = ContextMenuExtract.IsChecked == true;
            s.ContextMenuOpen = ContextMenuOpen.IsChecked == true;
            s.Save();

            try
            {
                if (s.ContextMenuCompress || s.ContextMenuExtract || s.ContextMenuOpen)
                {
                    if (s.ContextMenuCompress) XiaFlow.Core.Integration.ContextMenu.RegisterCompressMenu();
                    if (s.ContextMenuExtract || s.ContextMenuOpen) XiaFlow.Core.Integration.ContextMenu.RegisterExtractMenu();
                    XiaFlow.Core.Integration.FileAssociation.RegisterAllSupported();
                }
                else
                {
                    XiaFlow.Core.Integration.ContextMenu.RemoveAll();
                }
            }
            catch (Exception ex)
            {
                Ui.WarnBox("匣流", "注册表操作失败：" + ex.Message);
            }
        }

        void PickExtractDir_Click(object sender, RoutedEventArgs e)
        {
            var d = Ui.PickFolder("选择默认解压目录", DefaultExtractDir.Text);
            if (d != null) { DefaultExtractDir.Text = d; Changed(sender, e!); }
        }

        void PickBackupDir_Click(object sender, RoutedEventArgs e)
        {
            var d = Ui.PickFolder("选择默认备份目录", DefaultBackupDir.Text);
            if (d != null) { DefaultBackupDir.Text = d; Changed(sender, e!); }
        }

        void AddPwd_Click(object sender, RoutedEventArgs e)
        {
            var name = Views.InputDialog.Ask(Window.GetWindow(this), "添加密码", "密码名称：", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            var pwd = Views.InputDialog.Ask(Window.GetWindow(this), "添加密码", $"「{name}」的密码内容：", "");
            if (string.IsNullOrEmpty(pwd)) return;
            AppServices.Vault.Add(name, pwd);
            _vaultRows.Clear();
            foreach (var v in AppServices.Vault.GetAll()) _vaultRows.Add(v);
        }

        void RemovePwd_Click(object sender, RoutedEventArgs e)
        {
            if (VaultList.SelectedItem is not PasswordEntry entry) return;
            if (!Ui.Confirm("匣流", $"从密码库删除「{entry.Name}」？")) return;
            AppServices.Vault.Remove(entry.Id);
            _vaultRows.Remove(entry);
        }
    }
}
