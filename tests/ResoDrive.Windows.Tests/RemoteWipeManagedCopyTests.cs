using System.Diagnostics;
using System.Net;
using ResoDrive.Core.Settings;

namespace ResoDrive.Windows.Tests;

/// <summary>Real disposable files and DPAPI state; all Nextcloud HTTP is simulated.</summary>
public sealed class RemoteWipeManagedCopyTests : IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(
        Path.GetTempPath(), "resodrive-wipe-managed-copy", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths _paths;
    private readonly RemoteWipeRegistration _registration = new(Guid.NewGuid(),
        "https://cloud.example/nextcloud/remote.php/dav/files/test",
        "https://cloud.example/nextcloud", "test", "disposable-managed-copy-token");

    public RemoteWipeManagedCopyTests()
    {
        _paths = new ApplicationPaths(Path.Combine(_fixtureRoot, "data"));
        _paths.EnsureCreated();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedJobsAndMissingSettingsDoNotExcludeManagedCopies(bool deleteSettings)
    {
        var firstCopy = SeedManagedCopy("first download");
        var secondCopy = SeedManagedCopy("download from a deleted job");
        var outside = SeedOutsideFile();
        using (var settings = new AtomicSettingsStore(_paths))
            Assert.True((await settings.SaveAsync(new ManagerSettings(), 0)).Succeeded);
        if (deleteSettings) File.Delete(_paths.SettingsFile);

        var stopped = false;
        var acknowledgements = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.True(stopped);
            Assert.False(File.Exists(firstCopy));
            Assert.False(File.Exists(secondCopy));
            AssertNoContents(_paths.ManagedSyncRoot);
            Assert.Equal("outside managed data", File.ReadAllText(outside));
            Assert.EndsWith("/nextcloud/index.php/core/wipe/success",
                request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            acknowledgements++;
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        await new RemoteWipeCoordinator(_paths, client).AcceptAsync(_registration, CancellationToken.None);

        // A new coordinator must discover every managed copy without loading old jobs.
        await new RemoteWipeCoordinator(_paths, client).ResumeAsync(
            _ => { stopped = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(1, acknowledgements);
        var state = new RemoteWipeStateStore(_paths).Read()!;
        Assert.Equal(RemoteWipePhase.Completed, state.Phase);
        Assert.Null(state.Registration);
        Assert.True(state.ServerAcknowledged);
    }

    [Fact]
    public async Task LockedManagedFileSurvivesPendingWipeAndIsRemovedAfterRestart()
    {
        var copy = SeedManagedCopy("locked downloaded document");
        var outside = SeedOutsideFile();
        var acknowledgements = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            Assert.False(File.Exists(copy));
            Assert.Equal("outside managed data", File.ReadAllText(outside));
            acknowledgements++;
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        var coordinator = new RemoteWipeCoordinator(_paths, client);
        await coordinator.AcceptAsync(_registration, CancellationToken.None);

        using (var locked = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => coordinator.ResumeAsync(
                _ => Task.CompletedTask, CancellationToken.None));
            Assert.True(File.Exists(copy));
            AssertRequestedWithoutAcknowledgement(acknowledgements);
        }

        await new RemoteWipeCoordinator(_paths, client).ResumeAsync(
            _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, acknowledgements);
        Assert.Equal(RemoteWipePhase.Completed, new RemoteWipeStateStore(_paths).Read()!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheAndManagedCopyCleanupRemainDurableWhenEitherContainsALockedFile(bool lockCache)
    {
        var copy = SeedManagedCopy("managed bytes");
        var cached = Path.Combine(_paths.Cache, "cached-document.txt");
        File.WriteAllText(cached, "cached bytes");
        File.WriteAllText(_paths.ConfigSecretFile, "disposable credential fixture");
        var acknowledgements = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            Assert.False(File.Exists(copy));
            Assert.False(File.Exists(cached));
            acknowledgements++;
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        var coordinator = new RemoteWipeCoordinator(_paths, client);
        await coordinator.AcceptAsync(_registration, CancellationToken.None);

        using (var locked = new FileStream(lockCache ? cached : copy,
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => coordinator.ResumeAsync(
                _ => Task.CompletedTask, CancellationToken.None));
            AssertRequestedWithoutAcknowledgement(acknowledgements);
            Assert.False(File.Exists(_paths.ConfigSecretFile));
            // Failure in one storage area must not leave independent readable copies behind.
            Assert.False(File.Exists(lockCache ? copy : cached));
        }

        await new RemoteWipeCoordinator(_paths, client).ResumeAsync(
            _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, acknowledgements);
        Assert.Equal(RemoteWipePhase.Completed, new RemoteWipeStateStore(_paths).Read()!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedRootOrNestedJunctionCannotDeleteOutsideFilesOrAcknowledge(bool rootJunction)
    {
        var outside = SeedOutsideFile();
        var outsideDirectory = Path.GetDirectoryName(outside)!;
        var junction = rootJunction
            ? _paths.ManagedSyncRoot
            : Path.Combine(_paths.ManagedSyncFolder(Guid.NewGuid()), "redirected");
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        var acknowledgements = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            acknowledgements++;
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        var coordinator = new RemoteWipeCoordinator(_paths, client);

        try
        {
            await CreateJunctionAsync(junction, outsideDirectory);
            await coordinator.AcceptAsync(_registration, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => coordinator.ResumeAsync(
                _ => Task.CompletedTask, CancellationToken.None));

            Assert.Equal("outside managed data", File.ReadAllText(outside));
            Assert.True(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint));
            AssertRequestedWithoutAcknowledgement(acknowledgements);
        }
        finally
        {
            // Remove only the link; the fixture cleanup never recursively traverses it.
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task FileReplacingManagedRootBlocksAcknowledgement()
    {
        File.WriteAllText(_paths.ManagedSyncRoot, "unexpected file at the managed folder path");
        var acknowledgements = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            acknowledgements++;
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        var coordinator = new RemoteWipeCoordinator(_paths, client);
        await coordinator.AcceptAsync(_registration, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => coordinator.ResumeAsync(
            _ => Task.CompletedTask, CancellationToken.None));

        Assert.True(File.Exists(_paths.ManagedSyncRoot));
        AssertRequestedWithoutAcknowledgement(acknowledgements);
    }

    private string SeedManagedCopy(string contents)
    {
        var folder = Path.Combine(_paths.ManagedSyncFolder(Guid.NewGuid()), "nested");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "document.txt");
        File.WriteAllText(file, contents);
        return file;
    }

    private string SeedOutsideFile()
    {
        var folder = Path.Combine(_fixtureRoot, "outside");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "keep.txt");
        File.WriteAllText(file, "outside managed data");
        return file;
    }

    private void AssertRequestedWithoutAcknowledgement(int acknowledgements)
    {
        Assert.Equal(0, acknowledgements);
        var state = new RemoteWipeStateStore(_paths).Read()!;
        Assert.Equal(RemoteWipePhase.Requested, state.Phase);
        Assert.Equal(_registration, state.Registration);
        Assert.Null(state.ServerAcknowledged);
        Assert.True(new AccountDataGuard(_paths).IsBlocked);
    }

    private static void AssertNoContents(string path)
    {
        if (Directory.Exists(path)) Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }

    private static async Task CreateJunctionAsync(string junction, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", junction, target })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode);
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
