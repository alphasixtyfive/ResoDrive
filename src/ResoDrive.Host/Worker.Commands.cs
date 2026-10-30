using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Windows;

namespace ResoDrive.Host;

public sealed partial class Worker
{
    // HandleAsync owns the reload gate. These command helpers run under that
    // same gate, keeping validation, state changes and queue acceptance ordered.
    private async Task<HostResponse> HandleReloadCommandAsync(string command, HostRequest request, CancellationToken token)
    {
        if (command == "activate-runtime")
        {
            var refreshed = await RefreshRuntimeUserAgentAsync(token).ConfigureAwait(false);
            if (!refreshed.Succeeded) return Response(refreshed);
        }

        var reloaded = await ReloadCoreAsync(token, request.Confirmed).ConfigureAwait(false);
        if (!reloaded.Succeeded && Volatile.Read(ref _settings) is null)
            Volatile.Write(ref _initializationFailure, reloaded.Error);
        if (!reloaded.Succeeded || command == "reload") return Response(reloaded);

        // Only bootstrap/repair opt into startup activation. A normal runtime
        // refresh preserves drives the user deliberately disconnected.
        if (request.Confirmed) QueueEligibleAutoMounts(token);
        return Status();
    }

    private async Task<HostResponse> HandleDriveCommandAsync(string command, HostRequest request, CancellationToken token)
    {
        if (_mounts is null)
            return new(false, "host.not_ready", "The background host is not ready.");
        if (request.MountId is not Guid mountGuid || mountGuid == Guid.Empty)
            return new(false, "host.mount_id", "A valid mount ID is required.");

        var mountId = new MountId(mountGuid);
        if (command is "run-sync" or "cancel-sync")
            return await HandleSyncCommandAsync(command, request, mountId, token).ConfigureAwait(false);
        if (command == "stop")
            return await HandleStopCommandAsync(request, mountId, _mounts, token).ConfigureAwait(false);

        return QueueMountCommand(command, request, mountId, _mounts, token);
    }

    private async Task<HostResponse> HandleSyncCommandAsync(string command, HostRequest request, MountId mountId, CancellationToken token)
    {
        if (_syncs is null || request.SyncJobId is not Guid syncGuid || syncGuid == Guid.Empty)
            return new(false, "host.sync_job_id", "A valid sync job ID is required.");
        var syncId = new SyncJobId(syncGuid);
        if (!_definitions.Any(definition => definition.Id == mountId && definition.SyncJobs.Any(job => job.Id == syncId)))
            return new(false, "sync.not_found", "The sync job no longer exists on this drive.");

        if (command == "run-sync")
            return QueueSync(mountId, syncId, token) ? Status()
                : new(false, "host.operation_in_progress", "This sync job is already queued or running.");

        CancelOperation(SyncKey(syncId));
        var cancelled = await _syncs.CancelAsync(mountId, syncId, token).ConfigureAwait(false);
        if (cancelled.Error?.Code == "sync.not_running")
            await _syncs.MarkCancelledAsync(mountId, syncId).ConfigureAwait(false);
        return cancelled.Succeeded || cancelled.Error?.Code == "sync.not_running" ? Status() : Response(cancelled);
    }

    private async Task<HostResponse> HandleStopCommandAsync(HostRequest request, MountId mountId,
        RcloneMountCoordinator coordinator, CancellationToken token)
    {
        var key = MountKey(mountId);
        var stoppingConnectionWait = _connectionRetryOperations.ContainsKey(key) ||
            coordinator.GetSnapshots().Any(snapshot => snapshot.MountId == mountId &&
                snapshot.Lifecycle == MountLifecycle.WaitingForConnection);

        // A retry may be queued, probing, or entering mount readiness. Cancel it
        // before the upload-check gate, while retaining every normal stop guard.
        if (stoppingConnectionWait) CancelOperation(key);
        var target = coordinator.ValidateStopTarget(mountId);
        if (!target.Succeeded) return Response(target);
        if (!request.AllowPendingUploads)
        {
            var uploads = await coordinator.CheckPendingUploadsForMountAsync(mountId, token).ConfigureAwait(false);
            if (!uploads.Succeeded) return Response(uploads);
        }

        if (!stoppingConnectionWait) return QueueMountCommand("stop", request, mountId, coordinator, token);

        // Do not put Stop behind the queued retry's own operation key.
        var stopped = await coordinator.StopAsync(mountId, request.AllowPendingUploads, token).ConfigureAwait(false);
        return stopped.Succeeded ? Status() : Response(stopped);
    }

    private HostResponse QueueMountCommand(string command, HostRequest request, MountId mountId,
        RcloneMountCoordinator coordinator, CancellationToken token)
    {
        Func<CancellationToken, Task<OperationResult>>? action = command switch
        {
            "start" => cancellation => coordinator.StartAsync(mountId, cancellation),
            "stop" => cancellation => coordinator.StopAsync(mountId, request.AllowPendingUploads, cancellation),
            "restart" => cancellation => coordinator.RestartAsync(mountId, cancellation),
            _ => null,
        };
        if (action is null) return new(false, "host.command", $"Unknown host command '{request.Command}'.");

        return Queue(MountKey(mountId), action, token,
            () => coordinator.MarkPending(mountId, command is "stop" or "restart"))
            ? Status()
            : new(false, "host.operation_in_progress", "A mount operation is already queued or running.");
    }
}
