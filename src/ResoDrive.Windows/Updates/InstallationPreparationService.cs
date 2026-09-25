namespace ResoDrive.Windows;

internal interface IInstallationPreparationRuntime
{
    Task<HostResponse> ShutdownAsync(string directory, CancellationToken token);
    Task StopWindowsAsync(string directory, int? hostProcessId, CancellationToken token);
    Task WaitForHostExitAsync(int? processId, CancellationToken token);
    Task StopOrphanedUiAsync(string directory, CancellationToken token);
    Task VerifyStoppedAsync(string directory, CancellationToken token);
}

public sealed class InstallationPreparationService
{
    private readonly IInstallationPreparationRuntime _runtime;

    public InstallationPreparationService() : this(new Runtime()) { }
    internal InstallationPreparationService(IInstallationPreparationRuntime runtime) => _runtime = runtime;

    public async Task PrepareAsync(string installationDirectory, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        if (!Path.IsPathFullyQualified(installationDirectory))
            throw new ArgumentException("An absolute installation directory is required.", nameof(installationDirectory));
        var directory = Path.GetFullPath(installationDirectory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            progress?.Report("Checking uploads and stopping background work…");
            var response = await _runtime.ShutdownAsync(directory, timeout.Token).ConfigureAwait(false);
            if (response.ErrorCode == "host.unavailable")
            {
                progress?.Report("Checking whether only a ResoDrive window remains…");
                await _runtime.StopOrphanedUiAsync(directory, timeout.Token).ConfigureAwait(false);
                progress?.Report("ResoDrive is ready. Windows Installer will now continue.");
                return;
            }
            if (!response.Succeeded)
                throw new InvalidOperationException(response.ErrorMessage ?? "ResoDrive could not safely stop its background work.");

            // Close the UI as soon as shutdown is accepted, before its recovery loop can restart the host.
            progress?.Report("Closing ResoDrive windows…");
            await _runtime.StopWindowsAsync(directory, response.HostProcessId, timeout.Token).ConfigureAwait(false);
            progress?.Report("Waiting for mounted drives and sync jobs to stop…");
            if (response.Succeeded)
                await _runtime.WaitForHostExitAsync(response.HostProcessId, timeout.Token).ConfigureAwait(false);
            await _runtime.VerifyStoppedAsync(directory, timeout.Token).ConfigureAwait(false);
            progress?.Report("ResoDrive is ready. Windows Installer will now continue.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("ResoDrive did not stop within 60 seconds. Close open documents, let uploads finish, and try again.");
        }
    }

    private sealed class Runtime : IInstallationPreparationRuntime
    {
        public async Task<HostResponse> ShutdownAsync(string directory, CancellationToken token)
        {
            HostResponse response;
            for (var attempt = 0; ; attempt++)
            {
                response = await HostClient.SendToInstallationAsync(new HostRequest("shutdown", Confirmed: true),
                    directory, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                if (response.ErrorCode != "host.unavailable" || attempt == 2 ||
                    !InstallerProcessInspection.IsHostMutexPresent())
                    return response;
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }

        public Task StopWindowsAsync(string directory, int? hostProcessId, CancellationToken token) =>
            InstallerProcessInspection.StopVerifiedUiAsync(directory, hostProcessId, token);

        public Task StopOrphanedUiAsync(string directory, CancellationToken token) =>
            InstallerProcessInspection.StopOrphanedUiAsync(directory, token);

        public Task VerifyStoppedAsync(string directory, CancellationToken token) =>
            InstallerProcessInspection.VerifyStoppedAsync(directory, token);

        public async Task WaitForHostExitAsync(int? processId, CancellationToken token)
        {
            if (processId is not int id)
                throw new InvalidOperationException("The background host did not provide a verifiable process identity.");
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (ArgumentException) { /* Already exited. */ }
            catch (InvalidOperationException) { /* Exited while opening the handle. */ }
        }
    }
}
