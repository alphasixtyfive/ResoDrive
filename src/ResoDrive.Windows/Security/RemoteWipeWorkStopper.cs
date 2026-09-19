using System.Diagnostics;

namespace ResoDrive.Windows;

public static class RemoteWipeWorkStopper
{
    /// <summary>Stops only recorded, identity-verified rclone processes, including after a host crash.</summary>
    public static async Task StopOwnedMountsAsync(ApplicationPaths paths, CancellationToken cancellationToken)
    {
        using var ownership = new MountOwnershipStore(paths);
        foreach (var owned in await ownership.LoadForRemoteWipeAsync(cancellationToken).ConfigureAwait(false))
        {
            Process process;
            try { process = Process.GetProcessById(owned.ProcessId); }
            catch (ArgumentException) { continue; }
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    if (Math.Abs((process.StartTime.ToUniversalTime() - owned.StartTimeUtc).TotalSeconds) >= 1)
                        continue;
                    // Failure to inspect a live process is not proof that it has stopped.
                    var executable = process.MainModule?.FileName
                        ?? throw new IOException("Remote wipe could not verify a recorded mount process.");
                    if (!MountOwnershipStore.IsSameExecutablePath(executable, owned.ExecutablePath)) continue;
                    process.Kill();
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The recorded process exited while its identity was being inspected.
                }
            }
        }
    }
}
