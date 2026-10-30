using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Windows;

namespace ResoDrive.Host;

public sealed partial class Worker
{
    private const string MountOperationPrefix = "mount:";
    private const string SyncOperationPrefix = "sync:";

    private void QueueConnectionRetries(CancellationToken token)
    {
        var coordinator = _mounts;
        if (coordinator is null || _shutdownRequested) return;
        foreach (var candidate in coordinator.GetConnectionRetryCandidates())
        {
            var key = MountKey(candidate.MountId);
            Queue(key, cancellation => coordinator.RetryConnectionAsync(candidate, cancellation), token,
                () => _connectionRetryOperations[key] = _operations[key]);
        }
    }

    private bool QueueSync(MountId mountId, SyncJobId syncId, CancellationToken token)
    {
        var coordinator = _syncs;
        return coordinator is not null && Queue(SyncKey(syncId), async operationToken =>
        {
            var result = await coordinator.RunAsync(mountId, syncId, operationToken).ConfigureAwait(false);
            // Every dequeued scheduled attempt consumes its interval. Retrying launch,
            // configuration, or access failures on every 30-second scheduler tick can
            // otherwise create an unbounded error loop on unattended machines.
            _scheduleAnchors[syncId] = DateTimeOffset.UtcNow;
            await SaveScheduleStateAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }, token, () => coordinator.MarkQueued(mountId, syncId));
    }

    private bool Queue(
        string key,
        Func<CancellationToken, Task<OperationResult>> action,
        CancellationToken token,
        Action? onQueued = null)
    {
        if (_shutdownRequested)
            return false;
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!_operations.TryAdd(key, source))
        {
            source.Dispose();
            return false;
        }
        onQueued?.Invoke();
        UpdateSessionProtection();
        // Preflight can enter synchronous filesystem code before its first await.
        // Keep it off the command thread so the handler can release the reload gate promptly.
        Track(Task.Run(() => RunAsync(key, source, action), CancellationToken.None));
        return true;
    }

    private async Task RunAsync(string key, CancellationTokenSource source, Func<CancellationToken, Task<OperationResult>> action)
    {
        var slots = key.StartsWith(SyncOperationPrefix, StringComparison.Ordinal) ? _syncSlots : _mountSlots;
        try
        {
            if (!await AcquireOperationSlotAsync(key, slots, source.Token).ConfigureAwait(false)) return;
            try
            {
                var result = await action(source.Token).ConfigureAwait(false);
                if (!result.Succeeded && result.Error?.Code != "sync.cancelled")
                {
                    ReportMountOperationFailure(key, result.Error?.Message ?? "The drive operation could not finish. Retry the drive.");
                    LogOperationFailure(_logger, key, result.Error?.Code, result.Error?.Message);
                }
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            ReportMountOperationFailure(key, "The drive operation was cancelled. Retry the drive when ready.");
        }
        catch (Exception exception)
        {
            ReportMountOperationFailure(key, "The drive operation could not finish. Retry the drive.");
            LogOperationException(_logger, key, exception);
        }
        finally
        {
            _connectionRetryOperations.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, source));
            if (_operations.TryGetValue(key, out var current) && ReferenceEquals(current, source))
            {
                _operations.TryRemove(key, out _);
            }
            source.Dispose();
            UpdateSessionProtection();
        }
    }

    private async Task<bool> AcquireOperationSlotAsync(string key, SemaphoreSlim slots, CancellationToken token)
    {
        if (ReferenceEquals(slots, _syncSlots))
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            return true;
        }
        if (await slots.WaitAsync(MountQueueTimeout, token).ConfigureAwait(false)) return true;
        ReportMountOperationFailure(key, "The drive could not start because other drive operations are taking too long. Retry the drive.");
        return false;
    }

    private void CancelOperation(string key)
    {
        if (_operations.TryGetValue(key, out var source)) CancelOperation(source);
    }

    private static void CancelOperation(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException)
        {
            // A completing operation can remove and dispose its source concurrently.
        }
    }

    private void ReportMountOperationFailure(string key, string message)
    {
        if (TryGetOperationMountId(key, out var id)) _mounts?.ReportOperationFailure(id, message);
    }

    private bool HasBlockingOperations(IReadOnlyList<MountSnapshot> mounts)
    {
        foreach (var key in _operations.Keys)
        {
            if (!TryGetOperationMountId(key, out var id)) return true;
            if (!mounts.Any(snapshot => snapshot.MountId == id && IsIdleConnectionWait(snapshot))) return true;
        }
        return false;
    }

    private static bool TryGetOperationMountId(string key, out MountId id)
    {
        id = default;
        if (!key.StartsWith(MountOperationPrefix, StringComparison.Ordinal) ||
            !Guid.TryParse(key.AsSpan(MountOperationPrefix.Length), out var value)) return false;
        id = new MountId(value);
        return true;
    }

    private static bool IsIdleConnectionWait(MountSnapshot snapshot) => snapshot is
    {
        Lifecycle: MountLifecycle.WaitingForConnection, UploadRecoveryRequired: false,
        UploadStatusStale: false, UploadStatusChecking: false, UploadObservedAt: not null,
        UploadsQueued: 0, UploadsInProgress: 0, UploadsDirty: 0, UploadErrors: 0,
        Uploads: { Count: 0 }, UploadDetailsTruncated: false,
    };

    private static string MountKey(MountId id) => $"{MountOperationPrefix}{id.Value:N}";
    private static string SyncKey(SyncJobId id) => $"{SyncOperationPrefix}{id.Value:N}";

}
