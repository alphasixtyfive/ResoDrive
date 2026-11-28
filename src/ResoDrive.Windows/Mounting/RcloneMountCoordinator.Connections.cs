using System.Collections.Concurrent;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;

namespace ResoDrive.Windows;

public sealed record MountConnectionRetry(MountId MountId, long Generation);

public sealed partial class RcloneMountCoordinator
{
    private sealed record ConnectionWait(long Generation, StartIntent Intent, long RetryTimestamp, int UnreachableProbes)
    {
        // The first probe owns the request until it either launches or schedules a retry.
        internal static ConnectionWait BeforeFirstProbe(long generation, StartIntent intent) => new(generation, intent, long.MaxValue, 0);
    }

    // A generation belongs to one connection request. Stop or a newer request
    // invalidates older queued work, even when that work finishes a probe later.
    private readonly ConcurrentDictionary<MountId, long> _connectionGenerations = new();
    private readonly ConcurrentDictionary<MountId, ConnectionWait> _waitingConnections = new();
    private readonly ConcurrentDictionary<MountId, MountConnectionEndpoint> _connectionEndpoints = new();
    private static readonly string[] EndpointMetadataKeys = ["type", "url", "host", "port"];
    private static readonly TimeSpan[] ConnectionRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];
    internal TimeProvider ConnectionClock { get; init; } = TimeProvider.System;
    internal Func<MountConnectionEndpoint, CancellationToken, Task<bool>> ConnectionProbe { get; init; } = MountConnectionReachability.IsReachableAsync;
    internal Func<IReadOnlyList<string>, bool> DirectConnectionProbeAllowed { get; init; } = arguments => MountConnectionReachability.CanProbeDirectly(arguments);

    public IReadOnlyList<MountConnectionRetry> GetConnectionRetryCandidates()
    {
        var now = ConnectionClock.GetTimestamp();
        return _waitingConnections.Where(pair => pair.Value.RetryTimestamp <= now && ConnectionRequestIsCurrent(pair.Key, pair.Value.Generation))
            .Select(pair => new MountConnectionRetry(pair.Key, pair.Value.Generation)).ToArray();
    }

    public Task<OperationResult> RetryConnectionAsync(MountConnectionRetry retry, CancellationToken cancellationToken = default)
    {
        if (!_waitingConnections.TryGetValue(retry.MountId, out var wait) || wait.Generation != retry.Generation)
            return Task.FromResult(Result.Success());
        return StartCoreAsync(retry.MountId, wait.Intent, retry.Generation, wait, cancellationToken);
    }

    public void NotifyNetworkChanged()
    {
        var now = ConnectionClock.GetTimestamp();
        foreach (var pair in _waitingConnections)
            _waitingConnections.TryUpdate(pair.Key, pair.Value with { RetryTimestamp = now }, pair.Value);
    }

    private long NextConnectionGeneration(MountId id) =>
        _connectionGenerations.AddOrUpdate(id, 1, static (_, generation) => generation + 1);

    private bool ConnectionRequestIsCurrent(MountId id, long generation) =>
        !_lifetime.IsCancellationRequested && _connectionGenerations.TryGetValue(id, out var current) && current == generation;

    private void RetireConnectionWait(MountId id)
    {
        NextConnectionGeneration(id);
        _waitingConnections.TryRemove(id, out _);
        _restartAttempts.TryRemove(id, out _);
    }

    private void RestoreConnectionWait(MountId id, ConnectionWait? previous, long generation)
    {
        if (previous is null || !_definitions.ContainsKey(id) || !ConnectionRequestIsCurrent(id, generation)) return;

        _waitingConnections[id] = previous with
        {
            Generation = generation,
            RetryTimestamp = Math.Min(previous.RetryTimestamp, ConnectionTimestampAfter(ConnectionRetryDelays[0])),
        };
    }

    private void SetConnectionEndpoint(MountDefinition definition, IReadOnlyDictionary<string, string> values)
    {
        if (MountConnectionReachability.UsesCustomTransport(values))
        {
            _connectionEndpoints.TryRemove(definition.Id, out _);
            return;
        }
        // Reuse the existing allowlisted metadata parser; credentials never enter
        // the endpoint cache or network probe.
        var lines = EndpointMetadataKeys.Where(values.ContainsKey)
            .Select(key => key + " = " + values[key]);
        SetConnectionEndpoint(definition, RcloneConnectionMetadataService.Parse("[selected]\n" + string.Join('\n', lines))
            .GetValueOrDefault("selected"));
    }

    private void SetConnectionEndpoint(MountDefinition definition, RcloneConnectionMetadata? metadata)
    {
        var endpoint = DirectConnectionProbeAllowed(definition.Arguments) ? MountConnectionEndpoint.FromMetadata(metadata) : null;
        if (endpoint is null) _connectionEndpoints.TryRemove(definition.Id, out _);
        else _connectionEndpoints[definition.Id] = endpoint;
    }

    private async Task<bool> CanAttemptConnectionAsync(MountDefinition definition, StartIntent intent, long generation,
        ConnectionWait? previous, CancellationToken token)
    {
        if (!ConnectionRequestIsCurrent(definition.Id, generation)) return false;
        if (!_connectionEndpoints.TryGetValue(definition.Id, out var endpoint)) return true;

        var checking = previous ?? ConnectionWait.BeforeFirstProbe(generation, intent);
        _waitingConnections[definition.Id] = checking;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var reachable = await ConnectionProbe(endpoint, linked.Token).ConfigureAwait(false);
        if (!ConnectionRequestIsCurrent(definition.Id, generation)) return false;
        if (reachable) return true;

        ScheduleConnectionRetry(definition.Id, checking);
        PublishConnectionWait(definition);
        return false;
    }

    private void ScheduleConnectionRetry(MountId id, ConnectionWait wait)
    {
        var unreachableProbes = Math.Min(wait.UnreachableProbes + 1, ConnectionRetryDelays.Length);
        _waitingConnections[id] = wait with
        {
            UnreachableProbes = unreachableProbes,
            RetryTimestamp = ConnectionTimestampAfter(ConnectionRetryDelays[unreachableProbes - 1]),
        };
    }

    private long ConnectionTimestampAfter(TimeSpan delay) =>
        ConnectionClock.GetTimestamp() + (long)(delay.TotalSeconds * ConnectionClock.TimestampFrequency);

    private void PublishConnectionWait(MountDefinition definition)
    {
        var lastSnapshot = _snapshots.GetValueOrDefault(definition.Id);
        var hasRecoveryRecord = _recovery.Find(definition.Id) is not null;
        var snapshot = (lastSnapshot ?? Snapshot(definition, MountLifecycle.WaitingForConnection, "Waiting for connection")) with
        {
            Lifecycle = MountLifecycle.WaitingForConnection,
            StatusText = "Waiting for connection",
            UploadStatusChecking = false,
            UploadStatusStale = hasRecoveryRecord,
            UploadSpeedBytesPerSecond = null, UploadEtaSeconds = null,
            // This is an eligible original drive waiting for its server, not a
            // mismatched/disabled recovery requiring user intervention.
            UploadRecoveryRequired = false,
        };
        if (!hasRecoveryRecord)
            snapshot = snapshot with
            {
                UploadsQueued = 0, UploadsInProgress = 0, UploadsDirty = 0, UploadErrors = 0,
                Uploads = [], UploadDetailsTruncated = false, UploadBytesRemaining = 0,
                UploadObservedAt = ConnectionClock.GetUtcNow(),
            };
        Publish(snapshot);
    }
}
