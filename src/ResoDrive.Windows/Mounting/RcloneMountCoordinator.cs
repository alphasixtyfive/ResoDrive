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

public sealed class RcloneMountCoordinator : IAsyncDisposable
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
    private readonly ConcurrentDictionary<MountId, MountDefinition> _definitions = new();
    private readonly ConcurrentDictionary<MountId, Session> _sessions = new();
    private readonly ConcurrentDictionary<MountId, MountSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<MountId, int> _restartAttempts = new();
    private readonly ConcurrentDictionary<MountId, SemaphoreSlim> _operationGates = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
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
    }

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
                    var operationGate = OperationGate(old.Key);
                    await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var stopped = await StopCoreAsync(old.Key, old.Value, cancellationToken).ConfigureAwait(false);
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
                }
            }
            foreach (var definition in definitions)
            {
                _definitions[definition.Id] = definition;
                _snapshots.TryAdd(definition.Id, Snapshot(definition, MountLifecycle.Stopped, "Not mounted"));
            }
            if (!_recovered)
            {
                _recovered = true;
                await RecoverAsync(incoming, cancellationToken).ConfigureAwait(false);
            }
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

    public Task<OperationResult> StartAsync(MountId mountId, CancellationToken cancellationToken = default) => StartInternalAsync(mountId, false, cancellationToken);

    public async Task<OperationResult> CheckPendingUploadsAsync(CancellationToken cancellationToken = default)
    {
        var statuses = await Task.WhenAll(_sessions.Values.Select(session =>
            ReadTransfersAsync(session, cancellationToken))).ConfigureAwait(false);
        return statuses.Any(status => status.BlocksStop)
            ? Result.Failure("mount.uploads_pending", "Files are still uploading or the cache needs attention. Close open documents, let uploads finish, then try again.", true)
            : Result.Success();
    }

    public Task RefreshHealthAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(_sessions.Values.Select(session => RefreshHealthAsync(session, cancellationToken)));

    private async Task RefreshHealthAsync(Session session, CancellationToken cancellationToken)
    {
        var gate = OperationGate(session.Definition.Id);
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;
        try
        {
            if (session.Stopping || ProcessTermination.HasExitedOrUnavailable(session.Process))
                return;
            var ready = await ProbeReadyAsync(session, cancellationToken).ConfigureAwait(false);
            var transfers = await ReadTransfersAsync(session, cancellationToken).ConfigureAwait(false);
            if (session.Stopping || ProcessTermination.HasExitedOrUnavailable(session.Process))
                return;
            Publish(Snapshot(session.Definition, ready && !transfers.HasCacheError ? MountLifecycle.Mounted : MountLifecycle.Degraded,
                ready
                    ? transfers.Description
                    : "Drive is not responding · " + transfers.Description + " · Checking again automatically") with
            {
                UploadsQueued = (transfers.Current ?? transfers.LastKnown)?.Queued,
                UploadsInProgress = (transfers.Current ?? transfers.LastKnown)?.Uploading,
                UploadStatusStale = transfers.Current is null
            });
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (!session.Stopping && !ProcessTermination.HasExitedOrUnavailable(session.Process))
                Publish(Snapshot(session.Definition, MountLifecycle.Degraded, "Drive status unavailable · Checking again automatically"));
        }
        finally { gate.Release(); }
    }

    private static async Task<MountUploadObservation> ReadTransfersAsync(Session session, CancellationToken cancellationToken)
    {
        await session.TransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = session.Control is { } control && !ProcessTermination.HasExitedOrUnavailable(session.Process)
                ? await VfsStatusReader.ReadAsync(control.Address, control.User, control.Password, cancellationToken).ConfigureAwait(false)
                : null;
            if (current is not null)
                session.LastTransfers = current;
            return new(current, session.LastTransfers);
        }
        finally { session.TransferGate.Release(); }
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

    public async Task<OperationResult> StopAsync(MountId mountId, CancellationToken cancellationToken = default)
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
            return await StopCoreAsync(mountId, definition, cancellationToken).ConfigureAwait(false);
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
            _gate.Release();
            _gate.Dispose();
            foreach (var operationGate in _operationGates.Values)
                operationGate.Dispose();
            _ownership.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task<OperationResult> StartInternalAsync(MountId mountId, bool restarting, CancellationToken cancellationToken)
    {
        var operationGate = OperationGate(mountId);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            if (_sessions.Values.Any(session => Target(session.Definition.Target).Equals(target, StringComparison.OrdinalIgnoreCase)))
            {
                return Fail(definition, "mount.target_reserved", $"Target {target} is already reserved by ResoDrive.");
            }
            var occupied = await _inventory.GetOccupiedDriveLettersAsync(cancellationToken).ConfigureAwait(false);
            if (!occupied.Succeeded || occupied.Value is null)
            {
                return Fail(definition, occupied.Error?.Code ?? "drives.unavailable", occupied.Error?.Message ?? "Drive status is unavailable.");
            }
            if (definition.Target is MountTarget.Drive drive && occupied.Value.Contains(drive.Letter))
            {
                return Fail(definition, "mount.target_in_use", $"Drive {drive.Letter}: is already in use.");
            }

            Publish(Snapshot(definition, MountLifecycle.Starting, "Starting…"));
            var session = StartSession(definition);
            var process = session.Process;
            _sessions[mountId] = session;
            await _ownership.UpsertAsync(Owned(session), cancellationToken).ConfigureAwait(false);
            _ = ObserveExitAsync(session);
            if (!await ReadyAsync(session, cancellationToken).ConfigureAwait(false))
            {
                if (ProcessTermination.HasExitedOrUnavailable(process))
                {
                    return Result.Failure("mount.exited", "rclone stopped before the mount became ready.", true);
                }
                Publish(Snapshot(definition, MountLifecycle.Degraded, "rclone is running, but the target is not ready yet"));
                return Result.Failure("mount.readiness_timeout", "The mount did not become ready in time.", true);
            }
            Publish(Snapshot(definition, MountLifecycle.Mounted, "Mounted"));
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
            operationGate.Release();
        }
    }

    private SemaphoreSlim OperationGate(MountId mountId) =>
        _operationGates.GetOrAdd(mountId, static _ => new SemaphoreSlim(1, 1));

    private async Task<OperationResult> StopCoreAsync(MountId id, MountDefinition definition, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            await _ownership.RemoveAsync(id.Value, cancellationToken).ConfigureAwait(false);
            Publish(Snapshot(definition, MountLifecycle.Stopped, "Not mounted"));
            return Result.Success();
        }
        if (!_lifetime.IsCancellationRequested)
        {
            var transfers = await ReadTransfersAsync(session, cancellationToken).ConfigureAwait(false);
            if (transfers.BlocksStop)
            {
                Publish(Snapshot(definition, MountLifecycle.Degraded, transfers.Description + " · Wait before disconnecting"));
                return Result.Failure("mount.uploads_pending", "Close open documents and wait for uploads to finish before disconnecting.", true);
            }
        }
        session.RequestStop();
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
        return Result.Success();
    }

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
            await CompleteStoppedSessionAsync(session).ConfigureAwait(false);
            return;
        }
        if (!_sessions.TryRemove(session.Definition.Id, out var removed) || !ReferenceEquals(removed, session))
        {
            return;
        }
        session.Process.Dispose();
        try
        {
            await _ownership.RemoveAsync(session.Definition.Id.Value).ConfigureAwait(false);
        }
        catch (Exception exception) when (Expected(exception))
        {
            // A stale record is safe: recovery verifies PID, start time and image path
            // before it ever stops a process, and the next upsert replaces this mount ID.
        }
        var policy = session.Definition.Restart;
        var attempt = _restartAttempts.AddOrUpdate(session.Definition.Id, 1, static (_, count) => count + 1);
        if (!ShouldRestart(policy, attempt))
        {
            Fail(session.Definition, "mount.process_exited", $"rclone stopped unexpectedly (exit code {code}).");
            return;
        }
        var seconds = RestartDelay(policy, attempt).TotalSeconds;
        Publish(Snapshot(session.Definition, MountLifecycle.WaitingToRestart, $"Restarting in {seconds:0} seconds"));
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

    private async Task RecoverAsync(Dictionary<MountId, MountDefinition> definitions, CancellationToken cancellationToken)
    {
        foreach (var owned in await _ownership.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            var mountId = new MountId(owned.MountId);
            if (!definitions.TryGetValue(mountId, out var definition) ||
                owned.Source != Source(definition) ||
                !owned.Target.Equals(Target(definition.Target), StringComparison.OrdinalIgnoreCase) ||
                !MountOwnershipStore.IsSameExecutablePath(owned.ExecutablePath, _rclonePath))
            {
                if (await StopOwnedProcessAsync(owned).ConfigureAwait(false))
                {
                    await _ownership.RemoveAsync(owned.MountId, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            var process = MountOwnershipStore.TryOpenVerified(owned);
            if (process is null)
            {
                await _ownership.RemoveAsync(owned.MountId, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var session = new Session(process, definition, control: null);
            _sessions[mountId] = session;
            Publish(Snapshot(definition, MountLifecycle.Degraded, "Recovered drive · Checking readiness"));
            _ = ObserveExitAsync(session);
        }
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

    private static async Task<bool> StopOwnedProcessAsync(OwnedMount owned)
    {
        using var process = MountOwnershipStore.TryOpenVerified(owned);
        if (process is null)
            return true;
        Kill(process);
        return await ProcessTermination.WaitForExitAsync(
                process,
                ForcedStopTimeout,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static bool Expected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            NotSupportedException or System.ComponentModel.Win32Exception;

    private OperationResult Fail(MountDefinition definition, string code, string message)
    {
        message = RcloneErrorMessage.Clean(message, "The drive could not be mounted.");
        Publish(new MountSnapshot
        {
            MountId = definition.Id,
            Lifecycle = MountLifecycle.Failed,
            StatusText = message
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
