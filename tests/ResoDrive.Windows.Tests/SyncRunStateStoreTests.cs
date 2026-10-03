using ResoDrive.Core.Domain;

namespace ResoDrive.Windows.Tests;

public sealed class SyncRunStateStoreTests
{
    [Fact]
    public async Task NullHistoryEntriesDoNotDiscardValidOutcomes()
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), "rdrive-history-tests", Guid.NewGuid().ToString("N")));
        try
        {
            using var store = new SyncRunStateStore(paths);
            var snapshot = new SyncSnapshot { MountId = MountId.New(), JobId = SyncJobId.New(), Lifecycle = SyncLifecycle.Succeeded, CompletedAt = DateTimeOffset.UtcNow, StatusText = "Completed" };
            await store.SaveAsync(snapshot);
            var json = await File.ReadAllTextAsync(paths.SyncRunStateFile);
            await File.WriteAllTextAsync(paths.SyncRunStateFile, "[null," + json.Trim()[1..]);
            Assert.Equal(snapshot.JobId, Assert.Single(store.Load()).JobId);
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true); }
    }
}
