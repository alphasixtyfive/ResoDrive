using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ResoDrive.Windows;

namespace ResoDrive.App;

internal static partial class DirectoryMigrationStartup
{
    internal const string BridgeEnvironmentVariable = "RESODRIVE_LEGACY_HANDOFF";
    internal const string CompletionArgument = "--finish-user-data-migration";
    private const string RetryOnNextStartEnvironmentVariable = "RESODRIVE_MIGRATION_RETRY_ON_NEXT_START";
    private static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResoDriveMigration");
    internal static bool PendingHandoff { get; private set; }

    internal static void EnsureReady()
    {
        var configured = Environment.GetEnvironmentVariable("RDRIVE_DATA_DIR");
        // Explicit custom roots retain their owner-selected location.
        if (!string.IsNullOrWhiteSpace(configured) &&
            !InstallationDirectories.SamePath(configured, ApplicationPaths.LegacyDefaultRoot) &&
            !InstallationDirectories.SamePath(configured, ApplicationPaths.DefaultRoot)) return;
        // New installations pay only these existence checks: no migration helper,
        // journal creation, MSI lookup, credential reads, locks or migration UI.
        if (!Directory.Exists(ApplicationPaths.LegacyDefaultRoot) &&
            !File.Exists(Path.Combine(StateDirectory, "user-data-migration.json"))) return;
        if (Directory.Exists(ApplicationPaths.LegacyDefaultRoot))
        {
            var wipe = new RemoteWipeStateStore(new ApplicationPaths(ApplicationPaths.LegacyDefaultRoot)).Read();
            if (wipe is { Phase: not RemoteWipePhase.Completed })
            {
                // The original root must remain available to its recovery host.
                // Blocking every --host launch would prevent cleanup ever finishing.
                Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", ApplicationPaths.LegacyDefaultRoot);
                Environment.SetEnvironmentVariable(BridgeEnvironmentVariable, "1");
                return;
            }
            using var helpers = FindLegacyHelpers();
            if (helpers.Processes.Count != 0 || Environment.GetEnvironmentVariable(BridgeEnvironmentVariable) == "1")
            {
                Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", ApplicationPaths.LegacyDefaultRoot);
                PendingHandoff = true;
                return;
            }
        }
        MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(configured))
            Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", ApplicationPaths.DefaultRoot);
        WriteResult(true, "The user-data directory has migrated.");
    }

    internal static async Task FinishHandoffAsync(CancellationToken token)
    {
        // A visible startup window lets the old updater finish its readiness
        // handshake, without starting a host or mounting any drives beforehand.
        using var helpers = FindLegacyHelpers();
        foreach (var helper in helpers.Processes)
            await helper.WaitForExitAsync(token).ConfigureAwait(false);
        await MigrateAsync(token).ConfigureAwait(false);
        Environment.SetEnvironmentVariable(BridgeEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(RetryOnNextStartEnvironmentVariable, null);
        Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", ApplicationPaths.DefaultRoot);
        PendingHandoff = false;
        WriteResult(true, "Both application and user-data locations have migrated.");
    }

    internal static void RecordHandoffFailure(Exception exception)
    {
        try
        {
            _ = UserDataDirectoryMigration.PrepareHelperDirectory();
            WriteResult(false, RecoveryToolsService.Sanitize(exception.Message));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    private static async Task MigrateAsync(CancellationToken token)
    {
        var previous = Environment.GetEnvironmentVariable("RDRIVE_DATA_DIR");
        Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", ApplicationPaths.LegacyDefaultRoot);
        try
        {
            await new UserDataDirectoryMigration(ApplicationPaths.LegacyDefaultRoot, ApplicationPaths.DefaultRoot)
                .MigrateAsync(async cancellation =>
                {
                    // New app code is always used, including after a legacy MSI.
                    // Both the old and new locations are checked; portable hosts fail closed.
                    var installed = InstalledApplicationLocator.GetInstallationDirectories();
                    var directories = installed.Append(AppContext.BaseDirectory).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var directory in directories)
                        await InstallationPreparationService.ForUserDataMigration().PrepareAsync(directory, cancellationToken: cancellation).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
        }
        finally { Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", previous); }
    }

    internal static async Task<int> MigrateWithoutLaunchingAsync()
    {
        try
        {
            using var helpers = FindLegacyHelpers();
            if (helpers.Processes.Count != 0) return 1; // Let the active in-app handoff finish first.
            await MigrateAsync(CancellationToken.None).ConfigureAwait(false);
            _ = UserDataDirectoryMigration.PrepareHelperDirectory();
            WriteResult(true, "The user-data directory has migrated without changing application startup preferences.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or TimeoutException or JsonException)
        {
            RecordHandoffFailure(exception);
            return 1;
        }
    }

    // Compatibility entry point for an already copied older migration helper.
    internal static Task<int> CompleteAsync() => MigrateWithoutLaunchingAsync();

    private static void WriteResult(bool succeeded, string message) => File.WriteAllText(
        Path.Combine(StateDirectory, "completion.json"), JsonSerializer.Serialize(new { Succeeded = succeeded, Message = message, RecordedAtUtc = DateTimeOffset.UtcNow }));

    internal static ProcessLease FindLegacyHelpers()
    {
        var result = new List<Process>();
        var expected = Path.Combine(ApplicationPaths.LegacyDefaultRoot, "updates", "resodrive-update-helper.exe");
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName("resodrive-update-helper"))
            {
                var retained = false;
                try
                {
                    if (process.HasExited || process.SessionId != current.SessionId) continue;
                    if (process.MainModule?.FileName is not { } path) throw new IOException("An update helper's executable identity could not be verified.");
                    if (!InstallationDirectories.SamePath(path, expected)) continue;
                    CurrentUserPipe.ValidateProcessIdentity(process.Id);
                    result.Add(process);
                    retained = true;
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                finally { if (!retained) process.Dispose(); }
            }
            return new(result);
        }
        catch
        {
            foreach (var process in result) process.Dispose();
            throw;
        }
    }

    internal sealed class ProcessLease(IReadOnlyList<Process> processes) : IDisposable
    {
        internal IReadOnlyList<Process> Processes { get; } = processes;
        public void Dispose() { foreach (var process in Processes) process.Dispose(); }
    }
}
