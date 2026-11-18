using System;

namespace XiaFlow.Core.Engines
{
    /// <summary>归档内部条目。</summary>
    public sealed class ArchiveEntry
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public long PackedSize { get; set; }
        public DateTime? Modified { get; set; }
        public string Attributes { get; set; } = "";
        public string Method { get; set; } = "";
        public string Crc { get; set; } = "";
        public bool Encrypted { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsSymlink { get; set; }

        public string Extension => System.IO.Path.GetExtension(Name);
        public double CompressionRatio =>
            Size > 0 && PackedSize >= 0 ? Math.Round((1.0 - (double)PackedSize / Size) * 100, 1) : 0;
    }

    /// <summary>一次列取/加载的结果。</summary>
    public sealed class ArchiveListing
    {
        public string ArchivePath { get; set; } = "";
        public string FormatName { get; set; } = "";   // 引擎报告的类型，如 7z / zip / PE
        public System.Collections.Generic.List<ArchiveEntry> Entries { get; } = new();
        public long TotalUncompressed { get; set; }
        public long TotalPacked { get; set; }
        public int FileCount { get; set; }
        public int DirCount { get; set; }
        public bool HasEncryptedEntries { get; set; }
        public bool IsVolumeArchive { get; set; }      // 由分卷首卷打开
        public string? PasswordUsed { get; set; }
    }

    /// <summary>引擎错误类别。</summary>
    public enum ArchiveErrorKind
    {
        Unknown,
        WrongPassword,
        CorruptArchive,
        NotArchive,
        VolumeMissing,
        DiskFull,
        Cancelled,
        IoError
    }

    /// <summary>引擎操作异常（携带可分类错误）。</summary>
    public class ArchiveException : Exception
    {
        public ArchiveErrorKind Kind { get; }

        public ArchiveException(ArchiveErrorKind kind, string message, Exception? inner = null)
            : base(message, inner)
        {
            Kind = kind;
        }
    }

    /// <summary>进度信息（真实来自引擎输出）。</summary>
    public sealed class EngineProgress
    {
        public int? Percent { get; init; }
        public string? CurrentFile { get; init; }
        public string Text { get; init; } = "";
    }

    /// <summary>压缩等级。</summary>
    public enum CompressionLevelOption
    {
        Store = 0,      // 存储
        Fastest = 1,    // 极速
        Fast = 3,       // 快速
        Normal = 5,     // 标准
        High = 7,       // 高
        Maximum = 9     // 极限
    }

    /// <summary>解压冲突策略。</summary>
    public enum ConflictPolicy
    {
        Overwrite,   // 覆盖
        Skip,        // 跳过已存在
        AutoRename   // 自动重命名
    }
}
