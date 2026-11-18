using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Logging;
using XiaFlow.Core.Tasks;

namespace XiaFlow.Core.Batch
{
    /// <summary>
    /// 批量解压执行器：多任务并行、独立暂停/恢复/取消、冲突策略、输出模式、真实进度。
    /// </summary>
    public sealed class BatchRunner : IDisposable
    {
        readonly ConcurrentDictionary<string, SevenZipEngine> _engines = new();
        PasswordVault _vault;

        public event Action<BatchItem>? ItemChanged;
        public event Action<BatchReport>? ReportReady;

        public BatchRunner(PasswordVault vault) { _vault = vault; }

        public void Dispose()
        {
            foreach (var kv in _engines) kv.Value.Dispose();
            _engines.Clear();
        }

        void Notify(BatchItem item)
        {
            try { ItemChanged?.Invoke(item); } catch { /* UI */ }
        }

        // ---------- 输出目录计算 ----------

        static string CategoryOf(string entryName)
        {
            var ext = Path.GetExtension(entryName).ToLowerInvariant();
            if (".doc .docx .pdf .txt .md .xls .xlsx .ppt .pptx .rtf .odt .csv .json .xml .html .epub".Split(' ').Contains(ext))
                return "文档";
            if (".jpg .jpeg .png .gif .bmp .webp .svg .ico .tiff .psd .raw".Split(' ').Contains(ext))
                return "图片";
            if (".mp4 .mkv .avi .mov .wmv .flv .webm .m4v .mpg".Split(' ').Contains(ext))
                return "视频";
            if (".mp3 .wav .flac .aac .ogg .m4a .wma".Split(' ').Contains(ext))
                return "音频";
            if (".zip .7z .rar .tar .gz .bz2 .xz .zst .iso".Split(' ').Contains(ext))
                return "压缩包";
            return "其他";
        }

        internal static string ComputeTargetDir(BatchItem item, BatchOptions opt, ArchiveListing listing)
        {
            var archiveName = Path.GetFileName(item.Path);
            var nameNoExt = Path.GetFileNameWithoutExtension(item.Path);
            if (VolumeHelper.IsSplitArchive(item.Path))
            {
                var m = System.Text.RegularExpressions.Regex.Match(nameNoExt, @"\.\d{2,3}$");
                if (m.Success) nameNoExt = nameNoExt[..m.Index];
                else if (nameNoExt.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                {
                    var m2 = System.Text.RegularExpressions.Regex.Match(nameNoExt, @"\.part\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m2.Success) nameNoExt = nameNoExt[..m2.Index];
                }
            }
            return opt.Mode switch
            {
                BatchOutputMode.SingleDirectory => opt.OutputRoot,
                BatchOutputMode.OwnFolderEach => Path.Combine(opt.OutputRoot, nameNoExt),
                BatchOutputMode.PreserveStructure => Path.Combine(opt.OutputRoot),
                BatchOutputMode.ByFileType => Path.Combine(opt.OutputRoot, DominantCategory(listing), nameNoExt),
                BatchOutputMode.OnlyFileTypes => Path.Combine(opt.OutputRoot, nameNoExt),
                BatchOutputMode.OnlyNamePattern => Path.Combine(opt.OutputRoot, nameNoExt),
                _ => Path.Combine(opt.OutputRoot, nameNoExt)
            };
        }

        static string DominantCategory(ArchiveListing listing)
        {
            if (listing.Entries.Count == 0) return "其他";
            var dominant = listing.Entries.Where(e => !e.IsDirectory)
                .GroupBy(e => CategoryOf(e.Name))
                .OrderByDescending(g => g.Sum(e => e.Size))
                .FirstOrDefault()?.Key ?? "其他";
            return dominant;
        }

        internal static List<string> SelectEntries(BatchItem item, BatchOptions opt, ArchiveListing listing)
        {
            if (opt.Mode == BatchOutputMode.OnlyFileTypes && opt.FileTypeFilter.Count > 0)
            {
                var exts = opt.FileTypeFilter.Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return listing.Entries.Where(e => !e.IsDirectory && exts.Contains(Path.GetExtension(e.Name)))
                    .Select(e => e.Name).ToList();
            }
            if (opt.Mode == BatchOutputMode.OnlyNamePattern && !string.IsNullOrWhiteSpace(opt.NamePattern))
            {
                var patterns = opt.NamePattern.Split('|', ';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                var result = new List<string>();
                foreach (var e in listing.Entries.Where(e => !e.IsDirectory))
                {
                    var fileName = Path.GetFileName(e.Name);
                    if (patterns.Any(p => Matches(p, fileName) || Matches(p, e.Name)))
                        result.Add(e.Name);
                }
                return result;
            }
            return listing.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
        }

        static bool Matches(string pattern, string name)
        {
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // ---------- 执行 ----------

        /// <summary>执行批量解压（同步阻塞，可在任务线程运行）。onFinish 回调收集密码等待者。</summary>
        public BatchReport Run(List<BatchItem> items, BatchOptions opt,
            Func<BatchItem, string?>? resolvePassword = null,
            CancellationToken ct = default)
        {
            var report = new BatchReport { StartedAt = DateTime.Now, OutputRoot = opt.OutputRoot, Items = items };
            Directory.CreateDirectory(opt.OutputRoot);

            // 只处理首卷；跳过 重复/已处理(可选)/不支持
            var runnable = new List<BatchItem>();
            foreach (var it in items)
            {
                if (it.State is BatchState.Done or BatchState.Extracting) continue;
                if (!it.IsPrimaryVolume || it.IsDuplicateOf) { it.State = BatchState.Skipped; Notify(it); continue; }
                if (it.State is BatchState.Unsupported or BatchState.Corrupt or BatchState.Split) { Notify(it); continue; }
                if (opt.SkipProcessed && it.ProcessedBefore) { it.State = BatchState.Skipped; Notify(it); continue; }
                runnable.Add(it);
            }

            using var barrier = new SemaphoreSlim(Math.Max(1, opt.MaxParallel));
            var workers = runnable.Select(it => Task.Run(() => RunOne(it, opt, resolvePassword, barrier, ct))).ToArray();
            try { Task.WaitAll(workers, ct); }
            catch (AggregateException) { /* 每项错误已记录 */ }
            catch (OperationCanceledException)
            {
                foreach (var it in runnable.Where(i => i.State is BatchState.Extracting or BatchState.Queued))
                { it.State = BatchState.Cancelled; Notify(it); }
            }

            report.FinishedAt = DateTime.Now;
            ReportReady?.Invoke(report);
            RecordHistory(runnable);
            return report;
        }

        void RunOne(BatchItem item, BatchOptions opt, Func<BatchItem, string?>? resolvePassword,
            SemaphoreSlim barrier, CancellationToken ct)
        {
            try
            {
                barrier.Wait(ct);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                item.State = BatchState.Cancelled; Notify(item);
                return;
            }

            var engine = new SevenZipEngine();
            _engines[item.Id] = engine;
            var lastTime = Stopwatch.GetTimestamp();
            long lastBytes = 0;

            try
            {
                // 密码解析：用户输入 → 库匹配 → 待输入
                if (item.IsEncrypted && string.IsNullOrEmpty(item.Password))
                {
                    if (!string.IsNullOrEmpty(opt.Password)) item.Password = opt.Password;
                    else
                    {
                        var pw = resolvePassword?.Invoke(item);
                        if (string.IsNullOrEmpty(pw))
                        {
                            item.State = BatchState.NeedsPassword;
                            Notify(item);
                            return;
                        }
                        item.Password = pw;
                    }
                }

                // 列表（分卷取首卷）
                var archive = VolumeHelper.GetFirstVolume(item.Path);
                var listing = engine.List(archive, item.Password);

                item.TargetDir = ComputeTargetDir(item, opt, listing);
                Directory.CreateDirectory(item.TargetDir);

                var selected = SelectEntries(item, opt, listing);
                if (selected.Count == 0)
                {
                    item.State = BatchState.Skipped;
                    item.Error = "没有匹配的文件（筛选条件无命中）";
                    Notify(item);
                    return;
                }

                // 安全检查
                var limits = opt.Limits ?? new Security.ExtractSafetyLimits();
                var entries = listing.Entries.Where(e => selected.Contains(e.Name) || opt.Mode is BatchOutputMode.SingleDirectory or BatchOutputMode.OwnFolderEach or BatchOutputMode.PreserveStructure or BatchOutputMode.ByFileType)
                    .Select(e => (e.Name, e.Size, e.IsDirectory, e.IsSymlink));
                var safety = Security.PathGuard.EvaluateExtraction(entries, item.TargetDir, limits);
                if (!safety.IsSafe)
                {
                    item.State = BatchState.Failed;
                    item.Error = "安全检查未通过: " + string.Join("；", safety.Violations.Take(2));
                    Notify(item);
                    return;
                }

                item.State = BatchState.Extracting;
                item.TotalBytes = selected.Count == listing.Entries.Count(e => !e.IsDirectory)
                    ? listing.TotalUncompressed
                    : listing.Entries.Where(e => selected.Contains(e.Name)).Sum(e => e.Size);
                Notify(item);

                engine.Progress += (_, p) =>
                {
                    if (p.Percent is int pc)
                    {
                        item.Percent = pc;
                        item.ProcessedBytes = (long)(item.TotalBytes * pc / 100.0);
                        var now = Stopwatch.GetTimestamp();
                        var dt = (now - lastTime) / (double)Stopwatch.Frequency;
                        if (dt > 0.4)
                        {
                            item.SpeedBytesPerSec = (item.ProcessedBytes - lastBytes) / dt;
                            lastBytes = item.ProcessedBytes;
                            lastTime = now;
                        }
                        Notify(item);
                    }
                };

                engine.Extract(new SevenZipEngine.ExtractRequest
                {
                    ArchivePath = archive,
                    OutputDir = item.TargetDir,
                    Password = item.Password,
                    SelectedEntries = selected.Count == listing.Entries.Count(e => !e.IsDirectory) ? null : selected,
                    Conflict = opt.Conflict,
                }, ct);

                item.State = BatchState.Done;
                item.Percent = 100;
                item.ExtractedBytes = item.TotalBytes;
                item.ExtractedFiles = selected.Count;
                item.FinishedAt = DateTime.Now;
                Notify(item);
            }
            catch (OperationCanceledException)
            {
                item.State = BatchState.Cancelled; Notify(item);
            }
            catch (ArchiveException aex)
            {
                item.State = aex.Kind == ArchiveErrorKind.WrongPassword ? BatchState.NeedsPassword : BatchState.Failed;
                item.Error = aex.Kind == ArchiveErrorKind.WrongPassword ? "密码错误" : aex.Message;
                item.Password = null; // 清掉错误密码
                Notify(item);
            }
            catch (Exception ex)
            {
                Logger.Error($"批量解压失败: {item.Path}", ex);
                item.State = BatchState.Failed;
                item.Error = ex.Message;
                Notify(item);
            }
            finally
            {
                engine.Dispose();
                _engines.TryRemove(item.Id, out _);
                barrier.Release();
            }
        }

        // ---------- 控制 ----------

        public void Pause(BatchItem item)
        {
            if (_engines.TryGetValue(item.Id, out var e)) { e.Pause(); item.State = BatchState.Paused; Notify(item); }
        }

        public void Resume(BatchItem item)
        {
            if (_engines.TryGetValue(item.Id, out var e)) { e.Resume(); item.State = BatchState.Extracting; Notify(item); }
        }

        public void Cancel(BatchItem item)
        {
            if (_engines.TryGetValue(item.Id, out var e)) { e.Resume(); e.Cancel(); }
        }

        public void PauseAll()
        {
            foreach (var kv in _engines) { kv.Value.Pause(); }
        }

        public void ResumeAll()
        {
            foreach (var kv in _engines) { kv.Value.Resume(); }
        }

        public void CancelAll()
        {
            foreach (var kv in _engines) { kv.Value.Resume(); kv.Value.Cancel(); }
        }

        // ---------- 历史（防重复处理） ----------

        static void RecordHistory(List<BatchItem> items)
        {
            try
            {
                var file = Path.Combine(AppPaths.RootDir, "batch-history.json");
                var hist = JsonStore.Load<List<BatchHistoryRecord>>(file);
                foreach (var it in items.Where(i => i.State == BatchState.Done))
                {
                    hist.Add(new BatchHistoryRecord
                    {
                        Path = it.Path,
                        Size = it.SizeBytes,
                        FinishedAt = it.FinishedAt ?? DateTime.Now,
                    });
                }
                if (hist.Count > 1000) hist.RemoveRange(0, hist.Count - 1000);
                JsonStore.Save(file, hist);
            }
            catch (Exception ex) { Logger.Warn("批量历史记录失败: " + ex.Message); }
        }

        /// <summary>加载历史以标记“已处理过”。</summary>
        public static void MarkProcessedBefore(List<BatchItem> items)
        {
            try
            {
                var file = Path.Combine(AppPaths.RootDir, "batch-history.json");
                var hist = JsonStore.Load<List<BatchHistoryRecord>>(file);
                var map = hist.Select(h => h.Path + "|" + h.Size).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var it in items)
                {
                    if (map.Contains(it.Path + "|" + it.SizeBytes))
                        it.ProcessedBefore = true;
                }
            }
            catch { /* ignore */ }
        }
    }

    public sealed class BatchHistoryRecord
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public DateTime FinishedAt { get; set; }
    }
}
