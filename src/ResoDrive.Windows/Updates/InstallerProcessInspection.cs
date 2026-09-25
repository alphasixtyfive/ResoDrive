using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ResoDrive.Windows;

/// <summary>Fails closed when an installer cannot distinguish a UI from a host or an active rclone child.</summary>
internal static partial class InstallerProcessInspection
{
    private sealed record Candidate(Process Process, SafeProcessHandle Handle, DateTime StartTimeUtc) : IDisposable
    {
        public void Dispose()
        {
            Handle.Dispose();
            Process.Dispose();
        }
    }
    private const string BusyMessage = "ResoDrive background work is still running or cannot be verified. Close ResoDrive from its tray menu, let uploads finish, and retry. Your settings and cache have been preserved.";
    private const string OtherAccountMessage = "ResoDrive is running under another Windows account. Sign in as that user, exit ResoDrive from its tray menu, and retry the installation. Your settings and cache have been preserved.";

    internal static Task StopOrphanedUiAsync(string directory, CancellationToken token) =>
        Task.Run(() => WithNoHostMutex(() =>
        {
            EnsureNoRcloneWork(directory, includeOtherManagedRuntimes: true, token);
            StopVerifiedUi(directory, hostProcessId: null, token);
            EnsureNoRcloneWork(directory, includeOtherManagedRuntimes: true, token);
            EnsureNoInstalledProcesses(directory, token);
        }), token);

    internal static Task StopVerifiedUiAsync(string directory, int? hostProcessId, CancellationToken token) =>
        Task.Run(() => StopVerifiedUi(directory, hostProcessId, token), token);

    internal static Task VerifyStoppedAsync(string directory, CancellationToken token) =>
        Task.Run(() => WithNoHostMutex(() =>
        {
            EnsureNoRcloneWork(directory, includeOtherManagedRuntimes: false, token);
            EnsureNoInstalledProcesses(directory, token);
        }), token);

    private static void WithNoHostMutex(Action action)
    {
        var paths = new ApplicationPaths();
        using var mutex = new Mutex(initiallyOwned: true,
            $"Local\\{HostProtocol.GetPipeName(paths)}", out var createdNew);
        if (!createdNew)
            throw new IOException(BusyMessage);
        try
        {
            action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    internal static bool IsHostMutexPresent()
    {
        var paths = new ApplicationPaths();
        try
        {
            if (!Mutex.TryOpenExisting($"Local\\{HostProtocol.GetPipeName(paths)}", out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void StopVerifiedUi(string directory, int? hostProcessId, CancellationToken token)
    {
        var candidates = GetInstalledProcesses(directory, token);
        try
        {
            // Verify the entire set before closing any window. A newly started host,
            // another account, or an unknown command must leave all processes alone.
            foreach (var candidate in candidates)
            {
                var process = candidate.Process;
                token.ThrowIfCancellationRequested();
                if (process.Id == hostProcessId || HasConfirmedExit(process))
                    continue;
                VerifyUiCandidate(candidate, directory);
            }

            foreach (var candidate in candidates)
            {
                var process = candidate.Process;
                token.ThrowIfCancellationRequested();
                if (process.Id == hostProcessId || HasConfirmedExit(process))
                    continue;
                VerifyUiCandidate(candidate, directory);
                try
                {
                    if (process.HasExited) continue;
                    // Terminate the handle opened during inspection. Process.Kill would
                    // reopen by PID and could target a different process after PID reuse.
                    if (!TerminateProcess(candidate.Handle, -1))
                    {
                        if (HasConfirmedExit(process)) continue;
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                    while (!process.WaitForExit(200))
                        token.ThrowIfCancellationRequested();
                }
                catch (InvalidOperationException) when (HasConfirmedExit(process))
                {
                    // The UI exited after inspection.
                }
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private static void VerifyUiCandidate(Candidate candidate, string directory)
    {
        var process = candidate.Process;
        VerifySameAccount(candidate);
        if (!IsVerifiedUi(process) || HasConfirmedExit(process))
            throw new IOException(BusyMessage);
        try
        {
            if (process.StartTime.ToUniversalTime() != candidate.StartTimeUtc ||
                !Path.GetFullPath(process.MainModule?.FileName ?? string.Empty).Equals(
                    Path.GetFullPath(Path.Combine(directory, "resodrive.exe")), StringComparison.OrdinalIgnoreCase))
                throw new IOException(BusyMessage);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new IOException(BusyMessage, exception);
        }
    }

    private static void EnsureNoInstalledProcesses(string directory, CancellationToken token)
    {
        var candidates = GetInstalledProcesses(directory, token);
        try
        {
            foreach (var candidate in candidates)
            {
                if (!HasConfirmedExit(candidate.Process))
                    throw new IOException(BusyMessage);
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private static List<Candidate> GetInstalledProcesses(string directory, CancellationToken token)
    {
        var executable = Path.GetFullPath(Path.Combine(directory, "resodrive.exe"));
        var processes = Process.GetProcessesByName("resodrive");
        var matches = new List<Candidate>();
        try
        {
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                if (process.Id == Environment.ProcessId || HasConfirmedExit(process))
                    continue;
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (InvalidOperationException) when (HasConfirmedExit(process)) { continue; }
                catch (Win32Exception exception)
                {
                    // An opaque same-name process may be a different-account installed host.
                    throw new IOException(BusyMessage, exception);
                }
                if (path is null)
                    throw new IOException(BusyMessage);
                if (!Path.GetFullPath(path).Equals(executable, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (process.SessionId != Process.GetCurrentProcess().SessionId)
                    throw new IOException("ResoDrive is running in another Windows session. Sign out of that session and retry the installation.");
                // Keep this process object alive until the decision and termination.
                // Windows cannot recycle its PID while this kernel handle remains open.
                var handle = OpenProcess(0x00101001, false, process.Id); // QUERY_LIMITED | TERMINATE | SYNCHRONIZE
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new IOException(BusyMessage);
                }
                try { matches.Add(new Candidate(process, handle, process.StartTime.ToUniversalTime())); }
                catch { handle.Dispose(); throw; }
            }
            return matches;
        }
        catch
        {
            foreach (var candidate in matches) candidate.Dispose();
            throw;
        }
        finally
        {
            foreach (var process in processes)
                if (!matches.Any(candidate => ReferenceEquals(candidate.Process, process))) process.Dispose();
        }
    }

    private static void VerifySameAccount(Candidate candidate)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (!OpenProcessToken(candidate.Handle, 0x0008, out var token)) // TOKEN_QUERY
            throw new IOException(OtherAccountMessage);
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        if (current.User is null || owner.User is null || !current.User.Equals(owner.User))
            throw new IOException(OtherAccountMessage);
    }

    private static bool IsVerifiedUi(Process process)
    {
        string? commandLine = null;
        try
        {
            using var query = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            query.Options.Timeout = TimeSpan.FromSeconds(5);
            using var results = query.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    commandLine = item["CommandLine"] as string;
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            throw new IOException(BusyMessage, exception);
        }
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            if (HasConfirmedExit(process)) return false;
            throw new IOException(BusyMessage);
        }

        var arguments = ParseCommandLine(commandLine);
        if (arguments.Length is < 1 or > 2 ||
            !Path.IsPathFullyQualified(arguments[0]) ||
            !Path.GetFullPath(arguments[0]).Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException(BusyMessage);
        return arguments.Length == 1 || arguments[1].Equals("--background", StringComparison.OrdinalIgnoreCase) ||
            arguments[1].Equals("--show", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero)
            throw new IOException(BusyMessage, new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
                arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? string.Empty;
            return arguments;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static void EnsureNoRcloneWork(string installationDirectory, bool includeOtherManagedRuntimes,
        CancellationToken token)
    {
        var paths = new ApplicationPaths();
        using (var ownership = new MountOwnershipStore(paths))
        {
            var records = ownership.LoadForRemoteWipeAsync(token).GetAwaiter().GetResult();
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                Process process;
                try { process = Process.GetProcessById(record.ProcessId); }
                catch (ArgumentException) { continue; }
                using (process)
                {
                    if (HasConfirmedExit(process)) continue;
                    try
                    {
                        if (Math.Abs((process.StartTime.ToUniversalTime() - record.StartTimeUtc).TotalSeconds) < 1 &&
                            MountOwnershipStore.IsSameExecutablePath(process.MainModule?.FileName, record.ExecutablePath))
                            throw new IOException(BusyMessage);
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        if (!HasConfirmedExit(process)) throw new IOException(BusyMessage, exception);
                    }
                }
            }
        }

        foreach (var process in Process.GetProcessesByName("rclone"))
        {
            using (process)
            {
                token.ThrowIfCancellationRequested();
                if (HasConfirmedExit(process)) continue;
                try
                {
                    var path = process.MainModule?.FileName ?? throw new IOException(BusyMessage);
                    var canonical = RemoteWipeWorkStopper.CanonicalExecutablePath(path);
                    var configured = RemoteWipeWorkStopper.CanonicalExecutablePath(paths.RcloneExecutable);
                    var runtimeDirectory = Path.GetDirectoryName(canonical);
                    var componentsDirectory = Path.GetDirectoryName(runtimeDirectory);
                    var managedRuntime = string.Equals(Path.GetFileName(canonical), "rclone.exe", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Path.GetFileName(runtimeDirectory), "rclone", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Path.GetFileName(componentsDirectory), "components", StringComparison.OrdinalIgnoreCase);
                    if (canonical.Equals(configured, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(BusyMessage);
                    if (includeOtherManagedRuntimes && managedRuntime &&
                        !HasVerifiedUnrelatedLiveHost(process, canonical, paths, installationDirectory, token))
                        throw new IOException(BusyMessage);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    if (!HasConfirmedExit(process)) throw new IOException(BusyMessage, exception);
                }
            }
        }
    }

    private static bool HasConfirmedExit(Process process)
    {
        try { return process.HasExited; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return false; }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, int exitCode);

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr LocalFree(IntPtr memory);
}
