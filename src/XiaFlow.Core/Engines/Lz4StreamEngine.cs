using System;
using System.IO;
using System.Threading;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Security;

namespace XiaFlow.Core.Engines
{
    /// <summary>
    /// LZ4 流式引擎（.lz4 帧格式，管理库实现，纯流式处理，不整读内存）。
    /// 7z 引擎不支持 LZ4，这里独立实现，能力诚实标注。
    /// </summary>
    public sealed class Lz4StreamEngine
    {
        volatile bool _pauseRequested;
        volatile bool _cancelRequested;
        ManualResetEventSlim _resumeEvent = new(true);

        public event EventHandler<EngineProgress>? Progress;
        public bool IsPaused => _pauseRequested;

        public void Pause() { _pauseRequested = true; _resumeEvent.Reset(); }
        public void Resume() { _pauseRequested = false; _resumeEvent.Set(); }
        public void Cancel() { _cancelRequested = true; _resumeEvent.Set(); }

        void CheckFlags(CancellationToken ct)
        {
            if (_cancelRequested) throw new ArchiveException(ArchiveErrorKind.Cancelled, "操作已取消");
            ct.ThrowIfCancellationRequested();
            _resumeEvent.Wait(ct);
        }

        static void Report(EventHandler<EngineProgress>? h, long done, long total)
        {
            h?.Invoke(null, new EngineProgress
            {
                Percent = total > 0 ? (int)Math.Min(100, done * 100 / total) : null,
                Text = PathGuard.FormatBytes(done)
            });
        }

        public void Extract(string lz4Path, string outputPath, CancellationToken ct = default)
        {
            using var src = File.OpenRead(lz4Path);
            using var dst = File.Create(outputPath);
            long total = src.Length;
            var buf = new byte[1024 * 512];
            using var input = K4os.Compression.LZ4.Streams.LZ4Stream.Decode(src, leaveOpen: true);
            long done = 0;
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0)
            {
                CheckFlags(ct);
                dst.Write(buf, 0, n);
                done += n;
                if (done % (4 * 1024 * 1024) < n) Report(Progress, done, total);
            }
            Report(Progress, total, total);
        }

        public void Compress(string inputPath, string lz4Output, CancellationToken ct = default)
        {
            using var src = File.OpenRead(inputPath);
            using var dst = File.Create(lz4Output);
            long total = src.Length;
            var buf = new byte[1024 * 512];
            using (var enc = K4os.Compression.LZ4.Streams.LZ4Stream.Encode(dst,
                K4os.Compression.LZ4.LZ4Level.L09_HC, leaveOpen: true))
            {
                long done = 0;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    CheckFlags(ct);
                    enc.Write(buf, 0, n);
                    done += n;
                    if (done % (4 * 1024 * 1024) < n) Report(Progress, done, total);
                }
            }
            Report(Progress, total, total);
        }
    }
}
