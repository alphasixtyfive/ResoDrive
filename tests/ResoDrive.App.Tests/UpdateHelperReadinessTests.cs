using System.Diagnostics;
using System.Security.Cryptography;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class UpdateHelperReadinessTests
{
    [Fact]
    public async Task HandoffWaitsForTheHelperBeforeReturning()
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? pipeName = null;
        var start = fixture.Start((info, _) =>
        {
            pipeName = info.Environment[UpdateHelperReadiness.EnvironmentVariable];
            return new MemoryStream();
        }, token: timeout.Token);
        Assert.NotNull(pipeName);
        Assert.False(start.IsCompleted);
        await UpdateHelperReadiness.SignalAsync(pipeName, Environment.ProcessId, timeout.Token);
        await start;
        Assert.Equal("pending", ApplicationUpdateHandoff.ReadOutcome(fixture.Updates)!.Status);
    }

    [Fact]
    public async Task HelperThatNeverBecomesReadyFinalizesFailureWithoutChangingTheSource()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Start(
            (_, _) => new MemoryStream(), timeout: TimeSpan.FromMilliseconds(50)));
        var outcome = ApplicationUpdateHandoff.ReadOutcome(fixture.Updates)!;
        Assert.Equal("failed", outcome.Status);
        Assert.True(outcome.Finalized);
        Assert.Contains("did not become ready", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("source", await File.ReadAllTextAsync(fixture.Source));
        Assert.Equal("installer", await File.ReadAllTextAsync(fixture.Installer));
    }

    [Fact]
    public async Task CancelledParentDoesNotAcceptALateHelper()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        string? pipeName = null;
        var start = fixture.Start((info, _) =>
        {
            pipeName = info.Environment[UpdateHelperReadiness.EnvironmentVariable];
            return new MemoryStream();
        }, token: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateHelperReadiness.SignalAsync(pipeName!, Environment.ProcessId, timeout.Token));
        Assert.True(ApplicationUpdateHandoff.ReadOutcome(fixture.Updates)!.Finalized);
    }

    [Fact]
    public async Task HelperRejectsAPipeOwnedByAnotherProcess()
    {
        var name = "ResoDrive.Test.UpdateReady." + Guid.NewGuid().ToString("N");
        using var server = CurrentUserPipe.CreateServer(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = server.WaitForConnectionAsync(timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => UpdateHelperReadiness.SignalAsync(name, -1, timeout.Token));
        await accept;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "resodrive-readiness-" + Guid.NewGuid().ToString("N"));
        internal string Updates => Path.Combine(_root, "updates");
        internal string Source => Path.Combine(_root, "resodrive.exe");
        internal string Installer => Path.Combine(Updates, "resodrive-win-x64-0.3.0-setup.exe");

        internal Fixture()
        {
            Directory.CreateDirectory(Updates);
            File.WriteAllText(Source, "source");
            File.WriteAllText(Installer, "installer");
        }

        internal Task Start(Func<ProcessStartInfo, string, IDisposable?> launch,
            TimeSpan? timeout = null, CancellationToken token = default) =>
            ApplicationUpdateHandoff.StartAsync("0.3.0", Installer, Updates, Source, Source,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Installer))), launch, timeout, token);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
