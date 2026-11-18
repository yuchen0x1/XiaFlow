using System;
using System.Collections.Concurrent;
using System.IO;

namespace XiaFlow.Core.Logging
{
    /// <summary>
    /// 轻量文件日志。严禁将密码等敏感信息写入日志（调用方负责脱敏）。
    /// </summary>
    public static class Logger
    {
        static readonly BlockingCollection<string> _queue = new();
        static readonly object _initLock = new();
        static TextWriter? _writer;
        static bool _started;

        public static string CurrentLogFile { get; private set; } = "";

        public static void Initialize()
        {
            lock (_initLock)
            {
                if (_started) return;
                _started = true;
                try
                {
                    AppPaths.EnsureDirectories();
                    CurrentLogFile = Path.Combine(AppPaths.LogsDir, $"xiaflow-{DateTime.Now:yyyyMMdd}.log");
                    _writer = new StreamWriter(CurrentLogFile, append: true) { AutoFlush = true };
                }
                catch
                {
                    _writer = null; // 日志失败不影响主流程
                }
                var worker = new Thread(() =>
                {
                    foreach (var line in _queue.GetConsumingEnumerable())
                    {
                        try { _writer?.WriteLine(line); } catch { /* ignore */ }
                    }
                })
                { IsBackground = true, Name = "XiaFlow.Logger" };
                worker.Start();
            }
        }

        public static void Info(string message) => Write("INFO", message, null);
        public static void Warn(string message) => Write("WARN", message, null);
        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);
        public static void Debug(string message) => Write("DEBUG", message, null);

        static void Write(string level, string message, Exception? ex)
        {
            if (!_started) Initialize();
            var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"[{time}] [{level}] {message}";
            if (ex != null)
            {
                line += $"{Environment.NewLine}    {ex}";
                var inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 6)
                {
                    line += $"{Environment.NewLine}    ==> Inner[{depth}]: {inner}";
                    inner = inner.InnerException;
                    depth++;
                }
            }
            _queue.TryAdd(line);
        }
    }
}
