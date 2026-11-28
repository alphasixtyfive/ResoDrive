using System.IO;
using ResoDrive.Core.Results;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

public sealed class SettingsEditTransactionTests
{
    [Fact]
    public async Task QueuedDriveEditPreservesSettingsSavedWhileItWaits()
    {
        var paths = TestPaths();
        try
        {
            using var store = new AtomicSettingsStore(paths);
            using var gate = new SemaphoreSlim(1, 1);
            var initial = await store.SaveAsync(new ManagerSettings { Mounts = [Mount()] }, 0);
            Assert.True(initial.Succeeded, initial.Error?.Message);
            var current = initial.Value!;
            var draft = current.Mounts[0] with { DisplayName = "Edited drive" };
            await gate.WaitAsync();
            var pending = SettingsEditTransaction.ApplyAsync(
                async () => { await gate.WaitAsync(); return true; },
                () => gate.Release(),
                () => current,
                latest => SettingsEdits.UpdateMount(latest, draft.Id, draft, delete: false),
                async candidate =>
                {
                    var saved = await store.SaveAsync(candidate, current.Revision);
                    Assert.True(saved.Succeeded, saved.Error?.Message);
                    current = saved.Value!;
                    return saved.Succeeded;
                });
            Assert.False(pending.IsCompleted);
            var newerJob = Job("Added while waiting");
            var newer = await store.SaveAsync(current with
            {
                Application = current.Application with { MinimizeToTray = false, StartWithWindows = true },
                Mounts = [current.Mounts[0] with
                {
                    ConnectionHost = "new.example.invalid", ConnectionType = "sftp",
                    SyncJobs = [newerJob],
                }, Mount('S')],
            }, current.Revision);
            Assert.True(newer.Succeeded, newer.Error?.Message);
            current = newer.Value!;
            var expectedRevision = current.Revision + 1;
            gate.Release();

            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            var reloaded = (await store.LoadAsync()).Value!;
            Assert.Equal(expectedRevision, reloaded.Revision);
            Assert.False(reloaded.Application.MinimizeToTray);
            Assert.True(reloaded.Application.StartWithWindows);
            Assert.Equal(2, reloaded.Mounts.Count);
            Assert.Equal("Edited drive", reloaded.Mounts[0].DisplayName);
            Assert.Equal("new.example.invalid", reloaded.Mounts[0].ConnectionHost);
            Assert.Equal("sftp", reloaded.Mounts[0].ConnectionType);
            Assert.Equal(newerJob.Id, Assert.Single(reloaded.Mounts[0].SyncJobs).Id);
        }
        finally { Directory.Delete(paths.Root, recursive: true); }
    }

    [Fact]
    public void SyncEditUpdatesItsLatestDriveWithoutDroppingOtherJobs()
    {
        var job = Job("Original job");
        var other = Job("Newer job");
        var mount = Mount() with { DisplayName = "Newer drive", ConnectionHost = "new.example.invalid", SyncJobs = [job, other] };
        var current = new ManagerSettings { Revision = 11, Mounts = [mount], Application = new() { StartWithWindows = true } };

        var edited = SettingsEdits.UpdateSyncJob(current, mount.Id, job.Id, job with { DisplayName = "Edited job" }, delete: false);

        Assert.Same(current.Application, edited.Application);
        Assert.Equal("Newer drive", edited.Mounts[0].DisplayName);
        Assert.Equal("new.example.invalid", edited.Mounts[0].ConnectionHost);
        Assert.Equal("Edited job", edited.Mounts[0].SyncJobs[0].DisplayName);
        Assert.Same(other, edited.Mounts[0].SyncJobs[1]);
    }

    [Fact]
    public async Task RemovedDriveIsNotResurrectedAndReleasesMutationOwnership()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var removed = Mount();
        var saved = false;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => SettingsEditTransaction.ApplyAsync(
            async () => { await gate.WaitAsync(); return true; },
            () => gate.Release(),
            () => new ManagerSettings(),
            current => SettingsEdits.UpdateMount(current, removed.Id, removed, delete: false),
            _ => { saved = true; return Task.FromResult(true); }));
        Assert.Contains("removed", failure.Message, StringComparison.Ordinal);
        Assert.False(saved);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void RemovedJobAndRemovedJobDriveAreRejected()
    {
        var mount = Mount();
        var job = Job("Removed job");
        Assert.Throws<InvalidOperationException>(() => SettingsEdits.UpdateSyncJob(
            new ManagerSettings { Mounts = [mount] }, mount.Id, job.Id, job, delete: false));
        Assert.Throws<InvalidOperationException>(() => SettingsEdits.AddSyncJob(new ManagerSettings(), mount.Id, job));
    }

    [Fact]
    public async Task FailedStartupRollbackKeepsTheReasonAndGivesRecoverySteps()
    {
        var calls = 0;
        var warning = await SettingsRollback.RestoreStartupAsync(() =>
        {
            calls++;
            return Task.FromResult(Result.Failure("autostart.access_denied", "Windows denied access."));
        });
        Assert.Equal(1, calls);
        Assert.Contains("Review Start with Windows", warning, StringComparison.Ordinal);
        Assert.Contains("Windows denied access", warning, StringComparison.Ordinal);
        Assert.Equal("Settings file could not be saved.\n\n" + warning,
            SettingsRollback.WithRecovery("Settings file could not be saved.", warning));
        Assert.Equal("Settings file could not be saved.", SettingsRollback.WithRecovery("Settings file could not be saved.", null));
        Assert.Null(await SettingsRollback.RestoreStartupAsync(() => Task.FromResult(Result.Success())));
    }

    [Fact]
    public async Task StartupRollbackExceptionBecomesAnActionableFailure()
    {
        var warning = await SettingsRollback.RestoreStartupAsync(() =>
            Task.FromException<OperationResult>(new UnauthorizedAccessException("Task access denied.")));
        Assert.Contains("Task access denied", warning, StringComparison.Ordinal);
        Assert.Contains("could not be restored", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRollbackActivationDoesNotClaimTheHostReactivatedSettings()
    {
        var warning = SettingsRollback.ActivationMessage(new HostResponse(false, "host.unavailable", "Connection lost."));
        Assert.Contains("Restart ResoDrive", warning, StringComparison.Ordinal);
        Assert.Contains("Connection lost", warning, StringComparison.Ordinal);
        Assert.NotEmpty(SettingsRollback.ActivationMessage(null));
        Assert.Empty(SettingsRollback.ActivationMessage(new HostResponse(true)));
    }

    private static ApplicationPaths TestPaths() => new(Path.Combine(Path.GetTempPath(), "resodrive-settings-edit-tests", Guid.NewGuid().ToString("N")));
    private static MountSettings Mount(char letter = 'R') => new()
    {
        Id = Guid.NewGuid(), DisplayName = "Drive " + Guid.NewGuid().ToString("N"), RemoteName = "cloud",
        Target = new() { DriveLetter = letter },
    };
    private static SyncJobSettings Job(string name) => new()
    {
        Id = Guid.NewGuid(), DisplayName = name, LocalPath = @"C:\Backup", Mode = "CopyFromRemote",
    };
}
