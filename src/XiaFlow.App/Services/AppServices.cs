using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XiaFlow.Core;
using XiaFlow.Core.Backup;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Integration;
using XiaFlow.Core.Tasks;
using XiaFlow.Core.Update;

namespace XiaFlow.App.Services
{
    /// <summary>组合根：应用级服务单例（页面直接使用）。</summary>
    public static class AppServices
    {
        public static AppSettings Settings { get; private set; } = null!;
        public static PasswordVault Vault { get; private set; } = null!;
        public static TaskManager Tasks { get; private set; } = null!;
        public static BackupStore BackupStore { get; private set; } = null!;
        public static BackupRunner BackupRunner { get; private set; } = null!;
        public static BackupScheduler Scheduler { get; private set; } = null!;
        public static NotificationStore Notifications { get; private set; } = null!;
        public static UpdateService Updater { get; private set; } = null!;

        public static event Action<Core.Backup.BackupTaskModel>? BackupDue;

        public static void Initialize()
        {
            Core.Logging.Logger.Initialize();
            AppPaths.EnsureDirectories();

            Settings = AppSettings.Load();
            Vault = new PasswordVault();
            Tasks = new TaskManager(Math.Max(1, Settings.MaxParallelTasks));
            Notifications = new NotificationStore();
            BackupStore = new BackupStore();
            BackupRunner = new BackupRunner(BackupStore, Vault);
            Scheduler = new BackupScheduler(BackupStore, t => BackupDue?.Invoke(t));
            Updater = new UpdateService { Repository = Settings.UpdateSource };

            BackupDue += task => EnqueueBackupTask(task);
        }

        public static ArchiveTask EnqueueBackupTask(BackupTaskModel task)
        {
            var t = Tasks.Enqueue(TaskKind.Backup, $"备份: {task.Name}", task.StrategyText, string.Join("; ", task.SourcePaths), task.DestDir,
                ctx =>
                {
                    ctx.ReportProgress(0, null, null);
                    var result = BackupRunner.Run(task, ctx, default);
                    if (result.Success)
                    {
                        var msg = result.NoChanges
                            ? $"备份任务「{task.Name}」无文件变化，已跳过"
                            : $"备份任务「{task.Name}」完成：{Core.Security.PathGuard.FormatBytes(result.ArchiveBytes)}";
                        Notifications.Push("匣流备份", msg, result.NoChanges ? "info" : "success");
                    }
                    else
                    {
                        Notifications.Push("匣流备份失败", $"任务「{task.Name}」失败：{result.Error}", "error");
                        throw new ArchiveException(ArchiveErrorKind.IoError, result.Error ?? "备份失败");
                    }
                });
            return t;
        }

        /// <summary>应用启动时统一落盘设置变更。</summary>
        public static void ApplyGeneralSettings()
        {
            Tasks.MaxParallel = Math.Max(1, Settings.MaxParallelTasks);
            Themes.ThemeManager.Apply(Settings.Theme);
            try { XiaFlow.Core.Integration.AutoStart.Set(Settings.StartWithWindows); }
            catch (Exception ex) { Core.Logging.Logger.Warn("开机启动设置失败: " + ex.Message); }
            Settings.Save();
        }
    }
}
