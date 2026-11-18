using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XiaFlow.Core.Tasks;

namespace XiaFlow.Core
{
    public enum ThemeMode { Dark, Light, FollowSystem }

    /// <summary>全部设置（真实生效，JSON 持久化）。</summary>
    public sealed class AppSettings
    {
        // 常规
        public bool StartWithWindows { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        public bool EnableAnimations { get; set; } = true;
        public double UiZoom { get; set; } = 1.0;

        // 外观
        public ThemeMode Theme { get; set; } = ThemeMode.Dark;

        // 压缩默认
        public ArchiveFormat DefaultFormat { get; set; } = ArchiveFormat.SevenZ;
        public Engines.CompressionLevelOption DefaultLevel { get; set; } = Engines.CompressionLevelOption.Normal;
        public int DefaultThreads { get; set; } = Environment.ProcessorCount;
        public long DefaultVolumeSizeMB { get; set; } = 0; // 0 = 不分卷
        public bool TestAfterCompress { get; set; }
        public bool OpenFolderAfterDone { get; set; } = true;
        public List<string> DefaultExcludes { get; set; } = new() { "Thumbs.db", "desktop.ini", ".DS_Store" };

        // 解压默认
        public string DefaultExtractDir { get; set; } = "";
        public Engines.ConflictPolicy DefaultConflictPolicy { get; set; } = Engines.ConflictPolicy.AutoRename;
        public bool ExtractToFolderByName { get; set; } = true;

        // 批量处理
        public int MaxParallelTasks { get; set; } = 2;
        public int BatchMaxParallel { get; set; } = 2;
        public bool BatchSkipProcessed { get; set; }

        // 备份
        public string DefaultBackupDir { get; set; } = "";
        public int BackupRetryCount { get; set; } = 1;
        public bool RunMissedBackupOnStartup { get; set; } = true;
        public int DefaultKeepDays { get; set; } = 30;
        public bool NotifyOnBackupDone { get; set; } = true;

        // 安全
        public long MaxExtractTotalGB { get; set; } = 20;
        public long MaxExtractSingleFileGB { get; set; } = 8;
        public int MaxExtractFileCount { get; set; } = 200000;
        public bool WarnBeforeOverwrite { get; set; } = true;

        // 系统集成
        public bool ContextMenuEnabled { get; set; } = true;
        public bool ContextMenuCompress { get; set; } = true;
        public bool ContextMenuExtract { get; set; } = true;
        public bool ContextMenuOpen { get; set; } = true;

        // 更新
        public bool AutoCheckUpdate { get; set; } = true;
        public int UpdateCheckIntervalHours { get; set; } = 24;
        public string UpdateChannel { get; set; } = Branding.UpdateChannelDefault;
        public bool AutoDownloadUpdate { get; set; }
        public bool AutoInstallUpdate { get; set; }
        public string? SkippedVersion { get; set; }
        public string UpdateSource { get; set; } = Branding.RepositoryUrl;

        public static AppSettings Instance { get; private set; } = new();

        public static AppSettings Load()
        {
            Instance = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
            return Instance;
        }

        public void Save()
        {
            JsonStore.Save(AppPaths.SettingsFile, this);
            Instance = this;
        }

        public Security.ExtractSafetyLimits ToSafetyLimits() => new()
        {
            MaxTotalUncompressedBytes = Math.Max(1, MaxExtractTotalGB) * 1024L * 1024 * 1024,
            MaxSingleFileBytes = Math.Max(1, MaxExtractSingleFileGB) * 1024L * 1024 * 1024,
            MaxFileCount = MaxExtractFileCount,
        };
    }
}
