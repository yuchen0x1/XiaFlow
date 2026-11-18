using System;
using System.Collections.Generic;
using XiaFlow.Core.Engines;

namespace XiaFlow.Core.Tasks
{
    public enum TaskKind
    {
        Compress, Extract, BatchExtract, Test, Backup, Restore, Cleanup, Update
    }

    public enum TaskStatus
    {
        Queued, Running, Paused, Completed, CompletedWithWarnings, Failed, Cancelled
    }

    public static class TaskKindText
    {
        public static string Of(TaskKind k) => k switch
        {
            TaskKind.Compress => "压缩",
            TaskKind.Extract => "解压",
            TaskKind.BatchExtract => "批量解压",
            TaskKind.Test => "校验",
            TaskKind.Backup => "备份",
            TaskKind.Restore => "恢复",
            TaskKind.Cleanup => "清理",
            TaskKind.Update => "更新",
            _ => "任务"
        };
    }

    /// <summary>可暂停对象（引擎进程挂起 / 循环暂停标志）。</summary>
    public interface IPausable
    {
        void Pause();
        void Resume();
    }

    /// <summary>任务运行上下文（真实进度汇报通道）。</summary>
    public sealed class TaskRunContext
    {
        public CancellationToken Cancellation { get; init; }
        public Action<int?, long?, string?> ReportProgress { get; init; } = (_, _, _) => { };
        public Action<string> Log { get; init; } = _ => { };
    }

    /// <summary>统一任务模型：所有压缩/解压/备份/恢复任务共用。</summary>
    public sealed class ArchiveTask
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public TaskKind Kind { get; init; }
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public TaskStatus Status { get; set; } = TaskStatus.Queued;
        public int? Percent { get; set; }
        public long ProcessedBytes { get; set; }
        public long TotalBytes { get; set; }
        public double SpeedBytesPerSec { get; set; }
        public TimeSpan? RemainingTime { get; set; }
        public string? Error { get; set; }
        public bool HasWarnings { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public IPausable? Pausable { get; set; }

        /// <summary>任务即将开始时回调（用于挂接引擎与取消通道）。</summary>
        public Action<ArchiveTask>? BeforeStart { get; set; }

        internal Action<TaskRunContext>? Work { get; set; }

        public string StatusText => Status switch
        {
            TaskStatus.Queued => "排队中",
            TaskStatus.Running => "执行中",
            TaskStatus.Paused => "已暂停",
            TaskStatus.Completed => "已完成",
            TaskStatus.CompletedWithWarnings => "完成（有警告）",
            TaskStatus.Failed => "失败",
            TaskStatus.Cancelled => "已取消",
            _ => ""
        };

        public string ProgressText =>
            TotalBytes > 0
                ? $"{Security.PathGuard.FormatBytes(ProcessedBytes)} / {Security.PathGuard.FormatBytes(TotalBytes)}"
                : Percent is >= 0 and <= 100 ? $"{Percent}%" : "";

        public string SpeedText =>
            SpeedBytesPerSec > 1 ? Security.PathGuard.FormatBytes((long)SpeedBytesPerSec) + "/s" : "";

        public string EtaText => RemainingTime is { } t && t.TotalSeconds > 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : "";
    }
}
