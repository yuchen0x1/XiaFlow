using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XiaFlow.Core;
using XiaFlow.Core.Engines;
using Xunit;

namespace XiaFlow.Tests
{
    /// <summary>真实 7-Zip 引擎端到端测试（进程级）。</summary>
    public class SevenZipEngineTests : IDisposable
    {
        readonly string _ws;
        readonly SevenZipEngine _engine;

        public SevenZipEngineTests()
        {
            _ws = TestEnv.NewWorkspace();
            _engine = new SevenZipEngine();
        }

        string MakeFile(string rel, string content, DateTime? mtime = null)
        {
            var p = Path.Combine(_ws, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
            if (mtime != null) File.SetLastWriteTime(p, mtime.Value);
            return p;
        }

        string MakeDirWithFiles(string name, int fileCount)
        {
            var dir = Path.Combine(_ws, name);
            Directory.CreateDirectory(dir);
            for (int i = 0; i < fileCount; i++)
                File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), $"内容 {i} XiaFlow 测试文件 " + new string('X', 100 + i * 7));
            return dir;
        }

        [Fact]
        public void Create_Extract_Zip_Roundtrip()
        {
            var srcDir = MakeDirWithFiles("project", 5);
            var archive = Path.Combine(_ws, "out.zip");

            _engine.Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = archive,
                Format = ArchiveFormat.Zip,
                Level = CompressionLevelOption.Normal,
                InputPaths = { srcDir },
            });
            Assert.True(File.Exists(archive));

            var listing = _engine.List(archive);
            Assert.Equal(5, listing.FileCount);
            Assert.True(listing.TotalUncompressed > 0);
            Assert.Contains(listing.Entries, e => e.Name.Replace('\\', '/') == "project/f3.txt");

            var outDir = Path.Combine(_ws, "extracted");
            _engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = archive, OutputDir = outDir });
            Assert.True(File.Exists(Path.Combine(outDir, "project", "f3.txt")));
            Assert.Equal("内容 3 XiaFlow 测试文件 " + new string('X', 121), File.ReadAllText(Path.Combine(outDir, "project", "f3.txt")));
        }

        [Fact]
        public void Create_Extract_7z_Preserves_Structure_And_Mtime()
        {
            var srcDir = MakeDirWithFiles("docs", 3);
            var sub = Path.Combine(srcDir, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "deep.txt"), "深层文件");
            var mtime = new DateTime(2025, 6, 1, 12, 30, 0);
            File.SetLastWriteTime(Path.Combine(sub, "deep.txt"), mtime);

            var archive = Path.Combine(_ws, "out.7z");
            _engine.Create(new SevenZipEngine.CreateRequest { ArchivePath = archive, Format = ArchiveFormat.SevenZ, InputPaths = { srcDir } });

            var outDir = Path.Combine(_ws, "out");
            _engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = archive, OutputDir = outDir });
            var restored = Path.Combine(outDir, "docs", "sub", "deep.txt");
            Assert.True(File.Exists(restored));
            Assert.Equal("深层文件", File.ReadAllText(restored));
            // 保留修改时间（±1s）
            var got = File.GetLastWriteTime(restored);
            Assert.True(Math.Abs((got - mtime).TotalSeconds) <= 1, $"mtime 不匹配: {got} vs {mtime}");
        }

        [Fact]
        public void Password_7z_Wrong_And_Right()
        {
            var srcDir = MakeDirWithFiles("secret", 2);
            var archive = Path.Combine(_ws, "enc.7z");
            const string pwd = "XiaFlow@Test2026";

            _engine.Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = archive,
                Format = ArchiveFormat.SevenZ,
                InputPaths = { srcDir },
                Password = pwd,
                EncryptFileNames = true,
            });

            // 正确密码
            var outDir = Path.Combine(_ws, "out-ok");
            _engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = archive, OutputDir = outDir, Password = pwd });
            Assert.True(File.Exists(Path.Combine(outDir, "secret", "f0.txt")));

            // 错误密码 → WrongPassword
            var outDir2 = Path.Combine(_ws, "out-bad");
            var ex = Assert.Throws<ArchiveException>(() =>
                _engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = archive, OutputDir = outDir2, Password = "wrong" }));
            Assert.Equal(ArchiveErrorKind.WrongPassword, ex.Kind);

            // 加密文件名：无密码列出失败
            Assert.Throws<ArchiveException>(() => _engine.List(archive));
        }

        [Fact]
        public void Zip_AES_Encryption()
        {
            var srcDir = MakeDirWithFiles("zaes", 2);
            var archive = Path.Combine(_ws, "zaes.zip");
            _engine.Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = archive,
                Format = ArchiveFormat.Zip,
                InputPaths = { srcDir },
                Password = "aes-password",
            });
            var listing = _engine.List(archive);
            Assert.True(listing.HasEncryptedEntries, "ZIP 条目应标记为加密");
            var outDir = Path.Combine(_ws, "out");
            _engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = archive, OutputDir = outDir, Password = "aes-password" });
            Assert.True(File.Exists(Path.Combine(outDir, "zaes", "f1.txt")));
        }

        [Fact]
        public void Split_Volumes_Create_And_Extract()
        {
            // 生成约 20KB 内容（重复文本压缩比高，需关掉高压缩便于分卷）
            var dir = Path.Combine(_ws, "vol");
            Directory.CreateDirectory(dir);
            var rnd = new Random(42);
            var data = new byte[30 * 1024];
            rnd.NextBytes(data);
            File.WriteAllBytes(Path.Combine(dir, "data.bin"), data);

            var archive = Path.Combine(_ws, "voldata.7z");
            _engine.Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = archive,
                Format = ArchiveFormat.SevenZ,
                InputPaths = { dir },
                Level = CompressionLevelOption.Store, // 存储模式确保产生多个分卷
                VolumeSizeBytes = 10240,              // 10KB 分卷
            });

            var volumes = Directory.GetFiles(_ws, "voldata.7z.*");
            Assert.True(volumes.Length > 1, $"应产生多个分卷，实际 {volumes.Length}");

            // 用首卷解压
            var first = Path.Combine(_ws, "voldata.7z.001");
            Assert.True(File.Exists(first));
            var outDir = Path.Combine(_ws, "out");
            _engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = first, OutputDir = outDir });
            var restored = Path.Combine(outDir, "vol", "data.bin");
            Assert.True(File.Exists(restored));
            Assert.Equal(data.Length, new FileInfo(restored).Length);
        }

        [Fact]
        public void Corrupt_Archive_Detected()
        {
            var srcDir = MakeDirWithFiles("corrupt", 2);
            var archive = Path.Combine(_ws, "bad.7z");
            _engine.Create(new SevenZipEngine.CreateRequest { ArchivePath = archive, Format = ArchiveFormat.SevenZ, InputPaths = { srcDir } });

            // 破坏文件内容
            var bytes = File.ReadAllBytes(archive);
            for (int i = 32; i < Math.Min(2048, bytes.Length); i++) bytes[i] = 0xAB;
            File.WriteAllBytes(archive, bytes);

            // 列出或测试应报损坏/无法识别
            var ex = Assert.ThrowsAny<ArchiveException>(() => _engine.Test(archive));
            Assert.NotEqual(ArchiveErrorKind.WrongPassword, ex.Kind);
        }

        [Fact]
        public void Truncated_Zip_NotArchive_Or_Corrupt()
        {
            var srcDir = MakeDirWithFiles("trunc", 2);
            var archive = Path.Combine(_ws, "t.zip");
            _engine.Create(new SevenZipEngine.CreateRequest { ArchivePath = archive, Format = ArchiveFormat.Zip, InputPaths = { srcDir } });
            var bytes = File.ReadAllBytes(archive);
            File.WriteAllBytes(archive, bytes.Take(bytes.Length / 2).ToArray());
            var ex = Assert.ThrowsAny<ArchiveException>(() => _engine.Test(archive));
            Assert.True(ex.Kind is ArchiveErrorKind.NotArchive or ArchiveErrorKind.CorruptArchive or ArchiveErrorKind.Unknown, ex.Kind.ToString());
        }

        [Fact]
        public void Missing_Volume_Fails_With_VolumeMissing()
        {
            var dir = Path.Combine(_ws, "vm");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "big.bin"), new byte[40 * 1024]);
            var archive = Path.Combine(_ws, "m.7z");
            _engine.Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = archive, Format = ArchiveFormat.SevenZ, InputPaths = { dir },
                Level = CompressionLevelOption.Store, VolumeSizeBytes = 8192,
            });
            // 删除中间分卷
            var vols = Directory.GetFiles(_ws, "m.7z.0??").OrderBy(f => f).ToList();
            Assert.True(vols.Count > 1);
            File.Delete(vols[1]);
            var ex = Assert.Throws<ArchiveException>(() =>
                _engine.Extract(new SevenZipEngine.ExtractRequest
                {
                    ArchivePath = Path.Combine(_ws, "m.7z.001"),
                    OutputDir = Path.Combine(_ws, "out"),
                }));
            Assert.True(ex.Kind is ArchiveErrorKind.VolumeMissing or ArchiveErrorKind.CorruptArchive or ArchiveErrorKind.Unknown,
                $"实际错误: {ex.Kind}");
        }

        [Fact]
        public void Cancel_Throws_Cancelled()
        {
            // 构造较难压缩的随机数据，给取消留出时间窗口
            var dir = Path.Combine(_ws, "cancel");
            Directory.CreateDirectory(dir);
            var rnd = new Random(7);
            File.WriteAllBytes(Path.Combine(dir, "rnd.bin"), RandomBytes(rnd, 8 * 1024 * 1024));

            var cts = new CancellationTokenSource();
            _engine.Progress += (_, p) => { if (p.Percent is > 0) cts.Cancel(); };

            var ex = Assert.ThrowsAny<Exception>(() =>
                _engine.Create(new SevenZipEngine.CreateRequest
                {
                    ArchivePath = Path.Combine(_ws, "c.7z"),
                    Format = ArchiveFormat.SevenZ,
                    InputPaths = { dir },
                    Level = CompressionLevelOption.Maximum,
                }, cts.Token));
            Assert.True(ex is OperationCanceledException or ArchiveException { Kind: ArchiveErrorKind.Cancelled },
                $"取消应产生取消异常，实际 {ex.GetType().Name}: {ex.Message}");
        }

        [Fact]
        public void Archive_Modify_Add_Delete_Rename()
        {
            var srcDir = MakeDirWithFiles("mod", 2);
            var archive = Path.Combine(_ws, "mod.7z");
            _engine.Create(new SevenZipEngine.CreateRequest { ArchivePath = archive, Format = ArchiveFormat.SevenZ, InputPaths = { srcDir } });

            // 添加
            var extra = MakeFile("extra/new.txt", "新增文件");
            _engine.AddToArchive(archive, new List<(string, string)> { (extra, "mod/extra.txt") });
            var listing = _engine.List(archive);
            Assert.Contains(listing.Entries, e => e.Name.Replace('\\', '/') == "mod/extra.txt");

            // 重命名
            _engine.RenameEntries(archive, new List<(string, string)> { ("mod/extra.txt", "mod/renamed.txt") });
            listing = _engine.List(archive);
            Assert.Contains(listing.Entries, e => e.Name.Replace('\\', '/') == "mod/renamed.txt");

            // 删除
            _engine.DeleteEntries(archive, new[] { "mod/renamed.txt" });
            listing = _engine.List(archive);
            Assert.DoesNotContain(listing.Entries, e => e.Name.Replace('\\', '/') == "mod/renamed.txt");
        }

        [Fact]
        public void Test_Integrity_Pass()
        {
            var srcDir = MakeDirWithFiles("ok", 3);
            var archive = Path.Combine(_ws, "ok.zip");
            _engine.Create(new SevenZipEngine.CreateRequest { ArchivePath = archive, Format = ArchiveFormat.Zip, InputPaths = { srcDir } });
            var listing = _engine.Test(archive);
            Assert.Equal(3, listing.FileCount);
        }

        [Fact]
        public void Progress_Events_Received()
        {
            var dir = Path.Combine(_ws, "prog");
            Directory.CreateDirectory(dir);
            var rnd = new Random(1);
            for (int i = 0; i < 4; i++)
                File.WriteAllBytes(Path.Combine(dir, $"p{i}.bin"), RandomBytes(rnd, 2 * 1024 * 1024));

            int maxPercent = 0;
            _engine.Progress += (_, p) => { if (p.Percent is int v && v > maxPercent) maxPercent = v; };
            _engine.Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = Path.Combine(_ws, "p.7z"),
                Format = ArchiveFormat.SevenZ,
                InputPaths = { dir },
            });
            Assert.True(maxPercent > 0, "应收到真实引擎进度");
        }

        [Fact]
        public void Chinese_Filenames_Supported()
        {
            var dir = Path.Combine(_ws, "中文目录");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "文档一.txt"), "中文内容测试");
            File.WriteAllText(Path.Combine(dir, "文档二.txt"), "第二份");
            var archive = Path.Combine(_ws, "中文压缩.7z");
            _engine.Create(new SevenZipEngine.CreateRequest { ArchivePath = archive, Format = ArchiveFormat.SevenZ, InputPaths = { dir } });

            var listing = _engine.List(archive);
            Assert.Contains(listing.Entries, e => e.Name.Contains("文档一.txt"));
            var outDir = Path.Combine(_ws, "输出");
            _engine.Extract(new SevenZipEngine.ExtractRequest { ArchivePath = archive, OutputDir = outDir });
            Assert.True(File.Exists(Path.Combine(outDir, "中文目录", "文档一.txt")));
        }

        [Fact]
        public void Exclude_Patterns_Works()
        {
            var dir = Path.Combine(_ws, "exc");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(dir, "skip.log"), "skip");
            var archive = Path.Combine(_ws, "e.7z");
            _engine.Create(new SevenZipEngine.CreateRequest
            {
                ArchivePath = archive, Format = ArchiveFormat.SevenZ, InputPaths = { dir },
                ExcludePatterns = { "*.log" },
            });
            var listing = _engine.List(archive);
            Assert.Contains(listing.Entries, e => e.Name.EndsWith("keep.txt"));
            Assert.DoesNotContain(listing.Entries, e => e.Name.EndsWith("skip.log"));
        }

        static byte[] RandomBytes(Random r, int n)
        {
            var b = new byte[n];
            r.NextBytes(b);
            return b;
        }

        public void Dispose()
        {
            _engine.Dispose();
            TestEnv.Cleanup(_ws);
        }
    }
}
