using System;
using System.IO;
using System.Reflection;

namespace XiaFlow.Core
{
    /// <summary>品牌与应用标识常量（全项目统一使用 XiaFlow / 匣流）。</summary>
    public static class Branding
    {
        public const string AppName = "XiaFlow";
        public const string DisplayName = "匣流 XiaFlow";
        public const string ProductFullName = "XiaFlow Archive Manager";
        public const string ProductDescription = "匣流专业文件压缩与归档管理器";
        public const string ExecutableName = "XiaFlow.exe";
        public const string PackageId = "com.xiaflow.ArchiveManager";
        public const string RegistryCompanyKey = "Software\\XiaFlow";
        public const string UpdateChannelDefault = "stable";
        public const string RepositoryUrl = "https://github.com/yuchen0x1/XiaFlow";
        public const string ReleasesUrl = RepositoryUrl + "/releases";
        public const string HomepageUrl = RepositoryUrl;

        public static Version AppVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v ?? new Version(1, 0, 0);
            }
        }

        public static string VersionText => "v" + AppVersion.ToString(3);
    }

    /// <summary>统一的应用数据目录（配置 / 密码库 / 历史 / 日志 / 缓存）。</summary>
    public static class AppPaths
    {
        static string? _root;

        /// <summary>数据根目录（可用 XIAFLOW_DATA_DIR 环境变量重定向，测试/便携场景使用）。</summary>
        public static string RootDir => _root ??=
            Environment.GetEnvironmentVariable("XIAFLOW_DATA_DIR") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Branding.AppName);

        public static string SettingsFile => Path.Combine(RootDir, "settings.json");
        public static string PasswordVaultFile => Path.Combine(RootDir, "passwords.dat");
        public static string TaskHistoryFile => Path.Combine(RootDir, "task-history.json");
        public static string RecentArchivesFile => Path.Combine(RootDir, "recent-archives.json");
        public static string BackupDataDir => Path.Combine(RootDir, "backup");
        public static string BackupTasksFile => Path.Combine(BackupDataDir, "tasks.json");
        public static string BackupHistoryFile => Path.Combine(BackupDataDir, "history.json");
        public static string LogsDir => Path.Combine(RootDir, "logs");
        public static string CacheDir => Path.Combine(RootDir, "cache");
        public static string TempDir => Path.Combine(CacheDir, "temp");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(BackupDataDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(TempDir);
        }

        /// <summary>仅测试使用：清除缓存目录，使 RootDir 重新解析。</summary>
        public static void ResetForTests() => _root = null;
    }
}
