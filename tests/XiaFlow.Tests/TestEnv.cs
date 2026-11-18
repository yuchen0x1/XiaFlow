using System;
using System.IO;
using XiaFlow.Core;
using Xunit;

namespace XiaFlow.Tests
{
    /// <summary>测试公共设施：临时工作区、引擎路径。</summary>
    public static class TestEnv
    {
        static int _counter;
        public static string NewWorkspace()
        {
            var dir = Path.Combine(Path.GetTempPath(), "xiaflow-tests", "ws-" + DateTime.Now.Ticks + "-" + Interlocked.Increment(ref _counter));
            Directory.CreateDirectory(dir);
            // 每个工作区配套独立数据目录（顺序执行下保证测试隔离）
            var data = Path.Combine(Path.GetTempPath(), "xiaflow-tests", "data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("XIAFLOW_DATA_DIR", data);
            AppPaths.ResetForTests();
            return dir;
        }

        public static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        static TestEnv()
        {
            // 默认测试数据目录（类未调用 NewWorkspace 时的兜底）
            var data = Path.Combine(Path.GetTempPath(), "xiaflow-tests", "data");
            Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("XIAFLOW_DATA_DIR", data);
        }
    }
}
