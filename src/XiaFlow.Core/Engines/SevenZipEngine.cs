using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using XiaFlow.Core.Logging;
using XiaFlow.Core.Security;

namespace XiaFlow.Core.Engines
{
    /// <summary>
    /// 7-Zip (7z.exe) 进程封装引擎。
    /// - 真实进度来自 -bsp1 引擎输出；
    /// - 暂停/恢复通过 NtSuspendProcess / NtResumeProcess 实现；
    /// - 取消通过终止引擎进程实现；
    /// - 输出统一 UTF-8（-sccUTF-8），正确处理中文文件名。
    /// </summary>
    public sealed class SevenZipEngine : IDisposable, Tasks.IPausable
    {
        Process? _proc;
        volatile bool _cancelRequested;
        volatile bool _paused;
        readonly object _lock = new();

        public string EnginePath { get; }
        public event EventHandler<EngineProgress>? Progress;

        public SevenZipEngine(string? enginePath = null)
        {
            EnginePath = enginePath ?? LocateEngine()
                ?? throw new FileNotFoundException("未找到 7-Zip 引擎。请设置 XIAFLOW_ENGINE_DIR 指向引擎所在目录。");
        }

        /// <summary>引擎定位：环境变量 → 程序目录/engine/x64 → 程序集目录/engine/x64。</summary>
        public static string? LocateEngine()
        {
            var candidates = new List<string>();
            var env = Environment.GetEnvironmentVariable("XIAFLOW_ENGINE_DIR");
            if (!OperatingSystem.IsWindows())
                return !string.IsNullOrWhiteSpace(env) && Path.IsPathFullyQualified(env) &&
                    File.Exists(Path.Combine(env, "7zz")) ? Path.Combine(env, "7zz") : null;
            if (!string.IsNullOrEmpty(env)) candidates.Add(Path.Combine(env, "7z.exe"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "engine", "x64", "7z.exe"));
            try
            {
                var asmDir = Path.GetDirectoryName(typeof(SevenZipEngine).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                    candidates.Add(Path.Combine(asmDir, "engine", "x64", "7z.exe"));
            }
            catch { /* ignore */ }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        public static string? EngineDir()
        {
            var p = LocateEngine();
            return p == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(p));
        }

        // ---------- 暂停/恢复/取消 ----------

        public bool IsPaused => _paused;
        public bool IsRunning => _proc != null && !_proc.HasExited;

        public void Pause()
        {
            if (!OperatingSystem.IsWindows()) return;
            lock (_lock)
            {
                if (_paused || _proc == null || _proc.HasExited) return;
                try
                {
                    if (NtSuspendProcess(_proc.Handle) == 0) _paused = true;
                }
                catch (Exception ex) { Logger.Warn("暂停引擎进程失败: " + ex.Message); }
            }
        }

        public void Resume()
        {
            if (!OperatingSystem.IsWindows()) return;
            lock (_lock)
            {
                if (!_paused || _proc == null || _proc.HasExited) { _paused = false; return; }
                try
                {
                    if (NtResumeProcess(_proc.Handle) == 0) _paused = false;
                }
                catch (Exception ex) { Logger.Warn("恢复引擎进程失败: " + ex.Message); }
            }
        }

        public void Cancel()
        {
            _cancelRequested = true;
            try { _proc?.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        }

        // ---------- 列取（浏览/分析） ----------

        public ArchiveListing List(string archive, string? password = null)
        {
            archive = VolumeHelper.GetFirstVolume(archive);
            if (!File.Exists(archive))
                throw new ArchiveException(ArchiveErrorKind.NotArchive, "压缩包不存在: " + archive);

            var args = $"l -ba -slt -sccUTF-8 {Quote(archive)}";
            if (!string.IsNullOrEmpty(password)) args += $" -p{password}";
            else args += " -p";

            var (code, stdout, stderr) = RunCapture(args);
            var combined = stdout + "\n" + stderr;

            if (code == 0)
            {
                var listing = SlTParser.Parse(stdout);
                listing.ArchivePath = archive;
                listing.IsVolumeArchive = VolumeHelper.IsSplitArchive(archive);
                listing.PasswordUsed = password;
                return listing;
            }

            throw ClassifyError(code, combined, archive);
        }

        /// <summary>尝试无密码列出，用于检测加密（不抛异常）。</summary>
        public static bool DetectPasswordRequired(string archive, out bool headerEncrypted)
        {
            headerEncrypted = false;
            try
            {
                var engine = new SevenZipEngine();
                engine.List(archive, null);
                return false; // 无密码可列
            }
            catch (ArchiveException aex) when (aex.Kind == ArchiveErrorKind.WrongPassword)
            {
                headerEncrypted = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------- 解压 ----------

        public sealed class ExtractRequest
        {
            public string ArchivePath { get; set; } = "";
            public string OutputDir { get; set; } = "";
            public string? Password { get; set; }
            public List<string>? SelectedEntries { get; set; }   // null = 全部
            public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Overwrite;
            public bool RecreateRootFolder { get; set; }         // 需要时由调用方建目录
            public bool FailOnWarning { get; set; }
        }

        public ArchiveListing Extract(ExtractRequest req, CancellationToken ct = default)
        {
            Directory.CreateDirectory(req.OutputDir);
            var conflictSwitch = req.Conflict switch
            {
                ConflictPolicy.Overwrite => "-y",
                ConflictPolicy.Skip => "-aos",
                ConflictPolicy.AutoRename => "-aou",
                _ => "-y"
            };

            var args = $"x {conflictSwitch} -o{Quote(req.OutputDir)} -sccUTF-8 -bsp1 -bso0 {Quote(req.ArchivePath)}";
            if (!string.IsNullOrEmpty(req.Password)) args += $" -p{req.Password}";
            else args += " -p";

            string? listFile = null;
            if (req.SelectedEntries is { Count: > 0 })
            {
                listFile = WriteListFile(req.SelectedEntries);
                args += $" -scsUTF-8 @{Quote(listFile)}";
            }

            try
            {
                var (code, stdout, stderr) = RunWithProgress(args, ct);
                var combined = stdout + "\n" + stderr;
                if (code == 0 || (code == 1 && !req.FailOnWarning))
                {
                    if (code == 1)
                        Logger.Warn($"解压完成但存在警告: {req.ArchivePath} :: {Trim(combined)}");
                    return List(req.ArchivePath, req.Password);
                }
                throw ClassifyError(code, combined, req.ArchivePath);
            }
            finally
            {
                TryDelete(listFile);
            }
        }

        // ---------- 创建 ----------

        public sealed class CreateRequest
        {
            public string ArchivePath { get; set; } = "";
            public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZ;
            public CompressionLevelOption Level { get; set; } = CompressionLevelOption.Normal;
            public int Threads { get; set; } = Environment.ProcessorCount;
            public long? DictionaryBytes { get; set; }             // 仅 7z
            public bool Solid { get; set; } = true;                // 仅 7z
            public long? VolumeSizeBytes { get; set; }             // 分卷
            public string? Password { get; set; }
            public bool EncryptFileNames { get; set; }             // 仅 7z
            public List<string> InputPaths { get; set; } = new();  // 文件或目录
            public List<string> ExcludePatterns { get; set; } = new(); // 如 *.tmp、node_modules
            public bool KeepPaths { get; set; } = true;            // 保留目录结构
            public bool FailOnWarning { get; set; }
        }

        public void Create(CreateRequest req, CancellationToken ct = default)
        {
            if (req.InputPaths.Count == 0)
                throw new ArgumentException("没有输入文件");

            var sb = new StringBuilder("a");
            sb.Append(' ').Append(TypeSwitch(req.Format));
            sb.Append(" -mx").Append((int)req.Level);

            if (req.Format == ArchiveFormat.SevenZ)
            {
                sb.Append(" -mmt").Append(Math.Max(1, req.Threads));
                sb.Append(" -ms=").Append(req.Solid ? "on" : "off");
                if (req.DictionaryBytes is > 0)
                    sb.Append(" -md").Append(DictionarySize(req.DictionaryBytes.Value));
            }
            if (req.Format == ArchiveFormat.Zip || req.Format == ArchiveFormat.SevenZ)
            {
                if (!string.IsNullOrEmpty(req.Password))
                {
                    sb.Append(" -p").Append(req.Password);
                    if (req.Format == ArchiveFormat.Zip) sb.Append(" -mem=AES256");
                    if (req.Format == ArchiveFormat.SevenZ && req.EncryptFileNames) sb.Append(" -mhe=on");
                }
                if (req.VolumeSizeBytes is > 0) sb.Append(" -v").Append(VolumeSize(req.VolumeSizeBytes.Value));
            }

            foreach (var p in req.ExcludePatterns)
                sb.Append(" -xr!").Append(p);

            // 输出目录上下文：以输入路径公共祖先为工作目录（目录本身保留名称，如 project/...）
            string workDir = Directory.GetCurrentDirectory();
            var inputs = req.InputPaths.Select(Path.GetFullPath).ToList();
            if (req.KeepPaths && inputs.Count > 0)
            {
                var roots = inputs.Select(p => Path.GetDirectoryName(p) ?? p)
                                  .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var common = CommonRoot(roots);
                if (common != null && common.Length > 3)
                {
                    workDir = common;
                    inputs = inputs.Select(i => Path.GetRelativePath(common, i)).ToList();
                }
            }

            string? listFile = WriteListFile(inputs);
            sb.Append(" -scsUTF-8 -sccUTF-8 -bsp1 -bso0 ")
              .Append(Quote(req.ArchivePath))
              .Append(" @").Append(Quote(listFile));
            var args = sb.ToString();

            try
            {
                var (code, stdout, stderr) = RunWithProgress(args, ct, workDir);
                var combined = stdout + "\n" + stderr;
                if (code == 0 || (code == 1 && !req.FailOnWarning)) return;
                throw ClassifyError(code, combined, req.ArchivePath);
            }
            finally
            {
                TryDelete(listFile);
            }
        }

        static string DictionarySize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (1L << 30)).ToString() + "g";
            if (bytes >= 1L << 20) return (bytes / (1L << 20)).ToString() + "m";
            return (bytes / (1L << 10)).ToString() + "k";
        }

        static string VolumeSize(long bytes)
        {
            if (bytes % (1L << 30) == 0 && bytes >= 1L << 30) return (bytes / (1L << 30)) + "g";
            if (bytes % (1L << 20) == 0 && bytes >= 1L << 20) return (bytes / (1L << 20)) + "m";
            if (bytes % 1024 == 0) return (bytes / 1024) + "k";
            return bytes + "b";
        }

        static string TypeSwitch(ArchiveFormat f) => f switch
        {
            ArchiveFormat.Zip => "-tzip",
            ArchiveFormat.SevenZ => "-t7z",
            ArchiveFormat.Tar => "-ttar",
            ArchiveFormat.GZip => "-tgzip",
            ArchiveFormat.BZip2 => "-tbzip2",
            ArchiveFormat.XZ => "-txz",
            ArchiveFormat.Wim => "-twim",
            _ => throw new NotSupportedException($"格式 {f} 不支持创建（诚实声明，不做伪装）")
        };

        // ---------- 测试完整性 ----------

        public ArchiveListing Test(string archive, string? password = null, CancellationToken ct = default, bool failOnWarning = false)
        {
            var args = $"t -sccUTF-8 -bsp1 -bso0 {Quote(archive)}";
            if (!string.IsNullOrEmpty(password)) args += $" -p{password}";
            else args += " -p";
            var (code, stdout, stderr) = RunWithProgress(args, ct);
            var combined = stdout + "\n" + stderr;
            if (code == 0 || (code == 1 && !failOnWarning)) return List(archive, password);
            throw ClassifyError(code, combined, archive);
        }

        // ---------- 压缩包内部修改（7z/zip 支持） ----------

        public void AddToArchive(string archive, IList<(string sourcePath, string archivePath)> items,
            string? password = null, CancellationToken ct = default)
        {
            var staging = Path.Combine(AppPaths.TempDir, "staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                var rels = new List<string>();
                foreach (var (src, arcPath) in items)
                {
                    var rel = SanitizeRel(arcPath);
                    var dst = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (Directory.Exists(src))
                        Directory.CreateDirectory(dst);
                    else
                        File.Copy(src, dst, overwrite: true);
                    rels.Add(rel);
                }
                var listFile = WriteListFile(rels);
                var args = $"a {Quote(archive)} -sccUTF-8 -bsp1 -bso0 -scsUTF-8 @{Quote(listFile)}";
                if (!string.IsNullOrEmpty(password)) args += $" -p{password}";
                try
                {
                    var (code, stdout, stderr) = RunWithProgress(args, ct, staging);
                    var combined = stdout + "\n" + stderr;
                    if (code != 0 && code != 1) throw ClassifyError(code, combined, archive);
                }
                finally { TryDelete(listFile); }
            }
            finally
            {
                TryDeleteDir(staging);
            }
        }

        public void DeleteEntries(string archive, IEnumerable<string> entryPaths,
            string? password = null, CancellationToken ct = default)
        {
            var sb = new StringBuilder("d ").Append(Quote(archive));
            if (!string.IsNullOrEmpty(password)) sb.Append(" -p").Append(password);
            foreach (var e in entryPaths)
                sb.Append(" ").Append(Quote(e));
            var (code, stdout, stderr) = RunWithProgress(sb.ToString(), ct);
            var combined = stdout + "\n" + stderr;
            if (code != 0 && code != 1) throw ClassifyError(code, combined, archive);
        }

        public void RenameEntries(string archive, IList<(string from, string to)> renames,
            string? password = null, CancellationToken ct = default)
        {
            var sb = new StringBuilder("rn ").Append(Quote(archive));
            if (!string.IsNullOrEmpty(password)) sb.Append(" -p").Append(password);
            foreach (var (from, to) in renames)
                sb.Append(" ").Append(Quote(from)).Append(" ").Append(Quote(to));
            var (code, stdout, stderr) = RunWithProgress(sb.ToString(), ct);
            var combined = stdout + "\n" + stderr;
            if (code != 0 && code != 1) throw ClassifyError(code, combined, archive);
        }

        static string SanitizeRel(string rel)
        {
            var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            parts = parts.Select(p =>
            {
                var baseName = System.IO.Path.GetFileNameWithoutExtension(p).ToUpperInvariant();
                return PathGuard.ReservedNames.Contains(baseName) ? "_" + p : p;
            }).ToArray();
            return string.Join('/', parts);
        }

        // ---------- 进程执行核心 ----------

        (int code, string stdout, string stderr) RunCapture(string args, string? workDir = null)
        {
            var wd = workDir ?? AppPaths.RootDir;
            try { Directory.CreateDirectory(wd); } catch { /* ignore */ }
            var psi = new ProcessStartInfo
            {
                FileName = EnginePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = wd,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi) ?? throw new ArchiveException(ArchiveErrorKind.IoError, "无法启动 7z.exe");
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            return (proc.ExitCode, outTask.Result, errTask.Result);
        }

        /// <summary>执行引擎命令（带进度解析、暂停/取消支持），供备份等模块复用。</summary>
        public (int code, string stdout, string stderr) RunProcess(string args, CancellationToken ct, string? workDir = null)
            => RunWithProgress(args, ct, workDir);

        (int code, string stdout, string stderr) RunWithProgress(string args, CancellationToken ct, string? workDir = null)
        {
            if (_cancelRequested) throw new ArchiveException(ArchiveErrorKind.Cancelled, "操作已取消");
            _cancelRequested = false;
            _paused = false;

            ct.ThrowIfCancellationRequested();
            var reg = ct.Register(() => Cancel());

            var wd2 = workDir ?? AppPaths.RootDir;
            try { Directory.CreateDirectory(wd2); } catch { /* ignore */ }
            var psi = new ProcessStartInfo
            {
                FileName = EnginePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = wd2,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();
            try
            {
                lock (_lock)
                {
                    _proc = new Process { StartInfo = psi };
                    _proc.Start();
                }
                _proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (stdoutBuf) stdoutBuf.AppendLine(e.Data);
                    ParseProgressLine(e.Data);
                };
                _proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (stderrBuf) stderrBuf.AppendLine(e.Data);
                };
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                _proc.WaitForExit();

                var code = _proc.ExitCode;
                if (_cancelRequested)
                    throw new ArchiveException(ArchiveErrorKind.Cancelled, "操作已取消");
                return (code, stdoutBuf.ToString(), stderrBuf.ToString());
            }
            finally
            {
                reg.Dispose();
                lock (_lock)
                {
                    _proc?.Dispose();
                    _proc = null;
                }
                _paused = false;
            }
        }

        void ParseProgressLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            // -bsp1 输出样例: " 42% 12 + docs\file.txt"
            int? pct = null;
            string? file = null;
            var t = line.TrimStart();
            if (t.Length > 1 && char.IsDigit(t[0]))
            {
                int i = 0;
                while (i < t.Length && char.IsDigit(t[i])) i++;
                if (i < t.Length && t[i] == '%' && int.TryParse(t[..i], out var p) && p <= 100)
                    pct = p;
            }
            var plus = t.IndexOf(" + ", StringComparison.Ordinal);
            if (plus >= 0) file = t[(plus + 3)..].Trim();
            Progress?.Invoke(this, new EngineProgress { Percent = pct, CurrentFile = file, Text = t });
        }

        // ---------- 错误分类 ----------

        public static ArchiveException ClassifyError(int exitCode, string output, string archive)
        {
            Logger.Warn($"引擎退出码 {exitCode}: {archive} :: {Trim(output)}");
            if (output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase))
                return new ArchiveException(ArchiveErrorKind.WrongPassword, "密码错误或缺少密码", new Exception(output));
            if (output.Contains("Cannot open file as archive", StringComparison.OrdinalIgnoreCase))
                return new ArchiveException(ArchiveErrorKind.NotArchive, "无法识别为压缩包（文件损坏或格式不支持）");
            if (output.Contains("Cannot find", StringComparison.OrdinalIgnoreCase) &&
                VolumeHelper.IsSplitArchive(archive))
                return new ArchiveException(ArchiveErrorKind.VolumeMissing, "分卷文件缺失，无法完成操作");
            if (output.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("磁盘空间不足", StringComparison.Ordinal))
                return new ArchiveException(ArchiveErrorKind.DiskFull, "磁盘空间不足");
            if (exitCode == 2 &&
                (output.Contains("Unexpected end of data") ||
                 output.Contains("Data Error", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("CRC Failed", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("Headers Error", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("Crc Error", StringComparison.OrdinalIgnoreCase)))
                return new ArchiveException(ArchiveErrorKind.CorruptArchive, "压缩包数据损坏（CRC/数据错误）");
            return new ArchiveException(ArchiveErrorKind.Unknown, $"引擎操作失败（退出码 {exitCode}）", new Exception(output));
        }

        // ---------- 工具 ----------

        public static string Quote(string s) =>
            string.IsNullOrEmpty(s) || (s.IndexOf(' ') < 0 && s.IndexOf('"') < 0) ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

        static string WriteListFile(IEnumerable<string> lines)
        {
            Directory.CreateDirectory(AppPaths.TempDir);
            var f = Path.Combine(AppPaths.TempDir, "list-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(f, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return f;
        }

        static void TryDelete(string? f)
        {
            try { if (f != null && File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }

        static void TryDeleteDir(string? d)
        {
            try { if (d != null && Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* ignore */ }
        }

        static string? CommonRoot(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return null;
            var dirs = paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p) ?? p)
                            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
                            .ToList();
            var first = dirs[0];
            foreach (var d in dirs.Skip(1))
            {
                while (!d.StartsWith(first, StringComparison.OrdinalIgnoreCase) && first.Length > 3)
                    first = Path.GetDirectoryName(first) ?? "";
                if (first.Length <= 3) return null;
            }
            return first.Length > 3 ? first : null;
        }

        static string Trim(string s)
        {
            s = s.Trim();
            return s.Length > 400 ? s[..400] + "…" : s;
        }

        public void Dispose() => Cancel();

        [DllImport("ntdll.dll")]
        static extern uint NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        static extern uint NtResumeProcess(IntPtr processHandle);
    }

    /// <summary>分卷辅助。</summary>
    public static class VolumeHelper
    {
        /// <summary>是否为分卷压缩包（.001 / .zip.001 / .part1.rar / .z01 等）。</summary>
        public static bool IsSplitArchive(string path)
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) &&
                name.Contains(".part", StringComparison.OrdinalIgnoreCase)) return true;
            var dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                var ext = name[(dot + 1)..];
                if (ext.Length is 2 or 3 && ext.All(char.IsDigit)) return true; // .001 .01
                if (ext.Equals("r00", StringComparison.OrdinalIgnoreCase)) return false; // 后续卷
            }
            return false;
        }

        /// <summary>取分卷首卷路径（.001 / .part1.rar / .zip.001），否则原样返回。</summary>
        public static string GetFirstVolume(string path)
        {
            if (!File.Exists(path)) return path;
            var name = Path.GetFileName(path);
            var dir = Path.GetDirectoryName(path) ?? "";

            // RAR 多部分: xxx.part1.rar ... xxx.partN.rar / xxx.rar + xxx.r00
            var m = System.Text.RegularExpressions.Regex.Match(name,
                @"^(?<base>.+)\.part(?<num>\d+)\.rar$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var part1 = Path.Combine(dir, m.Groups["base"].Value + ".part1.rar");
                if (File.Exists(part1) && !string.Equals(part1, path, StringComparison.OrdinalIgnoreCase))
                    return part1;
                var any = Directory.GetFiles(dir, m.Groups["base"].Value + ".part*.rar")
                                   .OrderBy(f => VolumeNum(f)).FirstOrDefault();
                if (any != null) return any;
                return path;
            }
            if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                return path; // 单卷 rar 或以 .rar 开头

            // .001 / .zip.001 / .7z.001
            var m2 = System.Text.RegularExpressions.Regex.Match(name,
                @"^(?<base>.+?\.[a-z0-9]+)?\.(?<num>\d{2,3})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m2.Success && m2.Groups["num"].Value == "001")
                return path;
            if (m2.Success)
            {
                var base1 = Path.Combine(dir, m2.Groups["base"].Value + ".001");
                if (File.Exists(base1)) return base1;
                var any001 = Directory.GetFiles(dir, Path.GetFileNameWithoutExtension(m2.Groups["base"].Value) + "*.001");
                if (any001.Length > 0) return any001.OrderBy(f => f).First();
            }
            return path;
        }

        static int VolumeNum(string file)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"part(\d+)\.rar");
            return m.Success ? int.Parse(m.Groups[1].Value) : int.MaxValue;
        }
    }

    /// <summary>-slt 输出解析器。</summary>
    internal static class SlTParser
    {
        public static ArchiveListing Parse(string output)
        {
            var listing = new ArchiveListing();
            ArchiveEntry? cur = null;
            var lines = output.Split('\n');
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) { continue; }

                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0) continue;
                var key = line[..eq];
                var val = line[(eq + 3)..];

                switch (key)
                {
                    case "Path":
                        if (cur != null) Commit(cur, listing);
                        cur = new ArchiveEntry { Name = val };
                        break;
                    case "Size": cur!.Size = ParseLong(val); break;
                    case "Packed Size": cur!.PackedSize = ParseLong(val); break;
                    case "Modified": cur!.Modified = ParseDate(val); break;
                    case "Attributes": cur!.Attributes = val; break;
                    case "Method": cur!.Method = val; break;
                    case "CRC": cur!.Crc = val; break;
                    case "Encrypted": cur!.Encrypted = val == "+"; break;
                    case "Folder": cur!.IsDirectory = val == "+"; break;
                    case "SymLink":
                    case "Symbolic Link":
                    case "Hard Link":
                        if (cur != null && val.Length > 0 && val != "-") cur.IsSymlink = true;
                        break;
                    case "Type": listing.FormatName = val; break;
                }
            }
            if (cur != null) Commit(cur, listing);
            return listing;
        }

        static void Commit(ArchiveEntry e, ArchiveListing l)
        {
            // 目录判定补充：posix 属性首段含 d
            foreach (var attribute in e.Attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (attribute.Length != 10) continue;
                if (attribute[0] == 'd') e.IsDirectory = true;
                if (attribute[0] is 'l' or 'b' or 'c' or 'p' or 's') e.IsSymlink = true;
            }
            if (!e.IsDirectory)
            {
                l.FileCount++;
                l.TotalUncompressed += Math.Max(0, e.Size);
                l.TotalPacked += Math.Max(0, e.PackedSize);
            }
            else l.DirCount++;
            if (e.Encrypted) l.HasEncryptedEntries = true;
            l.Entries.Add(e);
        }

        static long ParseLong(string v) => long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;

        static DateTime? ParseDate(string v)
        {
            if (DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var d)) return d;
            if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d2)) return d2;
            return null;
        }
    }
}
