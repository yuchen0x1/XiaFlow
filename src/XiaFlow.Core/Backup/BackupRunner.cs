using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Logging;
using XiaFlow.Core.Tasks;

namespace XiaFlow.Core.Backup
{
    /// <summary>
    /// 备份执行器：完整 / 增量 / 差异备份，真实备份链与索引，避免重复备份未变化文件。
    /// 归档内条目路径 = 清单 Rel（相对公共祖先根、含源目录名），保证恢复映射精确一致。
    /// 备份绝不触碰源文件（只读）。
    /// </summary>
    public sealed class BackupRunner
    {
        readonly BackupStore _store;
        readonly PasswordVault _vault;

        public BackupRunner(BackupStore store, PasswordVault vault)
        {
            _store = store;
            _vault = vault;
        }

        // ---------- 布局 ----------

        internal sealed class SourceLayout
        {
            public string Path { get; set; } = "";
            public string RelRoot { get; set; } = "";      // 归档内前缀（也是真实相对路径）
            public string WorkDir { get; set; } = "";      // 7z 工作目录
            public string? StagingDir { get; set; }        // 暂存目录（需要改名/跨盘时）
        }

        internal sealed class CompressGroup
        {
            public string WorkDir { get; set; } = "";
            public List<string> Files { get; } = new();    // 相对 WorkDir 的路径
            public long Bytes { get; set; }
            public string? StagingDir { get; set; }
        }

        /// <summary>计算源 → 归档前缀布局。跨盘符或重名源使用暂存目录（诚实记录）。</summary>
        List<SourceLayout> BuildLayout(BackupTaskModel task, TaskRunContext? ctx)
        {
            var fulls = task.SourcePaths.Select(Path.GetFullPath).ToList();
            var parents = fulls.Select(p => Path.GetDirectoryName(p)!.TrimEnd(Path.DirectorySeparatorChar))
                               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            string? workRoot = null;
            var drives = parents.Select(p => Path.GetPathRoot(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (drives.Count == 1)
                workRoot = CommonRoot(parents);

            var layouts = new List<SourceLayout>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var src in fulls)
            {
                string? relRoot = null;
                if (workRoot != null)
                {
                    var r = Path.GetRelativePath(workRoot, src);
                    if (!Path.IsPathRooted(r) && !r.StartsWith("..")) relRoot = r.Replace('\\', '/');
                }

                if (relRoot != null && used.Add(relRoot))
                {
                    layouts.Add(new SourceLayout { Path = src, RelRoot = relRoot, WorkDir = workRoot });
                    continue;
                }

                // 跨盘 / 相对根不可用 / 重名 → 暂存目录
                var staging = Path.Combine(AppPaths.TempDir, "bkstage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                var baseTag = SafeName(Path.GetFileName(src.TrimEnd('\\', '/')));
                if (string.IsNullOrEmpty(baseTag) || baseTag == "_") baseTag = "src";
                var tag = baseTag;
                int n = 1;
                while (!used.Add(tag)) tag = $"{baseTag}_{n++}";
                var dst = Path.Combine(staging, tag);
                ctx?.Log($"源 {src} 使用暂存映射为 {tag}/（跨盘符或重名）");
                if (Directory.Exists(src)) CopyDir(src, dst);
                else { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(src, dst); }
                layouts.Add(new SourceLayout { Path = src, RelRoot = tag, WorkDir = staging, StagingDir = staging });
            }
            return layouts;
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, f);
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
            }
        }

        static string? CommonRoot(List<string> paths)
        {
            if (paths.Count == 0) return null;
            var first = paths[0];
            foreach (var p in paths.Skip(1))
            {
                while (!p.StartsWith(first, StringComparison.OrdinalIgnoreCase) && first.Length > 3)
                    first = Path.GetDirectoryName(first) ?? "";
                if (first.Length <= 3) return null;
            }
            return first.Length > 3 ? first : null;
        }

        List<CompressGroup> BuildGroups(List<SourceLayout> layouts, List<ManifestFile> files)
        {
            var groups = new List<CompressGroup>();
            foreach (var g in layouts.GroupBy(l => l.WorkDir, StringComparer.OrdinalIgnoreCase))
            {
                var group = new CompressGroup { WorkDir = g.Key, StagingDir = g.First().StagingDir };
                var relRoots = g.Select(l => l.RelRoot).ToList();
                foreach (var f in files.Where(f => relRoots.Any(r =>
                    f.Rel.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                    f.Rel.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))))
                {
                    group.Files.Add(f.Rel.Replace('/', Path.DirectorySeparatorChar));
                    group.Bytes += f.Size;
                }
                if (group.Files.Count > 0) groups.Add(group);
            }
            return groups;
        }

        // ---------- 主流程 ----------

        public BackupRunResult Run(BackupTaskModel task, TaskRunContext? ctx = null, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new BackupRunResult();
            var rec = new BackupHistoryRecord
            {
                TaskId = task.Id,
                TaskName = task.Name,
                Time = DateTime.Now,
            };

            ctx?.ReportProgress(null, 0, null);
            try
            {
                // 0. 校验
                var missing = task.SourcePaths.Where(p => !Directory.Exists(p) && !File.Exists(p)).ToList();
                if (missing.Count > 0)
                    throw new ArchiveException(ArchiveErrorKind.IoError, "源路径不存在: " + string.Join(", ", missing));

                Directory.CreateDirectory(task.DestDir);
                var password = ResolvePassword(task);

                // 1. 布局 + 扫描
                var layouts = BuildLayout(task, ctx);
                var manifest = BackupManifest.Load(task.Id);
                manifest.Sources = layouts.Select(l => new ManifestSource { Path = l.Path, Tag = l.RelRoot }).ToList();
                ctx?.Log("扫描源文件…");
                var current = ScanSources(layouts, task.HashVerify, ctx, ct);
                ctx?.ReportProgress(null, 0, null);

                // 2. 决定类型
                var kind = DecideKind(task, manifest);
                result.Kind = kind;

                // 3. 计算变化集
                List<ManifestFile> toArchive;
                int? lastFullIndex = LastIndexOfKind(manifest.Chain, BackupKind.Full);
                if (kind == BackupKind.Full)
                {
                    toArchive = current;
                    result.ChangedFiles = current.Count;
                }
                else if (kind == BackupKind.Incremental)
                {
                    var prev = manifest.Files.ToDictionary(f => f.Rel, StringComparer.OrdinalIgnoreCase);
                    toArchive = current.Where(c =>
                        !prev.TryGetValue(c.Rel, out var old) ||
                        old.Size != c.Size || old.MTime != c.MTime ||
                        (c.Hash != null && old.Hash != null && old.Hash != c.Hash)).ToList();
                    result.ChangedFiles = toArchive.Count;
                }
                else // Differential
                {
                    var fullSet = (manifest.FullFiles ?? manifest.Files).ToDictionary(f => f.Rel, StringComparer.OrdinalIgnoreCase);
                    toArchive = current.Where(c =>
                        !fullSet.TryGetValue(c.Rel, out var old) ||
                        old.Size != c.Size || old.MTime != c.MTime ||
                        (c.Hash != null && old.Hash != null && old.Hash != c.Hash)).ToList();
                    result.ChangedFiles = toArchive.Count;
                }

                result.SourceBytes = toArchive.Sum(f => f.Size);
                result.FileCount = toArchive.Count;

                // 4. 无变化 → 直接记录，不生成空备份
                if (toArchive.Count == 0 && kind != BackupKind.Full)
                {
                    result.Success = true;
                    result.NoChanges = true;
                    rec.Status = "无变化";
                    rec.SourceBytes = current.Sum(f => f.Size);
                    _store.Record(rec);
                    ctx?.Log("没有检测到文件变化，跳过本次备份");
                    ctx?.ReportProgress(100, 0, null);
                    FinishTaskState(task, rec);
                    return result;
                }

                // 5. 创建归档
                var typeTag = kind switch { BackupKind.Full => "Full", BackupKind.Incremental => "Incr", _ => "Diff" };
                var safeName = SafeName(task.Name);
                var fileName = $"{safeName}_{typeTag}_{DateTime.Now:yyyyMMdd_HHmmss}.{(task.Format == ArchiveFormat.Zip ? "zip" : "7z")}";
                var archivePath = Path.Combine(task.DestDir, fileName);
                result.ArchiveFile = archivePath;
                ctx?.Log($"创建{typeTag}备份: {fileName}");

                using (var engine = new SevenZipEngine())
                {
                    var groups = BuildGroups(layouts, toArchive);
                    long groupTotal = Math.Max(1, groups.Sum(g => g.Bytes));
                    long doneBytes = 0;
                    foreach (var group in groups)
                    {
                        ct.ThrowIfCancellationRequested();
                        var listFile = WriteTempList(group.Files);
                        try
                        {
                            var args = BuildAddArgs(task, archivePath, password);
                            args += $" -scsUTF-8 -sccUTF-8 -bsp1 -bso0 \"{archivePath}\" @\"{listFile}\"";
                            var baseDone = doneBytes;
                            var groupBytes = Math.Max(1, group.Bytes);
                            engine.Progress += (_, p) =>
                            {
                                if (p.Percent is int pc && groupTotal > 0)
                                {
                                    var overall = (int)Math.Min(100, (baseDone + (double)pc / 100 * groupBytes) * 100 / groupTotal);
                                    ctx?.ReportProgress(overall, null, p.CurrentFile);
                                }
                            };
                            var (code, stdout, stderr) = engine.RunProcess(args, ct, group.WorkDir);
                            if (code != 0 && code != 1)
                                throw SevenZipEngine.ClassifyError(code, stdout + "\n" + stderr, archivePath);
                        }
                        finally
                        {
                            TryDelete(listFile);
                            if (group.StagingDir != null) TryDeleteDir(group.StagingDir);
                        }
                        doneBytes += group.Bytes;
                    }

                    // 6. 完整性校验
                    if (task.VerifyAfterBackup)
                    {
                        ctx?.Log("校验备份完整性…");
                        var (ok, err) = ArchiveService.Verify(engine, archivePath, password, ct);
                        result.Verified = ok;
                        rec.Verified = ok;
                        if (!ok) throw new ArchiveException(ArchiveErrorKind.CorruptArchive, "备份校验失败: " + err);
                    }
                }

                result.ArchiveBytes = new FileInfo(archivePath).Length;
                result.Success = true;

                // 7. 更新清单与链
                manifest.Files = current;
                if (kind == BackupKind.Full) manifest.FullFiles = current.Select(CloneFile).ToList();
                manifest.Chain.Add(new ChainEntry
                {
                    File = fileName,
                    Type = kind,
                    Time = DateTime.Now,
                    BaseIndex = kind == BackupKind.Incremental ? manifest.Chain.Count - 1
                              : kind == BackupKind.Differential ? (lastFullIndex ?? -1)
                              : -1,
                    PasswordVaultId = task.PasswordVaultId,
                });
                if (kind == BackupKind.Full) task.IncrementalCount = 0; else task.IncrementalCount++;
                manifest.Save(task.Id);

                // 8. 保留策略清理
                var cleanup = RetentionService.Apply(task, manifest, result.SourceBytes);
                result.CleanupLog.AddRange(cleanup);
                foreach (var c in cleanup) ctx?.Log(c);

                // 9. 记录历史
                rec.Status = "成功";
                rec.Type = kind;
                rec.ArchiveFile = archivePath;
                rec.SourceBytes = result.SourceBytes;
                rec.ArchiveBytes = result.ArchiveBytes;
                rec.FileCount = result.FileCount;
                _store.Record(rec);
                ctx?.ReportProgress(100, null, null);
                FinishTaskState(task, rec);
                return result;
            }
            catch (OperationCanceledException)
            {
                rec.Status = "取消";
                _store.Record(rec);
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"备份失败: {task.Name}", ex);
                rec.Status = "失败";
                rec.Error = ex.Message;
                rec.DurationSeconds = sw.Elapsed.TotalSeconds;
                _store.Record(rec);
                result.Success = false;
                result.Error = ex.Message;
                FinishTaskState(task, rec);
                return result;
            }
        }

        void FinishTaskState(BackupTaskModel task, BackupHistoryRecord rec)
        {
            task.LastRun = DateTime.Now;
            task.NextRun = ScheduleCalc.NextRun(task, DateTime.Now);
            task.LastError = rec.Status == "失败" ? rec.Error : null;
            _store.SaveTask(task);
        }

        internal int? LastIndexOfKind(List<ChainEntry> chain, BackupKind kind)
        {
            for (int i = chain.Count - 1; i >= 0; i--)
                if (chain[i].Type == kind) return i;
            return null;
        }

        BackupKind DecideKind(BackupTaskModel task, BackupManifest manifest)
        {
            if (task.Strategy == BackupKind.Full) return BackupKind.Full;
            if (manifest.Chain.Count == 0) return BackupKind.Full;
            if (task.IncrementalCount >= Math.Max(1, task.FullBackupEveryN)) return BackupKind.Full;
            return task.Strategy switch
            {
                BackupKind.Incremental => BackupKind.Incremental,
                BackupKind.Differential => BackupKind.Differential,
                _ => BackupKind.Full
            };
        }

        List<ManifestFile> ScanSources(List<SourceLayout> layouts, bool withHash,
            TaskRunContext? ctx, CancellationToken ct)
        {
            var files = new List<ManifestFile>();
            foreach (var layout in layouts)
            {
                if (File.Exists(layout.Path))
                {
                    var fi = new FileInfo(layout.Path);
                    files.Add(new ManifestFile
                    {
                        Rel = layout.RelRoot.Replace('\\', '/'),
                        SourcePath = layout.Path,
                        Size = fi.Length,
                        MTime = fi.LastWriteTime,
                    });
                    continue;
                }
                foreach (var f in Directory.EnumerateFiles(layout.Path, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(f);
                        var inner = Path.GetRelativePath(layout.Path, f).Replace('\\', '/');
                        var mf = new ManifestFile
                        {
                            Rel = layout.RelRoot + "/" + inner,
                            SourcePath = f,
                            Size = fi.Length,
                            MTime = fi.LastWriteTime,
                        };
                        if (withHash)
                        {
                            mf.Hash = ArchiveService.Sha256(f, _ => { }, ct);
                            ctx?.ReportProgress(null, null, "哈希: " + Path.GetFileName(f));
                        }
                        files.Add(mf);
                    }
                    catch (Exception ex)
                    {
                        ctx?.Log($"跳过无法读取的文件: {f} ({ex.Message})");
                    }
                }
            }
            return files;
        }

        string ResolvePassword(BackupTaskModel task)
        {
            if (!task.Encrypted || string.IsNullOrEmpty(task.PasswordVaultId)) return null!;
            var entry = _vault.GetAll().FirstOrDefault(p => p.Id == task.PasswordVaultId);
            if (entry == null)
                throw new ArchiveException(ArchiveErrorKind.IoError, "备份密码已从密码库中移除，无法加密备份");
            return entry.Password;
        }

        internal static string BuildAddArgs(BackupTaskModel task, string archivePath, string password)
        {
            var args = "a";
            args += task.Format == ArchiveFormat.Zip ? " -tzip" : " -t7z";
            args += $" -mx{(int)task.Level}";
            if (task.Format == ArchiveFormat.SevenZ) args += " -ms=on";
            if (!string.IsNullOrEmpty(password))
            {
                args += " -p" + password;
                if (task.Format == ArchiveFormat.Zip) args += " -mem=AES256";
            }
            if (task.VolumeSizeMB > 0) args += $" -v{task.VolumeSizeMB}m";
            return args;
        }

        internal static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Backup" : name.Trim();
        }

        static string WriteTempList(List<string> lines)
        {
            Directory.CreateDirectory(AppPaths.TempDir);
            var f = Path.Combine(AppPaths.TempDir, "bk-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(f, lines, new System.Text.UTF8Encoding(true));
            return f;
        }

        static void TryDelete(string? f) { try { if (f != null && File.Exists(f)) File.Delete(f); } catch { } }

        static void TryDeleteDir(string? d) { try { if (d != null && Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

        static ManifestFile CloneFile(ManifestFile f) => new()
        {
            Rel = f.Rel, SourcePath = f.SourcePath, Size = f.Size, MTime = f.MTime, Hash = f.Hash
        };
    }

    /// <summary>
    /// 保留策略清理：只删除匣流管理的备份文件；删除前执行依赖检查，绝不触碰源文件。
    /// </summary>
    public static class RetentionService
    {
        public static List<string> Apply(BackupTaskModel task, BackupManifest manifest, long spaceFreed)
        {
            var log = new List<string>();
            var chain = manifest.Chain;
            if (chain.Count == 0) return log;

            var cutoff = DateTime.Now.AddDays(-Math.Max(0, task.KeepDays));
            List<ChainEntry> candidates = new();

            if (task.KeepDays > 0)
                candidates.AddRange(chain.Where(c => c.Time < cutoff));
            if (task.KeepCount > 0 && chain.Count > task.KeepCount)
                candidates.AddRange(chain.Take(chain.Count - task.KeepCount));

            var toDelete = candidates.Distinct().OrderBy(c => chain.IndexOf(c)).ToList();

            foreach (var entry in toDelete)
            {
                var idx = chain.IndexOf(entry);
                var path = Path.Combine(task.DestDir, entry.File);
                if (!File.Exists(path))
                {
                    chain.Remove(entry);
                    log.Add($"清理: {entry.File}（文件已不存在，移除记录）");
                    continue;
                }

                string? dep = DependsOnMe(chain, idx);
                if (dep != null)
                {
                    log.Add($"跳过清理 {entry.File}：后续备份 {dep} 依赖它，删除将破坏备份链");
                    continue;
                }

                try
                {
                    var size = new FileInfo(path).Length;
                    File.Delete(path);
                    chain.Remove(entry);
                    log.Add($"清理: {entry.File}（{Security.PathGuard.FormatBytes(size)}）");
                }
                catch (Exception ex)
                {
                    log.Add($"清理失败 {entry.File}: {ex.Message}");
                }
            }

            manifest.Save(task.Id);
            return log;
        }

        static string? DependsOnMe(List<ChainEntry> chain, int idx)
        {
            for (int i = idx + 1; i < chain.Count; i++)
            {
                var e = chain[i];
                if (e.Type == BackupKind.Differential && e.BaseIndex == idx) return e.File;
                if (e.Type == BackupKind.Incremental && i > idx) return e.File;
            }
            return null;
        }
    }
}
