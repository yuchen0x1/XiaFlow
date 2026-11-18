using System;
using System.Collections.Generic;
using System.Linq;
using XiaFlow.Core.Engines;

namespace XiaFlow.Core.Backup
{
    /// <summary>备份类型。</summary>
    public enum BackupKind { Full, Incremental, Differential }
    public enum ScheduleKind { Manual, Interval, Daily, Weekly, Monthly, OnLogon }

    [Flags]
    public enum DaysOfWeek { None = 0, Mon = 1, Tue = 2, Wed = 4, Thu = 8, Fri = 16, Sat = 32, Sun = 64 }

    /// <summary>备份任务。</summary>
    public sealed class BackupTaskModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public List<string> SourcePaths { get; set; } = new();
        public string DestDir { get; set; } = "";
        public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZ;
        public CompressionLevelOption Level { get; set; } = CompressionLevelOption.Normal;
        public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;
        public int IntervalMinutes { get; set; } = 60;          // Interval 模式
        public string TimeOfDay { get; set; } = "02:00";        // Daily/Weekly/Monthly
        public DaysOfWeek Days { get; set; } = DaysOfWeek.Mon | DaysOfWeek.Wed | DaysOfWeek.Fri;
        public int DayOfMonth { get; set; } = 1;
        /// <summary>备份策略：仅全量 / 全量+增量 / 全量+差异。</summary>
        public BackupKind Strategy { get; set; } = BackupKind.Full;
        public int FullBackupEveryN { get; set; } = 7;          // 增量模式下每隔 N 次强制全量
        public string? PasswordVaultId { get; set; }            // 密码库引用（不明文存任务）
        public bool Encrypted { get; set; }
        public long VolumeSizeMB { get; set; } = 0;
        public bool VerifyAfterBackup { get; set; } = true;
        public int KeepDays { get; set; } = 30;                 // 0 = 永久
        public int KeepCount { get; set; } = 0;                 // 0 = 不限数量
        public bool Enabled { get; set; } = true;
        public DateTime? LastRun { get; set; }
        public DateTime? NextRun { get; set; }
        public int IncrementalCount { get; set; }               // 距上次全量的增量次数
        public bool HashVerify { get; set; }                    // 内容哈希识别变化（较慢）
        public string? LastError { get; set; }

        public string ScheduleText => Schedule switch
        {
            ScheduleKind.Manual => "手动",
            ScheduleKind.Interval => $"每 {IntervalMinutes} 分钟",
            ScheduleKind.Daily => $"每天 {TimeOfDay}",
            ScheduleKind.Weekly => $"每周 {DaysText()} {TimeOfDay}",
            ScheduleKind.Monthly => $"每月 {DayOfMonth} 日 {TimeOfDay}",
            ScheduleKind.OnLogon => "登录时",
            _ => ""
        };

        public string StrategyText => Strategy switch
        {
            BackupKind.Full => "完整备份",
            BackupKind.Incremental => "完整 + 增量",
            BackupKind.Differential => "完整 + 差异",
            _ => ""
        };

        string DaysText()
        {
            var names = new (DaysOfWeek, string)[]
            {
                (DaysOfWeek.Mon, "一"), (DaysOfWeek.Tue, "二"), (DaysOfWeek.Wed, "三"),
                (DaysOfWeek.Thu, "四"), (DaysOfWeek.Fri, "五"), (DaysOfWeek.Sat, "六"), (DaysOfWeek.Sun, "日")
            };
            var s = names.Where(n => (Days & n.Item1) != 0).Select(n => n.Item2).ToList();
            return s.Count > 0 ? string.Join("、", s) : "未设置";
        }
    }

    /// <summary>备份历史记录。</summary>
    public sealed class BackupHistoryRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TaskId { get; set; } = "";
        public string TaskName { get; set; } = "";
        public DateTime Time { get; set; }
        public BackupKind Type { get; set; }
        public string ArchiveFile { get; set; } = "";
        public long SourceBytes { get; set; }
        public long ArchiveBytes { get; set; }
        public int FileCount { get; set; }
        public string Status { get; set; } = "";     // 成功 / 失败 / 无变化
        public bool Verified { get; set; }
        public double DurationSeconds { get; set; }
        public string? Error { get; set; }
        public int ChainIndex { get; set; } = -1;

        public string TypeText => Type switch
        {
            BackupKind.Full => "完整",
            BackupKind.Incremental => "增量",
            BackupKind.Differential => "差异",
            _ => ""
        };

        public double Ratio => SourceBytes > 0 && ArchiveBytes > 0
            ? Math.Round((1.0 - (double)ArchiveBytes / SourceBytes) * 100, 1) : 0;
    }

    /// <summary>备份运行结果。</summary>
    public sealed class BackupRunResult
    {
        public bool Success { get; set; }
        public bool NoChanges { get; set; }
        public string? ArchiveFile { get; set; }
        public BackupKind Kind { get; set; }
        public long SourceBytes { get; set; }
        public long ArchiveBytes { get; set; }
        public int FileCount { get; set; }
        public int ChangedFiles { get; set; }
        public bool Verified { get; set; }
        public string? Error { get; set; }
        public List<string> CleanupLog { get; } = new();
    }

    /// <summary>调度计算。</summary>
    public static class ScheduleCalc
    {
        public static DateTime? NextRun(BackupTaskModel task, DateTime after)
        {
            if (!task.Enabled || task.Schedule == ScheduleKind.Manual) return null;
            switch (task.Schedule)
            {
                case ScheduleKind.Interval:
                    var baseT = task.LastRun ?? after;
                    var next = baseT.AddMinutes(Math.Max(5, task.IntervalMinutes));
                    while (next <= after) next = next.AddMinutes(Math.Max(5, task.IntervalMinutes));
                    return next;
                case ScheduleKind.Daily:
                    return NextDaily(task, after);
                case ScheduleKind.Weekly:
                    var nd = NextDaily(task, after);
                    while (nd != DateTime.MaxValue && !DayMatches(task, nd))
                        nd = NextDaily(task, nd.AddDays(1).Date.AddMinutes(-1).AddSeconds(1).Date + Time(task));
                    return nd;
                case ScheduleKind.Monthly:
                    var cand = new DateTime(after.Year, after.Month, Math.Min(task.DayOfMonth, DateTime.DaysInMonth(after.Year, after.Month)), 0, 0, 0) + Time(task);
                    if (cand <= after)
                    {
                        var y = after.AddMonths(1);
                        cand = new DateTime(y.Year, y.Month, Math.Min(task.DayOfMonth, DateTime.DaysInMonth(y.Year, y.Month)), 0, 0, 0) + Time(task);
                    }
                    return cand;
                case ScheduleKind.OnLogon:
                    return null; // 由登录触发
                default:
                    return null;
            }
        }

        static TimeSpan Time(BackupTaskModel task)
        {
            try
            {
                var parts = (task.TimeOfDay ?? "02:00").Split(':');
                return new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), 0);
            }
            catch { return new TimeSpan(2, 0, 0); }
        }

        static DateTime NextDaily(BackupTaskModel task, DateTime after)
        {
            var t = after.Date + Time(task);
            if (t <= after) t = t.AddDays(1);
            return t;
        }

        static bool DayMatches(BackupTaskModel task, DateTime d)
        {
            return d.DayOfWeek switch
            {
                DayOfWeek.Monday => (task.Days & DaysOfWeek.Mon) != 0,
                DayOfWeek.Tuesday => (task.Days & DaysOfWeek.Tue) != 0,
                DayOfWeek.Wednesday => (task.Days & DaysOfWeek.Wed) != 0,
                DayOfWeek.Thursday => (task.Days & DaysOfWeek.Thu) != 0,
                DayOfWeek.Friday => (task.Days & DaysOfWeek.Fri) != 0,
                DayOfWeek.Saturday => (task.Days & DaysOfWeek.Sat) != 0,
                DayOfWeek.Sunday => (task.Days & DaysOfWeek.Sun) != 0,
                _ => false
            };
        }
    }
}
