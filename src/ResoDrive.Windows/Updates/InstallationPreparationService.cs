using System.ComponentModel;
using System.Diagnostics;

namespace ResoDrive.Windows;

internal interface IInstallationPreparationRuntime
{
    Task<HostResponse> ShutdownAsync(string directory, CancellationToken token);
    Task StopWindowsAsync(string directory, int? hostProcessId, CancellationToken token);
    Task WaitForHostExitAsync(int? processId, CancellationToken token);
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
            if (!response.Succeeded && response.ErrorCode is not ("host.unavailable" or "host.different_installation"))
                throw new InvalidOperationException(response.ErrorMessage ?? "ResoDrive could not safely stop its background work.");

            // Close the UI as soon as shutdown is accepted, before its recovery loop can restart the host.
            progress?.Report("Closing ResoDrive windows…");
            await _runtime.StopWindowsAsync(directory, response.HostProcessId, timeout.Token).ConfigureAwait(false);
            progress?.Report("Waiting for mounted drives and sync jobs to stop…");
            if (response.Succeeded)
                await _runtime.WaitForHostExitAsync(response.HostProcessId, timeout.Token).ConfigureAwait(false);
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
            var response = await HostClient.SendToInstallationAsync(new HostRequest("shutdown", Confirmed: true),
                directory, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (response.ErrorCode == "host.unavailable")
            {
                foreach (var process in Process.GetProcessesByName("resodrive"))
                {
                    using (process)
                    {
                        if (process.Id == Environment.ProcessId || process.SessionId != Process.GetCurrentProcess().SessionId)
                            continue;
                        try
                        {
                            if (string.Equals(process.MainModule?.FileName, Path.Combine(directory, "resodrive.exe"), StringComparison.OrdinalIgnoreCase))
                                throw new IOException("A running ResoDrive process is not responding. Exit ResoDrive from its tray menu and retry the installation. Your settings and cache have been preserved.");
                        }
                        catch (InvalidOperationException) { /* Already exited. */ }
                        catch (Win32Exception) { /* Cannot inspect a process owned by another account. */ }
                    }
                }
            }
            return response;
        }

        public async Task StopWindowsAsync(string directory, int? hostProcessId, CancellationToken token)
        {
            var executable = Path.Combine(directory, "resodrive.exe");
            foreach (var process in Process.GetProcessesByName("resodrive"))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || process.Id == hostProcessId ||
                        process.SessionId != Process.GetCurrentProcess().SessionId)
                        continue;
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch (InvalidOperationException) { continue; }
                    catch (Win32Exception) { continue; } // Another account's installation is outside this operation.
                    if (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        process.Kill(entireProcessTree: false);
                        await process.WaitForExitAsync(token).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException) { /* Already exited. */ }
                }
            }
        }

        public async Task WaitForHostExitAsync(int? processId, CancellationToken token)
        {
            if (processId is not int id)
                throw new InvalidOperationException("The background host did not provide a verifiable process identity.");
            try
            {
                using var process = Process.GetProcessById(id);
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (ArgumentException) { /* Already exited. */ }
            catch (InvalidOperationException) { /* Exited while opening the handle. */ }
        }
    }
}
