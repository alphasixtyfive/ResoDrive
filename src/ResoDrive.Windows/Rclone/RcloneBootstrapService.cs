using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed record RcloneBootstrapProgress(
    string Message,
    long BytesReceived = 0,
    long? TotalBytes = null)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

/// <summary>Installs the first ResoDrive-owned rclone runtime from a pinned official release.</summary>
public sealed class RcloneBootstrapService
{
    private static readonly SearchValues<char> TransactionHexCharacters =
        SearchValues.Create("0123456789abcdefABCDEF");
    private const long MaximumArchiveBytes = 64L * 1024 * 1024;
    private const long MaximumExecutableBytes = 256L * 1024 * 1024;
    internal static readonly HttpClient SharedClient = CreateClient();
    private readonly RcloneRuntimeLocator _locator;
    private readonly IRcloneProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly string _archiveSha256;
    private readonly TimeSpan _downloadTimeout;

    public const string ReleaseVersion = "v1.75.0";
    internal const string ArchiveSha256 = "203581f0a7baeae873f2347483a798c79e2eaf5c384a4e9d866aa374f1c89ac0";
    internal static readonly Uri ArchiveUri = new(
        "https://downloads.rclone.org/v1.75.0/rclone-v1.75.0-windows-amd64.zip");

    public RcloneBootstrapService(RcloneRuntimeLocator? locator = null)
        : this(locator ?? new RcloneRuntimeLocator(), new RcloneProcessRunner(), SharedClient)
    {
    }

    internal RcloneBootstrapService(
        RcloneRuntimeLocator locator,
        IRcloneProcessRunner processRunner,
        HttpClient httpClient,
        string? archiveSha256 = null,
        TimeSpan? downloadTimeout = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _archiveSha256 = archiveSha256 ?? ArchiveSha256;
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromHours(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_downloadTimeout, TimeSpan.Zero);
    }

    public async Task<OperationResult<InstallationStatus>> InstallAsync(
        IProgress<RcloneBootstrapProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _locator.Paths.EnsureCreated();
        await using var mutationLock = await RcloneRuntimeMutationLock.AcquireAsync(
            _locator.Paths.Rclone,
            cancellationToken).ConfigureAwait(false);
        var current = await _locator.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (current.Succeeded)
        {
            CleanupInterruptedArtifacts(includeBackups: true);
            TryDelete(Path.Combine(_locator.Paths.Rclone, ".rclone-download.zip"));
            return current;
        }

        var recovered = await TryRecoverPreviousRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (recovered is not null)
            return recovered;

        CleanupInterruptedArtifacts(includeBackups: false);

        var transaction = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(_locator.Paths.Rclone, ".rclone-download.zip");
        var stagedPath = Path.Combine(_locator.Paths.Rclone, $".rclone-{transaction}.exe");
        var backupPath = Path.Combine(_locator.Paths.Rclone, $".previous-{transaction}.exe");
        var replacementStarted = false;
        var preserveBackup = false;
        var preserveArchive = false;
        using var downloadDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadDeadline.CancelAfter(_downloadTimeout);
        try
        {
            progress?.Report(new RcloneBootstrapProgress("Connecting to the rclone download"));
            await DownloadAsync(archivePath, progress, downloadDeadline.Token).ConfigureAwait(false);
            progress?.Report(new RcloneBootstrapProgress("Verifying download"));
            if (!await HasExpectedHashAsync(archivePath, _archiveSha256, downloadDeadline.Token).ConfigureAwait(false))
            {
                TryDelete(archivePath);
                return Result.Failure<InstallationStatus>("rclone.download_hash", "The rclone download failed verification.");
            }

            await ExtractExecutableAsync(archivePath, stagedPath, downloadDeadline.Token).ConfigureAwait(false);
            var stagedVersion = await InspectExecutableAsync(stagedPath, downloadDeadline.Token).ConfigureAwait(false);
            if (!stagedVersion.Succeeded ||
                !string.Equals(stagedVersion.Value, ReleaseVersion, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<InstallationStatus>("rclone.download_invalid", "The downloaded rclone executable is invalid.");

            progress?.Report(new RcloneBootstrapProgress("Installing rclone"));
            downloadDeadline.Token.ThrowIfCancellationRequested();
            var target = _locator.ExecutablePath;
            if (File.Exists(target))
            {
                if (File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
                    return Result.Failure<InstallationStatus>(
                        "rclone.install_unsafe_path",
                        "The existing ResoDrive rclone runtime is a file-system link.");
                File.Replace(stagedPath, target, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(stagedPath, target, overwrite: false);
            }
            replacementStarted = true;
            var installed = await _locator.InspectAsync(CancellationToken.None).ConfigureAwait(false);
            if (!installed.Succeeded)
            {
                if (File.Exists(backupPath))
                {
                    if (!RestoreBackup(target, backupPath))
                    {
                        preserveBackup = true;
                        return Result.Failure<InstallationStatus>(
                            "rclone.install_rollback_failed",
                            $"The new runtime failed verification. Restore '{backupPath}' manually.");
                    }
                    return Result.Failure<InstallationStatus>(
                        "rclone.install_verify",
                        "The installed rclone runtime failed verification and the previous copy was restored.");
                }
                TryDelete(target);
                return Result.Failure<InstallationStatus>(
                    "rclone.install_verify",
                    "The installed rclone runtime failed verification and was removed.");
            }

            TryDelete(backupPath);
            CleanupInterruptedArtifacts(includeBackups: true);
            return installed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            preserveArchive = true;
            return Result.Failure<InstallationStatus>(
                "rclone.download_timeout",
                "The rclone download timed out.",
                true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            preserveArchive = true;
            return Result.Failure<InstallationStatus>(
                "rclone.download_network",
                "rclone could not be downloaded. Check the connection and try again.",
                true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            if (replacementStarted && File.Exists(backupPath) &&
                !RestoreBackup(_locator.ExecutablePath, backupPath))
                preserveBackup = true;
            if (preserveBackup)
                return Result.Failure<InstallationStatus>(
                    "rclone.install_rollback_failed",
                    $"Installation failed and the previous runtime could not be restored. Restore '{backupPath}' manually.");
            return Result.Failure<InstallationStatus>("rclone.install_failed", exception.Message, true);
        }
        finally
        {
            if (!preserveArchive)
                TryDelete(archivePath);
            TryDelete(stagedPath);
            if (!preserveBackup)
                TryDelete(backupPath);
        }
    }

    private async Task<OperationResult<InstallationStatus>?> TryRecoverPreviousRuntimeAsync(
        CancellationToken cancellationToken)
    {
        var target = _locator.ExecutablePath;
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
            return null;

        string[] backups;
        try
        {
            backups = Directory.EnumerateFiles(_locator.Paths.Rclone)
                .Where(path => IsTransactionArtifact(path, ".previous-", ".exe"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var backup in backups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetAttributes(backup).HasFlag(FileAttributes.ReparsePoint))
                    continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var inspected = await InspectExecutableAsync(backup, cancellationToken).ConfigureAwait(false);
            if (!inspected.Succeeded)
                continue;

            try
            {
                File.Move(backup, target, overwrite: true);
                var recovered = await _locator.InspectAsync(cancellationToken).ConfigureAwait(false);
                if (recovered.Succeeded)
                {
                    CleanupInterruptedArtifacts(includeBackups: true);
                    return recovered;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private void CleanupInterruptedArtifacts(bool includeBackups)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_locator.Paths.Rclone))
            {
                var isBootstrapArtifact =
                    IsTransactionArtifact(path, ".download-", ".zip") ||
                    IsTransactionArtifact(path, ".rclone-", ".exe") ||
                    includeBackups && IsTransactionArtifact(path, ".previous-", ".exe");
                if (isBootstrapArtifact)
                    TryDelete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked artifact is harmless and can be retried next time.
        }
    }

    private static bool IsTransactionArtifact(string path, string prefix, string suffix)
    {
        var name = Path.GetFileName(path);
        var transactionLength = 32;
        if (name.Length != prefix.Length + transactionLength + suffix.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        return name.AsSpan(prefix.Length, transactionLength).IndexOfAnyExcept(
            TransactionHexCharacters) < 0;
    }

    private async Task DownloadAsync(
        string destination,
        IProgress<RcloneBootstrapProgress>? progress,
        CancellationToken cancellationToken)
    {
        var downloadProgress = progress is null
            ? null
            : new CallbackProgress<DownloadProgress>(value => progress.Report(new RcloneBootstrapProgress(
                "Downloading rclone",
                value.BytesReceived,
                value.TotalBytes)));
        await ResumableHttpDownload.DownloadAsync(
            _httpClient,
            ArchiveUri,
            destination,
            MaximumArchiveBytes,
            downloadProgress,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash).Equals(expectedHash, StringComparison.Ordinal);
    }

    private static async Task ExtractExecutableAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var candidates = archive.Entries.Where(entry =>
            entry.Name.Equals("rclone.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1 || candidates[0].Length is <= 0 or > MaximumExecutableBytes)
            throw new InvalidDataException("The rclone archive has an unexpected layout.");

        await using var source = candidates[0].Open();
        await using var target = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult<string>> InspectExecutableAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                executable, ["version"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var line = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(value => value.StartsWith("rclone v", StringComparison.OrdinalIgnoreCase));
            return !result.TimedOut && result.ExitCode == 0 && line is not null
                ? Result.Success(line["rclone ".Length..])
                : Result.Failure<string>("rclone.download_invalid", "The downloaded executable did not report a version.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Result.Failure<string>("rclone.download_invalid", "Windows could not start the downloaded executable.");
        }
    }

    private static bool RestoreBackup(string target, string backup)
    {
        if (!File.Exists(backup))
            return false;
        try
        {
            File.Move(backup, target, overwrite: true);
            return File.Exists(target) && !File.Exists(backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static HttpClient CreateClient() => new()
    {
        Timeout = TimeSpan.FromHours(1),
        DefaultRequestHeaders = { UserAgent = { new("ResoDrive", "1.0") } }
    };
}
