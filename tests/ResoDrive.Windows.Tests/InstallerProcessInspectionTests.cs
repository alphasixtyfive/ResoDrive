using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ResoDrive.Windows.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstallerProcessIsolationGroup
{
    public const string Name = "Installer process isolation";
}

[Collection(InstallerProcessIsolationGroup.Name)]
public sealed partial class InstallerProcessInspectionTests
{
    [Fact]
    public async Task QueryableUnrelatedProcessWithoutSynchronizationRightsDoesNotBlockMigration()
    {
        using var fixture = new Fixture();
        using var unrelated = fixture.StartProcess(Path.Combine("..", "other-install", "resodrive.exe"));
        using var identity = WindowsIdentity.GetCurrent();
        SetProcessAccess(unrelated, identity.User!.Value, 0x1000);
        using var reader = CreateReaderWithoutDebugPrivilege();
        await WindowsIdentity.RunImpersonatedAsync(reader, async () =>
        {
            using var sync = OpenProcess(0x00101000, false, unrelated.Id);
            Assert.True(sync.IsInvalid); // This is the old account filter's false failure.
            Assert.Equal(5, Marshal.GetLastPInvokeError());
            using var query = OpenProcess(0x1000, false, unrelated.Id);
            Assert.False(query.IsInvalid);

            await InstallerProcessInspection.WaitForOtherAccountProcessesExitAsync(fixture.Binaries,
                CancellationToken.None, currentUserOnly: true);
            await InstallerProcessInspection.VerifyStoppedAsync(fixture.Binaries,
                CancellationToken.None, currentUserOnly: true);
            Assert.False(unrelated.HasExited);
        });
    }

    [Fact]
    public async Task QueryableMatchedProcessWithoutTerminationRightsStillBlocksAndKeepsData()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        var marker = Path.Combine(fixture.Data, "cache-marker");
        await File.WriteAllTextAsync(marker, "unsent changes");
        using var identity = WindowsIdentity.GetCurrent();
        SetProcessAccess(ui, identity.User!.Value, 0x1000);
        using var reader = CreateReaderWithoutDebugPrivilege();
        await WindowsIdentity.RunImpersonatedAsync(reader, async () =>
        {
            // Both account checks can read this matching process without requesting
            // synchronization or termination. A live process still blocks migration.
            await InstallerProcessInspection.WaitForOtherAccountProcessesExitAsync(fixture.Binaries,
                CancellationToken.None, currentUserOnly: true);
            await InstallerProcessInspection.WaitForOtherAccountProcessesExitAsync(fixture.Binaries,
                CancellationToken.None);
            var busy = await Assert.ThrowsAsync<IOException>(() => InstallerProcessInspection.VerifyStoppedAsync(
                fixture.Binaries, CancellationToken.None, currentUserOnly: true));
            Assert.Contains("background work is still running", busy.Message, StringComparison.Ordinal);

            var failure = await Assert.ThrowsAsync<IOException>(() => InstallerProcessInspection.StopVerifiedUiAsync(
                fixture.Binaries, null, CancellationToken.None, currentUserOnly: true));
            Assert.Contains("Windows error 5", failure.InnerException!.Message, StringComparison.Ordinal);
            Assert.Contains("matched installation", failure.InnerException.Message, StringComparison.Ordinal);
            Assert.False(ui.HasExited);
            Assert.Equal("unsent changes", await File.ReadAllTextAsync(marker));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessWhoseMinimumIdentityAccessIsDeniedStillBlocksWithItsNativeError(bool currentUserOnly)
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        using var identity = WindowsIdentity.GetCurrent();
        SetProcessAccess(ui, identity.User!.Value, 0x100000);
        using var reader = CreateReaderWithoutDebugPrivilege();
        await WindowsIdentity.RunImpersonatedAsync(reader, async () =>
        {
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                InstallerProcessInspection.WaitForOtherAccountProcessesExitAsync(
                    fixture.Binaries, CancellationToken.None, currentUserOnly: currentUserOnly));
            Assert.Contains("could not verify which Windows account", failure.Message, StringComparison.Ordinal);
            Assert.Contains("OpenProcess(query)", failure.InnerException!.Message, StringComparison.Ordinal);
            Assert.Equal(5, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
            Assert.False(ui.HasExited);
        });
    }

    [Fact]
    public async Task MigrationWaitsForNaturallyExitingProcessWhoseIdentityQueryIsDenied()
    {
        using var fixture = new Fixture();
        using var helper = fixture.StartProcess("resodrive.exe");
        var marker = Path.Combine(fixture.Data, "cache-marker");
        await File.WriteAllTextAsync(marker, "unsent changes");
        using var identity = WindowsIdentity.GetCurrent();
        SetProcessAccess(helper, identity.User!.Value, 0x100000);
        using var reader = CreateReaderWithoutDebugPrivilege();
        var inspection = WindowsIdentity.RunImpersonatedAsync(reader, async () =>
        {
            using var query = OpenProcess(0x1000, false, helper.Id);
            Assert.True(query.IsInvalid);
            Assert.Equal(5, Marshal.GetLastPInvokeError());
            await InstallerProcessInspection.WaitForOtherAccountProcessesExitAsync(
                fixture.Binaries, CancellationToken.None, currentUserOnly: true);
        });
        await Task.Delay(250);
        Assert.False(inspection.IsCompleted);
        Assert.False(helper.HasExited);
        await helper.StandardInput.WriteLineAsync("exit");
        await helper.StandardInput.FlushAsync();
        await inspection.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(helper.HasExited);
        Assert.Equal(0, helper.ExitCode);
        Assert.Equal("unsent changes", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task MigrationStillBlocksWhenIdentityAndExitWaitAccessAreBothDenied()
    {
        using var fixture = new Fixture();
        using var helper = fixture.StartProcess("resodrive.exe");
        using var identity = WindowsIdentity.GetCurrent();
        SetProcessAccess(helper, identity.User!.Value, 0x20000); // READ_CONTROL only
        using var reader = CreateReaderWithoutDebugPrivilege();
        await WindowsIdentity.RunImpersonatedAsync(reader, async () =>
        {
            using var wait = OpenProcess(0x100000, false, helper.Id);
            Assert.True(wait.IsInvalid);
            Assert.Equal(5, Marshal.GetLastPInvokeError());
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                InstallerProcessInspection.WaitForOtherAccountProcessesExitAsync(
                    fixture.Binaries, CancellationToken.None, currentUserOnly: true));
            Assert.Equal(5, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
        });
        Assert.False(helper.HasExited);
    }

    [Fact]
    public async Task MigrationCancellationDuringUnreadableProcessWaitPreservesTheProcessAndData()
    {
        using var fixture = new Fixture();
        using var helper = fixture.StartProcess("resodrive.exe");
        var marker = Path.Combine(fixture.Data, "cache-marker");
        await File.WriteAllTextAsync(marker, "unsent changes");
        using var identity = WindowsIdentity.GetCurrent();
        SetProcessAccess(helper, identity.User!.Value, 0x100000);
        using var reader = CreateReaderWithoutDebugPrivilege();
        using var canceled = new CancellationTokenSource();
        await WindowsIdentity.RunImpersonatedAsync(reader, async () =>
        {
            canceled.CancelAfter(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                InstallerProcessInspection.WaitForOtherAccountProcessesExitAsync(
                    fixture.Binaries, canceled.Token, currentUserOnly: true));
        });
        Assert.False(helper.HasExited);
        Assert.Equal("unsent changes", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task AbsentHostSkipsThePipeConnectionTimeout()
    {
        using var fixture = new Fixture();
        Assert.False(InstallerProcessInspection.IsHostMutexPresent());
        var response = await new InstallationPreparationService.Runtime()
            .ShutdownAsync(fixture.Binaries, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(response.Succeeded);
        Assert.Equal("host.unavailable", response.ErrorCode);
    }

    [Fact]
    public async Task AbsentHostStillHonorsCallerCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InstallationPreparationService.Runtime().ShutdownAsync(fixture.Binaries, cancellation.Token));
    }

    [Fact]
    public async Task PresentHostMutexStillRequiresTheShutdownHandshake()
    {
        using var fixture = new Fixture();
        using var mutex = new Mutex(false, $"Local\\{HostProtocol.GetPipeName(new ApplicationPaths())}", out var created);
        Assert.True(created);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InstallationPreparationService.Runtime().ShutdownAsync(fixture.Binaries, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RealShutdownUploadRejectionLeavesTheVerifiedUiAndCacheAlone()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        var paths = new ApplicationPaths();
        var cacheMarker = Path.Combine(paths.Root, "cache-marker");
        await File.WriteAllTextAsync(cacheMarker, "unsent changes");
        var pipeName = HostProtocol.GetPipeName(paths);
        using var mutex = new Mutex(false, $"Local\\{pipeName}", out var created);
        Assert.True(created);
        using var server = CurrentUserPipe.CreateServer(pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var preparation = new InstallationPreparationService().PrepareAsync(fixture.Binaries,
            cancellationToken: cancellation.Token);
        await server.WaitForConnectionAsync(cancellation.Token);
        var request = await HostProtocol.ReadAsync<HostRequest>(server, cancellation.Token);
        Assert.NotNull(request);
        Assert.Equal("shutdown", request.Command);
        Assert.True(request.Confirmed);
        Assert.Equal(fixture.Binaries, request.ExpectedHostBaseDirectory);
        await HostProtocol.WriteAsync(server,
            new HostResponse(false, "mount.pending_uploads", "Uploads are still pending."), cancellation.Token);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation);
        Assert.Contains("Uploads are still pending", error.Message, StringComparison.Ordinal);
        Assert.False(ui.HasExited);
        Assert.Equal("unsent changes", await File.ReadAllTextAsync(cacheMarker));
    }

    [Fact]
    public async Task HostStartingAfterTheFastProbeBlocksOrphanClosure()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        var paths = new ApplicationPaths();
        var cacheMarker = Path.Combine(paths.Root, "cache-marker");
        await File.WriteAllTextAsync(cacheMarker, "unsent changes");
        var runtime = new InstallationPreparationService.Runtime();
        var response = await runtime.ShutdownAsync(fixture.Binaries, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("host.unavailable", response.ErrorCode);

        using var mutex = new Mutex(false, $"Local\\{HostProtocol.GetPipeName(paths)}", out var created);
        Assert.True(created);
        await Assert.ThrowsAsync<IOException>(() => runtime.StopOrphanedUiAsync(fixture.Binaries, CancellationToken.None));
        Assert.False(ui.HasExited);
        Assert.Equal("unsent changes", await File.ReadAllTextAsync(cacheMarker));
    }

    [Fact]
    public async Task VerifiedIsolatedUiCanBeClosed()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        Assert.False(ui.HasExited);

        await InstallerProcessInspection.StopVerifiedUiAsync(fixture.Binaries, null, CancellationToken.None);

        Assert.True(ui.HasExited);
    }

    [Fact]
    public async Task OtherAccountWaitLeavesSameAccountUiRunning()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");

        await InstallerProcessInspection.WaitForOtherAccountProcessesExitAsync(
            fixture.Binaries, CancellationToken.None);

        Assert.False(ui.HasExited);
    }

    [Fact]
    public async Task ExistingHostMutexBlocksUiClosure()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        await Task.Run(() =>
        {
            using var mutex = new Mutex(true, $"Local\\{HostProtocol.GetPipeName(new ApplicationPaths())}", out var created);
            Assert.True(created);
            try
            {
                Assert.Throws<IOException>(() =>
                    InstallerProcessInspection.StopOrphanedUiAsync(fixture.Binaries, CancellationToken.None)
                        .GetAwaiter().GetResult());
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        });
        Assert.False(ui.HasExited);
    }

    [Fact]
    public async Task LivePrivateRcloneBlocksUiClosure()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        using var rclone = fixture.StartProcess(Path.Combine("components", "rclone", "rclone.exe"));

        await Assert.ThrowsAsync<IOException>(() =>
            InstallerProcessInspection.StopOrphanedUiAsync(fixture.Binaries, CancellationToken.None));

        Assert.False(ui.HasExited);
        Assert.False(rclone.HasExited);
    }

    [Fact]
    public async Task OrphanedManagedRcloneAtAnotherRootStillBlocksUiClosure()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        using var orphan = fixture.StartProcess(Path.Combine("..", "other-data",
            "components", "rclone", "rclone.exe"));

        await Assert.ThrowsAsync<IOException>(() =>
            InstallerProcessInspection.StopOrphanedUiAsync(fixture.Binaries, CancellationToken.None));

        Assert.False(ui.HasExited);
        Assert.False(orphan.HasExited);
    }

    [Fact]
    public void OnlyMatchingAuthenticatedLiveHostEvidenceCanExemptAnotherRoot()
    {
        var parentDirectory = Path.Combine(Path.GetTempPath(), "other-installation");
        var childStart = DateTime.UtcNow;
        var parentStart = childStart.AddSeconds(-10);
        var accepted = new HostResponse(true, HostBaseDirectory: parentDirectory, HostProcessId: 42);

        Assert.True(InstallerProcessInspection.MatchesAuthenticatedHostEvidence(
            42, childStart, 42, parentStart, parentDirectory, accepted));
        Assert.False(InstallerProcessInspection.MatchesAuthenticatedHostEvidence(
            43, childStart, 42, parentStart, parentDirectory, accepted));
        Assert.False(InstallerProcessInspection.MatchesAuthenticatedHostEvidence(
            42, childStart, 42, childStart.AddSeconds(1), parentDirectory, accepted));
        Assert.False(InstallerProcessInspection.MatchesAuthenticatedHostEvidence(
            42, childStart, 42, parentStart, parentDirectory,
            accepted with { HostProcessId = 43 }));
        Assert.False(InstallerProcessInspection.MatchesAuthenticatedHostEvidence(
            42, childStart, 42, parentStart, parentDirectory,
            accepted with { HostBaseDirectory = Path.Combine(Path.GetTempPath(), "wrong-installation") }));
    }

    [Fact]
    public void HostRoleAndRcloneConfigMustBeExactAndUnambiguous()
    {
        var root = Path.Combine(Path.GetTempPath(), "resodrive-lineage-test");
        var host = Path.Combine(root, "resodrive.exe");
        var rclone = Path.Combine(root, "components", "rclone", "rclone.exe");
        var config = Path.Combine(root, "rclone.conf");
        var otherConfig = Path.Combine(root, "other.conf");

        Assert.True(InstallerProcessInspection.IsHostCommandLine($"\"{host}\" --host", host));
        Assert.False(InstallerProcessInspection.IsHostCommandLine($"\"{host}\" --show", host));
        Assert.True(InstallerProcessInspection.HasExpectedConfig(
            $"\"{rclone}\" copy source remote: --config \"{config}\"", rclone, config));
        Assert.False(InstallerProcessInspection.HasExpectedConfig(
            $"\"{rclone}\" copy source remote: --config \"{config}\" --config \"{otherConfig}\"",
            rclone, config));
        Assert.False(InstallerProcessInspection.HasExpectedConfig(
            $"\"{rclone}\" copy source remote:", rclone, config));
    }

    [Fact]
    public async Task UnknownCommandLineBlocksUiClosure()
    {
        using var fixture = new Fixture();
        using var unknown = fixture.StartProcess("resodrive.exe", "/k");

        await Assert.ThrowsAsync<IOException>(() =>
            InstallerProcessInspection.StopOrphanedUiAsync(fixture.Binaries, CancellationToken.None));

        Assert.False(unknown.HasExited);
    }

    [Fact]
    public async Task HostRoleWithoutMutexIsNeverClosedAsUi()
    {
        using var fixture = new Fixture();
        using var host = fixture.StartProcess("resodrive.exe", "/k --host");

        await Assert.ThrowsAsync<IOException>(() =>
            InstallerProcessInspection.StopVerifiedUiAsync(fixture.Binaries, null, CancellationToken.None));

        Assert.False(host.HasExited);
    }

    [Fact]
    public async Task UserDataMigrationStillRefusesUnverifiedHostAndActiveUploadProcess()
    {
        using var fixture = new Fixture();
        using var host = fixture.StartProcess("resodrive.exe", "/k --host");
        await Assert.ThrowsAsync<IOException>(() =>
            InstallerProcessInspection.StopVerifiedUiAsync(fixture.Binaries, null, CancellationToken.None, currentUserOnly: true));
        Assert.False(host.HasExited);
        using var rclone = fixture.StartProcess(Path.Combine("components", "rclone", "rclone.exe"));
        await Assert.ThrowsAsync<IOException>(() =>
            InstallerProcessInspection.StopOrphanedUiAsync(fixture.Binaries, CancellationToken.None, currentUserOnly: true));
        Assert.False(host.HasExited);
        Assert.False(rclone.HasExited);
    }

    [Fact]
    public async Task CorruptOwnershipRecordBlocksOrphanRecovery()
    {
        using var fixture = new Fixture();
        using var ui = fixture.StartProcess("resodrive.exe");
        File.WriteAllText(new ApplicationPaths().OwnershipFile, "not json");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            InstallerProcessInspection.StopOrphanedUiAsync(fixture.Binaries, CancellationToken.None));

        Assert.Contains("recorded mount processes", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ui.HasExited);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<Process> _processes = [];
        private readonly string? _oldRoot = Environment.GetEnvironmentVariable("RDRIVE_DATA_DIR");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "resodrive-installer-tests-" + Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Binaries = Path.Combine(_root, "binaries");
            Data = Path.Combine(_root, "data");
            Directory.CreateDirectory(Binaries);
            Directory.CreateDirectory(Data);
            Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", Data);
        }

        internal string Binaries { get; }
        internal string Data { get; }

        internal Process StartProcess(string relativePath, string? arguments = null)
        {
            var executable = Path.Combine(relativePath.Equals("resodrive.exe", StringComparison.OrdinalIgnoreCase) ?
                Binaries : Data, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), executable);
            var startInfo = new ProcessStartInfo(executable, arguments ?? string.Empty)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Fixture process failed to start.");
            _processes.Add(process);
            Thread.Sleep(300);
            Assert.False(process.HasExited);
            return process;
        }

        public void Dispose()
        {
            foreach (var process in _processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", _oldRoot);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var fullRoot = Path.GetFullPath(_root);
            if (!fullRoot.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).StartsWith("resodrive-installer-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a test directory outside the expected temporary root.");
            for (var attempt = 0; attempt < 20 && Directory.Exists(fullRoot); attempt++)
            {
                try { Directory.Delete(fullRoot, recursive: true); }
                catch (Exception exception) when (attempt < 19 && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(250); // Windows may briefly retain the terminated test image.
                }
            }
        }
    }

    private static void SetProcessAccess(Process child, string userSid, uint access)
    {
        // Only this disposable child's DACL changes. Its original start handle
        // retains cleanup rights even after the query-only ACL is applied.
        Assert.True(ConvertStringSecurityDescriptorToSecurityDescriptorW(
            $"D:P(A;;0x{access:X};;;{userSid})", 1, out var descriptor, out _));
        try { Assert.True(SetKernelObjectSecurity(child.SafeHandle, 4, descriptor)); }
        finally { _ = LocalFree(descriptor); }
    }

    private static SafeAccessTokenHandle CreateReaderWithoutDebugPrivilege()
    {
        // Hosted runners can have SeDebugPrivilege enabled, bypassing process
        // DACLs. Restrict a private impersonation token, never the runner's token.
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        Assert.True(DuplicateTokenEx(identity.AccessToken, 0x002C, IntPtr.Zero, 2, 2, out var reader));
        try
        {
            Assert.True(LookupPrivilegeValueW(null, "SeDebugPrivilege", out var privilege));
            var privileges = new TokenPrivileges { Count = 1, Privilege = privilege, Attributes = 0 };
            Assert.True(AdjustTokenPrivileges(reader, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero));
            var error = Marshal.GetLastPInvokeError();
            Assert.True(error is 0 or 1300); // ERROR_NOT_ALL_ASSIGNED: absent is already disabled.
            return reader;
        }
        catch { reader.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Privilege;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(SafeAccessTokenHandle source, uint access, IntPtr attributes,
        uint impersonationLevel, uint tokenType, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out Luid privilege);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges privileges, uint length,
        IntPtr previous, IntPtr returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string descriptor, uint revision, out IntPtr securityDescriptor, out uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(SafeProcessHandle handle, uint information, IntPtr descriptor);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
