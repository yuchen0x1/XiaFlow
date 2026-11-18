using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using XiaFlow.Core.Update;
using Xunit;

namespace XiaFlow.Tests;

public sealed class SignedUpdateTests
{
    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("v1.1.0-beta.10", "1.1.0-beta.2", true)]
    [InlineData("v1.1.0", "1.1.0-beta.10", true)]
    [InlineData("v1.1.0-beta.10", "1.1.0", false)]
    [InlineData("v1.1.0", "1.1.0", false)]
    [InlineData("v1.1.0/other", "1.0.0", false)]
    public void VersionOrdering(string remote, string local, bool newer)
        => Assert.Equal(newer, UpdateService.IsNewer(remote, local));

    [Theory]
    [InlineData("valid", true)]
    [InlineData("invalid-signature", false)]
    [InlineData("incorrect-size", false)]
    [InlineData("incorrect-version", false)]
    [InlineData("incorrect-architecture", false)]
    [InlineData("incompatible-build", false)]
    [InlineData("incorrect-digest", false)]
    [InlineData("non-https-redirect", false)]
    public async Task ValidatesSignedMetadataAndMatchingInstaller(string scenario, bool valid)
    {
        const string tag = "v1.1.0";
        const string asset = "XiaFlow-1.1.0-setup-x64.exe";
        var installer = new byte[] { 10, 24, 36 };
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            version = scenario == "incorrect-version" ? "1.2.0" : "1.1.0",
            platform = "windows",
            architecture = scenario == "incorrect-architecture" ? "arm64" : "x64",
            minimumWindowsBuild = scenario == "incompatible-build" ? 30000 : 17763,
            asset,
            bytes = scenario == "incorrect-size" ? 4 : installer.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(installer))
        });
        using var key = RSA.Create(2048);
        var signature = key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (scenario == "invalid-signature") signature[0] ^= 1;
        if (scenario == "incorrect-digest") installer[0] ^= 1;

        using var handler = new FixtureHandler(new()
        {
            [UpdateService.MetadataAsset] = data,
            [UpdateService.SignatureAsset] = signature,
            [asset] = installer
        }, scenario == "non-https-redirect");
        using var http = new HttpClient(handler);
        var updater = new UpdateService(http, new Lazy<byte[]>(key.ExportSubjectPublicKeyInfo));
        var info = new UpdateInfo(tag, "", "", DateTime.UtcNow, installer.Length, true);
        var directory = Directory.CreateTempSubdirectory("xiaflow-update-tests-");
        try
        {
            if (valid)
            {
                var (result, _) = await updater.DownloadVerifiedAsync(info, directory.FullName, 26100, null, default);
                Assert.Equal(installer, File.ReadAllBytes(result));
                Assert.EndsWith(".exe", result);
            }
            else
            {
                var error = await Record.ExceptionAsync(() => updater.DownloadVerifiedAsync(info, directory.FullName, 26100, null, default));
                if (scenario == "non-https-redirect") Assert.IsType<HttpRequestException>(error);
                else Assert.IsType<InvalidDataException>(error);
                Assert.Empty(directory.EnumerateFiles());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    sealed class FixtureHandler(Dictionary<string, byte[]> assets, bool invalidRedirect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Assert.Equal("github.com", url.Host);
            Assert.StartsWith("/yuchen0x1/XiaFlow/releases/download/v1.1.0/", url.AbsolutePath);
            var source = invalidRedirect
                ? new HttpRequestMessage(HttpMethod.Get, "http://github.com" + url.AbsolutePath)
                : request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = source,
                Content = new ByteArrayContent(assets[url.Segments[^1]])
            });
        }
    }
}
