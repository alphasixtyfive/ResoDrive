using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Results;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.Host;

public sealed partial class Worker : BackgroundService
{
    private static readonly TimeSpan OperationDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(10);
    private readonly ApplicationPaths _paths;
    private readonly ILogger<Worker> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly SemaphoreSlim _scheduleStateGate = new(1, 1);
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _operations = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private readonly ConcurrentDictionary<SyncJobId, DateTimeOffset> _lastRuns = new();
    private readonly RemoteWipeStore _remoteWipe;
    private readonly RemoteWipeClient _remoteWipeClient;
    private readonly RemoteWipeCoordinator _wipeCoordinator;
    private readonly RemoteWipeEnrollmentService _wipeEnrollment;
    private IReadOnlyDictionary<Guid, string> _wipeEnrollmentStatuses = new Dictionary<Guid, string>();
    private DateTimeOffset _nextEnrollmentAttempt;
    private AtomicSettingsStore? _store;
    private RcloneMountCoordinator? _mounts;
    private RcloneSyncCoordinator? _syncs;
    private ManagerSettings? _settings;
    private IReadOnlyList<MountDefinition> _definitions = [];
    private string? _rclonePath;
    private string? _configPath;
    private int _taskId;
    private bool _firstSchedulePass = true;
    private bool _shutdownRequested;
    internal bool CanResumeRemoteWipe { get; private set; } = true;
    internal bool RestartForRemoteWipe { get; private set; }
    private bool _recoveringRemoteWipe;
    private string _clientUserAgent = ClientUserAgent.Value;
    private readonly bool _inspectRuntimeUserAgent;
    private readonly RcloneRuntimeLocator _runtimeLocator;
    private readonly TimeSpan _runtimeIdentityRetryInterval;
    private RuntimeIdentity _runtimeIdentity = new(null, null);

    private sealed record RuntimeIdentity(string? Version, string? ErrorCode);

    public Worker(
        ApplicationPaths paths,
        ILogger<Worker> logger,
        IHostApplicationLifetime applicationLifetime)
        : this(paths, logger, applicationLifetime, new RemoteWipeClient(), inspectRuntimeUserAgent: true)
    {
    }

    internal Worker(ApplicationPaths paths, ILogger<Worker> logger,
        IHostApplicationLifetime applicationLifetime, RemoteWipeClient remoteWipeClient,
        RemoteWipeEnrollmentService? wipeEnrollment = null, bool inspectRuntimeUserAgent = false,
        RcloneRuntimeLocator? runtimeLocator = null, TimeSpan? runtimeIdentityRetryInterval = null)
    {
        _paths = paths;
        _logger = logger;
        _applicationLifetime = applicationLifetime;
        _remoteWipeClient = remoteWipeClient;
        _remoteWipe = new RemoteWipeStore(paths);
        _wipeCoordinator = new(paths, _remoteWipeClient);
        _wipeEnrollment = wipeEnrollment ?? new(paths);
        _inspectRuntimeUserAgent = inspectRuntimeUserAgent;
        _runtimeLocator = runtimeLocator ?? new(paths);
        _runtimeIdentityRetryInterval = runtimeIdentityRetryInterval ?? TimeSpan.FromMinutes(1);
        if (_runtimeIdentityRetryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(runtimeIdentityRetryInterval));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_inspectRuntimeUserAgent)
        {
            var refreshed = await RefreshRuntimeUserAgentAsync(stoppingToken).ConfigureAwait(false);
            if (!refreshed.Succeeded)
                LogRuntimeInspectionFailure(_logger, refreshed.Error?.Code, refreshed.Error?.Message);
        }
        _remoteWipeClient.SetClientUserAgent(_clientUserAgent);
        // Resume accepted commands before loading settings or starting any automatic work.
        if (new RemoteWipeStateStore(_paths).Read() is { Phase: not RemoteWipePhase.Completed })
        {
            _recoveringRemoteWipe = true;
            await Task.WhenAll(ServeAsync(stoppingToken), ResumeRemoteWipeAsync(stoppingToken)).ConfigureAwait(false);
            return;
        }
        _store = new AtomicSettingsStore(_paths);
        await Task.WhenAll(ServeAsync(stoppingToken), InitializeAndMonitorAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task InitializeAndMonitorAsync(CancellationToken stoppingToken)
    {
        await LoadScheduleStateAsync(stoppingToken).ConfigureAwait(false);
        var result = await ReloadAsync(stoppingToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            LogInitializationFailure(_logger, result.Error?.Code, result.Error?.Message);
        }
        if (_shutdownRequested) return;
        await Task.WhenAll(
            ScheduleAsync(stoppingToken),
            MonitorMountsAsync(stoppingToken),
            MonitorRemoteWipeAsync(stoppingToken),
            _inspectRuntimeUserAgent
                ? MonitorRuntimeIdentityAsync(stoppingToken)
                : Task.CompletedTask).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var source in _operations.Values)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A completing operation can remove and dispose its source concurrently.
            }
        }

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var tasks = _tasks.Values.ToArray();
            if (tasks.Length != 0)
            {
                try
                {
                    await Task.WhenAll(tasks)
                        .WaitAsync(OperationDrainTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    LogDrainTimeout(_logger, tasks.Length);
                    CanResumeRemoteWipe = false;
                }
            }

            if (_mounts is not null)
            {
                await _mounts.DisposeAsync().ConfigureAwait(false);
            }
            _syncs?.Dispose();
            _store?.Dispose();

            // The next host lifetime resumes the durable command after this lifetime has
            // finished disposing work. Cleanup never races active host operations.
        }
    }

    public override void Dispose()
    {
        _remoteWipeClient.Dispose();
        _reloadGate.Dispose();
        _scheduleStateGate.Dispose();
        _slots.Dispose();
        foreach (var source in _operations.Values)
        {
            source.Dispose();
        }
        base.Dispose();
    }

    private async Task ServeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = CurrentUserPipe.CreateServer(HostProtocol.GetPipeName(_paths));
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            Track(HandleClientAsync(pipe, token));
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe.ConfigureAwait(false))
        {
            HostResponse response;
            var shutdownRequested = false;
            try
            {
                using var requestTimeout = new CancellationTokenSource(ClientRequestTimeout);
                using var requestToken = CancellationTokenSource.CreateLinkedTokenSource(
                    token,
                    requestTimeout.Token);
                var request = await HostProtocol.ReadAsync<HostRequest>(pipe, requestToken.Token)
                    .ConfigureAwait(false);
                response = request is null ? new(false, "host.invalid_request", "The request was empty.")
                    : await HandleAsync(request, token).ConfigureAwait(false);
                shutdownRequested = response.Succeeded && string.Equals(
                    request?.Command,
                    "shutdown",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                response = new(
                    false,
                    "host.request_timeout",
                    "The client did not send a complete request in time.");
            }
            catch (Exception exception)
            {
                LogClientFailure(_logger, exception);
                response = new(false, exception is IOException or InvalidDataException or JsonException or ArgumentException
                    ? "host.invalid_request" : "host.failure", "The host could not process the request.");
            }
            try
            {
                await HostProtocol.WriteAsync(pipe, response, token).ConfigureAwait(false);
                if (shutdownRequested)
                {
                    _applicationLifetime.StopApplication();
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                LogClientFailure(_logger, exception);
            }
        }
    }

    private async Task<HostResponse> HandleAsync(HostRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return new(false, "host.command", "A command is required.");
        }
        if (!HostProtocol.AcceptsBaseDirectory(
                request.ExpectedHostBaseDirectory,
                AppContext.BaseDirectory))
        {
            var status = Status();
            return status with
            {
                Succeeded = false,
                ErrorCode = "host.different_installation",
                ErrorMessage = "Another ResoDrive installation is already managing this account."
            };
        }
        var command = request.Command.Trim().ToLowerInvariant();
        if (_recoveringRemoteWipe || new RemoteWipeStateStore(_paths).Read() is { Phase: not RemoteWipePhase.Completed })
        {
            // An idle recovery host can be stopped for an update. Its durable work resumes
            // next launch; account operations remain blocked, even if acknowledgement just finished.
            return _recoveringRemoteWipe && command == "shutdown" && request.Confirmed
                ? new(true)
                : new(false, "host.remote_wipe", "Nextcloud requested removal of local ResoDrive data.");
        }
        if (command == "status")
        {
            return Status();
        }
        if (command == "shutdown")
        {
            return await HandleShutdownAsync(request, token).ConfigureAwait(false);
        }
        await _reloadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_shutdownRequested)
                return new(false, "host.stopping", "ResoDrive is stopping. Try again after it restarts.");
            if (command == "check-uploads")
                return _mounts is null ? Status() : Response(await _mounts.CheckPendingUploadsAsync(token).ConfigureAwait(false));
            if (command is "reload" or "activate-runtime")
            {
                if (command == "activate-runtime")
                {
                    var refreshed = await RefreshRuntimeUserAgentAsync(token).ConfigureAwait(false);
                    if (!refreshed.Succeeded)
                        return Response(refreshed);
                }
                var reloaded = await ReloadCoreAsync(token, request.Confirmed).ConfigureAwait(false);
                if (!reloaded.Succeeded || command == "reload")
                    return Response(reloaded);

                QueueEligibleAutoMounts(token);
                return Status();
            }
            if (_mounts is null)
            {
                return new(false, "host.not_ready", "The background host is not ready.");
            }
            var mountCoordinator = _mounts;
            if (request.MountId is not Guid mountGuid || mountGuid == Guid.Empty)
            {
                return new(false, "host.mount_id", "A valid mount ID is required.");
            }
            var mountId = new MountId(mountGuid);
            if (command is "run-sync" or "cancel-sync")
            {
                if (_syncs is null || request.SyncJobId is not Guid syncGuid || syncGuid == Guid.Empty)
                {
                    return new(false, "host.sync_job_id", "A valid sync job ID is required.");
                }
                var syncId = new SyncJobId(syncGuid);
                if (command == "cancel-sync")
                {
                    if (_operations.TryGetValue(SyncKey(syncId), out var source))
                    {
                        try
                        {
                            source.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                    var cancelled = await _syncs.CancelAsync(mountId, syncId, token).ConfigureAwait(false);
                    if (cancelled.Error?.Code == "sync.not_running")
                    {
                        await _syncs.MarkCancelledAsync(mountId, syncId).ConfigureAwait(false);
                    }
                    return cancelled.Succeeded || cancelled.Error?.Code == "sync.not_running" ? Status() : Response(cancelled);
                }
                return QueueSync(mountId, syncId, false, token) ? Status()
                    : new(false, "host.operation_in_progress", "This sync job is already queued or running.");
            }
            Func<CancellationToken, Task<OperationResult>>? action = command switch
            {
                "start" => operationToken => mountCoordinator.StartAsync(mountId, operationToken),
                "stop" => operationToken => mountCoordinator.StopAsync(mountId, operationToken),
                "restart" => operationToken => mountCoordinator.RestartAsync(mountId, operationToken),
                _ => null
            };
            if (action is null)
            {
                return new(false, "host.command", $"Unknown host command '{request.Command}'.");
            }
            return Queue(
                    MountKey(mountId),
                    action,
                    token,
                    () => mountCoordinator.MarkPending(mountId, command is "stop" or "restart"))
                ? Status()
                : new(false, "host.operation_in_progress", "A mount operation is already queued or running.");
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<HostResponse> HandleShutdownAsync(
        HostRequest request,
        CancellationToken token)
    {
        await _reloadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(request.ExpectedHostBaseDirectory) &&
                !HostProtocol.IsSameBaseDirectory(
                    request.ExpectedHostBaseDirectory,
                    AppContext.BaseDirectory))
            {
                return new(
                    false,
                    "host.changed",
                    "The active ResoDrive host changed before takeover could begin.",
                    HostBaseDirectory: AppContext.BaseDirectory
                );
            }

            if (_mounts is not null)
            {
                var uploads = await _mounts.CheckPendingUploadsAsync(token).ConfigureAwait(false);
                if (!uploads.Succeeded)
                    return Response(uploads);
            }
            _shutdownRequested = true;
            if (HasWork() && !request.Confirmed)
            {
                _shutdownRequested = false;
                return new(
                    false,
                    "host.work_active",
                    "Mounted drives or sync jobs are still active. Confirm exit to stop them.",
                    HostBaseDirectory: AppContext.BaseDirectory
                );
            }

            return new(true, HostBaseDirectory: AppContext.BaseDirectory);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task MonitorMountsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            await _reloadGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_mounts is not null && !_shutdownRequested)
                    await _mounts.RefreshHealthAsync(token).ConfigureAwait(false);
            }
            finally { _reloadGate.Release(); }
        }
    }

    private async Task MonitorRemoteWipeAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            if (await CheckForRemoteWipeAsync(token).ConfigureAwait(false)) return;
            if (DateTimeOffset.UtcNow < _nextEnrollmentAttempt) continue;
            await _reloadGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!_shutdownRequested)
                {
                    if (await EnrollRemoteWipeAsync(_definitions, token).ConfigureAwait(false) &&
                        await CheckForRemoteWipeAsync(token).ConfigureAwait(false)) return;
                }
            }
            finally { _reloadGate.Release(); }
        } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    private async Task MonitorRuntimeIdentityAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_runtimeIdentityRetryInterval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (Volatile.Read(ref _runtimeIdentity).Version is not null || _shutdownRequested)
                continue;
            await _reloadGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _runtimeIdentity).Version is null && !_shutdownRequested &&
                    (await RefreshRuntimeUserAgentAsync(token).ConfigureAwait(false)).Succeeded)
                    LogRuntimeInspectionRecovered(_logger);
            }
            finally { _reloadGate.Release(); }
        }
    }

    private async Task<bool> CheckForRemoteWipeAsync(CancellationToken token)
    {
        try
        {
            var registrations = await _remoteWipe.LoadAsync(token).ConfigureAwait(false);
            foreach (var registration in registrations.DistinctBy(item => (item.ServerBaseUrl, item.Username, item.AppToken)))
            {
                if (!await _remoteWipeClient.IsWipeRequestedAsync(registration, token).ConfigureAwait(false)) continue;
                await AcceptRemoteWipeAsync(registration, token).ConfigureAwait(false);
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or FormatException or HttpRequestException)
        {
            LogRemoteWipeRegistrationFailure(_logger, exception);
        }
        return false;
    }

    private async Task<bool> EnrollRemoteWipeAsync(IReadOnlyList<MountDefinition> definitions, CancellationToken token)
    {
        _nextEnrollmentAttempt = DateTimeOffset.UtcNow.AddMinutes(1);
        try
        {
            var result = await _wipeEnrollment.EnrollAsync(definitions, token).ConfigureAwait(false);
            _wipeEnrollmentStatuses = result.Statuses;
            return result.RegistrationsChanged;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or FormatException or InvalidOperationException or
            HttpRequestException or TimeoutException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // Parser exceptions can include source data. Never write credential-bearing details to diagnostics.
            LogRemoteWipeEnrollmentFailure(_logger);
            return false;
        }
    }

    private async Task AcceptRemoteWipeAsync(RemoteWipeRegistration registration, CancellationToken token)
    {
        await _wipeCoordinator.AcceptAsync(registration, token).ConfigureAwait(false);
        _shutdownRequested = true;
        RestartForRemoteWipe = true;
        LogRemoteWipeRequested(_logger);
        _applicationLifetime.StopApplication();
    }

    private async Task<bool> TryCompleteRemoteWipeAsync(CancellationToken token)
    {
        try
        {
            await _wipeCoordinator.ResumeAsync(
                cancellation => RemoteWipeWorkStopper.StopOwnedMountsAsync(_paths, cancellation), token)
                .ConfigureAwait(false);
            LogRemoteWipeCompleted(_logger, new RemoteWipeStateStore(_paths).Read()?.ServerAcknowledged == true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            HttpRequestException or TimeoutException or System.ComponentModel.Win32Exception ||
            exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            LogRemoteWipeFailure(_logger, exception);
            return false;
        }
    }

    private async Task ResumeRemoteWipeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (await TryCompleteRemoteWipeAsync(token).ConfigureAwait(false)) break;
            await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
        }
        _applicationLifetime.StopApplication();
    }

    private async Task ScheduleAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            var now = DateTimeOffset.UtcNow;
            var changed = false;
            await _reloadGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                foreach (var item in _definitions.SelectMany(mount =>
                             mount.SyncJobs.Select(job => (Mount: mount, Job: job))))
                {
                    if (!item.Job.Enabled)
                    {
                        continue;
                    }
                    var runOnStart = _firstSchedulePass && item.Job.Schedule.RunOnApplicationStart;
                    if (!_lastRuns.TryGetValue(item.Job.Id, out var previous))
                    {
                        _lastRuns[item.Job.Id] = now;
                        changed = true;
                        if (!runOnStart)
                        {
                            continue;
                        }
                    }
                    else if (!runOnStart && (!item.Job.Schedule.Enabled || now - previous < item.Job.Schedule.Interval))
                    {
                        continue;
                    }
                    QueueSync(item.Mount.Id, item.Job.Id, true, token);
                }
                _firstSchedulePass = false;
            }
            finally
            {
                _reloadGate.Release();
            }
            if (changed)
            {
                await SaveScheduleStateAsync(token).ConfigureAwait(false);
            }
        } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    private bool QueueSync(MountId mountId, SyncJobId syncId, bool scheduled, CancellationToken token)
    {
        var coordinator = _syncs;
        return coordinator is not null && Queue(SyncKey(syncId), async operationToken =>
        {
            var result = await coordinator.RunAsync(mountId, syncId, operationToken).ConfigureAwait(false);
            // Every dequeued scheduled attempt consumes its interval. Retrying launch,
            // configuration, or access failures on every 30-second scheduler tick can
            // otherwise create an unbounded error loop on unattended machines.
            _lastRuns[syncId] = DateTimeOffset.UtcNow;
            await SaveScheduleStateAsync(CancellationToken.None).ConfigureAwait(false);
            if (scheduled && !result.Succeeded)
            {
                LogOperationFailure(_logger, SyncKey(syncId), result.Error?.Code, result.Error?.Message);
            }
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
        Track(RunAsync(key, source, action));
        return true;
    }

    private async Task RunAsync(string key, CancellationTokenSource source, Func<CancellationToken, Task<OperationResult>> action)
    {
        try
        {
            await _slots.WaitAsync(source.Token).ConfigureAwait(false);
            try
            {
                var result = await action(source.Token).ConfigureAwait(false);
                if (!result.Succeeded && result.Error?.Code != "sync.cancelled")
                {
                    LogOperationFailure(_logger, key, result.Error?.Code, result.Error?.Message);
                }
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogOperationException(_logger, key, exception);
        }
        finally
        {
            if (_operations.TryGetValue(key, out var current) && ReferenceEquals(current, source))
            {
                _operations.TryRemove(key, out _);
            }
            source.Dispose();
        }
    }

    private async Task<OperationResult> ReloadAsync(CancellationToken token)
    {
        await _reloadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await ReloadCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<OperationResult> ReloadCoreAsync(
        CancellationToken token,
        bool restartChangedMounts = false)
    {
        if (_store is null)
        {
            return Result.Failure("host.not_ready", "The settings store is unavailable.");
        }
        var loaded = await _store.LoadAsync(token).ConfigureAwait(false);
        if (!loaded.Succeeded || loaded.Value is null)
        {
            return Result.Failure(loaded.Error?.Code ?? "settings.load_failed", loaded.Error?.Message ?? "Settings could not be loaded.");
        }
        var definitions = new List<MountDefinition>();
        foreach (var item in loaded.Value.Mounts)
        {
            var mapped = MountDefinitionMapper.ToDomain(item);
            if (!mapped.Succeeded || mapped.Value is null)
            {
                return Result.Failure(mapped.Error?.Code ?? "mount.invalid", mapped.Error?.Message ?? "A mount is invalid.");
            }
            definitions.Add(mapped.Value);
        }
        var rclone = new RcloneRuntimeLocator(_paths).ExecutablePath;
        var config = _paths.ConfigFile;
        var changed = !string.Equals(rclone, _rclonePath, StringComparison.OrdinalIgnoreCase) || !string.Equals(config, _configPath, StringComparison.OrdinalIgnoreCase);
        var definitionsChanged = _settings is not null &&
            !string.Equals(
                JsonSerializer.Serialize(_settings.Mounts),
                JsonSerializer.Serialize(loaded.Value.Mounts),
                StringComparison.Ordinal);
        if (_settings is null && await CheckForRemoteWipeAsync(token).ConfigureAwait(false))
            return Result.Failure("host.remote_wipe", "Nextcloud requested removal of local ResoDrive data.");
        if (_settings is null || definitionsChanged)
        {
            if (await EnrollRemoteWipeAsync(definitions, token).ConfigureAwait(false) &&
                await CheckForRemoteWipeAsync(token).ConfigureAwait(false))
                return Result.Failure("host.remote_wipe", "Nextcloud requested removal of local ResoDrive data.");
        }
        var definitionWork = AnalyzeDefinitionWork(loaded.Value);
        if (definitionsChanged && definitionWork.HasBlockingWork)
        {
            return Result.Failure(
                "host.work_active",
                "Unmount the drives being changed and wait for their queued operations and active sync jobs.");
        }
        if (definitionsChanged && definitionWork.ActiveChangedMountIds.Count != 0 && !restartChangedMounts)
        {
            return Result.Failure(
                "host.mount_restart_required",
                "The mounted drives being changed must briefly disconnect before the settings can be activated.");
        }
        if (changed && _mounts is not null)
        {
            if (HasWork())
            {
                return Result.Failure("host.restart_required", "Paths changed while work is active. Stop all work, then reload.");
            }
            await _mounts.DisposeAsync().ConfigureAwait(false);
            _mounts = null;
            _syncs?.Dispose();
            _syncs = null;
        }
        _mounts ??= new(rclone, config, _paths, new MountTargetInventory(), _clientUserAgent);
        var mountCoordinator = _mounts;
        var reconciled = await mountCoordinator.ReconcileAsync(definitions, token).ConfigureAwait(false);
        if (!reconciled.Succeeded)
        {
            return reconciled;
        }
        _definitions = definitions;
        if (restartChangedMounts && definitionWork.ActiveChangedMountIds.Count != 0)
        {
            var enabledIncomingIds = definitions
                .Where(definition => definition.Enabled)
                .Select(definition => definition.Id.Value)
                .ToHashSet();
            foreach (var id in definitionWork.ActiveChangedMountIds.Where(enabledIncomingIds.Contains))
            {
                var mountId = new MountId(id);
                Queue(
                    MountKey(mountId),
                    operationToken => mountCoordinator.StartAsync(mountId, operationToken),
                    token,
                    () => mountCoordinator.MarkPending(mountId, stopping: false));
            }
        }
        var currentJobIds = definitions
            .SelectMany(definition => definition.SyncJobs)
            .Select(job => job.Id)
            .ToHashSet();
        foreach (var obsoleteId in _lastRuns.Keys.Where(id => !currentJobIds.Contains(id)))
        {
            _lastRuns.TryRemove(obsoleteId, out _);
        }
        _syncs ??= new(rclone, config, _paths, () => _definitions, _clientUserAgent);
        _rclonePath = rclone;
        _configPath = config;
        var isFirstLoad = _settings is null;
        _settings = loaded.Value;
        if (isFirstLoad)
        {
            QueueEligibleAutoMounts(token);
        }
        return Result.Success();
    }

    private void QueueEligibleAutoMounts(CancellationToken token)
    {
        var mountCoordinator = _mounts;
        if (mountCoordinator is null)
            return;

        var lifecycles = mountCoordinator.GetSnapshots()
            .ToDictionary(snapshot => snapshot.MountId, snapshot => snapshot.Lifecycle);
        foreach (var definition in _definitions)
        {
            if (!lifecycles.TryGetValue(definition.Id, out var lifecycle) ||
                !definition.IsAutomaticStartEligible(lifecycle))
                continue;

            Queue(
                MountKey(definition.Id),
                operationToken => mountCoordinator.StartAsync(definition.Id, operationToken),
                token,
                () => mountCoordinator.MarkPending(definition.Id, stopping: false));
        }
    }

    private async Task<OperationResult> RefreshRuntimeUserAgentAsync(CancellationToken token)
    {
        var runtime = await _runtimeLocator.InspectAsync(token).ConfigureAwait(false);
        if (!runtime.Succeeded || runtime.Value is null)
        {
            var code = runtime.Error?.Code ?? "rclone.invalid";
            SetClientUserAgent(ClientUserAgent.Value);
            Volatile.Write(ref _runtimeIdentity, new(null, code));
            return Result.Failure(code,
                runtime.Error?.Message ?? "The managed rclone installation could not be verified.",
                runtime.Error?.IsTransient ?? false);
        }

        var userAgent = ClientUserAgent.WithRcloneVersion(runtime.Value.Version);
        if (userAgent == ClientUserAgent.Value)
        {
            SetClientUserAgent(ClientUserAgent.Value);
            Volatile.Write(ref _runtimeIdentity, new(null, "rclone.version_format"));
            return Result.Failure("rclone.version_format", "The managed rclone installation reported an unrecognized version.");
        }
        SetClientUserAgent(userAgent);
        Volatile.Write(ref _runtimeIdentity, new(runtime.Value.Version, null));
        return Result.Success();
    }

    private void SetClientUserAgent(string value)
    {
        _clientUserAgent = value;
        _remoteWipeClient.SetClientUserAgent(_clientUserAgent);
        _mounts?.SetClientUserAgent(_clientUserAgent);
        _syncs?.SetClientUserAgent(_clientUserAgent);
    }

    private bool HasWork() =>
        !_operations.IsEmpty ||
        (_mounts?.GetSnapshots().Any(snapshot => snapshot.Lifecycle is not MountLifecycle.Stopped and not MountLifecycle.Failed) ?? false) ||
        (_syncs?.GetSnapshots().Any(snapshot => snapshot.Lifecycle == SyncLifecycle.Running) ?? false);

    private DefinitionWorkAnalysis AnalyzeDefinitionWork(ManagerSettings incoming)
    {
        if (_settings is null)
            return new(new HashSet<Guid>(), new HashSet<Guid>(), false);

        var activeMountIds = _mounts?.GetSnapshots()
            .Where(snapshot => snapshot.Lifecycle is not MountLifecycle.Stopped and not MountLifecycle.Failed)
            .Select(snapshot => snapshot.MountId.Value) ?? [];
        var activeSyncIds = _syncs?.GetSnapshots()
            .Where(snapshot => snapshot.Lifecycle == SyncLifecycle.Running)
            .Select(snapshot => snapshot.JobId.Value) ?? [];

        return DefinitionWorkConflict.Analyze(
            _settings.Mounts,
            incoming.Mounts,
            activeMountIds,
            activeSyncIds,
            _operations.Keys);
    }

    private async Task LoadScheduleStateAsync(CancellationToken token)
    {
        if (!File.Exists(StatePath))
        {
            return;
        }
        try
        {
            await using var stream = File.OpenRead(StatePath);
            var state = await JsonSerializer.DeserializeAsync<Dictionary<Guid, DateTimeOffset>>(stream, cancellationToken: token).ConfigureAwait(false);
            if (state is not null)
            {
                foreach (var pair in state)
                {
                    _lastRuns[new SyncJobId(pair.Key)] = pair.Value;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            LogStateFailure(_logger, exception);
        }
    }

    private async Task SaveScheduleStateAsync(CancellationToken token)
    {
        await _scheduleStateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            var temporary = StatePath + ".tmp";
            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                var persistedRuns = _lastRuns.ToDictionary(pair => pair.Key.Value, pair => pair.Value);
                await JsonSerializer.SerializeAsync(stream, persistedRuns, cancellationToken: token).ConfigureAwait(false);
            }
            File.Move(temporary, StatePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogStateFailure(_logger, exception);
        }
        finally
        {
            _scheduleStateGate.Release();
        }
    }

    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref _taskId);
        _tasks[id] = task;
        _ = task.ContinueWith((_, state) =>
        {
            var taskState = ((ConcurrentDictionary<int, Task> Tasks, int Id))state!;
            taskState.Tasks.TryRemove(taskState.Id, out Task? _);
        }, (_tasks, id), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private HostResponse Status()
    {
        var identity = Volatile.Read(ref _runtimeIdentity);
        return new(
            true,
            Mounts: _mounts?.GetSnapshots().Select(snapshot => HostProtocol.ToStatus(snapshot) with
            {
                RemoteWipeStatus = _wipeEnrollmentStatuses.GetValueOrDefault(snapshot.MountId.Value)
            }).ToArray() ?? [],
            SyncJobs: _syncs?.GetSnapshots().Select(HostProtocol.ToStatus).ToArray() ?? [],
            HostBaseDirectory: AppContext.BaseDirectory,
            ReportedRcloneVersion: identity.Version,
            RcloneIdentityErrorCode: identity.ErrorCode);
    }
    private static HostResponse Response(OperationResult result) => new(
        result.Succeeded,
        result.Error?.Code,
        result.Error?.Message,
        HostBaseDirectory: AppContext.BaseDirectory
    );
    private static string MountKey(MountId id) => $"mount:{id.Value:N}";
    private static string SyncKey(SyncJobId id) => $"sync:{id.Value:N}";
    private string StatePath => Path.Combine(_paths.Root, "scheduler-state.json");

    [LoggerMessage(1001, LogLevel.Error, "Host initialization failed: {Code} {Message}")]
    private static partial void LogInitializationFailure(ILogger logger, string? code, string? message);
    [LoggerMessage(1002, LogLevel.Warning, "A host client request failed.")]
    private static partial void LogClientFailure(ILogger logger, Exception exception);
    [LoggerMessage(1003, LogLevel.Warning, "Operation {Operation} failed: {Code} {Message}")]
    private static partial void LogOperationFailure(ILogger logger, string operation, string? code, string? message);
    [LoggerMessage(1004, LogLevel.Error, "Operation {Operation} threw unexpectedly.")]
    private static partial void LogOperationException(ILogger logger, string operation, Exception exception);
    [LoggerMessage(1005, LogLevel.Warning, "Scheduler state I/O failed.")]
    private static partial void LogStateFailure(ILogger logger, Exception exception);
    [LoggerMessage(1006, LogLevel.Warning, "Timed out while draining {TaskCount} host operations during shutdown.")]
    private static partial void LogDrainTimeout(ILogger logger, int taskCount);
    [LoggerMessage(1007, LogLevel.Information, "Remote wipe local cleanup completed. Server acknowledgement confirmed: {ServerAcknowledged}.")]
    private static partial void LogRemoteWipeCompleted(ILogger logger, bool serverAcknowledged);
    [LoggerMessage(1008, LogLevel.Error, "Remote wipe could not be completed or acknowledged; retaining the protected wipe registration for retry.")]
    private static partial void LogRemoteWipeFailure(ILogger logger, Exception exception);
    [LoggerMessage(1009, LogLevel.Warning, "The protected remote-wipe registration could not be read.")]
    private static partial void LogRemoteWipeRegistrationFailure(ILogger logger, Exception exception);
    [LoggerMessage(1010, LogLevel.Warning, "Nextcloud requested a remote wipe; stopping local work before deleting account data.")]
    private static partial void LogRemoteWipeRequested(ILogger logger);
    [LoggerMessage(1012, LogLevel.Warning, "Remote-wipe enrollment could not be recovered. Existing connections were preserved and enrollment will be retried.")]
    private static partial void LogRemoteWipeEnrollmentFailure(ILogger logger);
    [LoggerMessage(1013, LogLevel.Warning, "The managed rclone version could not be added to the client identity: {Code} {Message}")]
    private static partial void LogRuntimeInspectionFailure(ILogger logger, string? code, string? message);
    [LoggerMessage(1014, LogLevel.Information, "The managed rclone version is now included in the client identity.")]
    private static partial void LogRuntimeInspectionRecovered(ILogger logger);
}
