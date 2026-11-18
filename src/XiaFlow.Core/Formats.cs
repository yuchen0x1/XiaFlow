using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XiaFlow.Core
{
    /// <summary>归档格式标识。</summary>
    public enum ArchiveFormat
    {
        Unknown,
        Zip, SevenZ, Rar, Tar,
        GZip, BZip2, XZ, Zstd, Lz4, Lzma,
        Wim, Iso, Cab, Cpio, Rpm, Deb,
        Arj, Lzh, Chm, Msi, Dmg, Vhd, Vdi,
        SquashFs, Udf, Split, Ntfs, Fat, Ext
    }

    /// <summary>格式能力标记（诚实声明，不做任何伪装）。</summary>
    [Flags]
    public enum ArchiveCapabilities
    {
        None = 0,
        List = 1,
        Extract = 2,
        Create = 4,
        Modify = 8,       // 可添加/删除/重命名内部文件
        TestIntegrity = 16,
        Encrypt = 32,     // 支持密码加密创建
        EncryptFileNames = 64 // 支持加密文件名（7z）
    }

    /// <summary>格式元数据。</summary>
    public sealed record FormatInfo(
        ArchiveFormat Format,
        string DisplayName,
        string[] Extensions,
        ArchiveCapabilities Capabilities,
        string IconGlyph = "archive");

    /// <summary>
    /// 格式注册表：静态声明 + 引擎运行时探测（7z i）共同决定最终能力。
    /// </summary>
    public static class FormatRegistry
    {
        static readonly List<FormatInfo> _formats = new()
        {
            new(ArchiveFormat.Zip, "ZIP", new[] { ".zip", ".jar", ".ipa", ".apk", ".docx", ".xlsx", ".pptx", ".epub", ".odt", ".odp", ".ods" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.Modify | ArchiveCapabilities.TestIntegrity | ArchiveCapabilities.Encrypt),
            new(ArchiveFormat.SevenZ, "7Z", new[] { ".7z" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.Modify | ArchiveCapabilities.TestIntegrity | ArchiveCapabilities.Encrypt | ArchiveCapabilities.EncryptFileNames),
            new(ArchiveFormat.Rar, "RAR", new[] { ".rar", ".r00", ".r01" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Tar, "TAR", new[] { ".tar", ".ova" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.Modify | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.GZip, "GZIP", new[] { ".gz", ".tgz", ".tpz" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.BZip2, "BZIP2", new[] { ".bz2", ".tbz2", ".tbz" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.XZ, "XZ", new[] { ".xz", ".txz" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Zstd, "ZSTD", new[] { ".zst", ".tzst" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Lz4, "LZ4", new[] { ".lz4" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Lzma, "LZMA", new[] { ".lzma", ".lzma86" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Wim, "WIM", new[] { ".wim", ".swm", ".esd" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Iso, "ISO", new[] { ".iso", ".img" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Cab, "CAB", new[] { ".cab" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Cpio, "CPIO", new[] { ".cpio" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Rpm, "RPM", new[] { ".rpm" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Deb, "DEB", new[] { ".deb" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Arj, "ARJ", new[] { ".arj" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Lzh, "LZH", new[] { ".lzh", ".lha" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Chm, "CHM", new[] { ".chm", ".chi", ".chw" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract),
            new(ArchiveFormat.Msi, "MSI", new[] { ".msi", ".msp", ".doc", ".xls", ".ppt" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Dmg, "DMG", new[] { ".dmg" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Vhd, "VHD", new[] { ".vhd" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
            new(ArchiveFormat.Vdi, "VDI", new[] { ".vdi" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract),
            new(ArchiveFormat.SquashFs, "SquashFS", new[] { ".squashfs" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract),
            new(ArchiveFormat.Udf, "UDF", new[] { ".udf" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract),
            new(ArchiveFormat.Split, "分卷", new[] { ".001", ".002" },
                ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity),
        };

        /// <summary>运行时探测到的 7z 支持格式（小写扩展名 → 是否可创建）。</summary>
        public static IReadOnlyDictionary<string, bool> EngineDetectedExtensions => _engineExts;
        static readonly Dictionary<string, bool> _engineExts = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<FormatInfo> Formats => _formats;

        /// <summary>根据扩展名识别格式。</summary>
        public static FormatInfo? FindByExtension(string path)
        {
            var name = Path.GetFileName(path);
            // 分卷优先：xxx.7z.001 / xxx.zip.001 / xxx.part1.rar
            if (name.Length > 4)
            {
                var dot = name.LastIndexOf('.');
                if (dot > 0)
                {
                    var tail = name[(dot + 1)..];
                    if (tail.Length is 2 or 3 && tail.All(char.IsDigit))
                    {
                        var baseExt = Path.GetExtension(name[..dot]).ToLowerInvariant();
                        return new FormatInfo(ArchiveFormat.Split, "分卷 (" + baseExt.TrimStart('.').ToUpperInvariant() + ")",
                            new[] { "." + tail }, ArchiveCapabilities.List | ArchiveCapabilities.Extract | ArchiveCapabilities.TestIntegrity);
                    }
                }
            }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) return null;
            foreach (var f in _formats)
                if (f.Extensions.Contains(ext)) return f;
            return null;
        }

        /// <summary>通过文件头魔数识别（覆盖无扩展名/错误扩展名的情况）。</summary>
        public static ArchiveFormat DetectByMagic(Stream stream)
        {
            byte[] buf = new byte[1024];
            int n = stream.Read(buf, 0, buf.Length);
            if (n < 4) return ArchiveFormat.Unknown;
            var b = buf.AsSpan(0, n);

            static bool Eq(ReadOnlySpan<byte> b, ReadOnlySpan<byte> sig) =>
                b.Length >= sig.Length && b[..sig.Length].SequenceEqual(sig);
            static bool At(ReadOnlySpan<byte> b, int offset, ReadOnlySpan<byte> sig) =>
                b.Length >= offset + sig.Length && b.Slice(offset, sig.Length).SequenceEqual(sig);

            if (Eq(b, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return ArchiveFormat.SevenZ;
            if (Eq(b, new byte[] { 0x50, 0x4B, 0x03, 0x04 }) || Eq(b, new byte[] { 0x50, 0x4B, 0x05, 0x06 }) || Eq(b, new byte[] { 0x50, 0x4B, 0x07, 0x08 })) return ArchiveFormat.Zip;
            if (Eq(b, "Rar!\x1A\x07"u8)) return ArchiveFormat.Rar;
            if (At(b, 0, "MSWIM\x00\x00\x00"u8) || At(b, 0, "WLPWM\x00\x00\x00"u8)) return ArchiveFormat.Wim;
            if (Eq(b, new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 })) return ArchiveFormat.XZ;
            if (Eq(b, new byte[] { 0x1F, 0x8B })) return ArchiveFormat.GZip;
            if (Eq(b, "BZh"u8) && n >= 10 && (buf[3] >= '0' && buf[3] <= '9') && buf[4] == 0x31 && buf[5] == 0x41 && buf[6] == 0x59 && buf[7] == 0x26) return ArchiveFormat.BZip2;
            if (Eq(b, new byte[] { 0x28, 0xB5, 0x2F, 0xFD })) return ArchiveFormat.Zstd;
            if (Eq(b, new byte[] { 0x04, 0x22, 0x4D, 0x18 })) return ArchiveFormat.Lz4;
            if (Eq(b, "MSCF\x00\x00\x00\x00"u8)) return ArchiveFormat.Cab;
            if (Eq(b, "ITOLITLS"u8)) return ArchiveFormat.Chm;
            if (Eq(b, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return ArchiveFormat.Msi;
            if (Eq(b, "\xED\xAB\xEE\xDB"u8)) return ArchiveFormat.Rpm;
            if (Eq(b, "\x60\xEA"u8)) return ArchiveFormat.Arj;
            if (At(b, 257, "ustar"u8)) return ArchiveFormat.Tar;
            if (Eq(b, "07070"u8)) return ArchiveFormat.Cpio;
            if (At(b, 32769, "CD001"u8) || At(b, 32768, "CD001"u8)) return ArchiveFormat.Iso;
            if (Eq(b, "koly"u8)) return ArchiveFormat.Dmg;
            if (Eq(b, "conectix"u8)) return ArchiveFormat.Vhd;
            if (At(b, 1024, "\x53\xEF"u8)) return ArchiveFormat.Ext;
            if (Eq(b, "hsqs"u8) || Eq(b, "sqsh"u8) || Eq(b, "shsq"u8) || Eq(b, "qshs"u8)) return ArchiveFormat.SquashFs;
            return ArchiveFormat.Unknown;
        }

        /// <summary>运行时探测 7z 引擎格式表，更新能力（用于诚实校准）。</summary>
        public static void CalibrateFromEngine(string engineListOutput)
        {
            _engineExts.Clear();
            foreach (var line in engineListOutput.Split('\n'))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                var parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && (t.Contains(" CK ") || t.StartsWith("0 C") || t.Contains(" C")))
                {
                    var canCreate = parts[1].Contains('C') || (parts.Length > 1 && parts[0] == "0" && parts[1].StartsWith('C'));
                    var exts = parts[2].Split(' ');
                    foreach (var e in exts)
                        if (e.StartsWith('.') || e.All(c => char.IsAsciiLetterOrDigit(c)))
                            _engineExts[e.ToLowerInvariant()] = canCreate;
                }
            }
        }
    }
}
