using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Windows;

namespace ResoDrive.Host;

public sealed partial class Worker
{
    private async Task<OperationResult> CheckPendingUploadsAsync(CancellationToken token)
    {
        if (_mounts is not null) return await _mounts.CheckPendingUploadsAsync(token).ConfigureAwait(false);
        if (Volatile.Read(ref _settings) is null && Volatile.Read(ref _initializationFailure) is null)
            return Result.Failure("mount.upload_status_unknown", "ResoDrive is still checking cached uploads.", true);
        return await RcloneMountCoordinator.CheckOfflineRecoveryAsync(_paths, token).ConfigureAwait(false);
    }

    private void UpdateSessionProtection()
    {
        lock (_sessionProtectionGate) UpdateSessionProtectionCore();
    }

    private void OnUploadStatusInvalidated(object? sender, EventArgs args) => UpdateSessionProtection();

    private void UpdateSessionProtectionCore()
    {
        var guard = _sessionGuard;
        if (guard is null) return;
        if (_recoveringRemoteWipe)
        {
            guard.Update(false);
            return;
        }
        if (Volatile.Read(ref _settings) is null)
        {
            guard.Update(true, "ResoDrive is checking for files that still need uploading.");
            return;
        }
        if (!_operations.IsEmpty ||
            _syncs?.GetSnapshots().Any(snapshot => snapshot.Lifecycle is SyncLifecycle.Running or SyncLifecycle.Queued) == true)
        {
            guard.Update(true, "ResoDrive is still transferring files. Wait for the uploads to finish.");
            return;
        }
        var mounts = _mounts?.GetSnapshots() ?? [];
        if (mounts.Any(snapshot => snapshot.UploadRecoveryRequired || snapshot.UploadsDirty > 0 ||
                snapshot.UploadsQueued > 0 || snapshot.UploadsInProgress > 0 || snapshot.UploadErrors > 0))
        {
            guard.Update(true, "ResoDrive has files awaiting upload, open files, or upload errors.");
            return;
        }
        if (mounts.Any(snapshot => snapshot.Lifecycle is MountLifecycle.Starting or MountLifecycle.Stopping or MountLifecycle.WaitingToRestart ||
                snapshot.Lifecycle is MountLifecycle.Mounted or MountLifecycle.Degraded &&
                (snapshot.UploadStatusStale || snapshot.UploadStatusChecking || snapshot.UploadObservedAt is null ||
                    DateTimeOffset.UtcNow - snapshot.UploadObservedAt > TimeSpan.FromSeconds(10))))
        {
            guard.Update(true, "ResoDrive could not yet confirm that all files are uploaded.");
            return;
        }
        guard.Update(false);
    }

}
