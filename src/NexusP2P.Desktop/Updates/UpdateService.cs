using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NexusP2P.Desktop.Updates;

internal sealed class UpdateService : IDisposable
{
    internal const string RepositoryUrl = "https://github.com/wenluwindy/NexusP2P";
    internal const string ReleasesUrl = RepositoryUrl + "/releases";

    private static readonly Uri LatestReleaseApi = new(
        "https://api.github.com/repos/wenluwindy/NexusP2P/releases/latest");

    private readonly HttpClient _client;
    private readonly string _downloadRoot;
    private readonly bool _ownsClient;

    internal UpdateService()
        : this(CreateClient(), GetDefaultDownloadRoot(), ownsClient: true)
    {
    }

    internal UpdateService(HttpClient client, string downloadRoot, bool ownsClient = false)
    {
        _client = client;
        _downloadRoot = downloadRoot;
        _ownsClient = ownsClient;
    }

    internal static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    internal async Task<UpdateRelease?> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(LatestReleaseApi, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub 返回了空的版本信息。");

        if (release.Draft || release.Prerelease ||
            !TryParseVersion(release.TagName, out var latestVersion) ||
            latestVersion <= currentVersion)
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(asset => IsWindowsInstaller(asset, latestVersion))
            ?? throw new InvalidDataException(
                $"发现新版本 {release.TagName}，但 Release 中没有 " +
                "NexusP2P-Setup-<版本>-win-x64.exe 安装程序。");

        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Release 安装程序的下载地址不是可信的 GitHub HTTPS 地址。");
        }

        return new UpdateRelease(
            latestVersion,
            release.TagName,
            release.Name,
            asset.Name,
            downloadUri,
            asset.Size,
            release.HtmlUrl);
    }

    internal async Task<string> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var versionDirectory = Path.Combine(_downloadRoot, release.Version.ToString(3));
        Directory.CreateDirectory(versionDirectory);

        var safeName = Path.GetFileName(release.AssetName);
        if (!string.Equals(safeName, release.AssetName, StringComparison.Ordinal) ||
            !IsInstallerFileName(safeName))
        {
            throw new InvalidDataException("安装程序文件名不安全或不受支持。");
        }

        var destination = Path.Combine(versionDirectory, safeName);
        if (File.Exists(destination) &&
            (release.Size <= 0 || new FileInfo(destination).Length == release.Size))
        {
            progress?.Report(new UpdateDownloadProgress(release.Size, release.Size));
            return destination;
        }

        var temporary = destination + ".download";

        try
        {
            using var response = await _client.GetAsync(
                release.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? release.Size;
            long downloaded = 0;
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var target = new FileStream(
                    temporary,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var buffer = new byte[81920];

                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    downloaded += read;
                    progress?.Report(new UpdateDownloadProgress(downloaded, totalBytes));
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (release.Size > 0 && downloaded != release.Size)
            {
                throw new InvalidDataException(
                    $"安装程序下载不完整：应为 {release.Size} 字节，实际为 {downloaded} 字节。");
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    internal static bool TryParseVersion(string tag, out Version version)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            value = value[..suffix];
        }

        if (!Version.TryParse(value, out var parsed) || parsed.Major < 0)
        {
            version = new Version(0, 0, 0);
            return false;
        }

        version = new Version(
            parsed.Major,
            Math.Max(0, parsed.Minor),
            Math.Max(0, parsed.Build));
        return true;
    }

    private static bool IsWindowsInstaller(GitHubAsset asset, Version version) =>
        string.Equals(asset.Name, ExpectedInstallerFileName(version), StringComparison.OrdinalIgnoreCase) &&
        asset.Size > 0;

    private static bool IsInstallerFileName(string name) =>
        name.StartsWith("NexusP2P-Setup-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase);

    private static string ExpectedInstallerFileName(Version version) =>
        $"NexusP2P-Setup-{version.ToString(3)}-win-x64.exe";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("NexusP2P-Desktop", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static string GetDefaultDownloadRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusP2P",
        "Updates");

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
        [property: JsonPropertyName("size")] long Size);
}

internal sealed record UpdateRelease(
    Version Version,
    string Tag,
    string Name,
    string AssetName,
    Uri DownloadUri,
    long Size,
    string ReleasePageUrl);

internal readonly record struct UpdateDownloadProgress(long DownloadedBytes, long TotalBytes)
{
    internal double Fraction => TotalBytes > 0
        ? Math.Clamp(DownloadedBytes / (double)TotalBytes, 0, 1)
        : 0;

    internal string Percentage => TotalBytes > 0
        ? Fraction.ToString("P0", CultureInfo.CurrentCulture)
        : string.Empty;
}
