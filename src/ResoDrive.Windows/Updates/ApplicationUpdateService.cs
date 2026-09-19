using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ResoDrive.Core;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed record ApplicationUpdateCheck(
    string CurrentVersion,
    string AvailableVersion,
    bool UpdateAvailable,
    Uri ReleasePage,
    Uri? InstallerDownload = null,
    Uri? ChecksumDownload = null);

public sealed record ApplicationUpdatePackage(string Version, string InstallerPath, string Sha256);

public sealed record ApplicationUpdateDownloadProgress(long BytesReceived, long? TotalBytes);

/// <summary>Checks and downloads the latest published stable ResoDrive release.</summary>
public sealed partial class ApplicationUpdateService
{
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly Uri _endpoint;

    public ApplicationUpdateService()
        : this(SharedClient, ProductLinks.LatestRelease)
    {
    }

    internal ApplicationUpdateService(HttpClient client, Uri endpoint)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public async Task<OperationResult<ApplicationUpdateCheck>> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!TryVersion(currentVersion, out var current))
        {
            return Result.Failure<ApplicationUpdateCheck>(
                "app.update_current_invalid",
                "The installed ResoDrive version is not recognized.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<ApplicationUpdateCheck>(
                    "app.update_check_failed",
                    response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "No published ResoDrive release is available yet."
                        : "The ResoDrive release service could not be reached.",
                    true);
            }

            var releasePage = response.RequestMessage?.RequestUri;
            if (!TryReadRelease(releasePage, out var available))
            {
                return Result.Failure<ApplicationUpdateCheck>(
                    "app.update_response_invalid",
                    "The latest release link did not resolve to a valid ResoDrive version.");
            }

            var updateAvailable = available > current;
            var version = available.ToString(3);
            var downloadRoot = ProductLinks.Repository.AbsoluteUri.TrimEnd('/') +
                $"/releases/download/v{version}/";
            var installer = updateAvailable
                ? new Uri(downloadRoot + $"resodrive-win-x64-{version}.msi")
                : null;
            var checksum = updateAvailable
                ? new Uri(installer!.AbsoluteUri + ".sha256")
                : null;

            return Result.Success(new ApplicationUpdateCheck(
                currentVersion,
                version,
                updateAvailable,
                releasePage!,
                installer,
                checksum));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<ApplicationUpdateCheck>(
                "app.update_check_timeout",
                "The ResoDrive update check timed out.",
                true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException)
        {
            return Result.Failure<ApplicationUpdateCheck>(
                "app.update_check_failed",
                "The ResoDrive update check failed: " + exception.Message,
                true);
        }
    }

    public async Task<OperationResult<ApplicationUpdatePackage>> DownloadInstallerAsync(
        ApplicationUpdateCheck update,
        string destinationDirectory,
        IProgress<ApplicationUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!update.UpdateAvailable || !TryVersion(update.AvailableVersion, out var version) ||
            update.AvailableVersion != version.ToString(3) ||
            update.InstallerDownload is null || update.ChecksumDownload is null ||
            !IsTrustedAsset(update.InstallerDownload, update.AvailableVersion, checksum: false) ||
            !IsTrustedAsset(update.ChecksumDownload, update.AvailableVersion, checksum: true))
        {
            return Result.Failure<ApplicationUpdatePackage>(
                "app.update_download_invalid",
                "The selected ResoDrive update cannot be downloaded safely.");
        }

        var directory = Path.GetFullPath(destinationDirectory);
        var installerPath = Path.Combine(
            directory,
            $"resodrive-win-x64-{update.AvailableVersion}.msi");
        var temporaryPath = installerPath + ".download";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        try
        {
            Directory.CreateDirectory(directory);
            var expectedHash = await DownloadChecksumAsync(update.ChecksumDownload,
                    Path.GetFileName(installerPath), timeout.Token)
                .ConfigureAwait(false);
            if (expectedHash is null)
            {
                return Result.Failure<ApplicationUpdatePackage>(
                    "app.update_checksum_invalid",
                    "The published installer checksum is invalid.");
            }

            var downloadProgress = progress is null
                ? null
                : new CallbackProgress<DownloadProgress>(value => progress.Report(
                    new ApplicationUpdateDownloadProgress(value.BytesReceived, value.TotalBytes)));
            await ResumableHttpDownload.DownloadAsync(
                _client,
                update.InstallerDownload,
                temporaryPath,
                MaximumInstallerBytes,
                downloadProgress,
                timeout.Token).ConfigureAwait(false);

            string actualHash;
            await using (var installer = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(installer, timeout.Token).ConfigureAwait(false));
            }
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                DeleteIfPresent(temporaryPath);
                return Result.Failure<ApplicationUpdatePackage>(
                    "app.update_checksum_mismatch",
                    "The downloaded installer failed SHA-256 verification.");
            }

            File.Move(temporaryPath, installerPath, true);
            RemoveOlderInstallers(directory, installerPath);
            return Result.Success(new ApplicationUpdatePackage(
                update.AvailableVersion,
                installerPath,
                expectedHash));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<ApplicationUpdatePackage>(
                "app.update_download_timeout",
                "The ResoDrive installer download timed out.",
                true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result.Failure<ApplicationUpdatePackage>(
                "app.update_download_failed",
                "The ResoDrive installer could not be downloaded: " + exception.Message,
                true);
        }
    }

    private async Task<string?> DownloadChecksumAsync(Uri uri, string installerName, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 4096)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[4097];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                break;
            length += count;
        }
        if (length == buffer.Length)
            return null;
        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1) return null;
        var match = ChecksumLinePattern().Match(lines[0]);
        return match.Success && match.Groups[2].Value.Equals(installerName, StringComparison.Ordinal)
            ? match.Groups[1].Value : null;
    }

    private static bool TryReadRelease(Uri? uri, out Version version)
    {
        version = null!;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(ProductLinks.Repository.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        var prefix = ProductLinks.Repository.AbsolutePath.TrimEnd('/') + "/releases/tag/v";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var tag = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]).TrimEnd('/');
        return !tag.Contains('/') && TryVersion(tag, out version);
    }

    private static bool IsTrustedAsset(Uri uri, string version, bool checksum)
    {
        var repositoryPath = ProductLinks.Repository.AbsolutePath.TrimEnd('/');
        return uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.Host.Equals(ProductLinks.Repository.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(repositoryPath + "/", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath[repositoryPath.Length..].Equals(
            $"/releases/download/v{version}/resodrive-win-x64-{version}.msi" + (checksum ? ".sha256" : string.Empty),
            StringComparison.Ordinal);
    }

    private static bool TryVersion(string? value, out Version version)
    {
        version = null!;
        var match = StableVersionPattern().Match(value?.Trim() ?? string.Empty);
        return match.Success && Version.TryParse(match.Groups[1].Value, out version!);
    }

    private static void RemoveOlderInstallers(string directory, string current)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "resodrive-win-x64-*.msi"))
        {
            if (!path.Equals(current, StringComparison.OrdinalIgnoreCase))
                DeleteIfPresent(path);
        }
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ResoDrive", "1.0"));
        return client;
    }

    [GeneratedRegex(@"\A([0-9a-fA-F]{64})[ \t]+\*?([^\\/\r\n]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLinePattern();

    [GeneratedRegex(@"(?i)^v?(\d+\.\d+\.\d+)(?:\+[0-9a-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();
}
