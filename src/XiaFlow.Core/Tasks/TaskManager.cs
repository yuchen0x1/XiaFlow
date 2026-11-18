using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Logging;

namespace XiaFlow.Core.Tasks
{
    /// <summary>
    /// 统一任务调度中心：队列、并行限制、暂停/恢复、取消、重试、历史。
    /// 任务状态与后台引擎真实状态同步（进度来自引擎输出）。
    /// </summary>
    public sealed class TaskManager : IDisposable
    {
        readonly object _lock = new();
        readonly List<ArchiveTask> _tasks = new();
        readonly SemaphoreSlim _slots;
        readonly TaskHistoryStore _history;
        int _maxParallel;

        public event Action<ArchiveTask>? TaskChanged;
        public event Action<ArchiveTask>? TaskAdded;
        public event Action<ArchiveTask>? TaskFinished;

        public TaskManager(int maxParallel = 2)
        {
            _maxParallel = Math.Max(1, maxParallel);
            _slots = new SemaphoreSlim(_maxParallel, _maxParallel);
            _history = new TaskHistoryStore();
        }

        public int MaxParallel
        {
            get => _maxParallel;
            set
            {
                if (value == _maxParallel) return;
                value = Math.Max(1, value);
                var diff = value - _maxParallel;
                _maxParallel = value;
                if (diff > 0) _slots.Release(diff);
                // 缩减不会强制停止已运行任务（诚实行为：对运行中任务无影响，后续排队生效）
            }
        }

        public IReadOnlyList<ArchiveTask> Tasks { get { lock (_lock) return _tasks.ToList(); } }
        public int ActiveCount { get { lock (_lock) return _tasks.Count(t => t.Status is TaskStatus.Running or TaskStatus.Paused or TaskStatus.Queued); } }

        /// <summary>入队并自动调度。</summary>
        public ArchiveTask Enqueue(TaskKind kind, string title, string detail, string source, string target,
            Action<TaskRunContext> work)
        {
            var task = new ArchiveTask
            {
                Kind = kind,
                Title = title,
                Detail = detail,
                Source = source,
                Target = target,
                Work = work
            };
            lock (_lock) _tasks.Add(task);
            TaskAdded?.Invoke(task);

            _ = Task.Run(() => RunAsync(task));
            return task;
        }

        async Task RunAsync(ArchiveTask task)
        {
            await _slots.WaitAsync();
            if (task.Status == TaskStatus.Cancelled) { _slots.Release(); return; }
            task.Status = TaskStatus.Running;
            task.StartTime = DateTime.Now;
            try { task.BeforeStart?.Invoke(task); }
            catch (Exception ex) { Logger.Warn("BeforeStart 回调异常: " + ex.Message); }
            Notify(task);

            long lastBytes = 0;
            var lastTime = Stopwatch.GetTimestamp();
            var sw = Stopwatch.StartNew();

            void OnProgress(int? pct, long? bytes, string? file)
            {
                if (bytes is long b && b >= 0)
                {
                    var now = Stopwatch.GetTimestamp();
                    var dt = (now - lastTime) / (double)Stopwatch.Frequency;
                    if (dt > 0.4)
                    {
                        var speed = (b - lastBytes) / dt;
                        if (speed >= 0) task.SpeedBytesPerSec = speed;
                        lastBytes = b;
                        lastTime = now;
                    }
                    task.ProcessedBytes = b;
                    if (task.TotalBytes > 0 && task.SpeedBytesPerSec > 1)
                    {
                        var remain = (double)(task.TotalBytes - b) / task.SpeedBytesPerSec;
                        task.RemainingTime = remain > 0 ? TimeSpan.FromSeconds(remain) : null;
                    }
                }
                else if (pct is int p) task.Percent = Math.Clamp(p, 0, 100);
                Notify(task);
            }

            try
            {
                task.Work!(new TaskRunContext
                {
                    Cancellation = CancellationToken.None,
                    ReportProgress = OnProgress,
                    Log = m => Logger.Info($"[{TaskKindText.Of(task.Kind)}] {task.Title}: {m}")
                });
                task.Status = task.HasWarnings ? TaskStatus.CompletedWithWarnings : TaskStatus.Completed;
                task.Percent = 100;
                task.ProcessedBytes = task.TotalBytes > 0 ? task.TotalBytes : task.ProcessedBytes;
            }
            catch (OperationCanceledException)
            {
                task.Status = TaskStatus.Cancelled;
            }
            catch (ArchiveException aex) when (aex.Kind == ArchiveErrorKind.Cancelled)
            {
                task.Status = TaskStatus.Cancelled;
            }
            catch (Exception ex)
            {
                task.Status = TaskStatus.Failed;
                task.Error = ex.Message;
                Logger.Error($"任务失败: {task.Title}", ex);
            }
            finally
            {
                task.EndTime = DateTime.Now;
                task.SpeedBytesPerSec = sw.Elapsed.TotalSeconds > 0.2
                    ? task.ProcessedBytes / sw.Elapsed.TotalSeconds : 0;
                Notify(task);
                _slots.Release();
                _history.Record(task);
                TaskFinished?.Invoke(task);
            }
        }

        public void Pause(ArchiveTask task)
        {
            if (task.Status != TaskStatus.Running) return;
            task.Pausable?.Pause();
            if (task.Pausable != null) { task.Status = TaskStatus.Paused; Notify(task); }
        }

        public void Resume(ArchiveTask task)
        {
            if (task.Status != TaskStatus.Paused) return;
            task.Pausable?.Resume();
            task.Status = TaskStatus.Running;
            Notify(task);
        }

        public void Cancel(ArchiveTask task)
        {
            task.Pausable?.Resume(); // 先恢复再取消，否则挂起进程收不到 kill
            if (task.Status is TaskStatus.Queued or TaskStatus.Running or TaskStatus.Paused)
            {
                task.Status = TaskStatus.Cancelled;
                Notify(task);
            }
            // 取消通过 ITaskCancellable 传播；引擎封装在 work 内部监听
            CancelRequested?.Invoke(task.Id);
        }

        /// <summary>取消请求通道：任务执行体注册 Id → Cancel。</summary>
        public event Action<string>? CancelRequested;

        /// <summary>注册可取消引擎（由执行体在运行时挂接）。</summary>
        public void BindCancellation(ArchiveTask task, Action onCancel)
        {
            void Handler(string id) { if (id == task.Id) onCancel(); }
            CancelRequested += Handler;
            void Cleanup(ArchiveTask finished)
            {
                if (finished == task) { CancelRequested -= Handler; TaskFinished -= Cleanup; }
            }
            TaskFinished += Cleanup;
        }

        public void Retry(ArchiveTask task)
        {
            if (task.Status is not (TaskStatus.Failed or TaskStatus.Cancelled)) return;
            var clone = new ArchiveTask
            {
                Kind = task.Kind,
                Title = task.Title,
                Detail = task.Detail,
                Source = task.Source,
                Target = task.Target,
                Work = task.Work,
            };
            lock (_lock) _tasks.Add(clone);
            TaskAdded?.Invoke(clone);
            _ = Task.Run(() => RunAsync(clone));
        }

        public void PauseAll()
        {
            foreach (var t in Tasks.Where(t => t.Status == TaskStatus.Running)) Pause(t);
        }

        public void ResumeAll()
        {
            foreach (var t in Tasks.Where(t => t.Status == TaskStatus.Paused)) Resume(t);
        }

        public void CancelAll()
        {
            foreach (var t in Tasks.Where(t => t.Status is TaskStatus.Queued or TaskStatus.Running or TaskStatus.Paused)) Cancel(t);
        }

        public IReadOnlyList<ArchiveTask> History() => _history.LoadAll();

        public void ClearHistory() => _history.Clear();

        void Notify(ArchiveTask t)
        {
            try { TaskChanged?.Invoke(t); }
            catch (Exception ex) { Logger.Error("TaskChanged 事件处理异常", ex); }
        }

        public void Dispose() => _history.Dispose();
    }

    /// <summary>任务历史持久化（JSON）。</summary>
    public sealed class TaskHistoryStore : IDisposable
    {
        readonly List<ArchiveTask> _cache;
        readonly object _lock = new();
        const int MaxRecords = 300;

        public TaskHistoryStore()
        {
            _cache = JsonStore.Load<List<ArchiveTask>>(AppPaths.TaskHistoryFile);
        }

        public void Record(ArchiveTask task)
        {
            lock (_lock)
            {
                _cache.Add(new ArchiveTask
                {
                    Id = task.Id,
                    Kind = task.Kind,
                    Title = task.Title,
                    Detail = task.Detail,
                    Source = task.Source,
                    Target = task.Target,
                    Status = task.Status,
                    Percent = task.Percent,
                    ProcessedBytes = task.ProcessedBytes,
                    TotalBytes = task.TotalBytes,
                    Error = task.Error,
                    HasWarnings = task.HasWarnings,
                    StartTime = task.StartTime,
                    EndTime = task.EndTime,
                });
                if (_cache.Count > MaxRecords) _cache.RemoveRange(0, _cache.Count - MaxRecords);
                JsonStore.Save(AppPaths.TaskHistoryFile, _cache);
            }
        }

        public IReadOnlyList<ArchiveTask> LoadAll() { lock (_lock) return _cache.ToList(); }
        public void Clear() { lock (_lock) { _cache.Clear(); JsonStore.Save(AppPaths.TaskHistoryFile, _cache); } }
        public void Dispose() { }
    }
}
