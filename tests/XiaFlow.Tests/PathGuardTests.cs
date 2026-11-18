using System;
using System.Collections.Generic;
using System.IO;
using XiaFlow.Core.Security;
using Xunit;

namespace XiaFlow.Tests
{
    public class PathGuardTests
    {
        [Theory]
        [InlineData("normal/file.txt", "normal/file.txt")]
        [InlineData("a/../b.txt", "b.txt")]
        [InlineData("a/b/../../c.txt", "c.txt")]
        [InlineData("./x/y.txt", "x/y.txt")]
        [InlineData("dir\\file.txt", "dir/file.txt")]
        [InlineData("a/../b/c.txt", "b/c.txt")]
        public void Sanitize_Allowed(string input, string expected)
        {
            Assert.Equal(expected, PathGuard.SanitizeEntryName(input));
        }

        [Theory]
        [InlineData("../escape.txt")]
        [InlineData("../../escape2.txt")]
        [InlineData("a/../../../escape3.txt")]
        [InlineData("C:/Windows/system32/evil.exe")]
        [InlineData("C:\\evil.txt")]
        public void Sanitize_Rejects_Dangerous(string input)
        {
            Assert.Null(PathGuard.SanitizeEntryName(input));
        }

        [Theory]
        [InlineData("aux.txt", "_aux.txt")]
        [InlineData("con/device.dll", "_con/device.dll")]
        public void Sanitize_Neutralizes_Reserved_Names(string input, string expected)
        {
            Assert.Equal(expected, PathGuard.SanitizeEntryName(input));
        }

        [Fact]
        public void Sanitize_Replaces_Illegal_Chars()
        {
            var s = PathGuard.SanitizeEntryName("file?.txt");
            Assert.NotNull(s);
            Assert.DoesNotContain('?', s);
        }

        [Fact]
        public void Evaluate_Bomb_Detection()
        {
            var ws = TestEnv.NewWorkspace();
            try
            {
                var entries = new List<(string, long, bool, bool)> { ("huge.bin", 100L * 1024 * 1024 * 1024, false, false) };
                var limits = new ExtractSafetyLimits { MaxTotalUncompressedBytes = 1024 * 1024 * 1024 };
                var report = PathGuard.EvaluateExtraction(entries, ws, limits);
                Assert.False(report.IsSafe);
                Assert.Contains(report.Violations, v => v.Contains("超过安全限制"));
            }
            finally { TestEnv.Cleanup(ws); }
        }

        [Fact]
        public void Evaluate_Symlink_Skip()
        {
            var ws = TestEnv.NewWorkspace();
            try
            {
                var entries = new List<(string, long, bool, bool)>
                {
                    ("normal.txt", 10, false, false),
                    ("link", 0, false, true),
                };
                var report = PathGuard.EvaluateExtraction(entries, ws, new ExtractSafetyLimits());
                Assert.True(report.IsSafe);
                Assert.Equal(1, report.SymlinkCount);
                Assert.Contains(report.SkippedEntries, s => s == "link");
            }
            finally { TestEnv.Cleanup(ws); }
        }

        [Fact]
        public void Evaluate_FileCount_Limit()
        {
            var ws = TestEnv.NewWorkspace();
            try
            {
                var entries = new List<(string, long, bool, bool)>();
                for (int i = 0; i < 10; i++) entries.Add(($"f{i}.txt", 1, false, false));
                var limits = new ExtractSafetyLimits { MaxFileCount = 5 };
                var report = PathGuard.EvaluateExtraction(entries, ws, limits);
                Assert.False(report.IsSafe);
                Assert.Contains(report.Violations, v => v.Contains("文件数量"));
            }
            finally { TestEnv.Cleanup(ws); }
        }

        [Fact]
        public void IsInsideDirectory_Blocks_Escape()
        {
            var root = @"C:\temp\target";
            Assert.True(PathGuard.IsInsideDirectory(@"C:\temp\target\a.txt", root));
            Assert.True(PathGuard.IsInsideDirectory(@"C:\temp\target\sub\b.txt", root));
            Assert.False(PathGuard.IsInsideDirectory(@"C:\temp\targetEvil\a.txt", root));
            Assert.False(PathGuard.IsInsideDirectory(@"C:\Windows\system.ini", root));
        }
    }
}
