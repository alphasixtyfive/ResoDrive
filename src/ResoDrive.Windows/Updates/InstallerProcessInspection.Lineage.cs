using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ResoDrive.Windows;

internal static partial class InstallerProcessInspection
{
    private sealed record ProcessFacts(string ExecutablePath, string CommandLine, int ParentProcessId);

    // A managed rclone outside the helper's data root remains a possible orphan
    // unless a live, authenticated host at another installation demonstrably owns it.
    private static bool HasVerifiedUnrelatedLiveHost(
        Process child, string executable, ApplicationPaths activePaths,
        string installationDirectory, CancellationToken token)
    {
        try
        {
            return HasVerifiedUnrelatedLiveHostCore(child, executable, activePaths, installationDirectory, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or ManagementException or
            COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool HasVerifiedUnrelatedLiveHostCore(
        Process child, string executable, ApplicationPaths activePaths,
        string installationDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var runtimeDirectory = Path.GetDirectoryName(executable);
        var componentsDirectory = Path.GetDirectoryName(runtimeDirectory);
        var otherRoot = Path.GetDirectoryName(componentsDirectory);
        if (otherRoot is null)
            return false;
        var otherPaths = new ApplicationPaths(otherRoot);
        var targetExecutable = Path.Combine(installationDirectory, "resodrive.exe");
        // Require the exact standard layout, and never exempt this install's root.
        if (!SameExecutable(executable, otherPaths.RcloneExecutable) ||
            SameExecutable(otherPaths.RcloneExecutable, activePaths.RcloneExecutable) ||
            !HasNoReparseComponents(executable) ||
            !HasNoReparseComponents(otherPaths.ConfigFile) ||
            !HasNoReparseComponents(activePaths.RcloneExecutable) ||
            !HasNoReparseComponents(targetExecutable) ||
            (File.Exists(activePaths.RcloneExecutable) &&
                SamePhysicalFile(executable, activePaths.RcloneExecutable)))
            return false;

        using var childHandle = OpenProcess(0x1000, false, child.Id); // QUERY_LIMITED_INFORMATION
        if (childHandle.IsInvalid || !IsCurrentAccount(childHandle) ||
            child.SessionId != Process.GetCurrentProcess().SessionId)
            return false;
        var childStart = child.StartTime.ToUniversalTime();
        if (!SameExecutable(child.MainModule?.FileName, executable))
            return false;
        var childFacts = ReadProcessFacts(child.Id);
        if (childFacts is null || childFacts.ParentProcessId <= 0 ||
            !SameExecutable(childFacts.ExecutablePath, executable) ||
            !HasExpectedConfig(childFacts.CommandLine, executable, otherPaths.ConfigFile))
            return false;

        if (new RemoteWipeStateStore(otherPaths).Read() is { Phase: not RemoteWipePhase.Completed })
            return false;

        using var parent = Process.GetProcessById(childFacts.ParentProcessId);
        using var parentHandle = OpenProcess(0x1000, false, parent.Id);
        if (parentHandle.IsInvalid || !IsCurrentAccount(parentHandle) ||
            HasConfirmedExit(parent) || parent.SessionId != child.SessionId)
            return false;
        var parentStart = parent.StartTime.ToUniversalTime();
        if (parentStart > childStart)
            return false; // Parent PID was recycled after the rclone process started.
        var parentExecutable = parent.MainModule?.FileName;
        if (parentExecutable is null ||
            !string.Equals(Path.GetFileName(parentExecutable), "resodrive.exe", StringComparison.OrdinalIgnoreCase) ||
            !HasNoReparseComponents(parentExecutable) ||
            SameExecutable(parentExecutable, targetExecutable) ||
            SamePhysicalFile(parentExecutable, targetExecutable))
            return false;
        var parentFacts = ReadProcessFacts(parent.Id);
        if (parentFacts is null || !SameExecutable(parentFacts.ExecutablePath, parentExecutable) ||
            !IsHostCommandLine(parentFacts.CommandLine, parentExecutable))
            return false;

        var parentDirectory = Path.GetDirectoryName(parentExecutable);
        if (parentDirectory is null)
            return false;
        // HostClient obtains HostProcessId from the authenticated pipe's kernel
        // server PID, not from the host's JSON response.
        var response = HostClient.SendToDataRootAsync(
            new HostRequest("status", ExpectedHostBaseDirectory: parentDirectory),
            otherPaths, TimeSpan.FromSeconds(2), token).GetAwaiter().GetResult();
        return MatchesAuthenticatedHostEvidence(childFacts.ParentProcessId, childStart,
                parent.Id, parentStart, parentDirectory, response) &&
            !HasConfirmedExit(parent) && !HasConfirmedExit(child) &&
            parent.StartTime.ToUniversalTime() == parentStart &&
            child.StartTime.ToUniversalTime() == childStart;
    }

    internal static bool MatchesAuthenticatedHostEvidence(
        int reportedParentId, DateTime childStartUtc, int parentId, DateTime parentStartUtc,
        string parentDirectory, HostResponse response) =>
        reportedParentId == parentId && parentStartUtc <= childStartUtc &&
        response.Succeeded && response.HostProcessId == parentId &&
        HostProtocol.IsSameBaseDirectory(response.HostBaseDirectory, parentDirectory);

    private static ProcessFacts? ReadProcessFacts(int processId)
    {
        using var query = new ManagementObjectSearcher(
            $"SELECT ExecutablePath, CommandLine, ParentProcessId FROM Win32_Process WHERE ProcessId = {processId}");
        query.Options.Timeout = TimeSpan.FromSeconds(2);
        using var results = query.Get();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (item["ExecutablePath"] is not string path ||
                    item["CommandLine"] is not string commandLine ||
                    item["ParentProcessId"] is not uint parentId || parentId > int.MaxValue)
                    return null;
                return new ProcessFacts(path, commandLine, (int)parentId);
            }
        }
        return null;
    }

    private static bool IsCurrentAccount(SafeProcessHandle process)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (!OpenProcessToken(process, 0x0008, out var token)) // TOKEN_QUERY
            return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return current.User is { } currentSid && identity.User is { } processSid &&
                currentSid.Equals(processSid);
    }

    private static bool SameExecutable(string? first, string second) =>
        first is not null &&
        RemoteWipeWorkStopper.CanonicalExecutablePath(first).Equals(
            RemoteWipeWorkStopper.CanonicalExecutablePath(second), StringComparison.OrdinalIgnoreCase);

    internal static bool HasNoReparseComponents(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // The helper's disposable data root may not contain rclone yet.
                // Missing components cannot currently redirect to another file.
            }
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return true;
    }

    internal static bool SamePhysicalFile(string first, string second)
    {
        using var left = File.OpenHandle(first, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var right = File.OpenHandle(second, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(left, out var leftInfo) ||
            !GetFileInformationByHandle(right, out var rightInfo))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return leftInfo.VolumeSerialNumber == rightInfo.VolumeSerialNumber &&
            leftInfo.FileIndexHigh == rightInfo.FileIndexHigh &&
            leftInfo.FileIndexLow == rightInfo.FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle, out FileInformation information);

    internal static bool IsHostCommandLine(string commandLine, string executable)
    {
        var arguments = ParseCommandLine(commandLine);
        return arguments.Length == 2 &&
            SameExecutable(arguments[0], executable) &&
            arguments[1].Equals("--host", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasExpectedConfig(string commandLine, string executable, string expectedConfig)
    {
        var arguments = ParseCommandLine(commandLine);
        if (arguments.Length < 4 || !SameExecutable(arguments[0], executable))
            return false;
        var found = false;
        for (var index = 1; index < arguments.Length; index++)
        {
            string? config = null;
            if (arguments[index].Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                if (++index == arguments.Length)
                    return false;
                config = arguments[index];
            }
            else if (arguments[index].StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                config = arguments[index]["--config=".Length..];
            }
            if (config is null)
                continue;
            if (!Path.IsPathFullyQualified(config) || !SameExecutable(config, expectedConfig))
                return false;
            found = true;
        }
        return found;
    }
}
