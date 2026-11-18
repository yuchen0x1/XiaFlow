using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XiaFlow.Core.Logging;

namespace XiaFlow.Core.Update
{
    /// <summary>更新信息。</summary>
    public sealed class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string ReleaseUrl { get; set; } = "";
        public string Notes { get; set; } = "";
        public DateTime PublishedAt { get; set; }
        public bool Prerelease { get; set; }
        public string? DownloadUrl { get; set; }
        public long DownloadSize { get; set; }
        public string? Sha256 { get; set; }
    }

    /// <summary>
    /// 自动更新：GitHub Releases 作为更新源。
    /// - 下载资产必须携带 SHA-256 摘要（Release Body 中 `sha256:<hex>` 行）才允许自动安装；
    /// - 校验失败绝不执行更新程序（安全要求）；
    /// - 断点续传：服务端支持 Range 时自动续传。
    /// </summary>
    public sealed class UpdateService
    {
        static readonly HttpClient Http = CreateHttp();

        static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("XiaFlow-Updater/1.0");
            return h;
        }

        public string Repository { get; set; } = Branding.RepositoryUrl;

        public event Action<(long downloaded, long total)>? DownloadProgress;

        /// <summary>检查最新版本（stable = 正式 release；beta = 含 prerelease）。</summary>
        public async Task<UpdateInfo?> CheckAsync(string channel = "stable", CancellationToken ct = default)
        {
            var repo = Repository.Replace("https://github.com/", "").Trim('/');
            if (string.IsNullOrEmpty(repo) || repo.Split('/').Length != 2)
                throw new InvalidOperationException("更新源未配置有效仓库（设置中可自定义）");

            var url = $"https://api.github.com/repos/{repo}/releases{(channel == "beta" ? "" : "/latest")}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub API 返回 {(int)resp.StatusCode}");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement rel;
            if (channel == "beta")
            {
                var releases = root.EnumerateArray()
                    .Where(r => r.GetProperty("draft").GetBoolean() == false)
                    .ToList();
                rel = releases.FirstOrDefault();
                if (rel.ValueKind == JsonValueKind.Undefined) return null;
            }
            else
            {
                rel = root;
            }

            var info = new UpdateInfo
            {
                Version = rel.GetProperty("tag_name").GetString() ?? "",
                ReleaseUrl = rel.GetProperty("html_url").GetString() ?? "",
                Notes = rel.GetProperty("body").GetString() ?? "",
                PublishedAt = rel.TryGetProperty("published_at", out var pub) ? pub.GetDateTime() : DateTime.MinValue,
                Prerelease = rel.GetProperty("prerelease").GetBoolean(),
            };

            // 找 Windows x64 安装包资产
            if (rel.TryGetProperty("assets", out var assets))
            {
                var asset = assets.EnumerateArray().FirstOrDefault(a =>
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                           name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                           (name.Contains("x64", StringComparison.OrdinalIgnoreCase) || name.Contains("win", StringComparison.OrdinalIgnoreCase));
                });
                if (asset.ValueKind == JsonValueKind.Undefined)
                {
                    asset = assets.EnumerateArray().FirstOrDefault(a =>
                        ((a.GetProperty("name").GetString() ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
                }
                if (asset.ValueKind != JsonValueKind.Undefined)
                {
                    info.DownloadUrl = asset.GetProperty("browser_download_url").GetString();
                    info.DownloadSize = asset.GetProperty("size").GetInt64();
                }
            }

            // 从 Release Body 提取 SHA-256（sha256:... 或 SHA-256:...）
            info.Sha256 = ExtractSha256(info.Notes);
            return info;
        }

        static string? ExtractSha256(string notes)
        {
            if (string.IsNullOrEmpty(notes)) return null;
            foreach (var line in notes.Split('\n'))
            {
                var idx = line.IndexOf("sha256", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var after = line[(idx + 6)..].TrimStart(':', ' ');
                var hex = new string(after.TakeWhile(c => char.IsAsciiHexDigit(c)).ToArray());
                if (hex.Length == 64) return hex.ToLowerInvariant();
            }
            return null;
        }

        public static bool IsNewer(string newVersion, string currentVersion)
        {
            static Version Parse(string s)
            {
                s = s.TrimStart('v', 'V', ' ');
                var dash = s.IndexOf('-');
                if (dash > 0) s = s[..dash];
                return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
            }
            return Parse(newVersion) > Parse(currentVersion);
        }

        /// <summary>下载更新包（支持断点续传），返回本地文件路径。</summary>
        public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(info.DownloadUrl))
                throw new InvalidOperationException("该版本未提供下载资产");

            Directory.CreateDirectory(AppPaths.TempDir);
            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
            var dest = Path.Combine(AppPaths.TempDir, "update-" + fileName);
            var partial = dest + ".part";

            long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = existing + (resp.Content.Headers.ContentLength ?? info.DownloadSize);

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(partial, existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent
                ? FileMode.Append : FileMode.Create, FileAccess.Write))
            {
                var buf = new byte[1 << 18];
                long done = existing;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress?.Report(total > 0 ? done * 100.0 / total : 0);
                    DownloadProgress?.Invoke((done, total));
                }
            }
            File.Move(partial, dest, overwrite: true);
            return dest;
        }

        /// <summary>校验 SHA-256。失败返回 false —— 调用方绝不能执行该文件。</summary>
        public static bool VerifyFile(string path, string expectedSha256)
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            return string.Equals(hash, expectedSha256.ToLowerInvariant().Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>执行安装（必须已通过 VerifyFile）。返回 true 表示已启动安装进程。</summary>
        public bool Install(string updateFilePath, bool silent = true)
        {
            if (!File.Exists(updateFilePath)) return false;
            var psi = new ProcessStartInfo(updateFilePath) { UseShellExecute = true };
            if (silent && updateFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                psi.Arguments = "/SILENT";
            Process.Start(psi);
            Logger.Info($"更新安装已启动: {updateFilePath}");
            return true;
        }
    }
}
