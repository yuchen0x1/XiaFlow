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
    /// <summary>恢复点。</summary>
    public sealed class RestorePoint
    {
        public int ChainIndex { get; set; }
        public string FileName { get; set; } = "";
        public BackupKind Type { get; set; }
        public DateTime Time { get; set; }
        public string FilePath { get; set; } = "";
        public long Size { get; set; }
        public string TypeText => Type switch { BackupKind.Full => "完整", BackupKind.Incremental => "增量", BackupKind.Differential => "差异", _ => "" };
    }

    /// <summary>恢复选项。</summary>
    public sealed class RestoreOptions
    {
        public RestorePoint Point { get; set; } = new();
        public bool ToOriginalLocation { get; set; }
        public string NewLocation { get; set; } = "";
        public List<string>? SelectedEntries { get; set; }   // 归档内相对路径（Tag/...）；null = 全部
        public ConflictPolicy Conflict { get; set; } = ConflictPolicy.AutoRename;
    }

    /// <summary>
    /// 文件恢复中心：浏览备份点、完整/选择性恢复、恢复到原位置/新位置、冲突处理。
    /// 恢复到原位置时先解压到暂存区，再逐文件安全映射回源位置（绝不直接向源目录解压）。
    /// </summary>
    public sealed class RestoreService
    {
        readonly BackupStore _store;
        readonly PasswordVault _vault;
        readonly SevenZipEngine _engine;

        public RestoreService(BackupStore store, PasswordVault vault, SevenZipEngine? engine = null)
        {
            _store = store;
            _vault = vault;
            _engine = engine ?? new SevenZipEngine();
        }

        public List<RestorePoint> GetRestorePoints(BackupTaskModel task)
        {
            var manifest = BackupManifest.Load(task.Id);
            var points = new List<RestorePoint>();
            foreach (var (entry, idx) in manifest.Chain.Select((e, i) => (e, i)))
            {
                var path = Path.Combine(task.DestDir, entry.File);
                if (!File.Exists(path)) continue;
                points.Add(new RestorePoint
                {
                    ChainIndex = idx,
                    FileName = entry.File,
                    Type = entry.Type,
                    Time = entry.Time,
                    FilePath = path,
                    Size = new FileInfo(path).Length,
                });
            }
            return points.OrderByDescending(p => p.Time).ToList();
        }

        /// <summary>某恢复点依赖的完整备份集合（校验备份链完整性）。</summary>
        public List<string> RequiredChainFiles(BackupTaskModel task, RestorePoint point)
        {
            var manifest = BackupManifest.Load(task.Id);
            var chain = manifest.Chain;
            var result = new List<string>();
            int i = Math.Clamp(point.ChainIndex, 0, Math.Max(0, chain.Count - 1));
            for (; i >= 0; i--)
            {
                result.Add(chain[i].File);
                if (chain[i].Type == BackupKind.Full) break;
            }
            return result;
        }

        /// <summary>恢复点的归档条目列表（合并依赖链，返回唯一路径集）。</summary>
        public List<ArchiveEntry> ListPointEntries(BackupTaskModel task, RestorePoint point, CancellationToken ct = default)
        {
            var manifest = BackupManifest.Load(task.Id);
            var password = ResolvePassword(task);
            var map = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var fileName in RequiredChainFiles(task, point))
            {
                var path = Path.Combine(task.DestDir, fileName);
                if (!File.Exists(path)) continue;
                var listing = _engine.List(path, password);
                foreach (var e in listing.Entries.Where(e => !e.IsDirectory && !e.IsSymlink))
                    map[e.Name] = e; // 后面的增量覆盖同名（代表最新状态）
            }
            return map.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>恢复到原位置时，预览将被覆盖的文件（供确认对话框）。</summary>
        public List<string> PreviewOverwrites(BackupTaskModel task, RestorePoint point, List<string>? selected)
        {
            var manifest = BackupManifest.Load(task.Id);
            var overwrite = new List<string>();
            var entries = ListPointEntries(task, point);
            foreach (var e in entries)
            {
                if (selected != null && !selected.Contains(e.Name)) continue;
                var src = manifest.Files.FirstOrDefault(f => f.Rel == e.Name)?.SourcePath;
                if (src != null && File.Exists(src)) overwrite.Add(src);
            }
            return overwrite;
        }

        /// <summary>执行恢复。</summary>
        public RestoreResult Restore(BackupTaskModel task, RestoreOptions opt, TaskRunContext? ctx = null, CancellationToken ct = default)
        {
            var result = new RestoreResult();
            var sw = Stopwatch.StartNew();
            var manifest = BackupManifest.Load(task.Id);
            var password = ResolvePassword(task);

            try
            {
                if (opt.ToOriginalLocation)
                {
                    RestoreToOriginal(task, manifest, opt, password, result, ctx, ct);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(opt.NewLocation))
                        throw new ArchiveException(ArchiveErrorKind.IoError, "未指定恢复目标位置");
                    RestoreToNew(task, manifest, opt, password, result, ctx, ct);
                }
                result.Success = true;
            }
            catch (OperationCanceledException) { result.Success = false; result.Error = "已取消"; throw; }
            catch (Exception ex)
            {
                Logger.Error($"恢复失败: {task.Name}", ex);
                result.Success = false;
                result.Error = ex.Message;
            }
            result.Duration = sw.Elapsed;
            return result;
        }

        void RestoreToNew(BackupTaskModel task, BackupManifest manifest, RestoreOptions opt, string? password,
            RestoreResult result, TaskRunContext? ctx, CancellationToken ct)
        {
            Directory.CreateDirectory(opt.NewLocation);
            // 备份链层叠恢复：后面的增量/差异必须覆盖前面的层（链顺序即时间顺序），
            // 因此链内层统一用 -y；冲突策略在“恢复到原位置”的文件映射阶段生效。
            const string conflictSwitch = "-y";
            string? listFile = null;
            try
            {
                var chainFiles = RequiredChainFiles(task, opt.Point);
                // 全量 → 增量按时间正序应用
                chainFiles.Reverse();
                foreach (var fileName in chainFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Path.Combine(task.DestDir, fileName);
                    if (!File.Exists(path)) continue;

                    var args = $"x {conflictSwitch} -o\"{opt.NewLocation}\" -bsp1 -bso0 -sccUTF-8 \"{path}\"";
                    if (!string.IsNullOrEmpty(password)) args += " -p" + password;
                    else args += " -p";
                    if (opt.SelectedEntries is { Count: > 0 })
                    {
                        listFile = Path.Combine(AppPaths.TempDir, "rs-" + Guid.NewGuid().ToString("N") + ".txt");
                        File.WriteAllLines(listFile, opt.SelectedEntries, new System.Text.UTF8Encoding(true));
                        args += $" -scsUTF-8 @\"{listFile}\"";
                    }
                    var (code, stdout, stderr) = _engine.RunProcess(args, ct);
                    if (code != 0 && code != 1)
                        throw SevenZipEngine.ClassifyError(code, stdout + "\n" + stderr, path);
                    result.RestoredArchives++;
                    ctx?.Log($"已应用 {fileName}");
                }
            }
            finally { TryDelete(listFile); }
        }

        void RestoreToOriginal(BackupTaskModel task, BackupManifest manifest, RestoreOptions opt, string? password,
            RestoreResult result, TaskRunContext? ctx, CancellationToken ct)
        {
            // 1. 解压到暂存区
            var staging = Path.Combine(AppPaths.TempDir, "restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var inner = new RestoreOptions
                {
                    Point = opt.Point,
                    ToOriginalLocation = false,
                    NewLocation = staging,
                    SelectedEntries = opt.SelectedEntries,
                    Conflict = ConflictPolicy.Overwrite,
                };
                RestoreToNew(task, manifest, inner, password, result, ctx, ct);

                // 2. 映射回源位置
                var byRel = manifest.Files.ToDictionary(f => f.Rel, StringComparer.OrdinalIgnoreCase);
                var staged = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories);
                foreach (var stagedFile in staged)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(staging, stagedFile).Replace('\\', '/');
                    if (opt.SelectedEntries is { Count: > 0 } && !opt.SelectedEntries.Contains(rel)) continue;
                    if (!byRel.TryGetValue(rel, out var mf) || string.IsNullOrEmpty(mf.SourcePath))
                    {
                        result.Unmapped++;
                        continue;
                    }
                    var target = mf.SourcePath;
                    Security.PathGuard.IsInsideDirectory(target, mf.SourcePath); // no-op sanity
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        var exists = File.Exists(target);
                        if (exists && opt.Conflict == ConflictPolicy.Skip)
                        {
                            result.Skipped++;
                            continue;
                        }
                        if (exists && opt.Conflict == ConflictPolicy.AutoRename)
                        {
                            target = UniqueName(target);
                            result.Renamed++;
                        }
                        else if (exists) result.Overwritten++;
                        File.Copy(stagedFile, target, overwrite: true);
                        try { File.SetLastWriteTime(target, mf.MTime); } catch { }
                        result.RestoredFiles++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{target}: {ex.Message}");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }

        static string UniqueName(string path)
        {
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                var cand = Path.Combine(dir, $"{name}({i}){ext}");
                if (!File.Exists(cand)) return cand;
            }
            return path;
        }

        string? ResolvePassword(BackupTaskModel task)
        {
            if (!task.Encrypted || string.IsNullOrEmpty(task.PasswordVaultId)) return null;
            return _vault.GetAll().FirstOrDefault(p => p.Id == task.PasswordVaultId)?.Password;
        }

        static void TryDelete(string? f) { try { if (f != null && File.Exists(f)) File.Delete(f); } catch { } }
    }

    public sealed class RestoreResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public TimeSpan Duration { get; set; }
        public int RestoredFiles { get; set; }
        public int RestoredArchives { get; set; }
        public int Overwritten { get; set; }
        public int Skipped { get; set; }
        public int Renamed { get; set; }
        public int Unmapped { get; set; }
        public List<string> Errors { get; } = new();
    }
}
