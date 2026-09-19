using System.Net;
using ResoDrive.Core.Settings;

namespace ResoDrive.Windows.Tests;

public sealed class RemoteWipeRecoveryTests : IDisposable
{
    private readonly ApplicationPaths _paths = new(Path.Combine(Path.GetTempPath(), "resodrive-wipe-recovery", Guid.NewGuid().ToString("N")));
    private static RemoteWipeRegistration Registration => new(Guid.NewGuid(),
        "https://cloud.example/nextcloud/remote.php/dav/files/test", "https://cloud.example/nextcloud", "test", "wipe-test-token");

    public RemoteWipeRecoveryTests() => _paths.EnsureCreated();

    [Fact]
    public async Task AcceptedWipeSurvivesRestartAndAcknowledgesOnlyAfterCleanup()
    {
        SeedData();
        using var staleSettings = new AtomicSettingsStore(_paths);
        var stopped = false;
        var acknowledgements = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.True(stopped);
            Assert.False(File.Exists(_paths.ConfigFile));
            Assert.Empty(Directory.EnumerateFileSystemEntries(_paths.Cache));
            Assert.False(File.Exists(_paths.RemoteWipeFile));
            Assert.EndsWith("/nextcloud/index.php/core/wipe/success", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            acknowledgements++;
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        await new RemoteWipeCoordinator(_paths, client).AcceptAsync(Registration, CancellationToken.None);
        Assert.DoesNotContain("wipe-test-token", File.ReadAllText(_paths.RemoteWipeStateFile), StringComparison.Ordinal);

        // A fresh coordinator has no memory of the first process: all recovery comes from disk.
        await new RemoteWipeCoordinator(_paths, client).ResumeAsync(_ => { stopped = true; return Task.CompletedTask; }, CancellationToken.None);
        var state = new RemoteWipeStateStore(_paths).Read();
        Assert.Equal(RemoteWipePhase.Completed, state!.Phase);
        Assert.Null(state.Registration);
        Assert.Equal(1, acknowledgements);
        Assert.True(File.Exists(_paths.ProfilesFile));
        Assert.True(File.Exists(_paths.RcloneExecutable));
        Assert.False((await staleSettings.SaveAsync(new ManagerSettings(), 0)).Succeeded);
        using var freshSettings = new AtomicSettingsStore(_paths);
        Assert.True((await freshSettings.SaveAsync(new ManagerSettings(), 0)).Succeeded);
    }

    [Fact]
    public async Task FailedAcknowledgementRetriesWithoutRepeatingDeletion()
    {
        SeedData();
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("Offline")));
        using var client = new RemoteWipeClient(http);
        var coordinator = new RemoteWipeCoordinator(_paths, client);
        await coordinator.AcceptAsync(Registration, CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.ResumeAsync(_ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(RemoteWipePhase.Cleaned, new RemoteWipeStateStore(_paths).Read()!.Phase);
        using var blockedSettings = new AtomicSettingsStore(_paths);
        Assert.False((await blockedSettings.SaveAsync(new ManagerSettings(), 0)).Succeeded);
        var sentinel = Path.Combine(_paths.Cache, "created-after-cleanup");
        File.WriteAllText(sentinel, "Do not delete again during acknowledgement retry");
        using var retryHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)));
        using var retryClient = new RemoteWipeClient(retryHttp);
        await new RemoteWipeCoordinator(_paths, retryClient).ResumeAsync(
            _ => throw new InvalidOperationException("Work must not restart during acknowledgement retry"), CancellationToken.None);
        Assert.True(File.Exists(sentinel));
        Assert.Null(new RemoteWipeStateStore(_paths).Read()!.Registration);
    }

    [Fact]
    public async Task LockedCacheFileBlocksAcknowledgementAndIsRetriedAfterClose()
    {
        SeedData();
        var acknowledgements = 0;
        using var http = new HttpClient(new Handler(_ => { acknowledgements++; return new(HttpStatusCode.OK); }));
        using var client = new RemoteWipeClient(http);
        var coordinator = new RemoteWipeCoordinator(_paths, client);
        await coordinator.AcceptAsync(Registration, CancellationToken.None);
        using (var locked = new FileStream(Path.Combine(_paths.Cache, "cached-file"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => coordinator.ResumeAsync(_ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, acknowledgements);
        Assert.Equal(RemoteWipePhase.Requested, new RemoteWipeStateStore(_paths).Read()!.Phase);
        // Independent secrets are removed even when a cached document is locked.
        Assert.False(File.Exists(_paths.ConfigSecretFile));
        await coordinator.ResumeAsync(_ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, acknowledgements);
    }

    [Fact]
    public async Task FailureToStopWorkPreservesDataAndDoesNotAcknowledge()
    {
        SeedData();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not acknowledge")));
        using var client = new RemoteWipeClient(http);
        var coordinator = new RemoteWipeCoordinator(_paths, client);
        await coordinator.AcceptAsync(Registration, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => coordinator.ResumeAsync(_ => throw new IOException("Still running"), CancellationToken.None));
        Assert.True(File.Exists(_paths.ConfigFile));
        Assert.Equal(RemoteWipePhase.Requested, new RemoteWipeStateStore(_paths).Read()!.Phase);
    }

    [Fact]
    public async Task SetupRollbackCannotRestoreRevokedCredentials()
    {
        File.WriteAllText(_paths.ConfigFile, "old secret");
        var staged = _paths.ConfigFile + ".test.setup-config";
        File.WriteAllText(staged, "new secret");
        using var transaction = new SetupFileTransaction([(staged, _paths.ConfigFile)], new AccountDataGuard(_paths));
        transaction.Apply();
        using var client = new RemoteWipeClient();
        await new RemoteWipeCoordinator(_paths, client).AcceptAsync(Registration, CancellationToken.None);
        RemoteWipeCleanup.DeleteAccountData(_paths);
        transaction.Rollback();
        Assert.False(File.Exists(_paths.ConfigFile));
        Assert.Empty(Directory.GetFiles(_paths.Root, "*.setup-backup"));
    }

    [Fact]
    public void CleanupIncludesBackupsAndStagingButPreservesUnrelatedFiles()
    {
        SeedData();
        string[] artifacts = ["settings.json.bak", "settings.pre-import-20260919.json", ".settings.json.abc.tmp",
            "rclone.conf.abc.setup-backup", "config-pass.dpapi.abc.setup-secret", "remote-wipe.dpapi.abc.setup-wipe",
            "ownership.json.bak", "sync-run-state.json.bak"];
        foreach (var name in artifacts) File.WriteAllText(Path.Combine(_paths.Root, name), "sensitive");
        var unrelated = Path.Combine(_paths.Root, "admin-readme.txt");
        File.WriteAllText(unrelated, "keep");
        RemoteWipeCleanup.DeleteAccountData(_paths);
        Assert.All(artifacts, name => Assert.False(File.Exists(Path.Combine(_paths.Root, name))));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(_paths.ProfilesFile));
        Assert.True(File.Exists(_paths.RcloneExecutable));
    }

    [Fact]
    public async Task CorruptRecoveryStateBlocksSettingsAndCleanupAcknowledgement()
    {
        File.WriteAllText(_paths.RemoteWipeStateFile, "invalid");
        Assert.Throws<IOException>(() => new AccountDataGuard(_paths));
        using var client = new RemoteWipeClient();
        await Assert.ThrowsAsync<IOException>(() => new RemoteWipeCoordinator(_paths, client)
            .ResumeAsync(_ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task MultipleProtectedRegistrationsAreNotLimitedToASinglePasswordSize()
    {
        var registrations = Enumerable.Range(0, 30).Select(_ => Registration).ToArray();
        var store = new RemoteWipeStore(_paths);
        var staged = await store.CreateStagedAsync(registrations);
        File.Move(staged, _paths.RemoteWipeFile);
        Assert.Equal(registrations, await store.LoadAsync());
    }

    private void SeedData()
    {
        File.WriteAllText(_paths.ConfigFile, "encrypted configuration");
        File.WriteAllText(_paths.ConfigSecretFile, "protected secret");
        File.WriteAllText(_paths.RemoteWipeFile, "other protected tokens");
        File.WriteAllText(_paths.ProfilesFile, "deployment profiles");
        File.WriteAllText(_paths.RcloneExecutable, "component fixture");
        File.WriteAllText(Path.Combine(_paths.Cache, "cached-file"), "cached bytes");
    }

    [Fact]
    public async Task LostAcknowledgementCompletionDoesNotLeaveRetiredTokenRetryingForever()
    {
        SeedData();
        FileStream? lockedState = null;
        using var http = new HttpClient(new Handler(_ =>
        {
            // Simulate a successful server acknowledgement followed by failure to commit locally.
            lockedState = new FileStream(_paths.RemoteWipeStateFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new(HttpStatusCode.OK);
        }));
        using var client = new RemoteWipeClient(http);
        var coordinator = new RemoteWipeCoordinator(_paths, client);
        await coordinator.AcceptAsync(Registration, CancellationToken.None);
        try
        {
            var failure = await Record.ExceptionAsync(() => coordinator.ResumeAsync(_ => Task.CompletedTask, CancellationToken.None));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        finally { lockedState?.Dispose(); }
        Assert.Equal(RemoteWipePhase.Cleaned, new RemoteWipeStateStore(_paths).Read()!.Phase);

        using var retiredHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        using var retiredClient = new RemoteWipeClient(retiredHttp);
        await new RemoteWipeCoordinator(_paths, retiredClient).ResumeAsync(_ => Task.CompletedTask, CancellationToken.None);
        var completed = new RemoteWipeStateStore(_paths).Read()!;
        Assert.Equal(RemoteWipePhase.Completed, completed.Phase);
        Assert.False(completed.ServerAcknowledged);
        Assert.Null(completed.Registration);
    }

    public void Dispose() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
