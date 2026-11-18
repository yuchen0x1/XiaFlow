using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using XiaFlow.Core;
using XiaFlow.Core.Batch;
using XiaFlow.Core.Engines;
using Xunit;

namespace XiaFlow.Tests
{
    /// <summary>批量解压：智能识别 + 分类 + 批量执行。</summary>
    public class BatchTests : IDisposable
    {
        readonly string _ws;
        readonly PasswordVault _vault;

        public BatchTests()
        {
            _ws = TestEnv.NewWorkspace();
            _vault = new PasswordVault();
        }

        string MakeArchive(string name, string folderName, int files, string? password = null)
        {
            var dir = Path.Combine(_ws, folderName);
            Directory.CreateDirectory(dir);
            for (int i = 0; i < files; i++)
                File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), $"内容 {i}");
            var archive = Path.Combine(_ws, name);
            new SevenZipEngine().Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = archive,
                Format = name.EndsWith(".zip") ? ArchiveFormat.Zip : ArchiveFormat.SevenZ,
                InputPaths = { dir },
                Password = password,
            });
            return archive;
        }

        [Fact]
        public void Analyze_Classifies_Archives()
        {
            var okZip = MakeArchive("ok.zip", "okf", 3);
            var ok7z = MakeArchive("ok.7z", "ok7", 2);
            var enc = MakeArchive("enc.7z", "encf", 2, "batch-pass");
            var bad = MakeArchive("bad.7z", "badf", 2);
            {   // 破坏
                var b = File.ReadAllBytes(bad);
                for (int i = 100; i < 1500 && i < b.Length; i++) b[i] = 0xCD;
                File.WriteAllBytes(bad, b);
            }
            File.WriteAllText(Path.Combine(_ws, "notarc.xyz"), "plain text");

            var items = BatchAnalyzer.Import(new[] { _ws }, recursive: true);
            Assert.Equal(4, items.Count); // notarc.xyz 不可识别，不导入

            var analyzer = new BatchAnalyzer(_vault);
            analyzer.Analyze(items);

            var zip = items.Single(i => i.Path == okZip);
            Assert.Equal(BatchState.Ready, zip.State);
            Assert.Equal(3, zip.FileCount);
            Assert.Equal("ZIP", zip.FormatName);

            var encItem = items.Single(i => i.Path == enc);
            Assert.Equal(BatchState.NeedsPassword, encItem.State);

            var badItem = items.Single(i => i.Path == bad);
            Assert.Equal(BatchState.Corrupt, badItem.State);
        }

        [Fact]
        public void Analyze_Duplicate_Detection()
        {
            // 同名同大小 → 判定为重复
            var a = MakeArchive(Path.Combine("subA", "dup.zip"), "dupf", 2);
            var b = MakeArchive(Path.Combine("subB", "dup.zip"), "dupf", 2);
            var items = BatchAnalyzer.Import(new[] { a, b });
            Assert.Equal(2, items.Count);
            var analyzer = new BatchAnalyzer(_vault);
            analyzer.Analyze(items);
            var dup = items.FirstOrDefault(i => i.IsDuplicateOf);
            Assert.NotNull(dup);
            Assert.Equal(BatchState.Duplicate, dup.State);
            var first = items.Single(i => !i.IsDuplicateOf);
            Assert.Equal(BatchState.Ready, first.State);
        }

        [Fact]
        public void Run_ModeB_EachToOwnFolder_With_Password()
        {
            var a1 = MakeArchive("first.zip", "f1", 2);
            var a2 = MakeArchive("second.7z", "f2", 3, "pw123");
            var items = BatchAnalyzer.Import(new[] { a1, a2 });
            var analyzer = new BatchAnalyzer(_vault);
            analyzer.Analyze(items);

            var opt = new BatchOptions
            {
                Mode = BatchOutputMode.OwnFolderEach,
                OutputRoot = Path.Combine(_ws, "out"),
                Password = "pw123",
                MaxParallel = 2,
            };
            var runner = new BatchRunner(_vault);
            var report = runner.Run(items, opt, resolvePassword: _ => "pw123");

            Assert.Equal(2, report.Success);
            Assert.True(File.Exists(Path.Combine(_ws, "out", "first", "f1", "f0.txt")));
            Assert.True(File.Exists(Path.Combine(_ws, "out", "second", "f2", "f2.txt")));
        }

        [Fact]
        public void Run_ModeA_SingleDirectory()
        {
            var a1 = MakeArchive("m1.zip", "m1f", 2);
            var a2 = MakeArchive("m2.zip", "m2f", 2);
            var items = BatchAnalyzer.Import(new[] { a1, a2 });
            new BatchAnalyzer(_vault).Analyze(items);
            var opt = new BatchOptions
            {
                Mode = BatchOutputMode.SingleDirectory,
                OutputRoot = Path.Combine(_ws, "flat"),
            };
            var report = new BatchRunner(_vault).Run(items, opt);
            Assert.Equal(2, report.Success);
            Assert.True(File.Exists(Path.Combine(_ws, "flat", "m1f", "f1.txt")));
            Assert.True(File.Exists(Path.Combine(_ws, "flat", "m2f", "f0.txt")));
        }

        [Fact]
        public void Run_ModeE_OnlySelectedFileTypes()
        {
            var dir = Path.Combine(_ws, "mixed");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "doc.txt"), "text");
            File.WriteAllText(Path.Combine(dir, "pic.png"), "fakepng");
            File.WriteAllText(Path.Combine(dir, "video.mp4"), "fakevideo");
            var archive = Path.Combine(_ws, "mixed.zip");
            new SevenZipEngine().Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = archive, Format = ArchiveFormat.Zip, InputPaths = { dir },
            });

            var items = BatchAnalyzer.Import(new[] { archive });
            new BatchAnalyzer(_vault).Analyze(items);
            var opt = new BatchOptions
            {
                Mode = BatchOutputMode.OnlyFileTypes,
                OutputRoot = Path.Combine(_ws, "typed"),
                FileTypeFilter = { ".png" },
            };
            var report = new BatchRunner(_vault).Run(items, opt);
            Assert.Equal(1, report.Success);
            var item = items.Single();
            Assert.Equal(1, item.ExtractedFiles);
            Assert.True(File.Exists(Path.Combine(_ws, "typed", "mixed", "mixed", "pic.png")),
                "应解压到 typed/mixed/<归档内部路径>");
            Assert.False(File.Exists(Path.Combine(_ws, "typed", "mixed", "mixed", "doc.txt")));
        }

        [Fact]
        public void Run_Vault_Password_Matching()
        {
            var a = MakeArchive("vaulted.zip", "vf", 2, "vault-secret");
            _vault.Add("测试密码", "vault-secret");
            var items = BatchAnalyzer.Import(new[] { a });
            var analyzer = new BatchAnalyzer(_vault);
            analyzer.Analyze(items);
            // 检测阶段已从密码库匹配
            var item = items.Single();
            Assert.Equal(BatchState.Ready, item.State);
            Assert.NotNull(item.Password);

            var opt = new BatchOptions { Mode = BatchOutputMode.OwnFolderEach, OutputRoot = Path.Combine(_ws, "outv") };
            var report = new BatchRunner(_vault).Run(items, opt);
            Assert.Equal(1, report.Success);
        }

        [Fact]
        public void Batch_History_Marks_Processed_Before()
        {
            var a = MakeArchive("hist.zip", "hf", 2);
            var items = BatchAnalyzer.Import(new[] { a });
            new BatchAnalyzer(_vault).Analyze(items);
            var opt = new BatchOptions { Mode = BatchOutputMode.OwnFolderEach, OutputRoot = Path.Combine(_ws, "outh") };
            new BatchRunner(_vault).Run(items, opt);

            // 再次导入 → 已处理过
            var items2 = BatchAnalyzer.Import(new[] { a });
            BatchRunner.MarkProcessedBefore(items2);
            Assert.True(items2.Single().ProcessedBefore);
        }

        [Fact]
        public void Volume_Group_Primary_Only()
        {
            // 构造一个真正可解压的分卷组（.7z.001/.002）+ 检查主卷标记
            var dir = Path.Combine(_ws, "vg");
            Directory.CreateDirectory(dir);
            new Random(9).NextBytes(new byte[1]);
            File.WriteAllBytes(Path.Combine(dir, "d.bin"), new byte[20 * 1024]);
            new SevenZipEngine().Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = Path.Combine(_ws, "split.7z"),
                Format = ArchiveFormat.SevenZ,
                InputPaths = { dir },
                Level = CompressionLevelOption.Store,
                VolumeSizeBytes = 4096,
            });
            var vols = Directory.GetFiles(_ws, "split.7z.*").OrderBy(f => f).ToList();
            Assert.True(vols.Count >= 2);

            var items = BatchAnalyzer.Import(vols);
            var primaries = items.Where(i => i.IsPrimaryVolume).ToList();
            Assert.Single(primaries);
            Assert.Contains(primaries, p => p.Path.EndsWith(".001", StringComparison.Ordinal));
        }

        [Fact]
        public void Report_Export()
        {
            var a = MakeArchive("rep.zip", "rf", 1);
            var items = BatchAnalyzer.Import(new[] { a });
            new BatchAnalyzer(_vault).Analyze(items);
            var opt = new BatchOptions { Mode = BatchOutputMode.OwnFolderEach, OutputRoot = Path.Combine(_ws, "outr") };
            var report = new BatchRunner(_vault).Run(items, opt);
            report.FinishedAt = DateTime.Now;

            var txt = report.ToText();
            Assert.Contains("匣流 XiaFlow", txt);
            Assert.Contains("成功 1", txt);
            var csv = report.ToCsv();
            Assert.StartsWith("序号,文件名", csv);
        }

        public void Dispose() => TestEnv.Cleanup(_ws);
    }
}
