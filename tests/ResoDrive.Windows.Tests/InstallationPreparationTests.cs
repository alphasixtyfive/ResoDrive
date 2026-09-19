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
        Assert.Equal(["shutdown", "windows:123", "wait:123"], runtime.Calls);
    }

    [Fact]
    public async Task ForeignInstallationIsNeverStopped()
    {
        var runtime = new FakeRuntime(new(false, "host.different_installation", HostProcessId: 456));
        await new InstallationPreparationService(runtime).PrepareAsync(@"C:\Program Files\rdrive");
        Assert.Equal(["shutdown", "windows:456"], runtime.Calls);
    }

    private sealed class FakeRuntime(HostResponse response) : IInstallationPreparationRuntime
    {
        public List<string> Calls { get; } = [];
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
    }
}
