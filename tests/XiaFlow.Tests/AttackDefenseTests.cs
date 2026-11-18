using System;
using System.IO;
using System.Linq;
using SharpCompress.Writers.Zip;
using ArchiveException = XiaFlow.Core.Engines.ArchiveException;using XiaFlow.Core;
using XiaFlow.Core.Engines;
using Xunit;

namespace XiaFlow.Tests
{
    /// <summary>解压安全攻击防护（真实攻击样本）。</summary>
    public class AttackDefenseTests : IDisposable
    {
        readonly string _ws;

        public AttackDefenseTests()
        {
            _ws = TestEnv.NewWorkspace();
        }

        /// <summary>手工构造带路径穿越条目的恶意 ZIP（stored，无压缩）。</summary>
        static void MakeEvilZip(string path, string entryName)
        {
            var data = System.Text.Encoding.UTF8.GetBytes("EVIL-CONTENT-8F3A");
            using var fs = File.Create(path);
            using (var w = new BinaryWriter(fs))
            {
                uint crc = Crc32(data);
                long localOffset = 0;
                // local file header
                w.Write(0x04034b50); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
                w.Write((ushort)0); w.Write((ushort)0); w.Write(crc);
                w.Write((uint)data.Length); w.Write((uint)data.Length);
                w.Write((ushort)entryName.Length); w.Write((ushort)0);
                w.Write(System.Text.Encoding.UTF8.GetBytes(entryName));
                w.Write(data);
                long cdOffset = fs.Position;
                // central directory
                w.Write(0x02014b50); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0);
                w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(crc);
                w.Write((uint)data.Length); w.Write((uint)data.Length);
                w.Write((ushort)entryName.Length); w.Write((ushort)0); w.Write((ushort)0);
                w.Write((ushort)0); w.Write((ushort)0); w.Write((uint)0); w.Write((uint)localOffset);
                w.Write(System.Text.Encoding.UTF8.GetBytes(entryName));
                long cdSize = fs.Position - cdOffset;
                // EOCD
                w.Write(0x06054b50); w.Write((ushort)0); w.Write((ushort)0);
                w.Write((ushort)1); w.Write((ushort)1);
                w.Write((uint)cdSize); w.Write((uint)cdOffset); w.Write((ushort)0);
            }
        }

        static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }

        [Fact]
        public void ZipSlip_Entry_Is_Blocked_By_Plan()
        {
            var evilZip = Path.Combine(_ws, "evil.zip");
            MakeEvilZip(evilZip, "../evil-zipslip.txt");
            var targetDir = Path.Combine(_ws, "target");
            Directory.CreateDirectory(targetDir);

            var plan = ArchiveService.PlanExtraction(evilZip, targetDir, null);
            Assert.False(plan.CanProceed);
            Assert.NotNull(plan.Safety);
            Assert.Contains(plan.Safety.Violations, v => v.Contains("不安全条目"));
        }

        [Fact]
        public void ZipSlip_Absolute_Path_Blocked()
        {
            var evilZip = Path.Combine(_ws, "evil2.zip");
            MakeEvilZip(evilZip, "C:/evil-absolute.txt");
            var targetDir = Path.Combine(_ws, "target2");
            Directory.CreateDirectory(targetDir);

            var plan = ArchiveService.PlanExtraction(evilZip, targetDir, null);
            Assert.False(plan.CanProceed);
        }

        [Fact]
        public void Engine_Extraction_Does_Not_Escape_Target()
        {
            // 即便直接调用引擎（绕过 PlanExtraction），7z 也不得写逃逸文件
            var evilZip = Path.Combine(_ws, "evil3.zip");
            MakeEvilZip(evilZip, "../evil3.txt");
            var targetDir = Path.Combine(_ws, "target3");
            Directory.CreateDirectory(targetDir);

            using var engine = new SevenZipEngine();
            try
            {
                engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = evilZip, OutputDir = targetDir });
            }
            catch (ArchiveException) { /* 引擎可能拒绝条目 — 可接受 */ }

            Assert.False(File.Exists(Path.Combine(_ws, "evil3.txt")), "路径穿越条目逃逸出目标目录！");
            Assert.False(File.Exists(Path.Combine(targetDir, "..", "evil3.txt")));
        }

        [Fact]
        public void Safe_Archive_Still_Extracts_After_Attack_Checks()
        {
            // 正常 zip 在相同防线下应成功
            var good = Path.Combine(_ws, "good.zip");
            MakeEvilZip(good, "innocent.txt");
            var targetDir = Path.Combine(_ws, "target4");
            var plan = ArchiveService.PlanExtraction(good, targetDir, null);
            Assert.True(plan.CanProceed, string.Join(";", plan.Safety?.Violations ?? new System.Collections.Generic.List<string>()));
            using var engine = new SevenZipEngine();
            engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = good, OutputDir = targetDir });
            Assert.True(File.Exists(Path.Combine(targetDir, "innocent.txt")));
            Assert.Equal("EVIL-CONTENT-8F3A", File.ReadAllText(Path.Combine(targetDir, "innocent.txt")));
        }

        public void Dispose() => TestEnv.Cleanup(_ws);
    }
}
