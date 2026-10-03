using System.Globalization;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed record RcloneUpdateCheck(
    string CurrentVersion,
    string AvailableVersion,
    bool UpdateAvailable);

public sealed record RcloneUpdateResult(
    string PreviousVersion,
    string CurrentVersion,
    bool Updated,
    string? CleanupPath = null);

/// <summary>
/// Checks and explicitly installs stable rclone updates for the bundled executable.
/// </summary>
/// <remarks>
/// This service intentionally has no activity awareness and never updates automatically.
/// The caller must stop all mounts and sync jobs before calling <see cref="UpdateAsync"/>.
/// </remarks>
public sealed class RcloneUpdateService
{
    private static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultUpdateTimeout = TimeSpan.FromHours(1);
    private readonly RcloneRuntimeLocator _locator;
    private readonly IRcloneProcessRunner _processRunner;
    private readonly RcloneBootstrapService _bootstrap;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _updateTimeout;

    public RcloneUpdateService(RcloneRuntimeLocator? locator = null)
    {
        _locator = locator ?? new RcloneRuntimeLocator();
        _processRunner = new RcloneProcessRunner();
        _bootstrap = new RcloneBootstrapService(_locator);
        _checkTimeout = DefaultCheckTimeout;
        _updateTimeout = DefaultUpdateTimeout;
    }

    internal RcloneUpdateService(
        RcloneRuntimeLocator locator,
        IRcloneProcessRunner processRunner,
        TimeSpan checkTimeout,
        TimeSpan updateTimeout,
        RcloneBootstrapService? bootstrap = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _bootstrap = bootstrap ?? new RcloneBootstrapService(
            locator,
            processRunner,
            RcloneBootstrapService.SharedClient);
        _checkTimeout = RequirePositive(checkTimeout, nameof(checkTimeout));
        _updateTimeout = RequirePositive(updateTimeout, nameof(updateTimeout));
    }

    public async Task<OperationResult<RcloneUpdateCheck>> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        _locator.Paths.EnsureCreated();
        await using var mutationLock = await RcloneRuntimeMutationLock.AcquireAsync(
            _locator.Paths.Rclone,
            cancellationToken).ConfigureAwait(false);
        await ReconcileInterruptedUpdateAsync(cancellationToken).ConfigureAwait(false);
        return await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads and installs the latest stable release after the caller has stopped all
    /// rclone mounts and sync jobs. This method never stops active work itself.
    /// </summary>
    public async Task<OperationResult<RcloneUpdateResult>> UpdateAsync(
        IProgress<RcloneBootstrapProgress>? bootstrapProgress = null,
        CancellationToken cancellationToken = default)
    {
        _locator.Paths.EnsureCreated();
        await using (var mutationLock = await RcloneRuntimeMutationLock.AcquireAsync(
            _locator.Paths.Rclone,
            cancellationToken).ConfigureAwait(false))
        {
            await ReconcileInterruptedUpdateAsync(cancellationToken).ConfigureAwait(false);
            if (File.Exists(_locator.ExecutablePath))
                return await UpdateCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        // Bootstrap owns the same mutation lock, so invoke it only after releasing the
        // updater reconciliation lock.
        var installed = await _bootstrap.InstallAsync(
            bootstrapProgress,
            cancellationToken).ConfigureAwait(false);
        if (!installed.Succeeded || installed.Value?.Version is null)
        {
            var error = installed.Error!;
            return Result.Failure<RcloneUpdateResult>(error.Code, error.Message, error.IsTransient);
        }
        return Result.Success(new RcloneUpdateResult(string.Empty, installed.Value.Version, true));
    }

    private async Task ReconcileInterruptedUpdateAsync(CancellationToken cancellationToken)
    {
        var directory = _locator.Paths.Rclone;
        var executablePath = _locator.ExecutablePath;
        var canonicalIsSafe = !File.Exists(executablePath) || !IsReparsePoint(executablePath);
        var canonical = canonicalIsSafe
            ? await InspectExecutableAsync(executablePath, cancellationToken).ConfigureAwait(false)
            : Result.Failure<string>("rclone.update_unsafe_path", "The managed runtime is a file-system link.");

        if (canonical.Succeeded)
        {
            CleanupUpdateArtifacts(directory, includeBackups: true);
            return;
        }

        if (canonicalIsSafe)
        {
            foreach (var backup in EnumerateUpdateArtifacts(directory, ".rclone-old-", ".exe")
                .OrderByDescending(GetLastWriteTimeUtcSafe))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(backup))
                    continue;

                var inspected = await InspectExecutableAsync(backup, cancellationToken).ConfigureAwait(false);
                if (!inspected.Succeeded)
                    continue;

                try
                {
                    // Copying preserves the verified rollback source if the process is
                    // interrupted before the restored canonical file is re-verified.
                    File.Copy(backup, executablePath, overwrite: true);
                    var restored = await InspectExecutableAsync(
                        executablePath,
                        cancellationToken).ConfigureAwait(false);
                    if (restored.Succeeded)
                    {
                        CleanupUpdateArtifacts(directory, includeBackups: true);
                        return;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve backups for a later retry or manual recovery.
                    break;
                }
            }
        }

        CleanupUpdateArtifacts(directory, includeBackups: false);
    }

    private static string[] EnumerateUpdateArtifacts(
        string directory,
        string prefix,
        string suffix)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(path => IsUpdateArtifact(path, prefix, suffix))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsUpdateArtifact(string path, string prefix, string suffix)
    {
        var name = Path.GetFileName(path);
        const int transactionLength = 32;
        if (name.Length != prefix.Length + transactionLength + suffix.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        foreach (var character in name.AsSpan(prefix.Length, transactionLength))
            if (!char.IsAsciiHexDigit(character))
                return false;
        return true;
    }

    private static void CleanupUpdateArtifacts(string directory, bool includeBackups)
    {
        foreach (var path in EnumerateUpdateArtifacts(directory, ".rclone-update-", ".exe"))
            TryDelete(path);
        if (includeBackups)
            foreach (var path in EnumerateUpdateArtifacts(directory, ".rclone-old-", ".exe"))
                TryDelete(path);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private async Task<OperationResult<RcloneUpdateCheck>> CheckCoreAsync(
        CancellationToken cancellationToken)
    {
        var installed = await _locator.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!installed.Succeeded || installed.Value?.Version is null)
        {
            if (installed.Error?.Code == "rclone.not_installed")
            {
                return Result.Success(new RcloneUpdateCheck(
                    string.Empty,
                    RcloneBootstrapService.ReleaseVersion,
                    true));
            }
            var error = installed.Error!;
            return Result.Failure<RcloneUpdateCheck>(error.Code, error.Message, error.IsTransient);
        }

        ProcessRunResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                _locator.ExecutablePath,
                ["version", "--check"],
                _checkTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result.Failure<RcloneUpdateCheck>(
                "rclone.update_check_failed",
                "Could not check for rclone updates.",
                true);
        }

        if (processResult.TimedOut)
        {
            return Result.Failure<RcloneUpdateCheck>(
                "rclone.update_check_timeout",
                "The rclone update check timed out.",
                true);
        }

        if (processResult.ExitCode != 0)
        {
            return Result.Failure<RcloneUpdateCheck>(
                "rclone.update_check_failed",
                "Could not reach the official rclone update service.",
                true);
        }

        // `selfupdate --check` only emits a human-oriented notice in current
        // rclone releases. `version --check` provides the stable `latest:` field.
        // Parse both streams because rclone may route notices differently between
        // releases and environments.
        var checkOutput = string.Concat(
            processResult.StandardOutput,
            Environment.NewLine,
            processResult.StandardError);
        var availableVersion = ParseLabeledVersion(checkOutput, "latest");
        if (availableVersion is null)
        {
            return Result.Failure<RcloneUpdateCheck>(
                "rclone.update_check_invalid",
                "rclone returned an unrecognized update response.");
        }

        var currentVersion = NormalizeStableVersion(installed.Value.Version);
        if (currentVersion is null)
        {
            return Result.Failure<RcloneUpdateCheck>(
                "rclone.update_current_invalid",
                "The installed rclone version is not a recognized stable release.");
        }

        return Result.Success(new RcloneUpdateCheck(
            currentVersion,
            availableVersion,
            CompareStableVersions(availableVersion, currentVersion) > 0));
    }

    private async Task<OperationResult<RcloneUpdateResult>> UpdateCoreAsync(
        CancellationToken cancellationToken)
    {
        var check = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
        if (!check.Succeeded || check.Value is null)
        {
            var error = check.Error!;
            return Result.Failure<RcloneUpdateResult>(error.Code, error.Message, error.IsTransient);
        }

        if (!check.Value.UpdateAvailable)
        {
            return Result.Success(new RcloneUpdateResult(
                check.Value.CurrentVersion,
                check.Value.CurrentVersion,
                false));
        }

        if (string.IsNullOrEmpty(check.Value.CurrentVersion))
        {
            return Result.Failure<RcloneUpdateResult>(
                "rclone.not_installed",
                "rclone is not installed. Retry the download.",
                true);
        }

        var executablePath = _locator.ExecutablePath;
        FileAttributes executableAttributes;
        try
        {
            executableAttributes = File.GetAttributes(executablePath);
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure<RcloneUpdateResult>(
                "rclone.update_read_only",
                "The managed component folder is read-only.");
        }
        catch (IOException)
        {
            return Result.Failure<RcloneUpdateResult>(
                "rclone.update_locked",
                "The managed rclone runtime is unavailable. Stop all mounts and sync jobs, then retry.",
                true);
        }

        if (executableAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Result.Failure<RcloneUpdateResult>(
                "rclone.update_unsafe_path",
                "Updates are disabled because the managed rclone runtime is a file-system link.");
        }

        if (executableAttributes.HasFlag(FileAttributes.ReadOnly))
        {
            return Result.Failure<RcloneUpdateResult>(
                "rclone.update_read_only",
                "The managed component folder is read-only.");
        }

        var directory = Path.GetDirectoryName(executablePath)!;
        var transactionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var stagedPath = Path.Combine(directory, $".rclone-update-{transactionId}.exe");
        var backupPath = Path.Combine(directory, $".rclone-old-{transactionId}.exe");
        var committed = false;
        var preserveBackup = false;

        try
        {
            var download = await _processRunner.RunAsync(
                executablePath,
                ["selfupdate", "--stable", "--output", stagedPath],
                _updateTimeout,
                cancellationToken).ConfigureAwait(false);
            if (download.TimedOut)
            {
                return Result.Failure<RcloneUpdateResult>(
                    "rclone.update_timeout",
                    "The rclone update download timed out.",
                    true);
            }

            if (download.ExitCode != 0)
            {
                return Result.Failure<RcloneUpdateResult>(
                    "rclone.update_download_failed",
                    "rclone could not download and verify the official stable update.",
                    true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(stagedPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return Result.Failure<RcloneUpdateResult>(
                    "rclone.update_staged_invalid",
                    "The staged rclone update is not a regular file.");
            }

            var staged = await InspectExecutableAsync(stagedPath, cancellationToken).ConfigureAwait(false);
            if (!staged.Succeeded || staged.Value is null ||
                !string.Equals(staged.Value, check.Value.AvailableVersion, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<RcloneUpdateResult>(
                    "rclone.update_staged_invalid",
                    "The staged rclone update did not match the expected stable version.");
            }

            // Keep the rollback copy separate, then use same-volume replacement for the
            // shortest possible commit. Cancellation is deliberately not observed mid-commit.
            File.Replace(stagedPath, executablePath, backupPath, ignoreMetadataErrors: true);
            committed = true;

            var installed = await InspectExecutableAsync(executablePath, CancellationToken.None).ConfigureAwait(false);
            if (!installed.Succeeded || installed.Value is null ||
                !string.Equals(installed.Value, check.Value.AvailableVersion, StringComparison.OrdinalIgnoreCase))
            {
                var rollback = RollBack(
                    executablePath,
                    backupPath,
                    "The installed rclone update could not be verified and was rolled back.");
                preserveBackup = rollback.Error?.Code == "rclone.update_rollback_failed";
                return rollback;
            }

            var cleanupPath = await TryDeleteBackupAsync(backupPath).ConfigureAwait(false)
                ? null
                : backupPath;
            return Result.Success(new RcloneUpdateResult(
                check.Value.CurrentVersion,
                installed.Value,
                true,
                cleanupPath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            if (File.Exists(backupPath))
            {
                var rollback = RollBack(
                    executablePath,
                    backupPath,
                    "The update could not be completed and the previous rclone was restored.");
                preserveBackup = rollback.Error?.Code == "rclone.update_rollback_failed";
                return rollback;
            }

            return Result.Failure<RcloneUpdateResult>(
                "rclone.update_read_only",
                "The managed component folder cannot be modified. Check its permissions and read-only attributes.");
        }
        catch (IOException)
        {
            if (File.Exists(backupPath))
            {
                var rollback = RollBack(
                    executablePath,
                    backupPath,
                    "The update could not be completed and the previous rclone was restored.");
                preserveBackup = rollback.Error?.Code == "rclone.update_rollback_failed";
                return rollback;
            }

            return Result.Failure<RcloneUpdateResult>(
                "rclone.update_locked",
                "rclone.exe is in use or the package folder is unavailable. Stop all mounts and sync jobs, then retry.",
                true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result.Failure<RcloneUpdateResult>(
                "rclone.update_download_failed",
                "rclone could not start the official stable update.",
                true);
        }
        finally
        {
            TryDelete(stagedPath);
            if (!committed && !preserveBackup)
            {
                TryDelete(backupPath);
            }
        }
    }

    private async Task<OperationResult<string>> InspectExecutableAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(executablePath) || new FileInfo(executablePath).Length == 0)
                return Result.Failure<string>("rclone.update_staged_missing", "The staged executable is missing.");
            var result = await _processRunner.RunAsync(
                executablePath,
                ["version"],
                _checkTimeout,
                cancellationToken).ConfigureAwait(false);
            var line = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(value => value.StartsWith("rclone v", StringComparison.OrdinalIgnoreCase));
            return !result.TimedOut && result.ExitCode == 0 && line is not null
                ? NormalizeStableVersion(line["rclone ".Length..]) is { } version
                    ? Result.Success(version)
                    : Result.Failure<string>("rclone.update_staged_invalid", "The staged executable reported an invalid version.")
                : Result.Failure<string>("rclone.update_staged_invalid", "The staged executable is invalid.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result.Failure<string>("rclone.update_staged_invalid", "The staged executable could not be verified.");
        }
    }

    private static OperationResult<RcloneUpdateResult> RollBack(
        string executablePath,
        string backupPath,
        string message)
    {
        try
        {
            File.Move(backupPath, executablePath, overwrite: true);
            return Result.Failure<RcloneUpdateResult>("rclone.update_rolled_back", message, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<RcloneUpdateResult>(
                "rclone.update_rollback_failed",
                $"The update failed and automatic rollback also failed. Restore '{backupPath}' manually.");
        }
    }

    private static string? ParseLabeledVersion(string output, string label)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator < 0 || !line[..separator].Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            var end = value.IndexOfAny([' ', '\t', '(']);
            return NormalizeStableVersion(end < 0 ? value : value[..end]);
        }

        return null;
    }

    private static string? NormalizeStableVersion(string version)
    {
        var trimmed = version.Trim().TrimStart('v', 'V');
        // Keep empty components: accepting "1..75.0" here would let the later
        // Version.Parse comparison throw instead of reporting an invalid response.
        var components = trimmed.Split('.');
        if (components.Length is < 2 or > 4 ||
            components.Any(component => !int.TryParse(
                component,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _)))
        {
            return null;
        }

        return $"v{trimmed}";
    }

    private static int CompareStableVersions(string left, string right) =>
        Version.Parse(left[1..]).CompareTo(Version.Parse(right[1..]));

    private static async Task<bool> TryDeleteBackupAsync(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (TryDelete(path))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1))).ConfigureAwait(false);
        }

        return !File.Exists(path);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(parameterName);
}
