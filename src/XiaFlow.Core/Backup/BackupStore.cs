using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XiaFlow.Core.Tasks;

namespace XiaFlow.Core.Backup
{
    /// <summary>源目录映射。</summary>
    public sealed class ManifestSource
    {
        public string Path { get; set; } = "";
        public string Tag { get; set; } = "";   // 归档内前缀（安全清洗后）
    }

    /// <summary>文件索引条目。</summary>
    public sealed class ManifestFile
    {
        public string Rel { get; set; } = "";       // 归档内相对路径（Tag/...）
        public string SourcePath { get; set; } = ""; // 完整源路径
        public long Size { get; set; }
        public DateTime MTime { get; set; }
        public string? Hash { get; set; }            // 可选 SHA-256
    }

    /// <summary>备份链条目。</summary>
    public sealed class ChainEntry
    {
        public string File { get; set; } = "";          // 归档文件名（位于 DestDir）
        public BackupKind Type { get; set; }
        public DateTime Time { get; set; }
        public int BaseIndex { get; set; } = -1;        // 依赖链：增量依赖前一条；差异依赖所属全量
        public string? PasswordVaultId { get; set; }
    }

    /// <summary>任务备份清单（索引 + 链）。</summary>
    public sealed class BackupManifest
    {
        public List<ManifestSource> Sources { get; set; } = new();
        public List<ManifestFile> Files { get; set; } = new();
        /// <summary>最近一次完整备份时的文件快照（差异备份基准）。</summary>
        public List<ManifestFile>? FullFiles { get; set; }
        public List<ChainEntry> Chain { get; set; } = new();

        public static string DirFor(string taskId) => Path.Combine(AppPaths.BackupDataDir, taskId);
        public static string FileFor(string taskId) => Path.Combine(DirFor(taskId), "manifest.json");

        public static BackupManifest Load(string taskId) => JsonStore.Load<BackupManifest>(FileFor(taskId));

        public void Save(string taskId) => JsonStore.Save(FileFor(taskId), this);
    }

    /// <summary>
    /// 备份存储：任务列表与历史记录持久化。
    /// </summary>
    public sealed class BackupStore
    {
        public List<BackupTaskModel> Tasks { get; private set; }
        public List<BackupHistoryRecord> History { get; private set; }
        readonly object _lock = new();

        public BackupStore()
        {
            Tasks = JsonStore.Load<List<BackupTaskModel>>(AppPaths.BackupTasksFile);
            History = JsonStore.Load<List<BackupHistoryRecord>>(AppPaths.BackupHistoryFile);
        }

        public BackupTaskModel? Find(string id) => Tasks.FirstOrDefault(t => t.Id == id);

        public void SaveTask(BackupTaskModel task)
        {
            lock (_lock)
            {
                var idx = Tasks.FindIndex(t => t.Id == task.Id);
                if (idx >= 0) Tasks[idx] = task; else Tasks.Add(task);
                JsonStore.Save(AppPaths.BackupTasksFile, Tasks);
            }
        }

        public void DeleteTask(string id)
        {
            lock (_lock)
            {
                Tasks.RemoveAll(t => t.Id == id);
                JsonStore.Save(AppPaths.BackupTasksFile, Tasks);
            }
            try
            {
                var dir = BackupManifest.DirFor(id);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { /* ignore */ }
        }

        public void Record(BackupHistoryRecord rec)
        {
            lock (_lock)
            {
                History.Add(rec);
                if (History.Count > 500) History.RemoveRange(0, History.Count - 500);
                JsonStore.Save(AppPaths.BackupHistoryFile, History);
            }
        }

        public void UpdateHistory(BackupHistoryRecord rec)
        {
            lock (_lock)
            {
                var idx = History.FindIndex(h => h.Id == rec.Id);
                if (idx >= 0) { History[idx] = rec; JsonStore.Save(AppPaths.BackupHistoryFile, History); }
            }
        }

        public IReadOnlyList<BackupHistoryRecord> HistoryFor(string taskId) =>
            History.Where(h => h.TaskId == taskId).OrderBy(h => h.Time).ToList();

        // ---------- 统计（全部来自真实记录） ----------

        public (int today, int week, int total, long totalBytes, DateTime? lastTime, int failedCount) Stats()
        {
            var now = DateTime.Now;
            var todayStart = now.Date;
            var weekStart = todayStart.AddDays(-(int)todayStart.DayOfWeek + 1); // 周一
            if (todayStart.DayOfWeek == DayOfWeek.Sunday) weekStart = todayStart.AddDays(-6);
            var ok = History.Where(h => h.Status == "成功").ToList();
            return (
                today: ok.Count(h => h.Time >= todayStart),
                week: ok.Count(h => h.Time >= weekStart),
                total: ok.Count,
                totalBytes: ok.Sum(h => h.ArchiveBytes),
                lastTime: ok.Count > 0 ? ok.Max(h => h.Time) : null,
                failedCount: History.Count(h => h.Status == "失败")
            );
        }
    }
}
