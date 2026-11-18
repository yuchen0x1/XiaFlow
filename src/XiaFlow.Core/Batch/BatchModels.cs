using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Logging;

namespace XiaFlow.Core.Batch
{
    /// <summary>批量项状态。</summary>
    public enum BatchState
    {
        PendingDetect,   // 待检测
        Detecting,       // 检测中
        Ready,           // 可解压
        NeedsPassword,   // 需要密码
        Split,           // 分卷
        Corrupt,         // 损坏
        Unsupported,     // 不支持
        Duplicate,       // 重复
        ProcessedBefore, // 之前已处理过
        Queued,          // 已排队
        Extracting,      // 解压中
        Paused,
        Done,            // 完成
        Failed,          // 失败
        Skipped,
        Cancelled
    }

    public static class BatchStateText
    {
        public static string Of(BatchState s) => s switch
        {
            BatchState.PendingDetect => "待检测",
            BatchState.Detecting => "检测中",
            BatchState.Ready => "正常",
            BatchState.NeedsPassword => "密码保护",
            BatchState.Split => "分卷压缩包",
            BatchState.Corrupt => "损坏",
            BatchState.Unsupported => "不支持",
            BatchState.Duplicate => "重复",
            BatchState.ProcessedBefore => "已处理过",
            BatchState.Queued => "排队中",
            BatchState.Extracting => "解压中",
            BatchState.Paused => "已暂停",
            BatchState.Done => "完成",
            BatchState.Failed => "失败",
            BatchState.Skipped => "已跳过",
            BatchState.Cancelled => "已取消",
            _ => ""
        };
    }

    /// <summary>输出模式（模式 A-F）。</summary>
    public enum BatchOutputMode
    {
        SingleDirectory,     // A: 全部解压到指定目录
        OwnFolderEach,       // B: 每个压缩包独立文件夹
        PreserveStructure,   // C: 按原始目录结构解压
        ByFileType,          // D: 按文件类型分类
        OnlyFileTypes,       // E: 只提取指定类型
        OnlyNamePattern      // F: 只提取指定文件名（通配符）
    }

    /// <summary>批量任务项。</summary>
    public sealed class BatchItem
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Path { get; set; } = "";
        public string DisplayName => System.IO.Path.GetFileName(Path);
        public long SizeBytes { get; set; }
        public string FormatName { get; set; } = "";
        public int? FileCount { get; set; }
        public BatchState State { get; set; } = BatchState.PendingDetect;
        public string? DetectInfo { get; set; }
        public bool IsEncrypted { get; set; }
        public bool HeaderEncrypted { get; set; }
        public string? Password { get; set; }
        public string? GroupId { get; set; }             // 分卷组
        public bool IsPrimaryVolume { get; set; } = true;
        public bool IsDuplicateOf { get; set; }
        public bool ProcessedBefore { get; set; }
        public string? TargetDir { get; set; }
        public int? Percent { get; set; }
        public long ProcessedBytes { get; set; }
        public long TotalBytes { get; set; }
        public double SpeedBytesPerSec { get; set; }
        public string? Error { get; set; }
        public DateTime? FinishedAt { get; set; }
        public long ExtractedBytes { get; set; }
        public int ExtractedFiles { get; set; }
    }

    /// <summary>批量解压选项。</summary>
    public sealed class BatchOptions
    {
        public BatchOutputMode Mode { get; set; } = BatchOutputMode.OwnFolderEach;
        public string OutputRoot { get; set; } = "";
        public ConflictPolicy Conflict { get; set; } = ConflictPolicy.AutoRename;
        public string? Password { get; set; }                  // 应用到全部
        public bool UseVaultPasswords { get; set; } = true;    // 从密码库匹配
        public List<string> FileTypeFilter { get; set; } = new(); // E 模式：.pdf .jpg ...
        public string NamePattern { get; set; } = "*";         // F 模式：*.pdf
        public bool SkipProcessed { get; set; } = true;
        public int MaxParallel { get; set; } = 2;
        public Security.ExtractSafetyLimits? Limits { get; set; }
    }

    /// <summary>批量分析器：智能识别 + 分类。</summary>
    public sealed class BatchAnalyzer
    {
        readonly PasswordVault _vault;
        public event Action<BatchItem>? ItemUpdated;

        public BatchAnalyzer(PasswordVault vault) => _vault = vault;

        void Update(BatchItem item, BatchState state, string? info = null)
        {
            item.State = state;
            if (info != null) item.DetectInfo = info;
            try { ItemUpdated?.Invoke(item); } catch { /* UI handler errors */ }
        }

        /// <summary>导入文件（含递归扫描目录）。</summary>
        public static List<BatchItem> Import(IEnumerable<string> paths, bool recursive = true)
        {
            var items = new List<BatchItem>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    var opt = recursive
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly;
                    var files = Directory.EnumerateFiles(p, "*", opt);
                    foreach (var f in files)
                    {
                        if (IsRecognizableArchive(f))
                        {
                            var it = NewItem(f);
                            items.Add(it);
                        }
                    }
                }
                else if (File.Exists(p))
                {
                    items.Add(NewItem(p));
                }
            }
            MarkDuplicates(items);
            MarkVolumeGroups(items);
            return items;
        }

        static BatchItem NewItem(string f)
        {
            long size = 0;
            try { size = new FileInfo(f).Length; } catch { }
            return new BatchItem { Path = f, SizeBytes = size };
        }

        static bool IsRecognizableArchive(string f)
        {
            var fi = FormatRegistry.FindByExtension(f);
            return fi != null;
        }

        static void MarkDuplicates(List<BatchItem> items)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                var key = System.IO.Path.GetFileName(it.Path).ToLowerInvariant() + "|" + it.SizeBytes;
                if (!seen.Add(key)) it.IsDuplicateOf = true;
            }
        }

        static void MarkVolumeGroups(List<BatchItem> items)
        {
            var groups = items.GroupBy(i => VolumeKey(i.Path));
            foreach (var g in groups)
            {
                if (g.Count() <= 1 && !VolumeHelper.IsSplitArchive(g.First().Path)) continue;
                var gid = g.Key;
                foreach (var i in g) i.GroupId = gid;
                // 首卷为主
                var primary = g.OrderBy(i => VolumeOrder(i.Path)).First();
                primary.IsPrimaryVolume = true;
                foreach (var i in g.Where(x => x != primary)) i.IsPrimaryVolume = false;
            }
        }

        static string VolumeKey(string path)
        {
            var name = System.IO.Path.GetFileName(path);
            var dir = System.IO.Path.GetDirectoryName(path) ?? "";
            var m = Regex.Match(name, @"^(.+?)\.part\d+\.rar$", RegexOptions.IgnoreCase);
            if (m.Success) return Path.Combine(dir, m.Groups[1].Value).ToLowerInvariant();
            var m2 = Regex.Match(name, @"^(.+?)\.\d{2,3}$", RegexOptions.IgnoreCase);
            if (m2.Success) return Path.Combine(dir, m2.Groups[1].Value).ToLowerInvariant();
            return Path.Combine(dir, name).ToLowerInvariant();
        }

        static int VolumeOrder(string path)
        {
            var name = System.IO.Path.GetFileName(path);
            var m = Regex.Match(name, @"part(\d+)\.rar$", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value);
            var m2 = Regex.Match(name, @"\.(\d{2,3})$");
            if (m2.Success) return int.Parse(m2.Groups[1].Value);
            return int.MaxValue;
        }

        /// <summary>逐项检测：格式/大小/加密/分卷/损坏/重复/已处理。</summary>
        public void Analyze(IEnumerable<BatchItem> items, CancellationToken ct = default)
        {
            foreach (var item in items.ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (!item.IsPrimaryVolume) { Update(item, BatchState.Skipped, "分卷组内非首卷，随首卷一起处理"); continue; }
                if (item.IsDuplicateOf) { Update(item, BatchState.Duplicate, "与已有压缩包重名且大小相同"); continue; }
                Update(item, BatchState.Detecting);
                DetectOne(item, ct);
            }
        }

        void DetectOne(BatchItem item, CancellationToken ct)
        {
            try
            {
                if (!File.Exists(item.Path)) { Update(item, BatchState.Failed, "文件不存在"); return; }
                var fi = FormatRegistry.FindByExtension(item.Path);
                if (fi == null)
                {
                    // 魔数兜底
                    try
                    {
                        using var fs = File.OpenRead(item.Path);
                        var f = FormatRegistry.DetectByMagic(fs);
                        if (f == ArchiveFormat.Unknown) { Update(item, BatchState.Unsupported, "无法识别的格式"); return; }
                    }
                    catch { Update(item, BatchState.Failed, "无法读取文件"); return; }
                }
                item.FormatName = fi?.DisplayName ?? "自动识别";

                if (VolumeHelper.IsSplitArchive(item.Path) && !IsFirstVolume(item.Path))
                {
                    Update(item, BatchState.Split, "非首卷");
                    return;
                }

                // 探测列表（尝试无密码 → 需要密码判定）
                using var engine = new SevenZipEngine();
                ArchiveListing? listing = null;
                try
                {
                    listing = engine.List(item.Path, null);
                    item.IsEncrypted = listing.HasEncryptedEntries;
                }
                catch (ArchiveException aex) when (aex.Kind == ArchiveErrorKind.WrongPassword)
                {
                    item.HeaderEncrypted = true;
                    item.IsEncrypted = true;
                    // 尝试密码库匹配
                    if (_vault.GetAll().Count > 0)
                    {
                        var match = _vault.TryMatch(item.Path);
                        if (match != null)
                        {
                            item.Password = match.Password;
                            listing = engine.List(item.Path, match.Password);
                            Update(item, BatchState.Ready, $"已从密码库匹配: {match.Name}");
                        }
                    }
                    if (listing == null)
                    {
                        Update(item, BatchState.NeedsPassword, "需要密码");
                        return;
                    }
                }
                catch (ArchiveException aex) when (aex.Kind is ArchiveErrorKind.NotArchive or ArchiveErrorKind.CorruptArchive)
                {
                    Update(item, BatchState.Corrupt, aex.Message);
                    return;
                }
                catch (ArchiveException aex) when (aex.Kind == ArchiveErrorKind.VolumeMissing)
                {
                    Update(item, BatchState.Split, "分卷文件缺失");
                    return;
                }

                ct.ThrowIfCancellationRequested();

                if (listing != null)
                {
                    item.FileCount = listing.FileCount;
                    item.TotalBytes = listing.TotalUncompressed;
                    item.IsEncrypted = item.IsEncrypted || listing.HasEncryptedEntries;

                    // 内容加密（未加密文件名）：无密码也能列出，但解压需要密码 → 尝试密码库
                    if (item.IsEncrypted && string.IsNullOrEmpty(item.Password) && !item.HeaderEncrypted)
                    {
                        if (_vault.GetAll().Count > 0)
                        {
                            var match = _vault.TryMatch(item.Path);
                            if (match != null)
                            {
                                item.Password = match.Password;
                                Update(item, BatchState.Ready, $"已从密码库匹配: {match.Name}");
                                return;
                            }
                        }
                    }

                    var state = item.IsEncrypted && string.IsNullOrEmpty(item.Password)
                        ? BatchState.NeedsPassword : BatchState.Ready;
                    var info = $"{listing.FormatName} · {listing.FileCount} 个文件 · {listing.TotalPacked} → {listing.TotalUncompressed}";
                    if (listing.HasEncryptedEntries) info += " · 含加密条目";
                    Update(item, state, info);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Error($"批量检测失败: {item.Path}", ex);
                Update(item, BatchState.Failed, ex.Message);
            }
        }

        static bool IsFirstVolume(string path)
        {
            var name = System.IO.Path.GetFileName(path);
            var m = Regex.Match(name, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value) == 1;
            if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return true;
            var m2 = Regex.Match(name, @"\.(\d{2,3})$");
            if (m2.Success) return int.Parse(m2.Groups[1].Value) == 1;
            return true;
        }
    }

    /// <summary>批量处理完成报告（可导出 TXT/CSV）。</summary>
    public sealed class BatchReport
    {
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }
        public string OutputRoot { get; set; } = "";
        public List<BatchItem> Items { get; set; } = new();

        public int Total => Items.Count;
        public int Success => Items.Count(i => i.State == BatchState.Done);
        public int Failed => Items.Count(i => i.State == BatchState.Failed);
        public int Skipped => Items.Count(i => i.State is BatchState.Skipped or BatchState.Duplicate or BatchState.ProcessedBefore or BatchState.Cancelled);
        public long TotalExtractedBytes => Items.Sum(i => i.ExtractedBytes);
        public TimeSpan Duration => FinishedAt - StartedAt;

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("匣流 XiaFlow — 批量解压报告");
            sb.AppendLine($"开始时间: {StartedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"结束时间: {FinishedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"耗时: {Duration:hh\\:mm\\:ss}");
            sb.AppendLine($"输出目录: {OutputRoot}");
            sb.AppendLine($"总计 {Total} | 成功 {Success} | 失败 {Failed} | 跳过 {Skipped} | 解压总量 {Security.PathGuard.FormatBytes(TotalExtractedBytes)}");
            sb.AppendLine(new string('-', 100));
            foreach (var i in Items)
            {
                sb.AppendLine($"{i.State,-8} | {i.DisplayName,-50} | {BatchStateText.Of(i.State)} | {i.Error ?? ""}");
            }
            return sb.ToString();
        }

        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("序号,文件名,路径,大小,格式,状态,错误信息,输出目录,解压文件数,解压大小,耗时结束时间");
            int idx = 1;
            foreach (var i in Items)
            {
                sb.AppendLine(string.Join(",",
                    idx++,
                    Csv(i.DisplayName),
                    Csv(i.Path),
                    i.SizeBytes,
                    Csv(i.FormatName),
                    Csv(BatchStateText.Of(i.State)),
                    Csv(i.Error ?? ""),
                    Csv(i.TargetDir ?? ""),
                    i.ExtractedFiles,
                    i.ExtractedBytes,
                    i.FinishedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""));
            }
            return sb.ToString();
        }

        static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
