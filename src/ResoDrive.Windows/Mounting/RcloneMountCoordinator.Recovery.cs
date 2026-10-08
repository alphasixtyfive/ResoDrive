using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed partial class RcloneMountCoordinator
{
    public static async Task<OperationResult> CheckOfflineRecoveryAsync(ApplicationPaths paths, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var inspectionToken = deadline.Token;
            using var recovery = new MountRecoveryStore(paths);
            using var ownership = new MountOwnershipStore(paths);
            var owned = await ownership.LoadAsync(inspectionToken).ConfigureAwait(false);
            var metadata = Path.Combine(paths.Cache, "vfsMeta");
            var data = Path.Combine(paths.Cache, "vfs");
            ManagedDataPath.ValidateAncestors(metadata);
            ManagedDataPath.ValidateAncestors(data);
            var unknownCache = false;
            if (Directory.Exists(data))
            {
                var directories = new Stack<string>();
                directories.Push(data);
                var inspected = 0;
                while (directories.TryPop(out var directory))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        inspectionToken.ThrowIfCancellationRequested();
                        if (++inspected > 100_000) throw new IOException("Cache data exceeds the inspection limit.");
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache data contains a linked path.");
                        if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                        else unknownCache |= !File.Exists(Path.Combine(metadata, Path.GetRelativePath(data, entry)));
                    }
                }
            }
            var dirty = false;
            if (Directory.Exists(metadata))
            {
                foreach (var directory in Directory.EnumerateDirectories(metadata))
                    dirty |= DirtyCacheReader.Read(directory, paths.Cache, inspectionToken).Files.Count > 0;
                dirty |= Directory.EnumerateFiles(metadata).Any();
            }
            return recovery.GetEntries().Count > 0 || owned.Any(mount => !MountOwnershipStore.IsDefinitelyGone(mount)) || dirty || unknownCache
                ? Result.Failure("mount.upload_status_unknown", "Cached files may still need recovery. Restore the drive configuration before updating.", true)
                : Result.Success();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Result.Failure("mount.upload_status_unknown", "Cached uploads could not be checked in time.", true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            return Result.Failure("mount.upload_status_unknown", "Cached uploads could not be checked.", true);
        }
    }

    private async Task<bool> InspectAndRetireCleanRecoveryAsync(CancellationToken cancellationToken)
    {
        if (!_unverifiedOwnedWork.IsEmpty)
        {
            foreach (var item in _unverifiedOwnedWork.ToArray())
            {
                var gate = OperationGate(new MountId(item.Key));
                if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) continue;
                try
                {
                    if (MountOwnershipStore.IsDefinitelyGone(item.Value))
                    {
                        _unverifiedOwnedWork.TryRemove(item.Key, out _);
                        await _ownership.RemoveAsync(item.Key, cancellationToken).ConfigureAwait(false);
                        var id = new MountId(item.Key);
                        if (_recovery.Find(id) is null) ClearRecoverySnapshot(id);
                    }
                }
                finally { gate.Release(); }
            }
        }
        var pendingRecovery = false;
        foreach (var entry in _recovery.GetEntries())
        {
            var id = new MountId(entry.MountId);
            var gate = OperationGate(id);
            if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { pendingRecovery = true; continue; }
            try
            {
                if (_sessions.ContainsKey(id)) continue;
                if (_unverifiedOwnedWork.ContainsKey(entry.MountId)) { pendingRecovery = true; continue; }
                if (_recovery.Find(id) is not { } current) continue;
                if (await RecoveryCacheIsCleanAsync(current, cancellationToken).ConfigureAwait(false))
                {
                    await _recovery.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
                    ClearRecoverySnapshot(id);
                }
                else pendingRecovery = true;
            }
            finally { gate.Release(); }
        }
        return pendingRecovery;
    }

    private async Task RecoverOwnedProcessesAsync(Dictionary<MountId, MountDefinition> definitions, CancellationToken cancellationToken)
    {
        foreach (var owned in await _ownership.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            var mountId = new MountId(owned.MountId);
            var operationGate = OperationGate(mountId);
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_sessions.ContainsKey(mountId)) continue;
                if (!definitions.TryGetValue(mountId, out var definition) ||
                    owned.Source != Source(definition) ||
                    (_recovery.Find(mountId) is { } journal && !MountRecoveryStore.Matches(journal, definition)) ||
                    !owned.Target.Equals(Target(definition.Target), StringComparison.OrdinalIgnoreCase) ||
                    !MountOwnershipStore.IsSameExecutablePath(owned.ExecutablePath, _rclonePath))
                {
                    using var previous = MountOwnershipStore.TryOpenVerified(owned);
                    if (previous is not null || !MountOwnershipStore.IsDefinitelyGone(owned))
                    {
                        await _recovery.RecordOrphanAsync(owned, cancellationToken).ConfigureAwait(false);
                        _unverifiedOwnedWork[owned.MountId] = owned;
                        Publish(new MountSnapshot { MountId = mountId, Lifecycle = MountLifecycle.Failed,
                            UploadStatusStale = true, UploadRecoveryRequired = true,
                            StatusText = "A surviving drive belongs to an earlier configuration. Restore that configuration and preserve its cache." });
                    }
                    else
                        await _ownership.RemoveAsync(owned.MountId, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var process = MountOwnershipStore.TryOpenVerified(owned);
                if (process is null)
                {
                    if (!MountOwnershipStore.IsDefinitelyGone(owned))
                    {
                        await _recovery.RecordOrphanAsync(owned, cancellationToken).ConfigureAwait(false);
                        _unverifiedOwnedWork[owned.MountId] = owned;
                        Publish(Snapshot(definition, MountLifecycle.Failed, "A surviving drive cannot be verified. Preserve its cache and restart ResoDrive with its original permissions.") with
                            { UploadRecoveryRequired = true, UploadStatusStale = true });
                        continue;
                    }
                    // Migrate a pre-journal launch record after a crash as well, so a
                    // manually mounted Never drive can restore its dirty cache.
                    if (_recovery.Find(mountId) is null)
                        await _recovery.RecordAsync(definition, null, cancellationToken).ConfigureAwait(false);
                    await _ownership.RemoveAsync(owned.MountId, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var storedControl = await _controls.LoadAsync(owned, cancellationToken).ConfigureAwait(false);
                var control = storedControl is null ? null : new RcloneControl(storedControl.Address, storedControl.User, storedControl.Password);
                var session = new Session(process, definition, control);
                _sessions[mountId] = session;
                _unverifiedOwnedWork.TryRemove(owned.MountId, out _);
                Publish(Snapshot(definition, MountLifecycle.Degraded, "Recovered drive · Checking readiness") with
                    { UploadRecoveryRequired = true, UploadStatusStale = true });
                TrackExitObserver(ObserveExitAsync(session));
            }
            finally { operationGate.Release(); }
        }
    }

    private async Task ResumePendingCacheRecoveryAsync(Dictionary<MountId, MountDefinition> definitions, CancellationToken token)
    {
        foreach (var entry in _recovery.GetEntries())
        {
            var id = new MountId(entry.MountId);
            var operationGate = OperationGate(id);
            await operationGate.WaitAsync(token).ConfigureAwait(false);
            var reconnect = false;
            try
            {
                if (_sessions.ContainsKey(id) || _unverifiedOwnedWork.ContainsKey(entry.MountId) ||
                    _recovery.Find(id) is not { } current) continue;
                // The exit observer owns this delayed reconnect and its backoff.
                // Reconciliation must not launch a second recovery attempt early.
                if (_snapshots.TryGetValue(id, out var pending) && pending.Lifecycle == MountLifecycle.WaitingToRestart)
                    continue;
                if (await RecoveryCacheIsCleanAsync(current, token).ConfigureAwait(false))
                {
                    await _recovery.RemoveAsync(id, token).ConfigureAwait(false);
                    ClearRecoverySnapshot(id);
                    continue;
                }
                if (_pausedRecovery.ContainsKey(id)) continue;
                if (definitions.TryGetValue(id, out var definition) && definition.Enabled && MountRecoveryStore.Matches(current, definition))
                    reconnect = true;
                else
                    Publish(new MountSnapshot
                    {
                        MountId = id, Lifecycle = MountLifecycle.Failed,
                        UploadRecoveryRequired = true, UploadStatusStale = true,
                        StatusText = $"Pending cache for {current.DisplayName}. Restore and enable the original drive configuration to finish its uploads."
                    });
            }
            finally { operationGate.Release(); }
            // Starting takes this same operation gate. Release inspection first.
            if (reconnect) await StartInternalAsync(id, StartIntent.CacheRecovery, token).ConfigureAwait(false);
        }
    }

    private async Task<bool> RecoveryCacheIsCleanAsync(MountRecoveryEntry entry, CancellationToken token)
    {
        if (entry.MetadataPath is null) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var status = await Task.Run(() => DirtyCacheReader.Read(entry.MetadataPath, _paths.Cache, timeout.Token), timeout.Token).ConfigureAwait(false);
            return status.Files.Count == 0;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private void ClearRecoverySnapshot(MountId id)
    {
        if (_unverifiedOwnedWork.ContainsKey(id.Value) || _sessions.ContainsKey(id)) return;
        if (_definitions.TryGetValue(id, out var definition))
            Publish(Snapshot(definition, MountLifecycle.Stopped, "Recovered uploads completed") with
                { UploadsQueued = 0, UploadsInProgress = 0, UploadsDirty = 0, UploadObservedAt = DateTimeOffset.UtcNow });
        else _snapshots.TryRemove(id, out _);
    }

}
