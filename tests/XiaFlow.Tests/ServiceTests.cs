using System;
using System.IO;
using System.Linq;
using XiaFlow.Core;
using XiaFlow.Core.Tasks;
using XiaFlow.Core.Update;
using Xunit;
using TaskStatus = XiaFlow.Core.Tasks.TaskStatus;

namespace XiaFlow.Tests
{
    public class PasswordVaultTests : IDisposable
    {
        readonly string _ws;
        public PasswordVaultTests()
        {
            _ws = TestEnv.NewWorkspace();
        }

        [Fact]
        public void DPAPI_Roundtrip()
        {
            var vault = new PasswordVault();
            var entry = vault.Add("网盘密码", "p@ss中文2026!");
            Assert.Single(vault.GetAll());
            Assert.Equal("网盘密码", vault.GetAll()[0].Name);
            Assert.Equal("p@ss中文2026!", vault.GetAll()[0].Password);

            // 新实例从 DPAPI 加密文件读取
            var vault2 = new PasswordVault();
            Assert.Single(vault2.GetAll());
            Assert.Equal("p@ss中文2026!", vault2.GetAll()[0].Password);
            Assert.Equal(entry.Id, vault2.GetAll()[0].Id);

            vault2.Remove(entry.Id);
            Assert.Empty(vault2.GetAll());
        }

        [Fact]
        public void Vault_File_Is_Encrypted_Not_Plaintext()
        {
            var vault = new PasswordVault();
            vault.Add("x", "SECRET-PLAINTEXT-MARKER");
            var file = AppPaths.PasswordVaultFile;
            Assert.True(File.Exists(file));
            var raw = File.ReadAllText(file);
            Assert.DoesNotContain("SECRET-PLAINTEXT-MARKER", raw);
        }

        [Fact]
        public void Strength_Evaluation()
        {
            Assert.Equal("无", PasswordStrength.Evaluate("").Label);
            Assert.Equal("弱", PasswordStrength.Evaluate("12345678").Label);
            Assert.Equal("极强", PasswordStrength.Evaluate("Xf#2026$SecurePwd!").Label);
        }

        [Fact]
        public void TryMatch_Finds_Correct_Password()
        {
            var ws = _ws;
            var dir = Path.Combine(ws, "src");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "data");
            var arc = Path.Combine(ws, "e.zip");
            new XiaFlow.Core.Engines.SevenZipEngine().Create(new XiaFlow.Core.Engines.SevenZipEngine.CreateRequest
            {
                ArchivePath = arc,
                Format = XiaFlow.Core.ArchiveFormat.Zip,
                InputPaths = { dir },
                Password = "the-right-pw",
            });
            var vault = new PasswordVault();
            vault.Add("wrong", "nope");
            vault.Add("right", "the-right-pw");
            var match = vault.TryMatch(arc);
            Assert.NotNull(match);
            Assert.Equal("the-right-pw", match.Password);
        }

        public void Dispose() => TestEnv.Cleanup(_ws);
    }

    public class UpdateServiceTests
    {
        [Theory]
        [InlineData("v1.1.0", "1.0.0", true)]
        [InlineData("1.0.1", "1.0.0", true)]
        [InlineData("1.0.0", "1.0.0", false)]
        [InlineData("0.9.0", "1.0.0", false)]
        [InlineData("v2.0.0-beta.1", "1.9.9", true)]
        public void Version_Compare(string newV, string curV, bool expected)
        {
            Assert.Equal(expected, UpdateService.IsNewer(newV, curV));
        }

        [Fact]
        public void VerifyFile_Detects_Mismatch()
        {
            var p = Path.Combine(Path.GetTempPath(), "xiaflow-tests", "verify-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4 });
            try
            {
                var realSha = XiaFlow.Core.ArchiveService.Sha256(p);
                Assert.True(UpdateService.VerifyFile(p, realSha));
                Assert.False(UpdateService.VerifyFile(p, new string('0', 64)));
            }
            finally { File.Delete(p); }
        }
    }

    public class TaskManagerTests
    {
        [Fact]
        public void Enqueue_Runs_And_Records_History()
        {
            var tm = new TaskManager(2);
            var done = new ManualResetEventSlim(false);
            ArchiveTask? finished = null;
            tm.TaskFinished += t => { finished = t; done.Set(); };

            var ran = false;
            tm.Enqueue(TaskKind.Compress, "测试任务", "", "src", "dst",
                ctx => { ran = true; ctx.ReportProgress(50, 500, null); ctx.ReportProgress(100, 1000, null); });

            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "任务应完成");
            Assert.True(ran);
            Assert.Equal(TaskStatus.Completed, finished!.Status);
            Assert.Equal(1000, finished.ProcessedBytes);
            Assert.True(tm.History().Count >= 1);
            tm.Dispose();
        }

        [Fact]
        public void Failed_Task_Records_Error()
        {
            var tm = new TaskManager(1);
            var done = new ManualResetEventSlim(false);
            ArchiveTask? finished = null;
            tm.TaskFinished += t => { finished = t; done.Set(); };

            tm.Enqueue(TaskKind.Compress, "失败任务", "", "", "", _ => throw new InvalidOperationException("boom"));
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(TaskStatus.Failed, finished!.Status);
            Assert.Contains("boom", finished.Error);
            tm.Dispose();
        }

        [Fact]
        public void Pause_Resume_State_Transitions()
        {
            var tm = new TaskManager(1);
            var pauseCalled = false;
            var resumeCalled = false;
            var gate = new ManualResetEvent(false);
            var finished = new ManualResetEventSlim(false);

            var task = tm.Enqueue(TaskKind.Extract, "暂停任务", "", "", "", ctx => { gate.WaitOne(15000); });
            task.Pausable = new FakePausable(() => pauseCalled = true, () => resumeCalled = true);
            tm.TaskFinished += _ => finished.Set();

            // 等到运行中
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (task.Status != TaskStatus.Running && sw.Elapsed < TimeSpan.FromSeconds(5))
                Thread.Sleep(20);
            Assert.Equal(TaskStatus.Running, task.Status);

            tm.Pause(task);
            Assert.Equal(TaskStatus.Paused, task.Status);
            Assert.True(pauseCalled);

            tm.Resume(task);
            Assert.Equal(TaskStatus.Running, task.Status);
            Assert.True(resumeCalled);

            gate.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(15)));
            Assert.Equal(TaskStatus.Completed, task.Status);
            tm.Dispose();
        }

        class FakePausable : IPausable
        {
            readonly Action _onPause;
            readonly Action _onResume;
            public FakePausable(Action onPause, Action onResume) { _onPause = onPause; _onResume = onResume; }
            public void Pause() => _onPause();
            public void Resume() => _onResume();
        }
    }
}
