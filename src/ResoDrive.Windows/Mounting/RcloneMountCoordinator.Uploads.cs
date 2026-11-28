using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed partial class RcloneMountCoordinator
{
    public async Task<OperationResult> CheckPendingUploadsForMountAsync(MountId id, CancellationToken token = default)
    {
        var gate = OperationGate(id);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_definitions.ContainsKey(id)) return Result.Failure("mount.not_found", "The drive no longer exists.");
            var pending = _unverifiedOwnedWork.ContainsKey(id.Value);
            if (_sessions.TryGetValue(id, out var session))
            {
                var status = await ReadTransfersAsync(session, token).ConfigureAwait(false);
                pending |= status.BlocksStop || status.Generation != Volatile.Read(ref session.CacheGeneration);
            }
            else if (_recovery.Find(id) is { } entry)
                pending |= !await RecoveryCacheIsCleanAsync(entry, token).ConfigureAwait(false);
            pending |= _snapshots.TryGetValue(id, out var snapshot) &&
                snapshot.Lifecycle is MountLifecycle.Starting or MountLifecycle.Stopping or MountLifecycle.WaitingToRestart;
            return pending ? Result.Failure("mount.uploads_pending", "Some files are still waiting to upload, or upload status could not be confirmed.", true) : Result.Success();
        }
        finally { gate.Release(); }
    }

    public OperationResult ValidateStopTarget(MountId id) =>
        !_definitions.ContainsKey(id) ? Result.Failure("mount.not_found", "The drive no longer exists.") :
        _unverifiedOwnedWork.ContainsKey(id.Value)
            ? Result.Failure("mount.unverified_process", "The surviving drive process could not be verified. Preserve its cache and reconnect with its original permissions.", true)
            : Result.Success();

    public async Task<OperationResult> CheckPendingUploadsAsync(CancellationToken cancellationToken = default)
    {
        var pendingRecovery = await InspectAndRetireCleanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        var sessions = _sessions.Values.ToArray();
        var statuses = await Task.WhenAll(sessions.Select(session =>
            ReadTransfersAsync(session, cancellationToken))).ConfigureAwait(false);
        var changed = sessions.Where((session, index) => statuses[index].Generation != Volatile.Read(ref session.CacheGeneration)).Any();
        return !_unverifiedOwnedWork.IsEmpty || pendingRecovery || changed || statuses.Any(status => status.BlocksStop) ||
            _snapshots.Values.Any(snapshot => snapshot.Lifecycle is MountLifecycle.Starting or MountLifecycle.Stopping or MountLifecycle.WaitingToRestart)
            ? Result.Failure("mount.uploads_pending", "Files are still uploading or the cache needs attention. Close open documents, let uploads finish, then try again.", true)
            : Result.Success();
    }

    public async Task RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(_sessions.Values.Select(session => RefreshHealthAsync(session, cancellationToken))).ConfigureAwait(false);
        await InspectAndRetireCleanRecoveryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshHealthAsync(Session session, CancellationToken cancellationToken)
    {
        // Idle cache trees need not be walked every second. A metadata watcher event
        // immediately invalidates this shortcut; active or uncertain work still polls
        // every tick. Five seconds stays inside the host's ten-second safety horizon.
        if (_snapshots.TryGetValue(session.Definition.Id, out var recent) &&
            recent is { Lifecycle: MountLifecycle.Mounted, UploadStatusStale: false, UploadStatusChecking: false,
                UploadsQueued: 0, UploadsInProgress: 0, UploadsDirty: 0, UploadErrors: 0, UploadRecoveryRequired: false,
                UploadObservedAt: { } observed } &&
            DateTimeOffset.UtcNow >= observed && DateTimeOffset.UtcNow - observed < TimeSpan.FromSeconds(5))
            return;
        var gate = OperationGate(session.Definition.Id);
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;
        try
        {
            if (session.Stopping && ProcessTermination.HasExitedOrUnavailable(session.Process))
            {
                await CompleteStoppedSessionAsync(session).ConfigureAwait(false);
                if (_recovery.Find(session.Definition.Id) is { } entry &&
                    !await RecoveryCacheIsCleanAsync(entry, cancellationToken).ConfigureAwait(false))
                    PublishRecoveryPaused(session.Definition);
                return;
            }
            if (session.Stopping || ProcessTermination.HasExitedOrUnavailable(session.Process))
                return;
            var ready = await ProbeReadyAsync(session, cancellationToken).ConfigureAwait(false);
            var transfers = await ReadTransfersAsync(session, cancellationToken).ConfigureAwait(false);
            if (session.Stopping || ProcessTermination.HasExitedOrUnavailable(session.Process))
                return;
            PublishTransfers(session, Snapshot(session.Definition, ready && !transfers.HasCacheError ? MountLifecycle.Mounted : MountLifecycle.Degraded,
                ready
                    ? transfers.Description
                    : "Drive is not responding · " + transfers.Description + " · Checking again automatically"), transfers);
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (!session.Stopping && !ProcessTermination.HasExitedOrUnavailable(session.Process))
                PublishTransfers(session, Snapshot(session.Definition, MountLifecycle.Degraded, "Drive status unavailable · Checking again automatically"),
                    new(null, session.LastTransfers) { Generation = Volatile.Read(ref session.CacheGeneration) });
        }
        finally { gate.Release(); }
    }

    private async Task<MountUploadObservation> ReadTransfersAsync(Session session, CancellationToken cancellationToken)
    {
        await session.TransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref session.CacheGeneration);
            var previousTransfers = session.LastTransfers;
            var current = session.Control is { } control && !ProcessTermination.HasExitedOrUnavailable(session.Process)
                ? await VfsStatusReader.ReadAsync(control.Address, control.User, control.Password, _paths.Cache, cancellationToken).ConfigureAwait(false)
                : null;
            if (current is not null)
            {
                if (_recovery.Find(session.Definition.Id) is { MetadataPath: not null } previous &&
                    !string.Equals(previous.MetadataPath, current.MetadataPath, StringComparison.OrdinalIgnoreCase) &&
                    !await RecoveryCacheIsCleanAsync(previous, cancellationToken).ConfigureAwait(false))
                    return new(null, session.LastTransfers) { Generation = generation };
                await _recovery.RecordAsync(session.Definition, current.MetadataPath, cancellationToken).ConfigureAwait(false);
                // A changing cache prevents a safe-stop verdict, but does not erase
                // the transfer counters just measured from the local control API.
                if (generation != Volatile.Read(ref session.CacheGeneration))
                {
                    var changed = new MountUploadObservation(current, previousTransfers) { Generation = generation }.DuringCacheChange();
                    session.LastTransfers = changed.Current ?? changed.LastKnown;
                    return changed;
                }
                session.LastTransfers = current;
            }
            return new(current, current is null ? session.LastTransfers : previousTransfers) { Generation = generation };
        }
        finally { session.TransferGate.Release(); }
    }

    private void PublishTransfers(Session session, MountSnapshot snapshot, MountUploadObservation observation)
    {
        lock (session.StatusGate)
        {
            if (observation.Generation != Volatile.Read(ref session.CacheGeneration))
            {
                observation = observation.DuringCacheChange();
                session.LastTransfers = observation.Current ?? observation.LastKnown;
            }
            Publish(snapshot.WithTransfers(observation));
        }
    }

    private void CacheChanged(object sender, FileSystemEventArgs args)
    {
        var metadataRoot = Path.Combine(_paths.Cache, "vfsMeta") + Path.DirectorySeparatorChar;
        if (args.FullPath.StartsWith(metadataRoot, StringComparison.OrdinalIgnoreCase) ||
            args.FullPath.Equals(Path.TrimEndingDirectorySeparator(metadataRoot), StringComparison.OrdinalIgnoreCase) ||
            args is RenamedEventArgs renamed && renamed.OldFullPath.StartsWith(metadataRoot, StringComparison.OrdinalIgnoreCase))
            InvalidateUploadStatus(args.FullPath, (args as RenamedEventArgs)?.OldFullPath);
    }

    private void CacheWatcherError(object sender, ErrorEventArgs args) => InvalidateUploadStatus();

    private void InvalidateUploadStatus(string? changedPath = null, string? oldPath = null)
    {
        var changed = false;
        foreach (var session in _sessions.Values)
        {
            if (session.Stopping) continue;
            lock (session.StatusGate)
            {
                var metadata = session.LastTransfers?.MetadataPath ?? _recovery.Find(session.Definition.Id)?.MetadataPath;
                if (!CacheChangeAffectsMount(metadata, changedPath, oldPath)) continue;
                Interlocked.Increment(ref session.CacheGeneration);
                if (_snapshots.TryGetValue(session.Definition.Id, out var snapshot))
                {
                    Publish(snapshot with { UploadStatusStale = true, UploadStatusChecking = true });
                    changed = true;
                }
            }
        }
        if (changed) UploadStatusInvalidated?.Invoke(this, EventArgs.Empty);
    }

    internal static bool CacheChangeAffectsMount(string? metadata, string? changedPath, string? oldPath = null)
    {
        // Overflow, or a drive whose cache location is not yet known, requires
        // checking every session. Directory changes also affect their descendants.
        if (metadata is null || changedPath is null) return true;
        return Overlaps(metadata, changedPath) || oldPath is not null && Overlaps(metadata, oldPath);

        static bool Overlaps(string first, string second)
        {
            first = Path.TrimEndingDirectorySeparator(first);
            second = Path.TrimEndingDirectorySeparator(second);
            return first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
                first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }

}
