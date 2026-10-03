using System.Collections.Specialized;
using ResoDrive.Core.Settings;
using ResoDrive.Host;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class DriveOrderTests
{
    [Theory]
    [InlineData(0, 2, false, "B,A,C")]
    [InlineData(0, 2, true, "B,C,A")]
    [InlineData(2, 0, false, "C,A,B")]
    [InlineData(2, 0, true, "A,C,B")]
    [InlineData(1, 0, false, "B,A,C")]
    [InlineData(1, 2, true, "A,C,B")]
    [InlineData(0, 1, false, "A,B,C")]
    [InlineData(2, 1, true, "A,B,C")]
    [InlineData(1, 1, true, "A,B,C")]
    public void DropBeforeOrAfterHandlesMovementInBothDirections(int source, int target, bool after,
        string expected)
    {
        var mounts = Mounts();
        var ordered = DriveOrder.Move(mounts, mounts[source].Id, mounts[target].Id, after);
        Assert.Equal(expected, string.Join(',', ordered.Select(mount => mount.DisplayName)));
        Assert.Equal("A,B,C", string.Join(',', mounts.Select(mount => mount.DisplayName)));
        Assert.Equal(3, ordered.Select(mount => mount.Id).Distinct().Count());
        Assert.All(ordered, mount => Assert.Contains(mounts, original => ReferenceEquals(original, mount)));
    }

    [Fact]
    public void RemovedDragSourceOrTargetIsAnUnchangedOrder()
    {
        var mounts = Mounts();
        Assert.Equal(mounts, DriveOrder.Move(mounts, Guid.NewGuid(), mounts[0].Id, false));
        Assert.Equal(mounts, DriveOrder.Move(mounts, mounts[0].Id, Guid.NewGuid(), true));
        Assert.Empty(DriveOrder.Move([], Guid.NewGuid(), Guid.NewGuid(), false));
        Assert.Throws<ArgumentException>(() => DriveOrder.Move([mounts[0], mounts[0]], mounts[0].Id, mounts[0].Id, true));
    }

    [Fact]
    public void ExistingDriveAndSyncRowsKeepTheirStateAndIdentity()
    {
        var mounts = Mounts();
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = mounts },
            mounts.Select(mount => new HostMountStatus(mount.Id, "Mounted", "Mounted", UploadsQueued: 2)).ToArray());
        var original = model.Mounts.ToArray();
        var jobs = model.Jobs.ToArray();
        var actions = new List<NotifyCollectionChangedAction>();
        model.Mounts.CollectionChanged += (_, args) => actions.Add(args.Action);

        model.ReorderMounts([mounts[2].Id, mounts[0].Id, mounts[1].Id]);

        Assert.Same(original[2], model.Mounts[0]);
        Assert.Same(original[0], model.Mounts[1]);
        Assert.Same(original[1], model.Mounts[2]);
        Assert.False(model.Mounts[2].Settings.Enabled);
        Assert.Equal(original[0].UploadActivityText, model.Mounts[1].UploadActivityText);
        Assert.Same(jobs[2], model.Jobs[0]);
        Assert.Same(jobs[0], model.Jobs[1]);
        Assert.Same(jobs[1], model.Jobs[2]);
        Assert.All(actions, action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
        actions.Clear();
        model.ReorderMounts([mounts[2].Id, mounts[0].Id, mounts[1].Id]);
        Assert.Empty(actions);
    }

    [Fact]
    public void InvalidViewOrderIsRejectedBeforeChangingAnyRows()
    {
        var mounts = Mounts();
        var model = new ShellViewModel();
        model.Load(new ManagerSettings { Mounts = mounts }, []);
        var original = model.Mounts.ToArray();
        Assert.Throws<ArgumentException>(() => model.ReorderMounts([mounts[0].Id, mounts[0].Id, mounts[2].Id]));
        Assert.Throws<ArgumentException>(() => model.ReorderMounts([mounts[0].Id, mounts[1].Id, Guid.NewGuid()]));
        Assert.Equal(original, model.Mounts);
    }

    [Fact]
    public async Task OrderSurvivesAnAtomicSettingsReloadAndDoesNotChangeMountDefinitions()
    {
        var paths = TestPaths();
        try
        {
            using var store = new AtomicSettingsStore(paths);
            var initial = (await store.SaveAsync(new ManagerSettings { Mounts = Mounts() }, 0)).Value!;
            var saved = await DriveOrder.SaveAsync(store, initial, initial.Mounts[2].Id, initial.Mounts[0].Id,
                after: false, CancellationToken.None);
            Assert.True(saved.Succeeded, saved.Error?.Message);
            var reloaded = await store.LoadAsync();
            Assert.Equal("C,A,B", string.Join(',', reloaded.Value!.Mounts.Select(mount => mount.DisplayName)));
            Assert.Equal(initial.Revision + 1, reloaded.Value.Revision);
            Assert.False(reloaded.Value.Mounts[2].Enabled);
            var analysis = DefinitionWorkConflict.Analyze(initial.Mounts, reloaded.Value.Mounts,
                initial.Mounts.Select(mount => mount.Id), initial.Mounts.SelectMany(mount => mount.SyncJobs).Select(job => job.Id), []);
            Assert.Empty(analysis.ChangedMountIds);
            Assert.Empty(analysis.ActiveChangedMountIds);
            Assert.False(analysis.HasBlockingWork);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AConflictingSettingsSaveLeavesTheOriginalOrderAndNewerFileUntouched()
    {
        var paths = TestPaths();
        try
        {
            using var store = new AtomicSettingsStore(paths);
            var initial = (await store.SaveAsync(new ManagerSettings { Mounts = Mounts() }, 0)).Value!;
            await store.SaveAsync(initial with { Application = new ApplicationSettings { MinimizeToTray = false } }, initial.Revision);
            var before = await File.ReadAllTextAsync(paths.SettingsFile);
            var result = await DriveOrder.SaveAsync(store, initial, initial.Mounts[2].Id, initial.Mounts[0].Id,
                after: false, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal("settings.revision_conflict", result.Error?.Code);
            Assert.Equal("A,B,C", string.Join(',', initial.Mounts.Select(mount => mount.DisplayName)));
            Assert.Equal(before, await File.ReadAllTextAsync(paths.SettingsFile));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task DroppingAtTheExistingPositionDoesNotCreateANewRevision()
    {
        var paths = TestPaths();
        try
        {
            using var store = new AtomicSettingsStore(paths);
            var initial = (await store.SaveAsync(new ManagerSettings { Mounts = Mounts() }, 0)).Value!;
            var result = await DriveOrder.SaveAsync(store, initial, initial.Mounts[0].Id, initial.Mounts[1].Id,
                after: false, CancellationToken.None);
            Assert.Same(initial, result.Value);
            Assert.Equal(initial.Revision, (await store.LoadAsync()).Value!.Revision);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationKeepsThePersistedAndVisibleOrder()
    {
        var paths = TestPaths();
        try
        {
            using var store = new AtomicSettingsStore(paths);
            var initial = (await store.SaveAsync(new ManagerSettings { Mounts = Mounts() }, 0)).Value!;
            var before = await File.ReadAllTextAsync(paths.SettingsFile);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DriveOrder.SaveAsync(store, initial,
                initial.Mounts[2].Id, initial.Mounts[0].Id, after: false, cancellation.Token));
            Assert.Equal(before, await File.ReadAllTextAsync(paths.SettingsFile));
            Assert.Equal("A,B,C", string.Join(',', initial.Mounts.Select(mount => mount.DisplayName)));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static ApplicationPaths TestPaths() => new(Path.Combine(Path.GetTempPath(),
        "resodrive-order-tests", Guid.NewGuid().ToString("N")));

    private static MountSettings[] Mounts() => Enumerable.Range(0, 3).Select(index => new MountSettings
    {
        Id = Guid.NewGuid(),
        DisplayName = ((char)('A' + index)).ToString(),
        RemoteName = "cloud",
        Enabled = index != 1,
        Target = new MountTargetSettings { DriveLetter = (char)('R' + index) },
        SyncJobs = [new SyncJobSettings
        {
            Id = Guid.NewGuid(), DisplayName = "Backup", LocalPath = @"C:\Backup",
        }],
    }).ToArray();
}
