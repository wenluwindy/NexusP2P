using System.Net;
using System.Text;
using NexusP2P.Desktop.Updates;

namespace NexusP2P.Desktop.Tests.Updates;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nexusp2p-update-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("2.5", 2, 5, 0)]
    [InlineData("V3.1.4+build.8", 3, 1, 4)]
    public void 版本标签可转换为程序集版本(
        string tag,
        int major,
        int minor,
        int build)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public async Task 新版本只选择约定的Windows安装器()
    {
        using var client = CreateClient(_ => JsonResponse("""
            {
              "tag_name": "v1.2.0",
              "name": "NexusP2P 1.2.0",
              "html_url": "https://github.com/wenluwindy/NexusP2P/releases/tag/v1.2.0",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "nexusp2p-win-x64.zip",
                  "browser_download_url": "https://github.com/wenluwindy/NexusP2P/releases/download/v1.2.0/nexusp2p-win-x64.zip",
                  "size": 99
                },
                {
                  "name": "NexusP2P-Setup-1.2.0-win-x64.exe",
                  "browser_download_url": "https://github.com/wenluwindy/NexusP2P/releases/download/v1.2.0/NexusP2P-Setup-1.2.0-win-x64.exe",
                  "size": 1234
                }
              ]
            }
            """));
        using var service = new UpdateService(client, _temporaryDirectory);

        var release = await service.CheckAsync(new Version(1, 1, 0), CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 0), release.Version);
        Assert.Equal("NexusP2P-Setup-1.2.0-win-x64.exe", release.AssetName);
        Assert.Equal(1234, release.Size);
    }

    [Fact]
    public async Task 相同或更旧版本不提示更新()
    {
        using var client = CreateClient(_ => JsonResponse(ReleaseJson("v1.0.0", size: 12)));
        using var service = new UpdateService(client, _temporaryDirectory);

        var release = await service.CheckAsync(new Version(1, 0, 0), CancellationToken.None);

        Assert.Null(release);
    }

    [Fact]
    public async Task 新版本没有安装器时明确报错()
    {
        using var client = CreateClient(_ => JsonResponse("""
            {
              "tag_name": "v2.0.0",
              "name": "2.0.0",
              "html_url": "https://github.com/wenluwindy/NexusP2P/releases/tag/v2.0.0",
              "draft": false,
              "prerelease": false,
              "assets": []
            }
            """));
        using var service = new UpdateService(client, _temporaryDirectory);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CheckAsync(new Version(1, 0, 0), CancellationToken.None));

        Assert.Contains("没有", error.Message, StringComparison.Ordinal);
        Assert.Contains("安装程序", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 安装器先完整下载再落到最终文件名()
    {
        var installer = Encoding.UTF8.GetBytes("fake setup payload");
        using var client = CreateClient(request =>
            request.RequestUri?.Host == "api.github.com"
                ? JsonResponse(ReleaseJson("v1.1.0", installer.Length))
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installer),
                });
        using var service = new UpdateService(client, _temporaryDirectory);
        var release = await service.CheckAsync(new Version(1, 0, 0), CancellationToken.None);
        Assert.NotNull(release);

        var path = await service.DownloadAsync(release, progress: null, CancellationToken.None);

        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path + ".download"));
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new StubHandler(response));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string ReleaseJson(string tag, int size) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "{{tag}}",
          "html_url": "https://github.com/wenluwindy/NexusP2P/releases/tag/{{tag}}",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "NexusP2P-Setup-1.1.0-win-x64.exe",
              "browser_download_url": "https://github.com/wenluwindy/NexusP2P/releases/download/{{tag}}/NexusP2P-Setup-1.1.0-win-x64.exe",
              "size": {{size}}
            }
          ]
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
