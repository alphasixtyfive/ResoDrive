using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Core.Validation;

namespace ResoDrive.Windows;

public sealed partial class RcloneMountCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan GracefulStopCommandTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan GracefulStopExitTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ForcedStopTimeout = TimeSpan.FromSeconds(3);
    private readonly string _rclonePath;
    private readonly string _configPath;
    private readonly ApplicationPaths _paths;
    private string _clientUserAgent;
    private readonly IMountTargetInventory _inventory;
    private readonly MountOwnershipStore _ownership;
    private readonly MountRecoveryStore _recovery;
    private readonly MountControlStore _controls;
    private readonly FileSystemWatcher _cacheWatcher;
    private readonly ConcurrentDictionary<Guid, OwnedMount> _unverifiedOwnedWork = new();
    private readonly ConcurrentDictionary<MountId, MountDefinition> _definitions = new();
    private readonly ConcurrentDictionary<MountId, Session> _sessions = new();
    private readonly ConcurrentDictionary<MountId, MountSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<MountId, int> _restartAttempts = new();
    private readonly ConcurrentDictionary<MountId, SemaphoreSlim> _operationGates = new();
    private readonly ConcurrentDictionary<Task, byte> _exitObservers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private enum RecoveryPause { Disconnected, ReconnectPolicy }
    private readonly ConcurrentDictionary<MountId, RecoveryPause> _pausedRecovery = new();
    private bool _recovered;

    public RcloneMountCoordinator(string rclonePath, string configPath, ApplicationPaths paths, IMountTargetInventory targetInventory)
        : this(rclonePath, configPath, paths, targetInventory, ClientUserAgent.Value)
    {
    }

    public RcloneMountCoordinator(string rclonePath, string configPath, ApplicationPaths paths,
        IMountTargetInventory targetInventory, string clientUserAgent)
    {
        _rclonePath = Path.GetFullPath(rclonePath);
        _configPath = Path.GetFullPath(configPath);
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _inventory = targetInventory ?? throw new ArgumentNullException(nameof(targetInventory));
        _clientUserAgent = string.IsNullOrWhiteSpace(clientUserAgent)
            ? throw new ArgumentException("A client User-Agent is required.", nameof(clientUserAgent))
            : clientUserAgent;
        _paths.EnsureCreated();
        _ownership = new(paths);
        _recovery = new(paths);
        _controls = new(paths);
        _cacheWatcher = new FileSystemWatcher(paths.Cache)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        _cacheWatcher.Changed += CacheChanged;
        _cacheWatcher.Created += CacheChanged;
        _cacheWatcher.Deleted += CacheChanged;
        _cacheWatcher.Renamed += CacheChanged;
        _cacheWatcher.Error += CacheWatcherError;
        _cacheWatcher.EnableRaisingEvents = true;
    }

    public event EventHandler? UploadStatusInvalidated;

    public IReadOnlyList<MountSnapshot> GetSnapshots() => _snapshots.Values.OrderBy(x => x.MountId.Value).ToArray();

    public void SetClientUserAgent(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Volatile.Write(ref _clientUserAgent, value);
    }

    public async Task<OperationResult> ReconcileAsync(IReadOnlyList<MountDefinition> definitions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var validation = new MountDefinitionValidator().ValidateCatalog(definitions);
        if (!validation.IsValid)
        {
            return Result.Failure("mount.catalog_invalid", validation.Issues[0].Message);
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var incoming = definitions.ToDictionary(x => x.Id);
            foreach (var old in _definitions.ToArray())
            {
                if (!incoming.TryGetValue(old.Key, out var next) || LaunchChanged(old.Value, next))
                {
                    var restoresRecovery = next is not null && !_sessions.ContainsKey(old.Key) &&
                        _recovery.Find(old.Key) is { } pending && MountRecoveryStore.Matches(pending, next);
                    var operationGate = OperationGate(old.Key);
                    await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var stopped = restoresRecovery ? Result.Success() :
                            await StopCoreAsync(old.Key, old.Value, cancellationToken).ConfigureAwait(false);
                        if (!stopped.Succeeded)
                            return stopped;
                    }
                    finally
                    {
                        operationGate.Release();
                    }
                }
                if (!incoming.ContainsKey(old.Key))
                {
                    _definitions.TryRemove(old.Key, out _);
                    _snapshots.TryRemove(old.Key, out _);
                    _restartAttempts.TryRemove(old.Key, out _);
                    _pausedRecovery.TryRemove(old.Key, out _);
                }
                else if (next is not null && old.Value.Restart != next.Restart && next.Enabled && next.Restart.Enabled &&
                    _pausedRecovery.TryGetValue(old.Key, out var pause) && pause == RecoveryPause.ReconnectPolicy &&
                    (!_restartAttempts.TryGetValue(old.Key, out var attempt) || ShouldRestart(next.Restart, attempt)))
                    _pausedRecovery.TryRemove(old.Key, out _);
            }
            foreach (var definition in definitions)
            {
                _definitions[definition.Id] = definition;
                _snapshots.TryAdd(definition.Id, Snapshot(definition, MountLifecycle.Stopped, "Not mounted"));
            }
            if (!_recovered || !_unverifiedOwnedWork.IsEmpty)
            {
                await RecoverAsync(incoming, cancellationToken).ConfigureAwait(false);
                _recovered = true;
            }
            await RecoverPendingCachesAsync(incoming, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Result.Failure("mount.reconcile_failed", exception.Message, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<OperationResult> StartAsync(MountId mountId, CancellationToken cancellationToken = default)
    {
        _pausedRecovery.TryRemove(mountId, out _);
        return StartInternalAsync(mountId, false, cancellationToken);
    }

    private async Task<bool> ProbeReadyAsync(Session session, CancellationToken cancellationToken)
    {
        // Directory probes can block in a filesystem driver. Keep at most one in flight
        // per session, and never block the host or accumulate probes after a timeout.
        session.ReadinessProbe ??= new MountReadinessProbe(async () =>
            {
                var result = await _inventory.IsMountedAsync(session.Definition.Target, _lifetime.Token).ConfigureAwait(false);
                return result.Succeeded && result.Value;
            });
        return await session.ReadinessProbe.CheckAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
    }

    public void MarkPending(MountId mountId, bool stopping)
    {
        if (_definitions.TryGetValue(mountId, out var definition))
        {
            var pending = Snapshot(
                definition,
                stopping ? MountLifecycle.Stopping : MountLifecycle.Starting,
                stopping ? "Unmount queued" : "Mount queued");

            if (stopping)
            {
                Publish(pending);
                return;
            }

            // A start request for an already active or recovered mount is a no-op. Do not
            // replace its truthful state with a queued state while the request is handled.
            // AddOrUpdate makes the eligibility check and update atomic with concurrent
            // readiness and process-exit publications.
            _snapshots.AddOrUpdate(
                mountId,
                pending,
                (_, current) => current.Lifecycle is MountLifecycle.Stopped or MountLifecycle.Failed
                    ? pending
                    : current);
        }
    }

    public Task<OperationResult> StopAsync(MountId mountId, CancellationToken cancellationToken = default) =>
        StopAsync(mountId, false, cancellationToken);

    public async Task<OperationResult> StopAsync(MountId mountId, bool allowPendingUploads, CancellationToken cancellationToken = default)
    {
        var operationGate = OperationGate(mountId);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_definitions.TryGetValue(mountId, out var definition))
            {
                return Result.Failure("mount.not_found", "The mount definition no longer exists.");
            }
            _restartAttempts.TryRemove(mountId, out _);
            return await StopCoreAsync(mountId, definition, cancellationToken, allowPendingUploads).ConfigureAwait(false);
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Result.Failure("mount.stop_failed", exception.Message, true);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<OperationResult> RestartAsync(MountId mountId, CancellationToken cancellationToken = default)
    {
        var stopped = await StopAsync(mountId, cancellationToken).ConfigureAwait(false);
        return stopped.Succeeded
            ? await StartAsync(mountId, cancellationToken).ConfigureAwait(false)
            : stopped;
    }

    public async ValueTask DisposeAsync()
    {
        _cacheWatcher.Dispose();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var stops = _sessions
                .ToArray()
                .Select(item => StopCoreAsync(
                    item.Key,
                    item.Value.Definition,
                    CancellationToken.None));
            await Task.WhenAll(stops).ConfigureAwait(false);
        }
        finally
        {
            try { await DrainExitObserversAsync().ConfigureAwait(false); }
            finally
            {
                _gate.Release();
                _gate.Dispose();
                _launchGate.Dispose();
                foreach (var operationGate in _operationGates.Values)
                    operationGate.Dispose();
                _ownership.Dispose();
                _recovery.Dispose();
                _lifetime.Dispose();
            }
        }
    }

    private async Task<OperationResult> StartInternalAsync(MountId mountId, bool restarting, CancellationToken cancellationToken)
    {
        var operationGate = OperationGate(mountId);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var launchReserved = false;
        try
        {
            if (!_definitions.TryGetValue(mountId, out var definition))
            {
                return Result.Failure("mount.not_found", "The mount definition no longer exists.");
            }
            if (!definition.Enabled)
            {
                return Fail(definition, "mount.disabled", "This mount is disabled.");
            }
            if (definition.Target is MountTarget.Directory)
            {
                return Fail(definition, "mount.directory_unsupported", "Directory mount targets are not supported yet.");
            }
            if (_sessions.TryGetValue(mountId, out var active) &&
                !ProcessTermination.HasExitedOrUnavailable(active.Process))
            {
                return Result.Success();
            }
            if (active is not null)
            {
                await CompleteStoppedSessionAsync(active).ConfigureAwait(false);
            }
            if (restarting && !CanRestart(mountId, definition))
                return CancelPendingRestart(definition);
            if (!restarting)
            {
                _restartAttempts.TryRemove(mountId, out _);
            }
            if (!File.Exists(_rclonePath))
            {
                return Fail(definition, "rclone.not_found", "rclone.exe could not be found.");
            }
            if (!File.Exists(_configPath))
            {
                return Fail(definition, "rclone.config_not_found", "The selected rclone configuration could not be found.");
            }
            var target = Target(definition.Target);
            var configFingerprint = await ConfigFingerprintAsync(definition, cancellationToken).ConfigureAwait(false);
            await _launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            launchReserved = true;
            if (!_unverifiedOwnedWork.IsEmpty)
                return Result.Failure("mount.recovery_unknown", "A surviving drive cannot be verified. Preserve its cache and restore its original configuration.", true);
            if (_recovery.Find(mountId) is { } recovery &&
                (!MountRecoveryStore.Matches(recovery, definition) ||
                    recovery.ConfigFingerprint is not null && recovery.ConfigFingerprint != configFingerprint))
            {
                if (!await RecoveryCacheIsCleanAsync(recovery, cancellationToken).ConfigureAwait(false))
                    return Fail(definition, "mount.cache_identity_changed", "Pending cache belongs to an earlier drive configuration. Restore that configuration before reconnecting.");
                await _recovery.RemoveAsync(mountId, cancellationToken).ConfigureAwait(false);
            }
            if (_sessions.Values.Any(session => Target(session.Definition.Target).Equals(target, StringComparison.OrdinalIgnoreCase)))
            {
                return Fail(definition, "mount.target_reserved", $"Target {target} is already reserved by ResoDrive.");
            }
            if (_sessions.Values.Any(session => CacheScopesOverlap(session.Definition, definition)))
                return Fail(definition, "mount.cache_in_use", "Another connected drive uses overlapping storage and cache. Disconnect it before connecting this drive.");
            var occupied = await _inventory.GetOccupiedDriveLettersAsync(cancellationToken).ConfigureAwait(false);
            if (!occupied.Succeeded || occupied.Value is null)
            {
                return Fail(definition, occupied.Error?.Code ?? "drives.unavailable", occupied.Error?.Message ?? "Drive status is unavailable.");
            }
            if (definition.Target is MountTarget.Drive drive && occupied.Value.Contains(drive.Letter))
            {
                return Fail(definition, "mount.target_in_use", $"Drive {drive.Letter}: is already in use.");
            }

            await _recovery.RecordAsync(definition, _recovery.Find(mountId)?.MetadataPath, cancellationToken, configFingerprint).ConfigureAwait(false);
            // Reconnect settings can change while inventory and journal I/O await.
            // Check the current policy again immediately before launching rclone.
            if (restarting && (!_definitions.TryGetValue(mountId, out var latest) || !CanRestart(mountId, latest)))
                return CancelPendingRestart(definition);
            Publish(Snapshot(definition, MountLifecycle.Starting, "Starting…") with { UploadStatusStale = true, UploadStatusChecking = true });
            var session = StartSession(definition);
            var process = session.Process;
            _sessions[mountId] = session;
            // Once rclone is running, finish recording ownership even if the caller
            // cancels. Recovery and the exit observer must be able to find it.
            await _ownership.UpsertAsync(Owned(session), CancellationToken.None).ConfigureAwait(false);
            if (session.Control is { } control)
                await _controls.SaveAsync(Owned(session), control.Address, control.User, control.Password, CancellationToken.None).ConfigureAwait(false);
            TrackExitObserver(ObserveExitAsync(session));
            _launchGate.Release();
            launchReserved = false;
            if (!await ReadyAsync(session, cancellationToken).ConfigureAwait(false))
            {
                if (ProcessTermination.HasExitedOrUnavailable(process))
                {
                    return Result.Failure("mount.exited", "rclone stopped before the mount became ready.", true);
                }
                Publish(Snapshot(definition, MountLifecycle.Degraded, "rclone is running, but the target is not ready yet"));
                return Result.Failure("mount.readiness_timeout", "The mount did not become ready in time.", true);
            }
            var initialTransfers = await ReadTransfersAsync(session, cancellationToken).ConfigureAwait(false);
            PublishTransfers(session, Snapshot(definition, MountLifecycle.Mounted, initialTransfers.Description), initialTransfers);
            return Result.Success();
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (_sessions.TryGetValue(mountId, out var failed))
            {
                failed.RequestStop();
                Kill(failed.Process);
                _ = await ProcessTermination.WaitForExitAsync(
                    failed.Process,
                    ForcedStopTimeout,
                    CancellationToken.None).ConfigureAwait(false);
                await CompleteStoppedSessionAsync(failed).ConfigureAwait(false);
            }
            return _definitions.TryGetValue(mountId, out var definition)
                ? Fail(definition, "mount.start_failed", exception.Message)
                : Result.Failure("mount.start_failed", exception.Message);
        }
        finally
        {
            if (launchReserved) _launchGate.Release();
            operationGate.Release();
        }
    }

    private SemaphoreSlim OperationGate(MountId mountId) =>
        _operationGates.GetOrAdd(mountId, static _ => new SemaphoreSlim(1, 1));

    private async Task<OperationResult> StopCoreAsync(MountId id, MountDefinition definition, CancellationToken cancellationToken, bool allowPendingUploads = false)
    {
        if (_unverifiedOwnedWork.ContainsKey(id.Value))
            return Result.Failure("mount.unverified_process", "The surviving drive process could not be verified. Preserve its cache and reconnect with its original permissions.", true);
        if (!_sessions.TryGetValue(id, out var session))
        {
            if (_recovery.Find(id) is { } entry)
            {
                if (!await RecoveryCacheIsCleanAsync(entry, cancellationToken).ConfigureAwait(false))
                {
                    if (!allowPendingUploads)
                        return Result.Failure("mount.upload_recovery_pending", "The cache has pending or unverified work. Restore the original drive and finish its uploads before disconnecting.", true);
                    _pausedRecovery[id] = RecoveryPause.Disconnected;
                    PublishRecoveryPaused(definition);
                    return Result.Success();
                }
                await _recovery.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            }
            await _ownership.RemoveAsync(id.Value, cancellationToken).ConfigureAwait(false);
            Publish(Snapshot(definition, MountLifecycle.Stopped, "Not mounted"));
            return Result.Success();
        }
        VfsTransferStatus? verifiedTransfers = null;
        if (!_lifetime.IsCancellationRequested && !allowPendingUploads)
        {
            var transfers = await ReadTransfersAsync(session, cancellationToken).ConfigureAwait(false);
            verifiedTransfers = transfers.Current;
            if (transfers.BlocksStop)
            {
                PublishTransfers(session, Snapshot(definition, MountLifecycle.Degraded,
                    transfers.Description + " · Wait before disconnecting"), transfers);
                return Result.Failure("mount.uploads_pending", "Close open documents and wait for uploads to finish before disconnecting.", true);
            }
            lock (session.StatusGate)
            {
                if (transfers.Generation != Volatile.Read(ref session.CacheGeneration))
                    return Result.Failure("mount.uploads_pending", "Cached files changed while checking uploads. Close open documents and try again after uploads finish.", true);
                session.RequestStop();
            }
        }
        else session.RequestStop();
        Publish(Snapshot(definition, MountLifecycle.Stopping, "Stopping…"));
        await RequestGracefulStopAsync(session, CancellationToken.None).ConfigureAwait(false);
        if (!ProcessTermination.HasExitedOrUnavailable(session.Process))
        {
            Kill(session.Process);
        }
        if (!await ProcessTermination.WaitForExitAsync(
                session.Process,
                ForcedStopTimeout,
                CancellationToken.None).ConfigureAwait(false))
        {
            Publish(Snapshot(
                definition,
                MountLifecycle.Degraded,
                "rclone did not stop. Try again or restart ResoDrive."));
            return Result.Failure(
                "mount.stop_timeout",
                "rclone did not stop within the allowed time.",
                true);
        }
        await CompleteStoppedSessionAsync(session).ConfigureAwait(false);
        if (!_lifetime.IsCancellationRequested)
        {
            // Files may change after preflight but before rclone exits. Inspect the
            // durable metadata again before discarding the recovery intent.
            var clean = _recovery.Find(id) is { } entry &&
                await RecoveryCacheIsCleanAsync(entry, CancellationToken.None).ConfigureAwait(false);
            if (clean || verifiedTransfers is { UsesDiskCache: false })
            {
                await _recovery.RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
                _controls.Remove(id.Value);
            }
            else
            {
                if (allowPendingUploads) _pausedRecovery[id] = RecoveryPause.Disconnected;
                PublishRecoveryPaused(definition);
            }
        }
        return Result.Success();
    }

    private void PublishRecoveryPaused(MountDefinition definition) =>
        Publish(Snapshot(definition, MountLifecycle.Stopped, "Disconnected · Cached uploads will resume when reconnected") with
        { UploadRecoveryRequired = true, UploadStatusStale = true });

    private async Task ObserveExitAsync(Session session)
    {
        int code;
        try
        {
            await session.Process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            code = session.Process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            // Stop cleanup may win the race and dispose the Process while this observer
            // is resuming. The owning stop path has already published final state.
            return;
        }
        if (session.Stopping)
        {
            // The stop operation owns cleanup and the final metadata inspection.
            return;
        }
        var gate = OperationGate(session.Definition.Id);
        int attempt;
        double seconds;
        try { await gate.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (!_sessions.TryGetValue(session.Definition.Id, out var currentSession) ||
                !ReferenceEquals(currentSession, session)) return;
            await CompleteStoppedSessionAsync(session).ConfigureAwait(false);
            if (!_definitions.TryGetValue(session.Definition.Id, out var definition)) return;
            var policy = definition.Restart;
            attempt = _restartAttempts.AddOrUpdate(session.Definition.Id, 1, static (_, count) => count + 1);
            if (!ShouldRestart(policy, attempt))
            {
                PauseRecoveryForPolicy(definition);
                Fail(session.Definition, "mount.process_exited", $"rclone stopped unexpectedly (exit code {code}).");
                return;
            }
            seconds = RestartDelay(policy, attempt).TotalSeconds;
            Publish(Snapshot(session.Definition, MountLifecycle.WaitingToRestart, $"Restarting in {seconds:0} seconds"));
        }
        finally { gate.Release(); }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), _lifetime.Token).ConfigureAwait(false);
            if (_restartAttempts.TryGetValue(session.Definition.Id, out var current) && current == attempt)
                await StartInternalAsync(session.Definition.Id, true, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CanRestart(MountId id, MountDefinition definition) =>
        !_lifetime.IsCancellationRequested && definition.Enabled &&
        _restartAttempts.TryGetValue(id, out var attempt) && ShouldRestart(definition.Restart, attempt);

    private OperationResult CancelPendingRestart(MountDefinition definition)
    {
        PauseRecoveryForPolicy(definition);
        if (_recovery.Find(definition.Id) is not null) PublishRecoveryPaused(definition);
        else Publish(Snapshot(definition, MountLifecycle.Stopped, "Not mounted"));
        return Result.Success();
    }

    private void PauseRecoveryForPolicy(MountDefinition definition)
    {
        if (_recovery.Find(definition.Id) is not null)
            _pausedRecovery.TryAdd(definition.Id, RecoveryPause.ReconnectPolicy);
    }

    internal void TrackExitObserver(Task observer)
    {
        _exitObservers[observer] = 0;
        _ = observer.ContinueWith((completed, state) =>
        {
            // Keep faults for disposal to observe after every observer has drained.
            if (!completed.IsFaulted)
                ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _);
        }, _exitObservers, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DrainExitObserversAsync()
    {
        Exception? failure = null;
        while (!_exitObservers.IsEmpty)
        {
            var observers = _exitObservers.Keys.ToArray();
            try { await Task.WhenAll(observers).ConfigureAwait(false); }
#pragma warning disable CA1031 // Drain every observer before rethrowing the first failure and disposing shared gates.
            catch (Exception exception) { failure ??= exception; }
#pragma warning restore CA1031
            foreach (var observer in observers) _exitObservers.TryRemove(observer, out _);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static bool ShouldRestart(RestartPolicy policy, int attempt)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return policy.Enabled && (policy.MaximumAttempts == 0 || attempt <= policy.MaximumAttempts);
    }

    internal static TimeSpan RestartDelay(RestartPolicy policy, int attempt)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var exponent = Math.Min(attempt - 1, 30);
        var seconds = Math.Min(
            policy.MaximumDelay.TotalSeconds,
            policy.InitialDelay.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task CompleteStoppedSessionAsync(Session session)
    {
        await session.CleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(session.Definition.Id, out var current) &&
                ReferenceEquals(current, session))
            {
                // Keep the old session discoverable until ownership cleanup completes. A new
                // start cannot then publish or persist a replacement that late cleanup removes.
                try
                {
                    await _ownership.RemoveAsync(session.Definition.Id.Value, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (Expected(exception))
                {
                    // Recovery verifies PID, start time, and image path before using a
                    // stale record, so cleanup failure must not retain a dead session.
                }
                var sessions = (ICollection<KeyValuePair<MountId, Session>>)_sessions;
                if (sessions.Remove(new KeyValuePair<MountId, Session>(session.Definition.Id, session)))
                {
                    Publish(Snapshot(session.Definition, MountLifecycle.Stopped, "Not mounted"));
                }
            }
        }
        finally
        {
            session.Process.Dispose();
            session.CleanupGate.Release();
        }
    }

    internal static bool CacheScopesOverlap(MountDefinition first, MountDefinition second)
    {
        if (!first.RemoteName.Equals(second.RemoteName, StringComparison.OrdinalIgnoreCase)) return false;
        var left = RemotePathUtility.Normalize(first.RemotePath).Trim('/');
        var right = RemotePathUtility.Normalize(second.RemotePath).Trim('/');
        return left.Length == 0 || right.Length == 0 || left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
            left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
            right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ConfigFingerprintAsync(MountDefinition definition, CancellationToken token)
    {
        if (File.Exists(_paths.ConfigSecretFile))
        {
            var remotes = await new RcloneRemoteConfigurationAccess().ReadAsync(_rclonePath, _configPath,
                RclonePasswordCommand.Create(), [definition.RemoteName], token).ConfigureAwait(false);
            if (!remotes.TryGetValue(definition.RemoteName, out var values))
                throw new IOException("The drive's protected configuration could not be verified.");
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
            try { return "remote:" + Convert.ToHexString(SHA256.HashData(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        await using var stream = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return "file:" + Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private Session StartSession(MountDefinition definition)
    {
        var control = RcloneControl.Create();
        var startInfo = new ProcessStartInfo
        {
            FileName = _rclonePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_rclonePath) ?? AppContext.BaseDirectory
        };
        foreach (var argument in Arguments(definition, control))
        {
            startInfo.ArgumentList.Add(argument);
        }
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("rclone could not be started.");
            }
            return new Session(process, definition, control);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
    private IEnumerable<string> Arguments(MountDefinition definition, RcloneControl control)
    {
        yield return "mount";
        yield return Source(definition);
        yield return Target(definition.Target);
        yield return "--config";
        yield return _configPath;
        yield return "--ask-password=false";
        foreach (var argument in RcloneUserAgentArguments.Create(
                     definition.Arguments, Environment.GetEnvironmentVariable("RCLONE_USER_AGENT"),
                     Volatile.Read(ref _clientUserAgent)))
            yield return argument;
        if (File.Exists(_paths.ConfigSecretFile))
        {
            yield return "--password-command";
            yield return RclonePasswordCommand.Create();
        }
        yield return "--rc";
        yield return "--rc-addr";
        yield return control.Address;
        yield return "--rc-user";
        yield return control.User;
        yield return "--rc-pass";
        yield return control.Password;
        yield return "--rc-enable-metrics=false";
        if (!RcloneMountOptions.HasOption(definition.Arguments, RcloneMountOptions.CacheModeOption))
        {
            yield return "--vfs-cache-mode";
            yield return RcloneMountOptions.LegacyCacheMode;
        }
        yield return "--cache-dir";
        yield return _paths.Cache;
        if (definition.Target is MountTarget.Drive drive)
        {
            var volumeName = HasOption(definition.Arguments, "--network-mode")
                ? NetworkVolumeName.Create(
                    definition.ConnectionHost,
                    definition.DisplayName,
                    drive.Letter)
                : NetworkVolumeName.CreateLocal(definition.DisplayName);
            if (volumeName is not null)
            {
                yield return "--volname";
                yield return volumeName;
            }
        }
        foreach (var argument in RcloneLogArguments.Create(
                     Path.Combine(_paths.Logs, RcloneLogFileName.ForMount(definition))))
        {
            yield return argument;
        }
        foreach (var argument in definition.Arguments)
        {
            yield return argument;
        }
    }

    private static bool HasOption(IEnumerable<string> arguments, string option) =>
        arguments.Any(argument =>
            argument.Equals(option, StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));

    private async Task RequestGracefulStopAsync(Session session, CancellationToken cancellationToken)
    {
        if (session.Control is null || ProcessTermination.HasExitedOrUnavailable(session.Process))
        {
            return;
        }

        ProcessRunResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                _rclonePath,
                [
                    "rc",
                    "core/quit",
                    "--rc-addr",
                    session.Control.Address,
                    "--rc-user",
                    session.Control.User,
                    "--rc-pass",
                    session.Control.Password,
                    "--config",
                    string.Empty
                ],
                GracefulStopCommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Expected(exception))
        {
            return;
        }
        if (result.ExitCode != 0 || result.TimedOut)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(GracefulStopExitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await session.Process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
    }
    private async Task<bool> ReadyAsync(Session session, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline &&
               !ProcessTermination.HasExitedOrUnavailable(session.Process))
        {
            if (await ProbeReadyAsync(session, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    internal static bool LaunchChanged(MountDefinition current, MountDefinition replacement) =>
        current.DisplayName != replacement.DisplayName ||
        current.RemoteName != replacement.RemoteName ||
        current.ConnectionHost != replacement.ConnectionHost ||
        current.RemotePath != replacement.RemotePath ||
        current.Target != replacement.Target ||
        current.Enabled != replacement.Enabled ||
        !current.Arguments.SequenceEqual(replacement.Arguments);

    private static string Source(MountDefinition definition) =>
        RemotePathUtility.FormatSource(definition.RemoteName, definition.RemotePath);

    private static string Target(MountTarget target) => target switch
    {
        MountTarget.Drive drive => $"{drive.Letter}:",
        MountTarget.Directory directory => Path.GetFullPath(directory.Path),
        _ => throw new InvalidOperationException("Unsupported mount target.")
    };

    private OwnedMount Owned(Session session) => new(
        session.Definition.Id.Value,
        session.Process.Id,
        session.Process.StartTime.ToUniversalTime(),
        _rclonePath,
        Source(session.Definition),
        Target(session.Definition.Target));

    private static bool Expected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            NotSupportedException or System.ComponentModel.Win32Exception or TimeoutException or HttpRequestException or System.Text.Json.JsonException;

    private OperationResult Fail(MountDefinition definition, string code, string message)
    {
        message = RcloneErrorMessage.Clean(message, "The drive could not be mounted.");
        Publish(new MountSnapshot
        {
            MountId = definition.Id,
            Lifecycle = MountLifecycle.Failed,
            StatusText = message,
            UploadRecoveryRequired = _recovery.Find(definition.Id) is not null,
            UploadStatusStale = _recovery.Find(definition.Id) is not null
        });
        return Result.Failure(code, message);
    }

    private static MountSnapshot Snapshot(
        MountDefinition definition,
        MountLifecycle lifecycle,
        string statusText) => new()
        {
            MountId = definition.Id,
            Lifecycle = lifecycle,
            StatusText = statusText
        };

    private void Publish(MountSnapshot snapshot)
    {
        _snapshots[snapshot.MountId] = snapshot;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception exception) when (Expected(exception))
        {
        }
    }

    private sealed class Session(Process process, MountDefinition definition, RcloneControl? control)
    {
        private int _stopping;

        public Process Process { get; } = process;
        public MountDefinition Definition { get; } = definition;
        public RcloneControl? Control { get; } = control;
        public SemaphoreSlim CleanupGate { get; } = new(1, 1);
        public SemaphoreSlim TransferGate { get; } = new(1, 1);
        public VfsTransferStatus? LastTransfers { get; set; }
        public long CacheGeneration;
        public object StatusGate { get; } = new();
        public MountReadinessProbe? ReadinessProbe { get; set; }
        public bool Stopping => Volatile.Read(ref _stopping) != 0;

        public void RequestStop() => Interlocked.Exchange(ref _stopping, 1);
    }

    private sealed record RcloneControl(string Address, string User, string Password)
    {
        public static RcloneControl Create()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            return new RcloneControl($"127.0.0.1:{endpoint.Port}", "rdrive", password);
        }
    }
}
