using System.Diagnostics;
using System.Net;

namespace ResoDrive.Windows.Tests;

/// <summary>Isolated child processes and real temporary files; Nextcloud HTTP is simulated.</summary>
public sealed class RemoteWipeOrphanProcessTests : IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(
        Path.GetTempPath(), "resodrive-wipe-orphan", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths _paths;

    public RemoteWipeOrphanProcessTests()
    {
        _paths = new ApplicationPaths(Path.Combine(_fixtureRoot, "data"));
        _paths.EnsureCreated();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrecordedPrivateRuntimeBlocksCleanupAndAcknowledgementUntilItExits(bool shortDataRoot)
    {
        var folder = _paths.ManagedSyncFolder(Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var managedFile = Path.Combine(folder, "downloaded-document.txt");
        File.WriteAllText(managedFile, "sensitive fixture");
        File.WriteAllText(_paths.ConfigFile, "disposable configuration");
        var registration = new RemoteWipeRegistration(Guid.NewGuid(),
            "https://cloud.example/nextcloud/remote.php/dav/files/test",
            "https://cloud.example/nextcloud", "test", "disposable-orphan-test-token");
        var acknowledgements = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            Assert.False(File.Exists(managedFile));
            Assert.False(File.Exists(_paths.ConfigFile));
            acknowledgements++;
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        using var orphan = await StartSleeperAsync(_paths.RcloneExecutable);
        try
        {
            // NTFS can expose a legitimate custom root through its 8.3 alias. On
            // volumes without short names, cmd returns the unchanged ordinary path.
            var recoveryPaths = shortDataRoot
                ? new ApplicationPaths(await GetShortPathAsync(_paths.Root))
                : _paths;
            var coordinator = new RemoteWipeCoordinator(recoveryPaths, client);
            await coordinator.AcceptAsync(registration, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => coordinator.ResumeAsync(
                token => RemoteWipeWorkStopper.StopOwnedMountsAsync(recoveryPaths, token), CancellationToken.None));

            Assert.False(orphan.HasExited);
            Assert.Equal("sensitive fixture", File.ReadAllText(managedFile));
            Assert.True(File.Exists(_paths.ConfigFile));
            Assert.Equal(0, acknowledgements);
            Assert.Equal(RemoteWipePhase.Requested, new RemoteWipeStateStore(_paths).Read()!.Phase);

            // The blocker never terminates an unverified child. Once it exits, recovery
            // from a new coordinator can clean files and acknowledge the same request.
            await StopSleeperAsync(orphan);
            await new RemoteWipeCoordinator(recoveryPaths, client).ResumeAsync(
                token => RemoteWipeWorkStopper.StopOwnedMountsAsync(recoveryPaths, token), CancellationToken.None);
            Assert.Equal(1, acknowledgements);
            Assert.Equal(RemoteWipePhase.Completed, new RemoteWipeStateStore(_paths).Read()!.Phase);
        }
        finally
        {
            await StopSleeperAsync(orphan);
        }
    }

    [Fact]
    public async Task SameNamedRuntimeFromAnotherFolderIsPreservedAndDoesNotBlockCleanup()
    {
        var unrelatedExecutable = Path.Combine(_fixtureRoot, "another-installation", "rclone.exe");
        using var unrelated = await StartSleeperAsync(unrelatedExecutable);
        try
        {
            await RemoteWipeWorkStopper.StopOwnedMountsAsync(_paths, CancellationToken.None);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            await StopSleeperAsync(unrelated);
        }
    }

    private static async Task<string> GetShortPathAsync(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            // cmd's quoting differs from the C-runtime escaping used by ArgumentList.
            // This path is generated entirely inside the disposable fixture.
            Arguments = $"/d /q /c for %I in (\"{path}\") do @echo %~sI",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        var shortPath = (await process.StandardOutput.ReadToEndAsync()).Trim();
        Assert.True(Directory.Exists(shortPath), $"Short-path fixture result was '{shortPath}'.");
        return shortPath;
    }

    private static async Task<Process> StartSleeperAsync(string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/q", "/c", "echo ready&set /p resodriveFixtureExit=" })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            process.Refresh();
            Assert.Equal("rclone", process.ProcessName, ignoreCase: true);
            Assert.True(MountOwnershipStore.IsSameExecutablePath(executable, process.MainModule!.FileName));
            return process;
        }
        catch
        {
            try { await StopSleeperAsync(process); }
            finally { process.Dispose(); }
            throw;
        }
    }

    private static async Task StopSleeperAsync(Process process)
    {
        if (process.HasExited) return;
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Only this test's own disposable child can reach this cleanup fallback.
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixtureRoot)) Directory.Delete(_fixtureRoot, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
