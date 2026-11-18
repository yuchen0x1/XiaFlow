using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Logging;
using XiaFlow.Core.Security;

namespace XiaFlow.Core
{
    /// <summary>压缩请求。</summary>
    public sealed class CompressOptions
    {
        public List<string> InputPaths { get; set; } = new();
        public string OutputArchive { get; set; } = "";
        public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZ;
        public CompressionLevelOption Level { get; set; } = CompressionLevelOption.Normal;
        public int Threads { get; set; } = Environment.ProcessorCount;
        public long? DictionaryBytes { get; set; }
        public bool Solid { get; set; } = true;
        public long? VolumeSizeBytes { get; set; }
        public string? Password { get; set; }
        public bool EncryptFileNames { get; set; }
        public List<string> ExcludePatterns { get; set; } = new();
        public bool TestAfterCreate { get; set; }
    }

    /// <summary>解压请求。</summary>
    public sealed class ExtractOptions
    {
        public string ArchivePath { get; set; } = "";
        public string OutputDir { get; set; } = "";
        public string? Password { get; set; }
        public List<string>? SelectedEntries { get; set; }
        public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Overwrite;
        public ExtractSafetyLimits? Limits { get; set; }
    }

    /// <summary>解压前置检查结果。</summary>
    public sealed class ExtractPlan
    {
        public bool CanProceed { get; set; }
        public ArchiveListing? Listing { get; set; }
        public SafetyReport? Safety { get; set; }
        public string? BlockReason { get; set; }
    }

    /// <summary>
    /// 高层归档服务：安全检查 + 引擎调用。供任务系统与 UI 共用。
    /// </summary>
    public static class ArchiveService
    {
        public static FormatInfo? DetectFormat(string path) => FormatRegistry.FindByExtension(path);

        /// <summary>加载列表（浏览/分析通用）。加密包抛 WrongPassword 异常。</summary>
        public static ArchiveListing LoadListing(string archive, string? password, SevenZipEngine? engine = null)
        {
            using var e = engine ?? new SevenZipEngine();
            return e.List(archive, password);
        }

        /// <summary>解压前置规划：格式识别 + 安全评估。</summary>
        public static ExtractPlan PlanExtraction(string archive, string targetDir, string? password,
            ExtractSafetyLimits? limits = null, List<string>? selected = null)
        {
            var plan = new ExtractPlan();
            limits ??= new ExtractSafetyLimits();

            FormatInfo? fi = FormatRegistry.FindByExtension(archive);
            if (fi == null)
            {
                // 用魔数兜底识别
                try
                {
                    using var fs = File.OpenRead(archive);
                    var f = FormatRegistry.DetectByMagic(fs);
                    if (f == ArchiveFormat.Unknown)
                    {
                        plan.BlockReason = "无法识别的文件格式";
                        return plan;
                    }
                }
                catch { plan.BlockReason = "无法读取文件"; return plan; }
            }

            ArchiveListing listing;
            try
            {
                listing = LoadListing(archive, password);
            }
            catch (ArchiveException aex)
            {
                plan.BlockReason = aex.Kind switch
                {
                    ArchiveErrorKind.WrongPassword => "需要密码或密码错误",
                    ArchiveErrorKind.NotArchive => "文件损坏或不是压缩包",
                    ArchiveErrorKind.VolumeMissing => "分卷文件缺失",
                    _ => aex.Message
                };
                return plan;
            }

            var entries = listing.Entries
                .Select(e => (e.Name, e.Size, e.IsDirectory, e.IsSymlink));
            var report = PathGuard.EvaluateExtraction(entries, targetDir, limits);

            plan.Listing = listing;
            plan.Safety = report;
            plan.CanProceed = report.IsSafe;
            if (!report.IsSafe)
                plan.BlockReason = string.Join("；", report.Violations.Take(3));
            return plan;
        }

        /// <summary>执行解压（含前置安全规划）。</summary>
        public static ExtractPlan Extract(SevenZipEngine engine, ExtractOptions opt, CancellationToken ct = default)
        {
            var plan = PlanExtraction(opt.ArchivePath, opt.OutputDir, opt.Password, opt.Limits, opt.SelectedEntries);
            if (!plan.CanProceed)
                throw new ArchiveException(ArchiveErrorKind.IoError, "安全检查未通过: " + plan.BlockReason);

            engine.Extract(new SevenZipEngine.ExtractRequest
            {
                ArchivePath = opt.ArchivePath,
                OutputDir = opt.OutputDir,
                Password = opt.Password,
                SelectedEntries = opt.SelectedEntries,
                Conflict = opt.Conflict
            }, ct);
            return plan;
        }

        /// <summary>执行压缩。</summary>
        public static void Compress(SevenZipEngine engine, CompressOptions opt, CancellationToken ct = default)
        {
            foreach (var p in opt.InputPaths)
            {
                if (!File.Exists(p) && !Directory.Exists(p))
                    throw new FileNotFoundException("输入路径不存在: " + p);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opt.OutputArchive))!);

            engine.Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = opt.OutputArchive,
                Format = opt.Format,
                Level = opt.Level,
                Threads = opt.Threads,
                DictionaryBytes = opt.DictionaryBytes,
                Solid = opt.Solid,
                VolumeSizeBytes = opt.VolumeSizeBytes,
                Password = opt.Password,
                EncryptFileNames = opt.EncryptFileNames,
                InputPaths = opt.InputPaths,
                ExcludePatterns = opt.ExcludePatterns,
            }, ct);

            if (opt.TestAfterCreate)
                engine.Test(opt.OutputArchive, opt.Password, ct);
        }

        /// <summary>校验完整性，返回是否通过。</summary>
        public static (bool ok, string? error) Verify(SevenZipEngine engine, string archive, string? password, CancellationToken ct = default)
        {
            try
            {
                engine.Test(archive, password, ct);
                return (true, null);
            }
            catch (ArchiveException aex) { return (false, aex.Message); }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>计算 SHA-256。</summary>
        public static string Sha256(string path, Action<long>? onProgress = null, CancellationToken ct = default)
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var buf = new byte[1024 * 1024];
            long total = stream.Length, read = 0;
            int n;
            while ((n = stream.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.TransformBlock(buf, 0, n, null, 0);
                read += n;
                onProgress?.Invoke(read);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
    }
}
