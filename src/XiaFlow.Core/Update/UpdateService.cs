using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XiaFlow.Core.Update;

public sealed record UpdateInfo(string Version, string ReleaseUrl, string Notes, DateTime PublishedAt,
    long DownloadSize, bool CanInstall);

public sealed partial class UpdateService
{
    internal const string MetadataAsset = "update-windows-x64.json";
    internal const string SignatureAsset = "update-windows-x64.sig";
    const string OfficialRepo = "yuchen0x1/XiaFlow";
    const long MaxInstallerBytes = 512 * 1024 * 1024;

    static readonly HttpClient SharedHttp = CreateHttp();
    static readonly Lazy<byte[]> PinnedKey = new(() =>
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("XiaFlow.UpdatePublicKey")
            ?? throw new InvalidOperationException("缺少更新签名公钥。");
        using var reader = new StreamReader(stream);
        return Convert.FromBase64String(reader.ReadToEnd().Trim());
    });

    readonly HttpClient _http;
    readonly Lazy<byte[]> _publicKey;

    public UpdateService() : this(SharedHttp, PinnedKey) { }

    internal UpdateService(HttpClient http, Lazy<byte[]> publicKey)
    {
        _http = http;
        _publicKey = publicKey;
    }

    public string Repository { get; set; } = Branding.RepositoryUrl;

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XiaFlow-Updater/2.0");
        return http;
    }

    public async Task<UpdateInfo?> CheckAsync(string channel = "stable", CancellationToken ct = default)
    {
        var repo = Repository.Trim().TrimEnd('/');
        if (repo.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            repo = repo["https://github.com/".Length..];
        if (!Regex.IsMatch(repo, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z") || repo.EndsWith("/.") || repo.EndsWith("/.."))
            throw new InvalidOperationException("请输入有效的 GitHub 仓库地址。");

        var beta = channel == "beta";
        var url = $"https://api.github.com/repos/{repo}/releases" + (beta ? "?per_page=30" : "/latest");
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var releases = beta ? json.RootElement.EnumerateArray().ToArray() : new[] { json.RootElement };
        var ranked = releases.Where(r => !r.GetProperty("draft").GetBoolean())
            .Select(r => (Release: r, Tag: r.GetProperty("tag_name").GetString() ?? ""))
            .Where(r => ParseTag(r.Tag) != null)
            .OrderByDescending(r => r.Tag, Comparer<string>.Create(CompareTags));
        foreach (var (release, tag) in ranked)
        {
            var prerelease = release.GetProperty("prerelease").GetBoolean() || tag.Contains('-');
            if (!beta && prerelease) continue;
            var assets = release.GetProperty("assets").EnumerateArray()
                .Where(a => a.GetProperty("state").GetString() == "uploaded")
                .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("size").GetInt64());
            long size = assets.GetValueOrDefault(InstallerName(tag));
            bool canInstall = repo.Equals(OfficialRepo, StringComparison.OrdinalIgnoreCase) && !prerelease &&
                size is > 0 and <= MaxInstallerBytes && assets.ContainsKey(MetadataAsset) && assets.ContainsKey(SignatureAsset);
            var published = release.GetProperty("published_at");
            return new UpdateInfo(tag, $"https://github.com/{repo}/releases/tag/{tag}",
                release.GetProperty("body").GetString() ?? "",
                published.ValueKind == JsonValueKind.String ? published.GetDateTime() : DateTime.MinValue, size, canInstall);
        }
        return null;
    }

    [GeneratedRegex(@"\Av(?<num>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?<pre>-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    static Match? ParseTag(string tag)
    {
        var match = TagPattern().Match(tag);
        return match.Success && Version.TryParse(match.Groups["num"].Value, out _) ? match : null;
    }

    static int CompareTags(string a, string b)
    {
        var left = ParseTag(a)!;
        var right = ParseTag(b)!;
        int number = Version.Parse(left.Groups["num"].Value).CompareTo(Version.Parse(right.Groups["num"].Value));
        if (number != 0) return number;
        var x = left.Groups["pre"];
        var y = right.Groups["pre"];
        if (!x.Success || !y.Success) return y.Success.CompareTo(x.Success);

        var xs = x.Value[1..].Split('.');
        var ys = y.Value[1..].Split('.');
        for (int i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            bool xNumber = xs[i].All(char.IsAsciiDigit);
            bool yNumber = ys[i].All(char.IsAsciiDigit);
            if (xNumber != yNumber) return xNumber ? -1 : 1;
            if (xNumber && xs[i].Length != ys[i].Length) return xs[i].Length.CompareTo(ys[i].Length);
            int order = string.CompareOrdinal(xs[i], ys[i]);
            if (order != 0) return order;
        }
        return xs.Length.CompareTo(ys.Length);
    }

    public static bool IsNewer(string version, string currentVersion)
    {
        if (!currentVersion.StartsWith('v')) currentVersion = "v" + currentVersion;
        if (!version.StartsWith('v')) version = "v" + version;
        return ParseTag(version) != null && ParseTag(currentVersion) != null && CompareTags(version, currentVersion) > 0;
    }

    public static bool VerifyFile(string path, string expectedSha256) =>
        string.Equals(ArchiveService.Sha256(path), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);

    static string InstallerName(string tag) => $"XiaFlow-{tag[1..]}-setup-x64.exe";

    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "xiaflow-installed.ini"));
    public static bool IsSupportedPlatform => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public async Task InstallAsync(UpdateInfo info, IProgress<(long Downloaded, long Total)>? progress,
        CancellationToken ct, Func<bool> canExit)
    {
        if (!IsInstalled || !IsSupportedPlatform || !info.CanInstall ||
            !IsNewer(info.Version, Branding.AppVersion.ToString(3)))
            throw new InvalidOperationException("当前安装或目标版本不支持就地更新。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var (path, digest) = await DownloadVerifiedAsync(info, AppPaths.TempDir, Environment.OSVersion.Version.Build, progress, timeout.Token);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var verified = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var installedBytes = await SHA256.HashDataAsync(verified, timeout.Token);
            if (!installedBytes.AsSpan().SequenceEqual(digest))
                throw new InvalidDataException("准备安装时文件校验失败。");
            if (!canExit()) throw new InvalidOperationException("请先等待所有任务完成。");
            var start = new ProcessStartInfo(path) { UseShellExecute = false };
            start.ArgumentList.Add("/XIAFLOWUPDATE");
            start.ArgumentList.Add("/NORESTART");
            start.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
            using var installer = Process.Start(start) ?? throw new InvalidOperationException("无法启动安装程序。");
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    internal async Task<(string Path, byte[] Digest)> DownloadVerifiedAsync(UpdateInfo info, string directory, int windowsBuild,
        IProgress<(long Downloaded, long Total)>? progress, CancellationToken ct)
    {
        if (!info.CanInstall || ParseTag(info.Version) is not { } tag || tag.Groups["pre"].Success)
            throw new InvalidOperationException("目标版本没有可验证的安装包。");
        var baseUrl = $"https://github.com/{OfficialRepo}/releases/download/{info.Version}/";
        var metadata = await ReadSmallAssetAsync(baseUrl + MetadataAsset, 8192, ct);
        var signature = await ReadSmallAssetAsync(baseUrl + SignatureAsset, 1024, ct);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(_publicKey.Value, out _);
        if (!rsa.VerifyData(metadata, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("更新签名校验失败。");

        using var json = JsonDocument.Parse(metadata);
        var fields = json.RootElement;
        var filename = InstallerName(info.Version);
        var size = fields.GetProperty("bytes").GetInt64();
        var digest = fields.GetProperty("sha256").GetString() ?? "";
        if (fields.GetProperty("schema").GetInt32() != 1 ||
            fields.GetProperty("platform").GetString() != "windows" ||
            fields.GetProperty("architecture").GetString() != "x64" ||
            fields.GetProperty("version").GetString() != info.Version[1..] ||
            fields.GetProperty("asset").GetString() != filename ||
            fields.GetProperty("minimumWindowsBuild").GetInt32() > windowsBuild ||
            size is <= 0 or > MaxInstallerBytes || size != info.DownloadSize ||
            digest.Length != 64 || !digest.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("签名元数据与发布资产或本机不匹配。");

        var expectedHash = Convert.FromHexString(digest);
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, $"update-{Guid.NewGuid():N}.part");
        try
        {
            using var response = await GetAssetAsync(baseUrl + filename, ct);
            if (response.Content.Headers.ContentLength is long length && length != size)
                throw new InvalidDataException("安装包长度与签名元数据不符。");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                long total = 0;
                var buffer = new byte[65536];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > size) throw new InvalidDataException("安装包长度超出签名元数据。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report((total, size));
                }
                if (total != size || !hash.GetHashAndReset().AsSpan().SequenceEqual(expectedHash))
                    throw new InvalidDataException("安装包长度或 SHA-256 校验失败。");
            }
            var installer = Path.ChangeExtension(partial, ".exe");
            File.Move(partial, installer);
            return (installer, expectedHash);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    async Task<byte[]> ReadSmallAssetAsync(string url, int limit, CancellationToken ct)
    {
        using var response = await GetAssetAsync(url, ct);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[limit + 1];
        int count = await source.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
        if (count == 0 || count > limit) throw new InvalidDataException("更新元数据长度无效。");
        return buffer[..count];
    }

    async Task<HttpResponseMessage> GetAssetAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var final = response.RequestMessage?.RequestUri;
        if (response.StatusCode == System.Net.HttpStatusCode.OK && final is { Scheme: "https", IsDefaultPort: true } &&
            final.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")
            return response;
        response.Dispose();
        throw new HttpRequestException("无法从 GitHub 官方资产服务获取更新。");
    }
}
