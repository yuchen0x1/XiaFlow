using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using XiaFlow.Core;
using XiaFlow.Core.Backup;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Tasks;
using Xunit;

namespace XiaFlow.Tests
{
    /// <summary>备份中心：全量/增量/差异、备份链、保留策略、恢复。</summary>
    public class BackupTests : IDisposable
    {
        readonly string _ws;
        readonly BackupStore _store;
        readonly BackupRunner _runner;
        readonly PasswordVault _vault;

        public BackupTests()
        {
            _ws = TestEnv.NewWorkspace();
            _store = new BackupStore();
            _vault = new PasswordVault();
            _runner = new BackupRunner(_store, _vault);
        }

        BackupTaskModel NewTask(BackupKind strategy = BackupKind.Full)
        {
            var src = Path.Combine(_ws, "source");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "AAA");
            File.WriteAllText(Path.Combine(src, "b.txt"), "BBB");

            return new BackupTaskModel
            {
                Name = "测试备份",
                SourcePaths = { src },
                DestDir = Path.Combine(_ws, "backups"),
                Strategy = strategy,
                KeepDays = 0, KeepCount = 0,
                Schedule = ScheduleKind.Manual,
                VerifyAfterBackup = true,
            };
        }

        [Fact]
        public void Full_Backup_Creates_Archive_And_Manifest()
        {
            var task = NewTask();
            _store.SaveTask(task);
            var result = _runner.Run(task, new TaskRunContext());

            Assert.True(result.Success);
            Assert.True(result.Verified);
            Assert.NotNull(result.ArchiveFile);
            Assert.True(File.Exists(result.ArchiveFile));
            Assert.Equal(BackupKind.Full, result.Kind);
            Assert.Equal(2, result.FileCount);

            var manifest = BackupManifest.Load(task.Id);
            Assert.Single(manifest.Chain);
            Assert.Equal(2, manifest.Files.Count);

            var hist = _store.HistoryFor(task.Id);
            Assert.Single(hist);
            Assert.Equal("成功", hist[0].Status);
            Assert.True(hist[0].ArchiveBytes > 0);
        }

        [Fact]
        public void Incremental_Contains_Only_Changes()
        {
            var task = NewTask(BackupKind.Incremental);
            _store.SaveTask(task);
            var r1 = _runner.Run(task);
            Assert.True(r1.Success, r1.Error);

            // 修改一个文件 + 新增一个文件
            var src = task.SourcePaths[0];
            File.WriteAllText(Path.Combine(src, "a.txt"), "AAA-CHANGED");
            File.WriteAllText(Path.Combine(src, "c.txt"), "NEW");
            Thread.Sleep(50); // 确保 mtime 变化

            var result = _runner.Run(task);
            Assert.True(result.Success, result.Error);
            Assert.Equal(BackupKind.Incremental, result.Kind);
            Assert.Equal(2, result.ChangedFiles); // a.txt + c.txt
            Assert.Equal(2, result.FileCount);

            // 增量归档内部应只含变化文件
            using var engine = new SevenZipEngine();
            var listing = engine.List(result.ArchiveFile!);
            var names = listing.Entries.Select(e => e.Name.Replace('\\', '/')).ToList();
            Assert.Contains(names, n => n.EndsWith("a.txt"));
            Assert.Contains(names, n => n.EndsWith("c.txt"));
            Assert.DoesNotContain(names, n => n.EndsWith("b.txt"));

            var manifest = BackupManifest.Load(task.Id);
            Assert.Equal(2, manifest.Chain.Count);      // Full + Incr
            Assert.Equal(3, manifest.Files.Count);      // a b c
            Assert.Equal(BackupKind.Incremental, manifest.Chain[1].Type);
            Assert.Equal(0, manifest.Chain[1].BaseIndex);
        }

        [Fact]
        public void Incremental_NoChanges_Produces_No_Archive()
        {
            var task = NewTask(BackupKind.Incremental);
            _store.SaveTask(task);
            _runner.Run(task);
            var result = _runner.Run(task);
            Assert.True(result.Success);
            Assert.True(result.NoChanges);
            var manifest = BackupManifest.Load(task.Id);
            Assert.Equal(1, manifest.Chain.Count);
        }

        [Fact]
        public void Differential_Uses_Last_Full_As_Base()
        {
            var task = NewTask(BackupKind.Differential);
            _store.SaveTask(task);
            _runner.Run(task);

            var src = task.SourcePaths[0];
            File.WriteAllText(Path.Combine(src, "a.txt"), "DIFF-1");
            _runner.Run(task); // diff1: a.txt

            File.WriteAllText(Path.Combine(src, "a.txt"), "DIFF-2");
            _runner.Run(task); // diff2: a.txt (相对全量的差异)

            var manifest = BackupManifest.Load(task.Id);
            Assert.Equal(3, manifest.Chain.Count);
            Assert.Equal(BackupKind.Full, manifest.Chain[0].Type);
            Assert.Equal(BackupKind.Differential, manifest.Chain[1].Type);
            Assert.Equal(0, manifest.Chain[1].BaseIndex); // 依赖全量
            Assert.Equal(BackupKind.Differential, manifest.Chain[2].Type);
            Assert.Equal(0, manifest.Chain[2].BaseIndex);
        }

        [Fact]
        public void Full_Force_After_N_Incrementals()
        {
            var task = NewTask(BackupKind.Incremental);
            task.FullBackupEveryN = 2;
            _store.SaveTask(task);
            var r1 = _runner.Run(task); // Full
            Assert.True(r1.Success, r1.Error);

            var src = task.SourcePaths[0];
            File.WriteAllText(Path.Combine(src, "a.txt"), "V2");
            var r2 = _runner.Run(task); // Incr (count=1)
            Assert.True(r2.Success, r2.Error);
            File.WriteAllText(Path.Combine(src, "a.txt"), "V3");
            var r3 = _runner.Run(task); // Incr #2
            Assert.True(r3.Success, r3.Error);
            File.WriteAllText(Path.Combine(src, "a.txt"), "V4");
            var r4 = _runner.Run(task); // 增量数达到 N=2 → 强制 Full
            Assert.True(r4.Success, r4.Error);

            var manifest = BackupManifest.Load(task.Id);
            Assert.Equal(4, manifest.Chain.Count);
            Assert.Equal(BackupKind.Full, manifest.Chain[3].Type);
        }

        [Fact]
        public void Restore_To_New_Location()
        {
            var task = NewTask(BackupKind.Incremental);
            _store.SaveTask(task);
            var full = _runner.Run(task);
            Assert.True(full.Success, full.Error);
            File.WriteAllText(Path.Combine(task.SourcePaths[0], "a.txt"), "V2-CHANGED");
            var incr = _runner.Run(task);
            Assert.True(incr.Success, incr.Error);

            var restore = new RestoreService(_store, _vault);
            var points = restore.GetRestorePoints(task);
            Assert.Equal(2, points.Count);
            var latest = points[0];

            var entries = restore.ListPointEntries(task, latest);
            Assert.Contains(entries, e => e.Name.EndsWith("a.txt"));

            var opt = new RestoreOptions
            {
                Point = latest,
                ToOriginalLocation = false,
                NewLocation = Path.Combine(_ws, "restored"),
            };
            var result = restore.Restore(task, opt, new TaskRunContext());
            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(Path.Combine(_ws, "restored", "source", "a.txt")));
            Assert.Equal("V2-CHANGED", File.ReadAllText(Path.Combine(_ws, "restored", "source", "a.txt")));
            Assert.True(File.Exists(Path.Combine(_ws, "restored", "source", "b.txt")));
        }

        [Fact]
        public void Restore_To_Original_Location()
        {
            var task = NewTask(BackupKind.Incremental);
            _store.SaveTask(task);
            var full = _runner.Run(task);
            Assert.True(full.Success, full.Error);
            var src = task.SourcePaths[0];
            // 备份完成后修改源文件（模拟误操作），随后恢复全量备份点
            File.WriteAllText(Path.Combine(src, "a.txt"), "REPLACED");

            var restore = new RestoreService(_store, _vault);
            var fullPoint = restore.GetRestorePoints(task).Single(p => p.Type == BackupKind.Full);
            var opt = new RestoreOptions
            {
                Point = fullPoint,
                ToOriginalLocation = true,
                Conflict = ConflictPolicy.Overwrite,
            };
            var result = restore.Restore(task, opt, new TaskRunContext());
            Assert.True(result.Success, result.Error);
            Assert.Equal("AAA", File.ReadAllText(Path.Combine(src, "a.txt"))); // 恢复为备份内容
            Assert.Equal(2, result.Overwritten); // a.txt 与 b.txt 均被覆盖还原
        }

        [Fact]
        public void Restore_With_Selective_Files()
        {
            var task = NewTask();
            _store.SaveTask(task);
            _runner.Run(task);
            var restore = new RestoreService(_store, _vault);
            var point = restore.GetRestorePoints(task)[0];
            var entries = restore.ListPointEntries(task, point);
            var onlyB = entries.Where(e => e.Name.EndsWith("b.txt")).Select(e => e.Name).ToList();

            var opt = new RestoreOptions
            {
                Point = point,
                ToOriginalLocation = false,
                NewLocation = Path.Combine(_ws, "sel"),
                SelectedEntries = onlyB,
            };
            var result = restore.Restore(task, opt, new TaskRunContext());
            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(Path.Combine(_ws, "sel", "source", "b.txt")));
            Assert.False(File.Exists(Path.Combine(_ws, "sel", "source", "a.txt")));
        }

        [Fact]
        public void Retention_KeepCount_Respects_Chain_Dependency()
        {
            var task = NewTask(BackupKind.Incremental);
            task.KeepCount = 2; // 只留 2 个
            _store.SaveTask(task);
            var r0 = _runner.Run(task); // Full #0
            Assert.True(r0.Success, r0.Error);
            var src = task.SourcePaths[0];
            File.WriteAllText(Path.Combine(src, "a.txt"), "V2");
            _runner.Run(task); // Incr #1
            File.WriteAllText(Path.Combine(src, "a.txt"), "V3");
            _runner.Run(task); // Incr #2

            var manifest = BackupManifest.Load(task.Id);
            // 超出数量的 Full(#0) 因被增量依赖 → 跳过；链上其余 2 条保留
            Assert.Equal(3, manifest.Chain.Count);
            Assert.True(File.Exists(Path.Combine(task.DestDir, manifest.Chain[0].File)));
        }

        [Fact]
        public void Retention_Diff_Can_Be_Deleted_Independently()
        {
            var task = NewTask(BackupKind.Differential);
            task.KeepCount = 1;
            _store.SaveTask(task);
            var rf = _runner.Run(task); // Full
            Assert.True(rf.Success, rf.Error);
            var src = task.SourcePaths[0];
            File.WriteAllText(Path.Combine(src, "a.txt"), "D1");
            _runner.Run(task); // Diff #1
            File.WriteAllText(Path.Combine(src, "a.txt"), "D2");
            _runner.Run(task); // Diff #2

            var manifest = BackupManifest.Load(task.Id);
            // Diff #1 被清理（无依赖），Full 保留（被 Diff#2 依赖）
            Assert.Equal(2, manifest.Chain.Count);
            Assert.Equal(BackupKind.Full, manifest.Chain[0].Type);
            Assert.Equal(BackupKind.Differential, manifest.Chain[1].Type);
        }

        [Fact]
        public void Encrypted_Backup_With_Vault()
        {
            var task = NewTask();
            task.Encrypted = true;
            var entry = _vault.Add("备份密码", "backup-pwd-2026");
            task.PasswordVaultId = entry.Id;
            _store.SaveTask(task);

            var result = _runner.Run(task);
            Assert.True(result.Success, result.Error);
            Assert.True(result.Verified);

            // 内容加密：列出可见但条目加密；Test 无密码报 WrongPassword
            using var engine = new SevenZipEngine();
            var listing = engine.List(result.ArchiveFile!);
            Assert.True(listing.HasEncryptedEntries, "备份条目应为加密状态");
            var ex = Assert.Throws<ArchiveException>(() => engine.Test(result.ArchiveFile!));
            Assert.Equal(ArchiveErrorKind.WrongPassword, ex.Kind);

            // 恢复（从密码库取密码）到新位置
            var restore = new RestoreService(_store, _vault);
            var point = restore.GetRestorePoints(task)[0];
            var r = restore.Restore(task, new RestoreOptions
            {
                Point = point,
                NewLocation = Path.Combine(_ws, "restored-enc"),
            }, new TaskRunContext());
            Assert.True(r.Success, r.Error);
            Assert.True(File.Exists(Path.Combine(_ws, "restored-enc", "source", "a.txt")));
        }

        [Fact]
        public void Schedule_NextRun_Calculation()
        {
            var task = new BackupTaskModel { Schedule = ScheduleKind.Daily, TimeOfDay = "02:00", Enabled = true };
            var after = new DateTime(2026, 11, 18, 3, 0, 0);
            var next = ScheduleCalc.NextRun(task, after);
            Assert.Equal(new DateTime(2026, 11, 19, 2, 0, 0), next);

            var task2 = new BackupTaskModel
            {
                Schedule = ScheduleKind.Weekly,
                TimeOfDay = "09:30",
                Days = DaysOfWeek.Mon | DaysOfWeek.Fri,
                Enabled = true,
            };
            var wed = new DateTime(2026, 11, 18, 10, 0, 0); // 周三
            var next2 = ScheduleCalc.NextRun(task2, wed);
            Assert.Equal(new DateTime(2026, 11, 20, 9, 30, 0), next2); // 周五
        }

        [Fact]
        public void Backup_Does_Not_Modify_Source()
        {
            var task = NewTask();
            _store.SaveTask(task);
            var src = task.SourcePaths[0];
            var before = Directory.GetFiles(src, "*", SearchOption.AllDirectories)
                .Select(f => (f, File.ReadAllText(f), File.GetLastWriteTimeUtc(f))).ToList();
            _runner.Run(task);
            var after = Directory.GetFiles(src, "*", SearchOption.AllDirectories)
                .Select(f => (f, File.ReadAllText(f), File.GetLastWriteTimeUtc(f))).ToList();
            Assert.Equal(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].f, after[i].f);
                Assert.Equal(before[i].Item2, after[i].Item2);
                Assert.Equal(before[i].Item3, after[i].Item3);
            }
        }

        public void Dispose() => TestEnv.Cleanup(_ws);
    }
}
