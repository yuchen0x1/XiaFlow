using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XiaFlow.Core.Security
{
    /// <summary>解压安全检查结果。</summary>
    public sealed class SafetyReport
    {
        public bool IsSafe => Violations.Count == 0;
        public List<string> Violations { get; } = new();
        public List<string> SanitizedEntries { get; } = new(); // 需要改名的条目
        public long TotalUncompressedBytes { get; set; }
        public int FileCount { get; set; }
        public int SymlinkCount { get; set; }
        public List<string> SkippedEntries { get; } = new();
    }

    /// <summary>
    /// 解压安全防线：路径穿越（Zip Slip）、绝对路径、非法字符、符号链接、
    /// 压缩炸弹、超大文件、海量小文件、磁盘空间不足。
    /// </summary>
    public static class PathGuard
    {
        static readonly char[] InvalidNameChars = { ':', '*', '?', '"', '<', '>', '|' };
        public static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };

        /// <summary>
        /// 校验并清洗归档内条目名，返回相对目标根目录的安全相对路径。
        /// 不安全则返回 null（调用方应跳过该条目）。
        /// </summary>
        public static string? SanitizeEntryName(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName)) return null;
            var name = entryName.Replace('\\', '/').Trim();

            // 拒绝绝对路径（盘符 / UNC / Win32 命名空间）
            if (name.Contains(':') && (name.Length > 1 && name[1] == ':')) return null;
            if (name.StartsWith("//") || name.StartsWith("/")) name = name.TrimStart('/');
            if (entryName.Contains(':')) return null; // 任何冒号都视为不安全（NTFS 流 / 盘符）

            var sb = new StringBuilder();
            int depth = 0;
            var segments = new List<string>();
            foreach (var rawSeg in name.Split('/'))
            {
                var seg = rawSeg.Trim();
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..")
                {
                    depth--;
                    if (depth < 0) return null; // 试图逃逸根目录
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                    continue;
                }
                // 清洗非法字符
                foreach (var c in InvalidNameChars)
                    seg = seg.Replace(c, '_');
                // 保留 Windows 设备命名空间防穿越
                if (seg.StartsWith("\\\\?\\") || seg.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase)) return null;
                // 保留名
                var baseName = Path.GetFileNameWithoutExtension(seg).ToUpperInvariant();
                if (ReservedNames.Contains(baseName)) seg = "_" + seg;
                // 尾部空格/点
                seg = seg.TrimEnd(' ', '.');
                if (seg.Length == 0) seg = "_unnamed_";
                segments.Add(seg);
                depth++;
            }
            if (segments.Count == 0) return null;
            return string.Join('/', segments);
        }

        /// <summary>
        /// 解压前整体安全评估：条目路径、总量炸弹、文件数、磁盘空间。
        /// </summary>
        public static SafetyReport EvaluateExtraction(
            IEnumerable<(string Name, long Size, bool IsDir, bool IsLink)> entries,
            string targetDir,
            ExtractSafetyLimits limits)
        {
            var report = new SafetyReport();
            var root = Path.GetFullPath(targetDir);

            foreach (var (rawName, size, isDir, isLink) in entries)
            {
                if (isLink)
                {
                    report.SymlinkCount++;
                    report.SkippedEntries.Add(rawName);
                    continue;
                }
                var safe = SanitizeEntryName(rawName);
                if (safe == null)
                {
                    report.Violations.Add($"不安全条目已拒绝: {rawName}");
                    continue;
                }
                if (!string.Equals(safe, rawName.Replace('\\', '/'), StringComparison.Ordinal))
                    report.SanitizedEntries.Add($"{rawName} → {safe}");

                if (!isDir)
                {
                    report.FileCount++;
                    report.TotalUncompressedBytes += Math.Max(0, size);
                    if (size > limits.MaxSingleFileBytes)
                        report.Violations.Add($"超大文件（{FormatBytes(size)} 超过限制 {FormatBytes(limits.MaxSingleFileBytes)}）: {rawName}");
                }
            }

            if (report.TotalUncompressedBytes > limits.MaxTotalUncompressedBytes)
                report.Violations.Add($"解压总量 {FormatBytes(report.TotalUncompressedBytes)} 超过安全限制 {FormatBytes(limits.MaxTotalUncompressedBytes)}（疑似压缩炸弹）");
            if (report.FileCount > limits.MaxFileCount)
                report.Violations.Add($"文件数量 {report.FileCount} 超过限制 {limits.MaxFileCount}（海量小文件防护）");

            // 磁盘空间检查
            try
            {
                Directory.CreateDirectory(targetDir);
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetDir)) ?? "C:\\");
                long needed = (long)(report.TotalUncompressedBytes * limits.FreeSpaceFactor);
                if (drive.IsReady && drive.AvailableFreeSpace < needed)
                    report.Violations.Add($"磁盘空间不足：需要约 {FormatBytes(needed)}，当前可用 {FormatBytes(drive.AvailableFreeSpace)}");
            }
            catch (Exception ex)
            {
                report.Violations.Add("无法检查磁盘空间: " + ex.Message);
            }

            return report;
        }

        /// <summary>目标路径最终防线：确保完整路径位于目标根目录内。</summary>
        public static bool IsInsideDirectory(string fullPath, string rootDir)
        {
            var fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(fullPath);
            return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.##} {units[u]}";
        }
    }

    /// <summary>解压安全限制（可在设置中调整）。</summary>
    public sealed class ExtractSafetyLimits
    {
        public long MaxTotalUncompressedBytes { get; set; } = 20L * 1024 * 1024 * 1024; // 20GB
        public long MaxSingleFileBytes { get; set; } = 8L * 1024 * 1024 * 1024;        // 8GB
        public int MaxFileCount { get; set; } = 200_000;
        public double FreeSpaceFactor { get; set; } = 1.05;                             // 需要 105% 空间
    }
}
