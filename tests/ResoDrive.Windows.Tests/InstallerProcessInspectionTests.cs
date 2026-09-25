using System.Diagnostics;

namespace ResoDrive.Windows.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstallerProcessIsolationGroup
{
    public const string Name = "Installer process isolation";
}

[Collection(InstallerProcessIsolationGroup.Name)]
public sealed class InstallerProcessInspectionTests
{
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
}
