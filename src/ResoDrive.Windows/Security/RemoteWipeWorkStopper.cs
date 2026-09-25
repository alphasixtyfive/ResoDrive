using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResoDrive.Windows;

public static partial class RemoteWipeWorkStopper
{
    /// <summary>Stops verified mounts and refuses cleanup while an unrecorded private runtime remains.</summary>
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

        // Sync children have no durable mount-ownership record. A host crash can leave
        // one alive, so do not delete files or acknowledge while it can recreate them.
        // An executable match permits blocking cleanup, never killing an unrecorded process.
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(paths.RcloneExecutable));
        try
        {
            foreach (var process in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.HasExited) continue;
                    var executable = process.MainModule?.FileName;
                    if (executable is null)
                    {
                        if (HasConfirmedExit(process)) continue;
                        throw new IOException("Remote wipe could not verify a running rclone process.");
                    }
                    if (CanonicalExecutablePath(executable).Equals(
                            CanonicalExecutablePath(paths.RcloneExecutable), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Remote wipe is waiting for a remaining private rclone process to exit.");
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    if (HasConfirmedExit(process)) continue;
                    // Even an unrelated same-name process must block this attempt if its
                    // executable cannot be inspected. A later attempt can retry safely.
                    throw new IOException("Remote wipe could not verify that the private rclone runtime has stopped.", exception);
                }
            }
        }
        finally
        {
            foreach (var process in candidates) process.Dispose();
        }
    }

    private static bool HasConfirmedExit(Process process)
    {
        try { return process.HasExited; }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return false; }
    }

    internal static unsafe string CanonicalExecutablePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var buffer = new char[32_768];
        fixed (char* destination = buffer)
        {
            var length = GetLongPathName(fullPath, destination, checked((uint)buffer.Length));
            if (length > 0 && length < buffer.Length)
                return new string(buffer, 0, checked((int)length));
            if (length >= buffer.Length)
                throw new IOException("A running rclone executable path is too long to verify.");
        }

        var error = Marshal.GetLastPInvokeError();
        // A fresh data directory need not contain rclone yet. Resolve existing parent
        // names so a legitimate 8.3 data-root alias cannot hide a matching executable.
        if (error is 2 or 3 && Path.GetDirectoryName(fullPath) is { } parent)
            return Path.Combine(CanonicalExecutablePath(parent), Path.GetFileName(fullPath));
        throw new System.ComponentModel.Win32Exception(error);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathName(string path, char* longPath, uint bufferLength);
}
