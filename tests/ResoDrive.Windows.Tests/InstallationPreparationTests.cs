namespace ResoDrive.Windows.Tests;

public sealed class InstallationPreparationTests
{
    [Fact]
    public async Task UploadRejectionDoesNotCloseWindowsOrStopHost()
    {
        var runtime = new FakeRuntime(new(false, "mount.pending_uploads", "Uploads are still pending."));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InstallationPreparationService(runtime).PrepareAsync(@"C:\Program Files\rdrive"));
        Assert.Contains("Uploads are still pending", error.Message, StringComparison.Ordinal);
        Assert.Equal(["shutdown"], runtime.Calls);
    }

    [Theory]
    [InlineData("host.access_denied")]
    [InlineData("host.response_timeout")]
    [InlineData("host.connection_lost")]
    public async Task FailedHandshakeDoesNotForceKillProcesses(string errorCode)
    {
        var runtime = new FakeRuntime(new(false, errorCode, "Could not verify shutdown."));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InstallationPreparationService(runtime).PrepareAsync(@"C:\Program Files\rdrive"));
        Assert.Equal(["shutdown"], runtime.Calls);
    }

    [Fact]
    public async Task SuccessfulShutdownClosesUiBeforeWaitingForHostExit()
    {
        var runtime = new FakeRuntime(new(true, HostProcessId: 123));
        await new InstallationPreparationService(runtime).PrepareAsync(@"C:\Program Files\rdrive");
        Assert.Equal(["shutdown", "windows:123", "wait:123", "verify"], runtime.Calls);
    }

    [Fact]
    public async Task MissingHostClosesOnlyVerifiedOrphanedUi()
    {
        var runtime = new FakeRuntime(new(false, "host.unavailable"));
        await new InstallationPreparationService(runtime).PrepareAsync(@"C:\Program Files\rdrive");
        Assert.Equal(["shutdown", "wait-other-account", "orphan-ui"], runtime.Calls);
    }

    [Fact]
    public async Task OtherAccountStillRunningDoesNotInspectOrCloseItsUi()
    {
        var runtime = new FakeRuntime(new(false, "host.unavailable"))
        {
            OtherAccountFailure = new IOException("Close ResoDrive from the signed-in account."),
        };
        var error = await Assert.ThrowsAsync<IOException>(() =>
            new InstallationPreparationService(runtime).PrepareAsync(@"C:\Program Files\rdrive"));
        Assert.Contains("signed-in account", error.Message, StringComparison.Ordinal);
        Assert.Equal(["shutdown", "wait-other-account"], runtime.Calls);
    }

    [Theory]
    [InlineData("A host mutex is still held.")]
    [InlineData("A rclone transfer is still running.")]
    [InlineData("A process belongs to another Windows account.")]
    public async Task UnverifiableOrphanDoesNotContinueInstallation(string reason)
    {
        var runtime = new FakeRuntime(new(false, "host.unavailable")) { OrphanFailure = new IOException(reason) };
        var error = await Assert.ThrowsAsync<IOException>(() =>
            new InstallationPreparationService(runtime).PrepareAsync(@"C:\Program Files\rdrive"));
        Assert.Equal(reason, error.Message);
        Assert.Equal(["shutdown", "wait-other-account", "orphan-ui"], runtime.Calls);
    }

    [Fact]
    public async Task ForeignInstallationDoesNotStopItsHostOrUi()
    {
        var runtime = new FakeRuntime(new(false, "host.different_installation", HostProcessId: 456));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InstallationPreparationService(runtime).PrepareAsync(@"C:\Program Files\rdrive"));
        Assert.Equal(["shutdown"], runtime.Calls);
    }

    private sealed class FakeRuntime(HostResponse response) : IInstallationPreparationRuntime
    {
        public List<string> Calls { get; } = [];
        public IOException? OrphanFailure { get; init; }
        public IOException? OtherAccountFailure { get; init; }
        public Task<HostResponse> ShutdownAsync(string directory, CancellationToken token)
        {
            Calls.Add("shutdown");
            return Task.FromResult(response);
        }
        public Task StopWindowsAsync(string directory, int? hostProcessId, CancellationToken token)
        {
            Calls.Add($"windows:{hostProcessId}");
            return Task.CompletedTask;
        }
        public Task WaitForHostExitAsync(int? processId, CancellationToken token)
        {
            Calls.Add($"wait:{processId}");
            return Task.CompletedTask;
        }
        public Task StopOrphanedUiAsync(string directory, CancellationToken token)
        {
            Calls.Add("orphan-ui");
            return OrphanFailure is null ? Task.CompletedTask : Task.FromException(OrphanFailure);
        }
        public Task WaitForOtherAccountProcessesExitAsync(string directory, CancellationToken token)
        {
            Calls.Add("wait-other-account");
            return OtherAccountFailure is null ? Task.CompletedTask : Task.FromException(OtherAccountFailure);
        }
        public Task VerifyStoppedAsync(string directory, CancellationToken token)
        {
            Calls.Add("verify");
            return Task.CompletedTask;
        }
    }
}
