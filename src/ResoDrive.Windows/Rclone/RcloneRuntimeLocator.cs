using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

/// <summary>Resolves only the private rclone runtime owned by this application.</summary>
public sealed class RcloneRuntimeLocator
{
    private readonly IRcloneProcessRunner _processRunner;

    public RcloneRuntimeLocator(ApplicationPaths? paths = null)
        : this(paths ?? new ApplicationPaths(), new RcloneProcessRunner())
    {
    }

    internal RcloneRuntimeLocator(ApplicationPaths paths, IRcloneProcessRunner processRunner)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    internal ApplicationPaths Paths { get; }
    public string ExecutablePath => Paths.RcloneExecutable;

    public async Task<OperationResult<InstallationStatus>> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ExecutablePath))
        {
            return Result.Failure<InstallationStatus>(
                "rclone.not_installed",
                "The managed rclone component is not installed.");
        }

        try
        {
            var result = await _processRunner.RunAsync(
                ExecutablePath,
                ["version"],
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            if (result.TimedOut)
            {
                return Result.Failure<InstallationStatus>(
                    "rclone.version_timeout",
                    "rclone did not respond in time.",
                    true);
            }

            var version = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith("rclone v", StringComparison.OrdinalIgnoreCase));
            if (result.ExitCode != 0 || version is null)
                return Result.Failure<InstallationStatus>(
                    "rclone.invalid",
                    "The managed rclone installation could not be verified.");
            var reportedVersion = "v" + version["rclone v".Length..];
            // A valid engine remains usable even when its version cannot fit in the
            // storage identity; the host checks reportability separately.
            return Result.Success(new InstallationStatus(reportedVersion, ExecutablePath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Result.Failure<InstallationStatus>("rclone.invalid", exception.Message);
        }
    }
}
