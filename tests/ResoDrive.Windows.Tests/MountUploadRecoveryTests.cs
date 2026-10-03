using System.Diagnostics;
using System.Text.Json;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class MountUploadRecoveryTests : IDisposable
{
    private readonly ApplicationPaths _paths = new(Path.Combine(Path.GetTempPath(), "rdrive-recovery-tests", Guid.NewGuid().ToString("N")));
    private string Metadata => Path.Combine(_paths.Cache, "vfsMeta", "remote");
    public MountUploadRecoveryTests()
    {
        _paths.EnsureCreated();
        Directory.CreateDirectory(Metadata);
    }
    private static MountDefinition Definition(char letter = 'R', bool enabled = true) => new()
    {
        Id = MountId.New(), DisplayName = $"Recovery drive {letter}", RemoteName = "remote", Target = new MountTarget.Drive(letter),
        AutoMount = AutoMountPolicy.Never, Enabled = enabled
    };
    private RcloneMountCoordinator Coordinator() => new(Path.Combine(_paths.Root, "missing-rclone.exe"), _paths.ConfigFile, _paths, new EmptyInventory());

    [Fact]
    public async Task RepairedOwnershipFileRetriesRecoveryOfSurvivingProcess()
    {
        var definition = Definition();
        await using var coordinator = Coordinator();
        await File.WriteAllTextAsync(_paths.OwnershipFile, "not json");
        await File.WriteAllTextAsync(_paths.OwnershipFile + ".bak", "not json");
        Assert.False((await coordinator.ReconcileAsync([definition])).Succeeded);

        using var process = Process.GetCurrentProcess();
        var owned = new OwnedMount(definition.Id.Value, process.Id, process.StartTime.ToUniversalTime(),
            process.MainModule!.FileName, "remote:", "R:");
        await File.WriteAllTextAsync(_paths.OwnershipFile,
            JsonSerializer.Serialize(new[] { owned }, JsonSerializerOptions.Web));

        Assert.True((await coordinator.ReconcileAsync([definition])).Succeeded);
        Assert.True(Assert.Single(coordinator.GetSnapshots()).UploadRecoveryRequired);
        Assert.False((await coordinator.CheckPendingUploadsAsync()).Succeeded);
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task JournalSurvivesRestartAndDoesNotForgetTheMetadataLocation()
    {
        var definition = Definition();
        using (var store = new MountRecoveryStore(_paths))
        {
            await store.RecordAsync(definition, Metadata, CancellationToken.None, "fingerprint");
            await store.RecordAsync(definition, null, CancellationToken.None);
        }
        using var reloaded = new MountRecoveryStore(_paths);
        var entry = Assert.Single(reloaded.GetEntries());
        Assert.Equal(Metadata, entry.MetadataPath);
        Assert.Equal("fingerprint", entry.ConfigFingerprint);
        Assert.True(MountRecoveryStore.Matches(entry, definition));
        Assert.False(MountRecoveryStore.Matches(entry, definition with { Arguments = ["--vfs-cache-mode=full"] }));
    }

    [Fact]
    public async Task NeverMountWithDirtyCacheAttemptsRecoveryRatherThanDiscardingItsWork()
    {
        var definition = Definition();
        await SeedAsync(definition, dirty: true);
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([definition])).Succeeded);
        var snapshot = Assert.Single(coordinator.GetSnapshots());
        Assert.True(snapshot.UploadRecoveryRequired);
        Assert.Equal(MountLifecycle.Failed, snapshot.Lifecycle);
        Assert.Contains("rclone.exe", snapshot.StatusText, StringComparison.Ordinal);
        Assert.False((await coordinator.CheckPendingUploadsAsync()).Succeeded);
        Assert.True(File.Exists(Path.Combine(Metadata, "file.txt")));
    }

    [Fact]
    public async Task DisabledDrivePreservesDirtyCacheWithoutLaunchingIt()
    {
        var definition = Definition(enabled: false);
        await SeedAsync(definition, dirty: true);
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([definition])).Succeeded);
        var snapshot = Assert.Single(coordinator.GetSnapshots());
        Assert.True(snapshot.UploadRecoveryRequired);
        Assert.Contains("enable", snapshot.StatusText, StringComparison.Ordinal);
        Assert.False((await coordinator.CheckPendingUploadsAsync()).Succeeded);
    }

    [Fact]
    public async Task CleanInterruptedNeverDriveDoesNotMountAgain()
    {
        var definition = Definition();
        await SeedAsync(definition, dirty: false);
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([definition])).Succeeded);
        Assert.Equal(MountLifecycle.Stopped, Assert.Single(coordinator.GetSnapshots()).Lifecycle);
        Assert.True((await coordinator.CheckPendingUploadsAsync()).Succeeded);
        using var reloaded = new MountRecoveryStore(_paths);
        Assert.Empty(reloaded.GetEntries());
    }

    [Fact]
    public async Task ExplicitDisconnectKeepsPendingCacheAndDoesNotReconnectOnReload()
    {
        var definition = Definition(enabled: false);
        await SeedAsync(definition, dirty: true);
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([definition])).Succeeded);
        Assert.False((await coordinator.CheckPendingUploadsForMountAsync(definition.Id)).Succeeded);
        Assert.False((await coordinator.StopAsync(definition.Id)).Succeeded);

        Assert.True((await coordinator.StopAsync(definition.Id, allowPendingUploads: true)).Succeeded);
        Assert.True((await coordinator.ReconcileAsync([definition with { Enabled = true }])).Succeeded);

        var snapshot = Assert.Single(coordinator.GetSnapshots());
        Assert.Equal(MountLifecycle.Stopped, snapshot.Lifecycle);
        Assert.True(snapshot.UploadRecoveryRequired);
        Assert.False((await coordinator.CheckPendingUploadsAsync()).Succeeded);
        Assert.True(File.Exists(Path.Combine(Metadata, "file.txt")));
        using var reloaded = new MountRecoveryStore(_paths);
        Assert.Equal("remote:", Assert.Single(reloaded.GetEntries()).Source);
        Assert.False((await coordinator.StartAsync(definition.Id)).Succeeded);
        Assert.Contains("rclone.exe", Assert.Single(coordinator.GetSnapshots()).StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoringOrEnablingOriginalDefinitionRetriesItsRecovery(bool disabled)
    {
        var original = Definition();
        await SeedAsync(original, dirty: true);
        var initial = disabled ? original with { Enabled = false } : original with { RemoteName = "changed" };
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([initial])).Succeeded);

        Assert.True((await coordinator.ReconcileAsync([original])).Succeeded);

        Assert.Contains("rclone.exe", Assert.Single(coordinator.GetSnapshots()).StatusText, StringComparison.Ordinal);
        using var reloaded = new MountRecoveryStore(_paths);
        Assert.True(MountRecoveryStore.Matches(Assert.Single(reloaded.GetEntries()), original));
    }

    [Fact]
    public async Task OfflineInspectionProtectsDirtyMetadataWithoutARecoveryJournal()
    {
        await File.WriteAllTextAsync(Path.Combine(Metadata, "file.txt"), "{\"Dirty\":true,\"Size\":20}");
        Assert.False((await RcloneMountCoordinator.CheckOfflineRecoveryAsync(_paths)).Succeeded);
        await File.WriteAllTextAsync(Path.Combine(Metadata, "file.txt"), "{\"Dirty\":false,\"Size\":20}");
        Assert.True((await RcloneMountCoordinator.CheckOfflineRecoveryAsync(_paths)).Succeeded);
    }

    [Fact]
    public async Task OfflineInspectionKeepsUnverifiedDataUnknownEvenWhenMetadataRootExists()
    {
        Assert.True((await RcloneMountCoordinator.CheckOfflineRecoveryAsync(_paths)).Succeeded);
        var data = Path.Combine(_paths.Cache, "vfs", "remote");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "file.txt"), "cached contents");
        Assert.False((await RcloneMountCoordinator.CheckOfflineRecoveryAsync(_paths)).Succeeded);
        await File.WriteAllTextAsync(Path.Combine(Metadata, "file.txt"), "{\"Dirty\":false,\"Size\":15}");
        Assert.True((await RcloneMountCoordinator.CheckOfflineRecoveryAsync(_paths)).Succeeded);
    }

    [Fact]
    public async Task DeletedDriveStillShowsRecoverableWorkAndBlocksShutdown()
    {
        var definition = Definition();
        await SeedAsync(definition, dirty: true);
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([])).Succeeded);
        Assert.True(Assert.Single(coordinator.GetSnapshots()).UploadRecoveryRequired);
        Assert.False((await coordinator.CheckPendingUploadsAsync()).Succeeded);
    }

    [Fact]
    public async Task ChangedIdentityCannotOverwriteTheOldCacheJournal()
    {
        var definition = Definition();
        await SeedAsync(definition, dirty: true);
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([definition with { RemoteName = "changed" }])).Succeeded);
        Assert.True(Assert.Single(coordinator.GetSnapshots()).UploadRecoveryRequired);
        Assert.False((await coordinator.CheckPendingUploadsAsync()).Succeeded);
        using var reloaded = new MountRecoveryStore(_paths);
        Assert.Equal("remote:", Assert.Single(reloaded.GetEntries()).Source);
    }

    [Fact]
    public async Task SharedRemoteCacheProtectsBothMountsWithoutDeletingSharedData()
    {
        var first = Definition('R');
        var second = Definition('S', enabled: false);
        await SeedAsync(first, dirty: true);
        using (var store = new MountRecoveryStore(_paths)) await store.RecordAsync(second, Metadata, CancellationToken.None);
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([first, second])).Succeeded);
        Assert.All(coordinator.GetSnapshots(), snapshot => Assert.True(snapshot.UploadRecoveryRequired));
        Assert.False((await coordinator.CheckPendingUploadsAsync()).Succeeded);
        Assert.True(File.Exists(Path.Combine(Metadata, "file.txt")));
    }

    [Fact]
    public async Task CorruptRecoveryRecordsCannotSilentlyAuthorizeDiscardingTheCache()
    {
        var path = Path.Combine(_paths.Root, "mount-upload-recovery.json");
        await File.WriteAllTextAsync(path, "not json");
        Assert.Throws<IOException>(() => new MountRecoveryStore(_paths));
    }

    [Fact]
    public async Task RecoveryRecordsUseTheAtomicBackupWhenPrimaryIsDamaged()
    {
        var definition = Definition();
        using (var store = new MountRecoveryStore(_paths))
        {
            await store.RecordAsync(definition, Metadata, CancellationToken.None);
            await store.RecordAsync(definition with { DisplayName = "Renamed" }, Metadata, CancellationToken.None);
        }
        await File.WriteAllTextAsync(Path.Combine(_paths.Root, "mount-upload-recovery.json"), "not json");
        using var reloaded = new MountRecoveryStore(_paths);
        Assert.Equal(definition.Id.Value, Assert.Single(reloaded.GetEntries()).MountId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanRecoveryClearsTheWarningOrRemovesADeletedDrive(bool deleted)
    {
        var definition = Definition(enabled: false);
        await SeedAsync(definition, dirty: true);
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync(deleted ? [] : [definition])).Succeeded);
        Assert.True(Assert.Single(coordinator.GetSnapshots()).UploadRecoveryRequired);

        await File.WriteAllTextAsync(Path.Combine(Metadata, "file.txt"), "{\"Dirty\":false,\"Size\":100}");
        await coordinator.RefreshHealthAsync();
        if (deleted) Assert.Empty(coordinator.GetSnapshots());
        else Assert.False(Assert.Single(coordinator.GetSnapshots()).UploadRecoveryRequired);
        Assert.True((await coordinator.CheckPendingUploadsAsync()).Succeeded);

        if (deleted) Assert.Empty(coordinator.GetSnapshots());
        else Assert.False(Assert.Single(coordinator.GetSnapshots()).UploadRecoveryRequired);
    }

    [Theory]
    [InlineData("", "folder", true)]
    [InlineData("folder", "folder/child", true)]
    [InlineData("/folder/", "FOLDER", true)]
    [InlineData("folder", "folder-other", false)]
    [InlineData("folder/one", "folder/two", false)]
    public void CacheScopesAccountForParentPathsWithoutConfusingSiblingPrefixes(string first, string second, bool overlap)
    {
        var left = Definition() with { RemotePath = first };
        var right = Definition('S') with { RemotePath = second };
        Assert.Equal(overlap, RcloneMountCoordinator.CacheScopesOverlap(left, right));
        Assert.Equal(overlap, RcloneMountCoordinator.CacheScopesOverlap(right, left));
        Assert.False(RcloneMountCoordinator.CacheScopesOverlap(left, right with { RemoteName = "other" }));
    }

    [Fact]
    public async Task CorruptControlCredentialsStayUnknownAndArePreserved()
    {
        var owned = new OwnedMount(Guid.NewGuid(), 1, DateTime.UtcNow, "rclone.exe", "remote:", "R:");
        var path = Path.Combine(_paths.Root, "mount-controls", owned.MountId.ToString("N") + ".dpapi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "invalid base64");
        Assert.Null(await new MountControlStore(_paths).LoadAsync(owned, CancellationToken.None));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task LegacyOrphanKeepsADurableUnknownCacheMarkerUntilItCanBeInspected()
    {
        var definition = Definition(enabled: false);
        using (var store = new MountRecoveryStore(_paths))
        {
            await store.RecordOrphanAsync(new(definition.Id.Value, 1, DateTime.UtcNow, "rclone.exe", "remote:", "R:"), CancellationToken.None);
        }
        using var reloaded = new MountRecoveryStore(_paths);
        var entry = Assert.Single(reloaded.GetEntries());
        Assert.False(entry.ArgumentsKnown);
        Assert.Null(entry.MetadataPath);
        Assert.True(MountRecoveryStore.Matches(entry, definition with { Arguments = ["--vfs-cache-mode=full"] }));
        await using var coordinator = Coordinator();
        Assert.True((await coordinator.ReconcileAsync([definition])).Succeeded);
        await coordinator.RefreshHealthAsync();
        Assert.True(Assert.Single(coordinator.GetSnapshots()).UploadRecoveryRequired);
        Assert.False((await coordinator.CheckPendingUploadsAsync()).Succeeded);
    }

    private async Task SeedAsync(MountDefinition definition, bool dirty)
    {
        await File.WriteAllTextAsync(Path.Combine(Metadata, "file.txt"), $"{{\"Dirty\":{dirty.ToString().ToLowerInvariant()},\"Size\":100}}");
        using var store = new MountRecoveryStore(_paths);
        await store.RecordAsync(definition, Metadata, CancellationToken.None);
    }
    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }
    private sealed class EmptyInventory : IMountTargetInventory
    {
        public Task<OperationResult<IReadOnlySet<char>>> GetOccupiedDriveLettersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlySet<char>>(new HashSet<char>()));
        public Task<OperationResult<bool>> IsMountedAsync(MountTarget target, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success(false));
    }
}
