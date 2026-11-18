using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using XiaFlow.Core.Logging;
using XiaFlow.Core.Tasks;

namespace XiaFlow.Core.Backup
{
    /// <summary>
    /// 备份调度器：
    /// 1. 应用内定时器调度（主窗口打开时）；
    /// 2. Windows Task Scheduler 注册（软件关闭后仍可靠执行）；
    /// 3. 启动时检查遗漏任务（错过窗口的备份在下次启动时补跑）。
    /// </summary>
    public sealed class BackupScheduler : IDisposable
    {
        readonly BackupStore _store;
        readonly Timer _timer;
        readonly Action<BackupTaskModel> _onDue;
        readonly TimeSpan _tolerance = TimeSpan.FromMinutes(2);
        readonly HashSet<string> _inFlight = new();

        public BackupScheduler(BackupStore store, Action<BackupTaskModel> onDue)
        {
            _store = store;
            _onDue = onDue;
            _timer = new Timer(_ => CheckAll(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        }

        public void CheckAll()
        {
            var now = DateTime.Now;
            foreach (var task in _store.Tasks.ToList())
            {
                if (!task.Enabled) continue;
                if (!_inFlight.Add(task.Id)) continue;
                try
                {
                    var next = task.NextRun ?? ScheduleCalc.NextRun(task, task.LastRun ?? now);
                    if (next == null) { _inFlight.Remove(task.Id); continue; }

                    if (next.Value <= now)
                    {
                        task.NextRun = ScheduleCalc.NextRun(task, now);
                        _store.SaveTask(task);
                        Logger.Info($"调度触发备份: {task.Name}");
                        _onDue(task);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"调度检查异常: {task.Name}", ex);
                }
                finally
                {
                    _inFlight.Remove(task.Id);
                }
            }
        }

        /// <summary>启动时补跑遗漏的备份（错过执行窗口的当天/当周任务）。</summary>
        public List<string> RunMissedOnStartup()
        {
            var missed = new List<string>();
            var now = DateTime.Now;
            foreach (var task in _store.Tasks.ToList())
            {
                if (!task.Enabled) continue;
                var next = task.NextRun ?? ScheduleCalc.NextRun(task, task.LastRun ?? now);
                if (next != null && next.Value <= now - _tolerance)
                {
                    missed.Add(task.Name);
                    task.NextRun = ScheduleCalc.NextRun(task, now);
                    _store.SaveTask(task);
                    _onDue(task);
                }
            }
            return missed;
        }

        // ---------- Windows Task Scheduler (schtasks.exe) ----------

        public const string TaskFolder = "XiaFlow";

        /// <summary>把任务同步注册到 Windows 计划任务（主程序关闭后仍可执行）。</summary>
        public static bool RegisterWindowsTask(BackupTaskModel task, string exePath)
        {
            try
            {
                DeleteWindowsTask(task.Id); // 先删旧的
                var tn = TaskName(task.Id);
                var tr = $"\"{exePath}\" --run-backup {task.Id}";
                string args = task.Schedule switch
                {
                    ScheduleKind.Interval => $"/Create /F /TN \"{tn}\" /TR \"{tr}\" /SC MINUTE /MO {Math.Max(5, task.IntervalMinutes)}",
                    ScheduleKind.Daily => $"/Create /F /TN \"{tn}\" /TR \"{tr}\" /SC DAILY /ST {task.TimeOfDay}",
                    ScheduleKind.Weekly => $"/Create /F /TN \"{tn}\" /TR \"{tr}\" /SC WEEKLY /D {DaysArg(task.Days)} /ST {task.TimeOfDay}",
                    ScheduleKind.Monthly => $"/Create /F /TN \"{tn}\" /TR \"{tr}\" /SC MONTHLY /D {task.DayOfMonth} /ST {task.TimeOfDay}",
                    ScheduleKind.OnLogon => $"/Create /F /TN \"{tn}\" /TR \"{tr}\" /SC ONLOGON",
                    _ => null
                };
                if (args == null) return false;
                args += " /F"; // 强制覆盖
                var psi = new ProcessStartInfo("schtasks.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit(15000);
                var ok = p.ExitCode == 0;
                if (!ok)
                    Logger.Warn($"schtasks 注册失败({p.ExitCode}): {p.StandardOutput.ReadToEnd()} {p.StandardError.ReadToEnd()}");
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Error("Windows 计划任务注册失败", ex);
                return false;
            }
        }

        public static bool DeleteWindowsTask(string taskId)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /F /TN \"{TaskName(taskId)}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit(15000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        public static string TaskName(string taskId) => $@"{TaskFolder}\Backup_{taskId[..12]}";

        static string DaysArg(DaysOfWeek days)
        {
            var list = new List<string>();
            if ((days & DaysOfWeek.Mon) != 0) list.Add("MON");
            if ((days & DaysOfWeek.Tue) != 0) list.Add("TUE");
            if ((days & DaysOfWeek.Wed) != 0) list.Add("WED");
            if ((days & DaysOfWeek.Thu) != 0) list.Add("THU");
            if ((days & DaysOfWeek.Fri) != 0) list.Add("FRI");
            if ((days & DaysOfWeek.Sat) != 0) list.Add("SAT");
            if ((days & DaysOfWeek.Sun) != 0) list.Add("SUN");
            return list.Count > 0 ? string.Join(",", list) : "MON";
        }

        public void Dispose() => _timer.Dispose();
    }

    /// <summary>通知中心（内部通知 + Windows 系统通知由 App 层转发）。</summary>
    public sealed class NotificationStore
    {
        public sealed class Notification
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Title { get; set; } = "";
            public string Message { get; set; } = "";
            public string Level { get; set; } = "info"; // info / success / warn / error
            public DateTime Time { get; set; } = DateTime.Now;
            public bool Read { get; set; }
        }

        readonly List<Notification> _items;
        readonly object _lock = new();
        public event Action<Notification>? Added;

        public NotificationStore()
        {
            _items = JsonStore.Load<List<Notification>>(Path.Combine(AppPaths.RootDir, "notifications.json"));
        }

        public IReadOnlyList<Notification> Items { get { lock (_lock) return _items.ToList(); } }
        public int UnreadCount { get { lock (_lock) return _items.Count(i => !i.Read); } }

        public void Push(string title, string message, string level = "info")
        {
            var n = new Notification { Title = title, Message = message, Level = level };
            lock (_lock)
            {
                _items.Insert(0, n);
                if (_items.Count > 200) _items.RemoveRange(200, _items.Count - 200);
                JsonStore.Save(Path.Combine(AppPaths.RootDir, "notifications.json"), _items);
            }
            Added?.Invoke(n);
        }

        public void MarkAllRead()
        {
            lock (_lock)
            {
                foreach (var i in _items) i.Read = true;
                JsonStore.Save(Path.Combine(AppPaths.RootDir, "notifications.json"), _items);
            }
        }
    }
}
