using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Setup;
using ResoDrive.Core.Validation;
using ResoDrive.Windows;
using WpfFrameworkElement = System.Windows.FrameworkElement;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfMessageBox = ResoDrive.App.ModernMessageBox;
using WpfWindow = System.Windows.Window;

namespace ResoDrive.App;

#pragma warning disable CA1001 // WPF owns this window's lifetime; Closed disposes all owned resources.
public partial class MainWindow : WpfWindow
#pragma warning restore CA1001
{
    private static readonly TimeSpan InitialHostProbeTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StartingHostProbeTimeout = TimeSpan.FromMilliseconds(750);
    private const int MaximumAutomaticHostRecoveryAttempts = 3;
    private readonly ApplicationPaths _paths = new();
    private readonly AccountDataGuard _accountData = new(new ApplicationPaths());
    private readonly ShellViewModel _model = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _settingsMutationGate = new(1, 1);
    private CancellationTokenSource? _rcloneOperationCancellation;
    private CancellationTokenSource? _applicationDownloadCancellation;
    private AtomicSettingsStore? _store;
    private ManagerSettings _settings = new();
    private bool _refreshing;
    private bool _exitRequested;
    private bool _exitChecking;
    private bool _rcloneUpdateBusy;
    private bool _rcloneMutationBusy;
    private bool _settingsSaveBusy;
    private bool _settingsClosing;
    private int _componentStatusGeneration;
    private bool _hostUnavailableReported;
    private bool _hostRecoveryBusy;
    private int _hostRecoveryAttempts;
    private readonly HashSet<string> _activeUiActions = new(StringComparer.Ordinal);
    private WindowState _windowStateBeforeMinimize = WindowState.Normal;
    private readonly bool _startInBackground;
    private RcloneUpdateCheck? _rcloneUpdate;
    private bool _rcloneRepairRequested;
    private bool _rcloneRuntimeReady;
    private string? _rcloneInstalledVersion;
    private bool _applicationUpdateBusy;
    private bool _applicationUpdateCheckFailed;
    private bool _rcloneUpdateCheckFailed;
    private ApplicationUpdateCheck? _applicationUpdate;
    private readonly TrayController _tray;

    internal event EventHandler? StartupReady;

    internal bool IsStartupReady { get; private set; }
    internal bool IsClosing { get; private set; }

    public MainWindow(bool startInBackground = false)
    {
        _startInBackground = startInBackground;
        InitializeComponent();
        StatusVisuals.ApplyPending(ApplicationUpdateStatusIcon);
        SetLiveText(ApplicationUpdateStatusText, $"Installed {ProductInfo.Version} · Checking for updates…");
        SourceInitialized += (_, _) =>
        {
            WindowAppearance.ApplyDarkTitleBar(this);
            WindowAppearance.ConstrainToWorkArea(this, margin: 0, constrainMaximum: false);
        };
        DataContext = _model;
        _tray = new TrayController(
            Dispatcher,
            () => _model.Mounts,
            () => _model.Jobs,
            RunTrayMountAsync,
            RunTraySyncAsync,
            OpenDrive,
            RefreshTrayAsync,
            RestoreWindow,
            ExitApplication,
            exception => _model.AddLogEntry("\uE783", "Tray action failed", exception.Message, true));
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        StateChanged += MainWindow_StateChanged;
        SizeChanged += (_, _) => ApplyResponsiveNavigation();
        System.Windows.Application.Current.SessionEnding += Application_SessionEnding;
        _timer.Tick += Timer_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowWelcomeForNewProfile();
            var store = await Task.Run(() => new AtomicSettingsStore(_paths));
            if (_settingsClosing)
            {
                store.Dispose();
                return;
            }
            _store = store;
            await LoadSettingsAsync();
            await ReconcileAutostartAsync();
            var runtimeReady = (await new RcloneRuntimeLocator(_paths)
                .InspectAsync(_lifetimeCancellation.Token)).Succeeded;
            var setupNeeded = !File.Exists(_paths.ConfigFile);
            var setupDeferred = !runtimeReady;
            if (runtimeReady && setupNeeded && !_startInBackground)
            {
                setupDeferred = !await RunSetupAsync(firstRun: true);
            }
            else if (setupNeeded)
            {
                setupDeferred = true;
            }

            if (!await LoadAndConnectAsync(setupDeferred ? "Settings" : "Mounts"))
            {
                Close();
                return;
            }
            _timer.Start();
            IsStartupReady = true;
            StartupReady?.Invoke(this, EventArgs.Empty);
            _ = ObservePreviousUpdateOutcomeAsync();
            await RefreshConnectionMetadataAsync();
            await Task.WhenAll(CheckApplicationUpdateAsync(), CheckRcloneUpdateAsync());
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window was closed while startup work was still in flight.
        }
        catch (Exception exception)
        {
            var errorId = UiDiagnosticLog.Current.Exception("startup.failed", exception);
            ShowError(
                $"{ProductInfo.Name} could not start",
                $"{exception.Message}\n\nError ID: {errorId}");
            _exitRequested = true;
            Close();
        }
    }

    private async Task ObservePreviousUpdateOutcomeAsync()
    {
        ApplicationUpdateOutcome? outcome = null;
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                outcome = ApplicationUpdateHandoff.ReadOutcome(_paths.Updates);
                if (outcome is null || outcome.Finalized)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(250), _lifetimeCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (outcome is null)
            return;

        var failed = !outcome.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
            !outcome.RelaunchAcknowledged;
        var message = outcome.Finalized
            ? outcome.Message
            : "The previous update handoff did not finish. ResoDrive reopened safely; check the update log for details.";
        _model.AddLogEntry(
            failed || !outcome.Finalized ? "\uE783" : "\uE73E",
            outcome.Finalized ? "Application update result" : "Application update interrupted",
            message,
            failed || !outcome.Finalized);
        if (failed || !outcome.Finalized)
            ShowError("Previous update did not finish", message);
        ApplicationUpdateHandoff.DeleteOutcome(_paths.Updates);
    }

    private void ShowWelcomeForNewProfile()
    {
        if (_startInBackground || File.Exists(_paths.WelcomeCompletedFile) ||
            File.Exists(_paths.SettingsFile) || File.Exists(_paths.ConfigFile))
            return;

        var welcome = new WelcomeWindow { Owner = this };
        if (welcome.ShowDialog() != true)
            return;

        try
        {
            _paths.EnsureCreated();
            File.WriteAllText(_paths.WelcomeCompletedFile, "1");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _model.AddLogEntry("\uE783", "Welcome state was not saved", exception.Message, true);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        IsClosing = true;
        IsStartupReady = false;
        _timer.Stop();
        _settingsClosing = true;
        _lifetimeCancellation.Cancel();
        _componentStatusGeneration++;
        System.Windows.Application.Current.SessionEnding -= Application_SessionEnding;
        _tray.Dispose();
        _ = DisposeSettingsStoreAfterDrainAsync();
    }

    private async Task DisposeSettingsStoreAfterDrainAsync()
    {
        await _settingsMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _store?.Dispose();
            _store = null;
        }
        finally
        {
            _settingsMutationGate.Release();
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshStatusAsync();
        }
        catch (Exception exception)
        {
            _model.AddLogEntry("\uE783", "Status refresh failed", exception.Message, true);
        }
    }

    private async Task<bool> LoadAndConnectAsync(string initialPage)
    {
        var response = await EnsureHostAsync();
        if (!_startInBackground &&
            response.ErrorCode?.Equals(
                "host.different_installation",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            var takeover = await OfferTakeControlAsync(response);
            if (takeover is null)
                return false;
            response = takeover;
        }
        _model.Load(
            _settings,
            response.Succeeded ? response.Mounts : null,
            response.Succeeded ? response.SyncJobs : null
        );
        UpdateTrayStatus();
        LoadSettingsControls();
        UpdateConnectionStatus();
        if (!response.Succeeded)
        {
            ShowHostInterrupted(response.ErrorMessage, recoveryExhausted: false);
            _model.AddLogEntry(
                "\uE783",
                "Background host unavailable",
                response.ErrorMessage ?? "The host did not respond.",
                true
            );
        }
        else
        {
            ShowHostConnected();
        }
        SelectPage(initialPage);
        return true;
    }

    private async Task<HostResponse?> OfferTakeControlAsync(HostResponse foreignHost)
    {
        if (string.IsNullOrWhiteSpace(foreignHost.HostBaseDirectory))
            return foreignHost;

        var activeDrives = foreignHost.Mounts?.Count(status =>
            status.Lifecycle is not "Stopped" and not "Failed") ?? 0;
        var activeJobs = foreignHost.SyncJobs?.Count(status =>
            status.Lifecycle.Equals("Running", StringComparison.OrdinalIgnoreCase)) ?? 0;
        var hasActiveWork = activeDrives > 0 || activeJobs > 0;
        var workSummary = hasActiveWork
            ? $"\n\nActive work: {activeDrives} drive{(activeDrives == 1 ? string.Empty : "s")} and {activeJobs} sync job{(activeJobs == 1 ? string.Empty : "s")}."
            : string.Empty;
        var message =
            $"{ProductInfo.Name} is already running from:\n{foreignHost.HostBaseDirectory}\n\nOnly one copy can manage this account at a time.{workSummary}" +
            (hasActiveWork ? "\n\nTaking control will stop this work. Remote files are not deleted." : string.Empty);
        var confirmed = WpfMessageBox.Confirm(
            this,
            message,
            "Another copy is managing this account",
            hasActiveWork ? "Stop work and take control" : "Take control"
        );
        if (!confirmed)
            return null;

        var shutdown = await HostClient.ShutdownForeignHostAsync(
            foreignHost.HostBaseDirectory,
            hasActiveWork
        );
        if (!shutdown.Succeeded &&
            shutdown.ErrorCode?.Equals("host.work_active", StringComparison.OrdinalIgnoreCase) == true)
        {
            var stopConfirmed = WpfMessageBox.Confirm(
                this,
                "Work started in the other copy while you were deciding. Stop it and take control? Remote files are not deleted.",
                "Active work detected",
                "Stop work and take control"
            );
            if (!stopConfirmed)
                return null;
            shutdown = await HostClient.ShutdownForeignHostAsync(
                foreignHost.HostBaseDirectory,
                confirmed: true
            );
        }
        if (!shutdown.Succeeded)
        {
            ShowError(
                "Could not take control",
                shutdown.ErrorMessage ?? $"The other {ProductInfo.Name} host did not stop."
            );
            return null;
        }

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(500);
            var response = await EnsureHostAsync();
            if (response.Succeeded ||
                response.ErrorCode?.Equals(
                    "host.different_installation",
                    StringComparison.OrdinalIgnoreCase) != true)
            {
                return response;
            }
        }

        ShowError(
            "Could not take control",
            $"The other {ProductInfo.Name} host did not stop in time. Close it from its notification-area menu and try again."
        );
        return null;
    }

    private async Task LoadSettingsAsync()
    {
        if (!await EnterSettingsMutationAsync())
        {
            throw new OperationCanceledException(_lifetimeCancellation.Token);
        }

        try
        {
            var result = await (
                _store ?? throw new InvalidOperationException("Settings are not ready.")
            ).LoadAsync();
            if (!result.Succeeded || result.Value is null)
                throw new InvalidOperationException(
                    result.Error?.Message ?? "Settings could not be loaded."
                );
            _settings = result.Value;
        }
        finally
        {
            _settingsMutationGate.Release();
        }
    }

    private async Task ReconcileAutostartAsync()
    {
        var autostart = new ScheduledTaskAutostartService(CurrentExecutablePath);
        var current = await autostart.IsEnabledAsync(_lifetimeCancellation.Token);
        if (!current.Succeeded)
        {
            _model.AddLogEntry(
                "\uE783",
                "Windows startup task could not be checked",
                current.Error?.Message ?? "The startup task could not be read.",
                true);
            return;
        }
        if (current.Value == _settings.Application.StartWithWindows)
            return;

        var changed = await autostart.SetEnabledAsync(
            _settings.Application.StartWithWindows,
            _lifetimeCancellation.Token);
        if (!changed.Succeeded)
        {
            _model.AddLogEntry(
                "\uE783",
                "Windows startup task was not reconciled",
                changed.Error?.Message ?? "The startup task could not be updated.",
                true);
        }
    }

    private async Task<bool> EnrichConnectionMetadataAsync()
    {
        if (!await EnterSettingsMutationAsync())
        {
            return false;
        }

        try
        {
            if (_settings.Mounts.All(mount =>
                    !string.IsNullOrWhiteSpace(mount.ConnectionHost) &&
                    !string.IsNullOrWhiteSpace(mount.ConnectionType)))
            {
                return false;
            }

            var metadata = await RcloneConnectionMetadataService.ReadAsync(
                new RcloneRuntimeLocator(_paths).ExecutablePath,
                _paths);
            if (!metadata.Succeeded || metadata.Value is null || metadata.Value.Count == 0)
            {
                return false;
            }

            var changed = false;
            var mounts = _settings.Mounts.Select(mount =>
            {
                if (!metadata.Value.TryGetValue(mount.RemoteName, out var connection))
                {
                    return mount;
                }

                var host = string.IsNullOrWhiteSpace(mount.ConnectionHost)
                    ? connection.Host
                    : mount.ConnectionHost;
                var type = string.IsNullOrWhiteSpace(mount.ConnectionType)
                    ? connection.Type
                    : mount.ConnectionType;
                if (host == mount.ConnectionHost && type == mount.ConnectionType)
                    return mount;
                changed = true;
                return mount with { ConnectionHost = host, ConnectionType = type };
            }).ToArray();
            if (!changed)
            {
                return false;
            }

            var saved = await (_store ?? throw new InvalidOperationException("Settings are not ready."))
                .SaveAsync(_settings with { Mounts = mounts }, _settings.Revision);
            if (saved.Succeeded && saved.Value is not null)
            {
                _settings = saved.Value;
                return true;
            }
            return false;
        }
        finally
        {
            _settingsMutationGate.Release();
        }
    }

    private async Task RefreshConnectionMetadataAsync()
    {
        try
        {
            if (!await EnrichConnectionMetadataAsync())
                return;

            var status = await HostClient.SendAsync(new HostRequest("status"));
            _model.Load(
                _settings,
                status.Succeeded ? status.Mounts : null,
                status.Succeeded ? status.SyncJobs : null);
            UpdateTrayStatus();
        }
        catch (Exception exception)
        {
            _model.AddLogEntry(
                "\uE783",
                "Connection details unavailable",
                exception.Message,
                true);
        }
    }

    private static async Task<HostResponse> EnsureHostAsync()
    {
        var statusRequest = new HostRequest("status");
        var response = await HostClient.SendAsync(statusRequest, InitialHostProbeTimeout);
        if (
            response.Succeeded
            || !string.Equals(
                response.ErrorCode,
                "host.unavailable",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return response;
        var hostPath = CurrentExecutablePath;
        if (!File.Exists(hostPath))
            return new HostResponse(
                false,
                "host.not_found",
                $"{ProductInfo.Name} was not found at '{hostPath}'."
            );
        using var hostProcess =
            await Task.Run(() => Process.Start(
                new ProcessStartInfo(hostPath, "--host")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                }
            )) ?? throw new InvalidOperationException("The background host could not be started.");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(350);
            response = await HostClient.SendAsync(statusRequest, StartingHostProbeTimeout);
            if (response.Succeeded)
                return response;
        }
        return response;
    }

    private async Task RefreshStatusAsync()
    {
        if (CloseForRemoteWipe()) return;
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var response = await HostClient.SendAsync(new HostRequest("status"));
            if (response.Succeeded)
            {
                if (_hostUnavailableReported)
                {
                    _model.AddLogEntry("\uE73E", "Background host connected", "Status updates resumed");
                    _hostUnavailableReported = false;
                }
                ShowHostConnected();
                _model.ApplyStatus(response.Mounts);
                _model.ApplySyncStatus(response.SyncJobs);
                UpdateTrayStatus();
            }
            else
            {
                if (!_hostUnavailableReported)
                {
                    _model.AddLogEntry(
                        "\uE783",
                        "Status delayed",
                        response.ErrorMessage ?? "Host unavailable",
                        true
                    );
                    _hostUnavailableReported = true;
                }
                ShowHostInterrupted(response.ErrorMessage, recoveryExhausted: false);
                if (response.ErrorCode?.Equals("host.unavailable", StringComparison.OrdinalIgnoreCase) == true)
                    await TryRecoverHostAsync();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task TryRecoverHostAsync(bool userInitiated = false)
    {
        if (_hostRecoveryBusy || IsClosing)
            return;
        if (userInitiated)
            _hostRecoveryAttempts = 0;
        if (_hostRecoveryAttempts >= MaximumAutomaticHostRecoveryAttempts)
        {
            ShowHostInterrupted(null, recoveryExhausted: true);
            return;
        }

        _hostRecoveryBusy = true;
        var attempt = ++_hostRecoveryAttempts;
        try
        {
            ShowHostInterrupted(null, recoveryExhausted: false);
            if (attempt > 1)
                await Task.Delay(TimeSpan.FromSeconds(attempt - 1), _lifetimeCancellation.Token);
            var response = await EnsureHostAsync();
            if (response.Succeeded)
            {
                _model.ApplyStatus(response.Mounts);
                _model.ApplySyncStatus(response.SyncJobs);
                UpdateTrayStatus();
                if (_hostUnavailableReported)
                    _model.AddLogEntry("\uE73E", "Background host connected", "Status updates resumed");
                _hostUnavailableReported = false;
                ShowHostConnected();
                return;
            }

            ShowHostInterrupted(
                response.ErrorMessage,
                recoveryExhausted: attempt >= MaximumAutomaticHostRecoveryAttempts);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowHostInterrupted(
                exception.Message,
                recoveryExhausted: attempt >= MaximumAutomaticHostRecoveryAttempts);
            if (attempt >= MaximumAutomaticHostRecoveryAttempts)
                _model.AddLogEntry("\uE783", "Host recovery paused", exception.Message, true);
        }
        finally
        {
            _hostRecoveryBusy = false;
        }
    }

    private void ShowHostConnected()
    {
        _hostRecoveryAttempts = 0;
        HostRecoveryBanner.Visibility = Visibility.Collapsed;
        HostRetryButton.Visibility = Visibility.Collapsed;
    }

    private void ShowHostInterrupted(string? detail, bool recoveryExhausted)
    {
        HostRecoveryBanner.Visibility = Visibility.Visible;
        HostRetryButton.Visibility = recoveryExhausted ? Visibility.Visible : Visibility.Collapsed;
        SetLiveText(HostRecoveryText, recoveryExhausted
            ? $"Connection interrupted · Automatic recovery paused.{FormatHostDetail(detail)}"
            : $"Connection interrupted · Reconnecting…{FormatHostDetail(detail)}");
    }

    private static string FormatHostDetail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : $"  {detail.Trim()}";

    private async void RetryHost_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiActionAsync(
            "Host recovery failed",
            () => TryRecoverHostAsync(userInitiated: true));

    private async Task<bool> SaveAndReloadAsync(ManagerSettings updated)
    {
        if (!await EnterSettingsMutationAsync())
        {
            return false;
        }

        try
        {
            return await SaveAndReloadCoreAsync(updated);
        }
        finally
        {
            _settingsMutationGate.Release();
        }
    }

    private async Task<bool> SaveAndReloadCoreAsync(ManagerSettings updated)
    {
        var definitions = new List<ResoDrive.Core.Domain.MountDefinition>();
        foreach (var mount in updated.Mounts)
        {
            var mapped = MountDefinitionMapper.ToDomain(mount);
            if (!mapped.Succeeded || mapped.Value is null)
            {
                ShowError(
                    "Invalid settings",
                    mapped.Error?.Message ?? $"Mount '{mount.DisplayName}' is invalid."
                );
                return false;
            }
            definitions.Add(mapped.Value);
        }
        var validation = new MountDefinitionValidator().ValidateCatalog(definitions);
        if (!validation.IsValid)
        {
            ShowError(
                "Invalid settings",
                string.Join(
                    Environment.NewLine,
                    validation.Issues.Select(issue => "• " + issue.Message)
                )
            );
            return false;
        }
        var previousSettings = _settings;
        var result = await (
            _store ?? throw new InvalidOperationException("Settings are not ready.")
        ).SaveAsync(updated, _settings.Revision);
        if (!result.Succeeded || result.Value is null)
        {
            ShowError(
                "Settings were not saved",
                result.Error?.Message ?? "Unknown settings error."
            );
            return false;
        }
        _settings = result.Value;
        var reload = await HostClient.SendAsync(new HostRequest("reload"));
        var restartDeclined = false;
        if (!reload.Succeeded &&
            reload.ErrorCode?.Equals("host.mount_restart_required", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (WpfMessageBox.Confirm(
                    this,
                    "Applying these changes will briefly disconnect the affected mounted drives. Drives that remain enabled will reconnect automatically. Continue?",
                    "Reconnect affected drives?",
                    "Apply and reconnect"))
            {
                reload = await HostClient.SendAsync(new HostRequest("reload", Confirmed: true));
            }
            else
            {
                restartDeclined = true;
            }
        }
        if (!reload.Succeeded)
        {
            var rollback = await _store.SaveAsync(previousSettings, _settings.Revision);
            if (rollback.Succeeded && rollback.Value is not null)
            {
                _settings = rollback.Value;
                await HostClient.SendAsync(new HostRequest("reload"));
            }
            if (!restartDeclined)
            {
                ShowError(
                    "Settings were not activated",
                    rollback.Succeeded
                        ? $"{reload.ErrorMessage ?? "The background host rejected the settings."}\n\nThe previous settings were restored."
                        : $"{reload.ErrorMessage ?? "The background host rejected the settings."}\n\nThe previous settings could not be restored automatically."
                );
            }
        }
        // A failed reload response contains no mount or sync snapshots. Query the host
        // again after rollback so the UI does not briefly present every drive as stopped.
        var status = await HostClient.SendAsync(new HostRequest("status"));
        _model.Load(
            _settings,
            status.Succeeded ? status.Mounts : null,
            status.Succeeded ? status.SyncJobs : null
        );
        UpdateTrayStatus();
        LoadSettingsControls();
        return reload.Succeeded;
    }

    private void LoadSettingsControls()
    {
        MinimizeToTrayBox.IsChecked = _settings.Application.MinimizeToTray;
        StartWithWindowsBox.IsChecked = _settings.Application.StartWithWindows;
    }

    private async Task<bool> SaveApplicationSettingsCoreAsync(ApplicationSettings application)
    {
        var result = await (
            _store ?? throw new InvalidOperationException("Settings are not ready."))
            .SaveAsync(_settings with { Application = application }, _settings.Revision);
        if (!result.Succeeded || result.Value is null)
        {
            ShowError(
                "Settings were not saved",
                result.Error?.Message ?? "Unknown settings error.");
            return false;
        }

        _settings = result.Value;
        LoadSettingsControls();
        return true;
    }

    private async void SettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsSaveBusy)
        {
            return;
        }

        _settingsSaveBusy = true;
        MinimizeToTrayBox.IsEnabled = false;
        StartWithWindowsBox.IsEnabled = false;
        ScheduledTaskAutostartService? autostart = null;
        bool? previousAutostart = null;
        var mutationEntered = false;
        try
        {
            mutationEntered = await EnterSettingsMutationAsync();
            if (!mutationEntered)
            {
                return;
            }

            var startWithWindows = StartWithWindowsBox.IsChecked == true;
            var autostartChanged = startWithWindows != _settings.Application.StartWithWindows;
            if (autostartChanged)
            {
                var executablePath = CurrentExecutablePath;
                autostart = new ScheduledTaskAutostartService(executablePath);
                var previous = await autostart.IsEnabledAsync();
                if (!previous.Succeeded)
                {
                    ShowError(
                        "Startup setting unavailable",
                        previous.Error?.Message ?? "The Windows startup task could not be read."
                    );
                    return;
                }
                previousAutostart = previous.Value == true;
                var autostartResult = await autostart.SetEnabledAsync(startWithWindows);
                if (!autostartResult.Succeeded)
                {
                    ShowError(
                        "Startup setting was not changed",
                        autostartResult.Error?.Message
                            ?? "The Windows startup task could not be changed."
                    );
                    return;
                }
            }
            var application = _settings.Application with
            {
                MinimizeToTray = MinimizeToTrayBox.IsChecked == true,
                StartWithWindows = startWithWindows,
            };
            if (!await SaveApplicationSettingsCoreAsync(application) &&
                autostart is not null && previousAutostart.HasValue)
            {
                await autostart.SetEnabledAsync(previousAutostart.Value);
            }
        }
        catch (Exception exception)
        {
            if (autostart is not null && previousAutostart.HasValue)
                await autostart.SetEnabledAsync(previousAutostart.Value);
            ShowError("Settings were not saved", exception.Message);
        }
        finally
        {
            if (mutationEntered)
            {
                _settingsMutationGate.Release();
            }
            LoadSettingsControls();
            MinimizeToTrayBox.IsEnabled = true;
            StartWithWindowsBox.IsEnabled = true;
            _settingsSaveBusy = false;
        }
    }

    private async Task<bool> RunSetupAsync(bool firstRun, bool hostAlreadyRunning = false)
    {
        var reservedDriveLetters = _settings.Mounts
            .Select(mount => mount.Target.DriveLetter)
            .Where(letter => letter.HasValue)
            .Select(letter => letter!.Value);
        var wizard = new SetupWindow(_paths, firstRun, reservedDriveLetters) { Owner = this };
        if (wizard.ShowDialog() != true || wizard.Result is null)
        {
            return false;
        }

        return await ApplyProvisioningResultAsync(
            wizard.Result,
            reloadHost: !firstRun || hostAlreadyRunning,
            applyStartupPreference: firstRun);
    }

    private async Task<bool> ApplyProvisioningResultAsync(
        ProfileProvisioningResult provisioning,
        bool reloadHost,
        bool applyStartupPreference)
    {
        using (provisioning)
        {
            var drive = provisioning.NewMount.Target.DriveLetter;
            var occupied = await new MountTargetInventory().GetOccupiedDriveLettersAsync();
            if (!occupied.Succeeded || occupied.Value is null)
            {
                ShowError(
                    "Drive letters unavailable",
                    occupied.Error?.Message ?? "Windows drive letters could not be checked.");
                return false;
            }
            if (drive is null || occupied.Value.Contains(char.ToUpperInvariant(drive.Value)))
            {
                ShowError(
                    "Drive letter is no longer available",
                    $"{drive}: became occupied while setup was running. Choose another drive and try again.");
                return false;
            }

            return await CommitProvisioningAsync(
                provisioning,
                reloadHost,
                applyStartupPreference);
        }
    }

    private async Task<bool> CommitProvisioningAsync(
        ProfileProvisioningResult provisioning,
        bool reloadHost,
        bool applyStartupPreference)
    {
        if (!await EnterSettingsMutationAsync())
        {
            return false;
        }

        try
        {
            return await CommitProvisioningCoreAsync(
                provisioning,
                reloadHost,
                applyStartupPreference);
        }
        finally
        {
            _settingsMutationGate.Release();
        }
    }

    private async Task<bool> CommitProvisioningCoreAsync(
        ProfileProvisioningResult provisioning,
        bool reloadHost,
        bool applyStartupPreference)
    {
        var executablePath = CurrentExecutablePath;

        var autostart = new ScheduledTaskAutostartService(executablePath);
        var previous = await autostart.IsEnabledAsync();
        if (!previous.Succeeded)
        {
            ShowError("Startup setting unavailable", previous.Error?.Message ?? "The Windows startup task could not be read.");
            return false;
        }

        var previousSettings = _settings;
        var desired = applyStartupPreference
            ? provisioning.StartWithWindows
            : previous.Value == true;
        var filesApplied = false;
        var autostartApplied = false;
        var settingsApplied = false;
        try
        {
            provisioning.Files.Apply();
            filesApplied = true;

            if (applyStartupPreference)
            {
                var changed = await autostart.SetEnabledAsync(desired);
                if (!changed.Succeeded)
                {
                    ShowError("Setup was not completed", changed.Error?.Message ?? "The Windows startup task could not be changed.");
                    return false;
                }
                autostartApplied = true;
            }

            var updated = previousSettings with
            {
                Application = previousSettings.Application with
                {
                    StartWithWindows = applyStartupPreference
                        ? provisioning.StartWithWindows
                        : previousSettings.Application.StartWithWindows,
                },
                Mounts = previousSettings.Mounts.Append(provisioning.NewMount).ToArray(),
            };
            var save = await (_store ?? throw new InvalidOperationException("Settings are not ready."))
                .SaveAsync(updated, previousSettings.Revision);
            if (!save.Succeeded || save.Value is null)
            {
                ShowError("Setup was not saved", save.Error?.Message ?? "The settings could not be saved.");
                return false;
            }

            _settings = save.Value;
            settingsApplied = true;
            if (reloadHost)
            {
                var reload = await HostClient.SendAsync(new HostRequest("reload"));
                if (!reload.Succeeded)
                {
                    ShowError(
                        "Setup could not be activated",
                        reload.ErrorMessage ?? "The background host rejected the new connection.");
                    return false;
                }
                _model.Load(_settings, reload.Mounts, reload.SyncJobs);
            }

            provisioning.Files.Complete();
            filesApplied = false;
            if (reloadHost && provisioning.NewMount.AutoMount.Equals(
                    "OnApplicationStart",
                    StringComparison.OrdinalIgnoreCase))
            {
                await HostClient.SendAsync(new HostRequest(
                    "start",
                    provisioning.NewMount.Id));
            }
            LoadSettingsControls();
            if (reloadHost)
            {
                await RefreshStatusAsync();
            }

            _model.AddLogEntry(
                "\uE77B",
                "Drive added",
                $"{provisioning.NewMount.DisplayName} · {provisioning.ConnectionSummary}");
            return true;
        }
        finally
        {
            if (filesApplied)
            {
                var settingsRestored = !settingsApplied || await TryRestoreSettingsAsync(previousSettings);
                if (settingsRestored)
                {
                    if (autostartApplied)
                    {
                        await autostart.SetEnabledAsync(previous.Value == true);
                    }
                    provisioning.Files.Rollback();
                    if (reloadHost && settingsApplied)
                    {
                        await HostClient.SendAsync(new HostRequest("reload"));
                    }
                }
                else
                {
                    // Keep the new config when settings cannot be restored; the files on disk
                    // remain a consistent pair and the next app start can activate them.
                    provisioning.Files.Complete();
                }
            }
        }
    }

    private async Task<bool> EnterSettingsMutationAsync()
    {
        if (_settingsClosing || CloseForRemoteWipe())
        {
            return false;
        }

        try
        {
            await _settingsMutationGate.WaitAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }

        if (!_settingsClosing && !CloseForRemoteWipe())
        {
            return true;
        }

        _settingsMutationGate.Release();
        return false;
    }

    private bool CloseForRemoteWipe()
    {
        if (IsClosing) return true;
        if (!_accountData.IsBlocked) return false;
        _settingsClosing = true;
        _exitRequested = true;
        _exitChecking = false;
        _timer.Stop();
        _lifetimeCancellation.Cancel();
        IsEnabled = false;
        foreach (System.Windows.Window dialog in OwnedWindows.Cast<System.Windows.Window>().ToArray())
            dialog.Close();
        WpfMessageBox.Show(this,
            "Nextcloud requested removal of local ResoDrive data. ResoDrive will close while cleanup finishes. " +
            "Close documents opened from its drives and keep this computer online.",
            "Remote wipe requested", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
        return true;
    }

    private async Task<bool> TryRestoreSettingsAsync(ManagerSettings previousSettings)
    {
        var result = await (_store ?? throw new InvalidOperationException("Settings are not ready."))
            .SaveAsync(previousSettings, _settings.Revision);
        if (!result.Succeeded || result.Value is null)
        {
            ShowError(
                "Setup requires an app restart",
                $"The new connection was saved, but the previous settings could not be restored after activation failed. Restart {ProductInfo.Name} to activate the consistent saved configuration.");
            return false;
        }

        _settings = result.Value;
        LoadSettingsControls();
        return true;
    }

    private void OpenWinFspReleases_Click(object sender, RoutedEventArgs e) =>
        SetupWindow.OpenWinFspReleases();

    private void OpenProfilesFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var samplePath = Path.Combine(AppContext.BaseDirectory, "profiles.sample.json");
            if (!File.Exists(_paths.ProfilesFile))
            {
                if (!File.Exists(samplePath))
                {
                    WpfMessageBox.Show(
                        this,
                        $"No profile sample was found at:\n{samplePath}",
                        "Profiles file",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                _paths.EnsureCreated();
                File.Copy(samplePath, _paths.ProfilesFile);
            }

            var editorPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var startInfo = new ProcessStartInfo(editorPath)
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(_paths.ProfilesFile);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                ShowError("Could not open profiles.json", "Windows Notepad could not be started.");
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                  System.ComponentModel.Win32Exception)
        {
            ShowError("Could not open profiles.json", exception.Message);
        }
    }

    private async void ExportSettings_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiActionAsync(
            "Settings export failed",
            ExportSettingsAsync);

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiActionAsync("Diagnostic export failed", async () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export diagnostic report",
                Filter = "Text files (*.txt)|*.txt",
                FileName = $"resodrive-diagnostics-{DateTime.Now:yyyyMMdd}.txt",
                DefaultExt = ".txt",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true)
                return;
            var status = await HostClient.SendAsync(new HostRequest("status"), _lifetimeCancellation.Token);
            var winFsp = await WinFspPrerequisiteService.InspectAsync();
            var lines = File.Exists(_paths.UiLogFile)
                ? await File.ReadAllLinesAsync(_paths.UiLogFile, _lifetimeCancellation.Token)
                : [];
            var report = DiagnosticReport.Create(_settings, status, ProductInfo.Version,
                _rcloneInstalledVersion, winFsp.Value?.Version, lines);
            await new RecoveryToolsService(_paths).ExportDiagnosticReportAsync(dialog.FileName, report, _lifetimeCancellation.Token);
            WpfMessageBox.Show(this, "The report is saved. It omits names, addresses, paths, credentials and raw log messages. Review it before sharing.",
                "Diagnostic report saved", MessageBoxButton.OK, MessageBoxImage.Information);
        });

    private async Task ExportSettingsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export ResoDrive settings",
            Filter = "JSON files (*.json)|*.json",
            FileName = $"resodrive-settings-{DateTime.Now:yyyyMMdd}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var result = await new RecoveryToolsService(_paths).ExportSettingsAsync(
            dialog.FileName,
            _lifetimeCancellation.Token);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Error?.Message ?? "The export failed.");

        UiDiagnosticLog.Current.Information("recovery.settings_exported");
    }

    private async void ImportSettings_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiActionAsync(
            "Settings import failed",
            ImportSettingsAsync);

    private async Task ImportSettingsAsync()
    {
        if (_store is null)
            throw new InvalidOperationException("Settings are still loading.");
        if (_model.Mounts.Any(mount => mount.ShouldStop || mount.IsTransient) ||
            _model.Jobs.Any(job => job.IsBusy))
        {
            ShowError(
                "Stop active work before importing",
                "Unmount all drives and stop running sync jobs, then import the settings file again.");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import ResoDrive settings",
            Filter = "ResoDrive settings (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (!WpfMessageBox.Confirm(
                this,
                $"Import settings from {Path.GetFileName(dialog.FileName)}?\n\n" +
                "Your current settings will be kept as a pre-import copy. Connection credentials and rclone configuration are not imported.",
                "Import settings?",
                "Import settings"))
        {
            return;
        }

        if (!await EnterSettingsMutationAsync())
            return;
        try
        {
            var liveStatus = await HostClient.SendAsync(
                new HostRequest("status"),
                _lifetimeCancellation.Token);
            if (!liveStatus.Succeeded)
                throw new InvalidOperationException(
                    liveStatus.ErrorMessage ?? "The background host status could not be checked.");
            if (HasActiveHostWork(liveStatus))
            {
                ShowError(
                    "Stop active work before importing",
                    "Unmount all drives and stop running sync jobs, then import the settings file again.");
                return;
            }

            var previousSettings = _settings;
            var imported = await _store.ImportAsync(dialog.FileName, _lifetimeCancellation.Token);
            if (!imported.Succeeded || imported.Value is null)
                throw new InvalidOperationException(
                    imported.Error?.Message ?? "The settings file could not be imported.");

            _settings = imported.Value;
            var reload = await HostClient.SendAsync(new HostRequest("reload"), _lifetimeCancellation.Token);
            if (!reload.Succeeded)
            {
                var rollback = await _store.SaveAsync(
                    previousSettings,
                    _settings.Revision,
                    _lifetimeCancellation.Token);
                HostResponse? rollbackReload = null;
                if (rollback.Succeeded && rollback.Value is not null)
                {
                    _settings = rollback.Value;
                    rollbackReload = await HostClient.SendAsync(
                        new HostRequest("reload"),
                        _lifetimeCancellation.Token);
                }
                var rollbackStatus = rollbackReload?.Succeeded == true
                    ? await HostClient.SendAsync(
                        new HostRequest("status"),
                        _lifetimeCancellation.Token)
                    : rollbackReload;
                LoadSettingsControls();
                _model.Load(
                    _settings,
                    rollbackStatus?.Succeeded == true ? rollbackStatus.Mounts : null,
                    rollbackStatus?.Succeeded == true ? rollbackStatus.SyncJobs : null);
                UpdateTrayStatus();
                ShowError(
                    "Settings were not activated",
                    rollback.Succeeded
                        ? $"{reload.ErrorMessage ?? "The background host rejected the imported settings."}\n\nThe previous settings were restored."
                        : $"{reload.ErrorMessage ?? "The background host rejected the imported settings."}\n\nThe previous settings could not be restored automatically.");
                return;
            }

            LoadSettingsControls();
            await ReconcileAutostartAsync();
            _model.Load(
                _settings,
                reload.Mounts,
                reload.SyncJobs);
            UpdateTrayStatus();
            UiDiagnosticLog.Current.Information("recovery.settings_imported");
        }
        finally
        {
            _settingsMutationGate.Release();
        }
    }

    private static bool HasActiveHostWork(HostResponse response) =>
        response.Mounts?.Any(mount =>
            !Enum.TryParse(mount.Lifecycle, true, out MountLifecycle lifecycle) ||
            lifecycle is not MountLifecycle.Stopped and not MountLifecycle.Failed) == true ||
        response.SyncJobs?.Any(job =>
            !Enum.TryParse(job.Lifecycle, true, out SyncLifecycle lifecycle) ||
            lifecycle is SyncLifecycle.Queued or SyncLifecycle.Running) == true;

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureCreated();
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(_paths.Logs);
            Process.Start(startInfo);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                  System.ComponentModel.Win32Exception)
        {
            ShowError("Could not open logs", exception.Message);
        }
    }

    private async void MountAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfFrameworkElement)?.DataContext is not MountRow row)
            return;
        var command = row.ShouldStop ? "stop" : "start";
        await ExecuteUiActionAsync(
            "Mount action failed",
            () => RunMountActionAsync(row, command));
    }

    private async Task RunMountActionAsync(MountRow row, string command)
    {
        if (command == "start" && _rcloneMutationBusy)
        {
            throw new InvalidOperationException(
                "Wait for the rclone component operation to finish before mounting a drive.");
        }
        var response = await HostClient.SendAsync(new HostRequest(command, row.Id));
        if (!response.Succeeded)
            throw new InvalidOperationException(RcloneErrorMessage.Clean(
                response.ErrorMessage,
                "The host rejected the request."));
        _model.ApplyStatus(response.Mounts);
        UpdateTrayStatus();
    }

    private void MountErrorDetails_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfFrameworkElement)?.DataContext is not MountRow row ||
            string.IsNullOrWhiteSpace(row.ErrorDetail))
            return;

        ShowError($"{row.Name} could not be mounted", row.ErrorDetail);
    }

    private async void JobAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfFrameworkElement)?.DataContext is not SyncRow row)
            return;
        await ExecuteUiActionAsync(
            "Sync action failed",
            () => RunSyncAsync(row));
    }

    private async Task RunSyncAsync(SyncRow row)
    {
        if (!row.IsBusy && _rcloneMutationBusy)
        {
            throw new InvalidOperationException(
                "Wait for the rclone component operation to finish before starting a sync.");
        }
        if (
            !row.IsBusy
            && row.IsMirror
            && !WpfMessageBox.Confirm(
                this,
                $"{row.Name} will mirror:\n\n{row.Route}\n\nFiles that exist only at the destination may be deleted. Run it now?",
                "Confirm mirror run",
                "Run mirror"
            )
        )
        {
            return;
        }
        var command = row.IsBusy ? "cancel-sync" : "run-sync";
        var response = await HostClient.SendAsync(new HostRequest(command, row.MountId, row.Id));
        if (!response.Succeeded)
            throw new InvalidOperationException(
                response.ErrorMessage ?? "The host rejected the request.");
        _model.ApplySyncStatus(response.SyncJobs);
        UpdateTrayStatus();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await ExecuteUiActionAsync(
            "Refresh failed",
            RefreshStatusAsync);

    private void OpenMount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfFrameworkElement)?.DataContext is MountRow row)
            OpenDrive(row);
    }

    private async void AddMount_Click(object sender, RoutedEventArgs e)
        => await ExecuteUiActionAsync(
            "Could not add drive",
            AddMountAsync);

    private async Task AddMountAsync()
    {
        if (_rcloneMutationBusy || !_rcloneRuntimeReady)
        {
            ShowError("rclone is not ready",
                "Download or repair the managed rclone component in Settings before adding a drive.");
            return;
        }
        var runtime = await new RcloneRuntimeLocator(_paths)
            .InspectAsync(_lifetimeCancellation.Token);
        if (!runtime.Succeeded)
        {
            SelectPage("Settings");
            ShowError(
                "rclone is required",
                "Download or repair the managed rclone component in Settings before adding a drive.");
            return;
        }

        await RunSetupAsync(firstRun: false);
    }

    private async void Options_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfFrameworkElement)?.DataContext is not MountRow row)
            return;
        await ExecuteUiActionAsync(
            "Could not update drive",
            () => EditMountAsync(row));
    }

    private async Task EditMountAsync(MountRow row)
    {
        var editor = new MountEditorWindow(
            row.Settings,
            row.Settings.RemoteName
        )
        {
            Owner = this,
        };
        if (editor.ShowDialog() != true)
            return;
        var mounts = editor.DeleteRequested
            ? _settings.Mounts.Where(mount => mount.Id != row.Id).ToArray()
            : _settings
                .Mounts.Select(mount => mount.Id == row.Id ? editor.Value ?? mount : mount)
                .ToArray();
        await SaveAndReloadAsync(_settings with { Mounts = mounts });
    }

    private async void NewJob_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(
            "Could not add sync job",
            AddSyncJobAsync);
    }

    private async Task AddSyncJobAsync()
    {
        if (_settings.Mounts.Count == 0)
        {
            SelectPage("Mounts");
            return;
        }
        var editor = new SyncEditorWindow(_settings.Mounts, null, null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Value is null || editor.SelectedMount is null)
            return;
        var id = editor.SelectedMount.Id;
        var mounts = _settings
            .Mounts.Select(mount =>
                mount.Id == id
                    ? mount with
                    {
                        SyncJobs = mount.SyncJobs.Append(editor.Value).ToArray(),
                    }
                    : mount
            )
            .ToArray();
        await SaveAndReloadAsync(_settings with { Mounts = mounts });
    }

    private async void JobOptions_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfFrameworkElement)?.DataContext is not SyncRow row)
            return;
        await ExecuteUiActionAsync(
            "Could not update sync job",
            () => EditSyncJobAsync(row));
    }

    private async Task EditSyncJobAsync(SyncRow row)
    {
        if (row.IsBusy)
        {
            ShowError("Job is running", "Stop this sync job before editing or deleting it.");
            return;
        }
        var editor = new SyncEditorWindow(_settings.Mounts, row.MountId, row.Settings)
        {
            Owner = this,
        };
        if (editor.ShowDialog() != true)
            return;
        var mounts = _settings
            .Mounts.Select(mount =>
                mount.Id != row.MountId
                    ? mount
                    : mount with
                    {
                        SyncJobs = editor.DeleteRequested
                            ? mount.SyncJobs.Where(job => job.Id != row.Id).ToArray()
                            : mount
                                .SyncJobs.Select(job =>
                                    job.Id == row.Id ? editor.Value ?? job : job
                                )
                                .ToArray(),
                    }
            )
            .ToArray();
        await SaveAndReloadAsync(_settings with { Mounts = mounts });
    }

    private async Task ExecuteUiActionAsync(
        string heading,
        Func<Task> action)
    {
        if (!_activeUiActions.Add(heading))
            return;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _model.AddLogEntry("\uE783", heading, exception.Message, true);
            ShowError(heading, exception.Message);
        }
        finally
        {
            _activeUiActions.Remove(heading);
        }
    }

    private static void SetLiveText(System.Windows.Controls.TextBlock target, string text)
    {
        if (target.Text.Equals(text, StringComparison.Ordinal))
            return;
        target.Text = text;
        var peer = UIElementAutomationPeer.FromElement(target) ??
            UIElementAutomationPeer.CreatePeerForElement(target);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static void SetProgressText(System.Windows.Controls.TextBlock target, string text)
    {
        if (!target.Text.Equals(text, StringComparison.Ordinal))
            target.Text = text;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfRadioButton { Tag: string page })
            SelectPage(page);
    }

    private void SelectPage(string page)
    {
        MountsPage.Visibility = page == "Mounts" ? Visibility.Visible : Visibility.Collapsed;
        JobsPage.Visibility = page == "Jobs" ? Visibility.Visible : Visibility.Collapsed;
        LogPage.Visibility = page == "Log" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { MountsNav, JobsNav, LogNav, SettingsNav })
            button.IsChecked = Equals(button.Tag, page);
    }

    private void ApplyResponsiveNavigation()
    {
        var compact = ActualWidth < 800;
        NavColumn.Width = new GridLength(compact ? 56 : 176);
        BrandPanel.HorizontalAlignment = compact
            ? System.Windows.HorizontalAlignment.Center
            : System.Windows.HorizontalAlignment.Left;
        BrandPanel.Margin = compact ? new Thickness(0) : new Thickness(12, 0, 0, 0);
        foreach (var button in new[] { MountsNav, JobsNav, LogNav, SettingsNav })
        {
            button.HorizontalContentAlignment = compact
                ? System.Windows.HorizontalAlignment.Center
                : System.Windows.HorizontalAlignment.Stretch;
            button.Padding = compact ? new Thickness(0) : new Thickness(12, 0, 0, 0);
        }
        foreach (var label in new[] { Brand, MountsLabel, JobsLabel, LogLabel, SettingsLabel })
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MountsNav.ToolTip = compact ? "Drives" : null;
        JobsNav.ToolTip = compact ? "Sync" : null;
        LogNav.ToolTip = compact ? "Log" : null;
        SettingsNav.ToolTip = compact ? "Settings" : null;
    }

    private void OpenDrive(MountRow row)
    {
        if (row.Drive == '?')
            return;
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo($"{row.Drive}:\\") { UseShellExecute = true }
            );
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowError("Could not open drive", exception.Message);
        }
    }

    private async Task<TrayActionResult> RunTrayMountAsync(MountRow row)
    {
        var command = row.ShouldStop ? "stop" : "start";
        if (command == "start" && _rcloneMutationBusy)
            return TrayActionResult.Failure("rclone is being updated", "Try again when the component operation finishes.");
        var response = await HostClient.SendAsync(new HostRequest(command, row.Id));
        if (!response.Succeeded)
        {
            return TrayActionResult.Failure(
                $"{row.Name} failed",
                RcloneErrorMessage.Clean(
                    response.ErrorMessage,
                    "The background host rejected the request."));
        }

        _model.ApplyStatus(response.Mounts);
        UpdateTrayStatus();
        var updated = _model.Mounts.FirstOrDefault(mount => mount.Id == row.Id);
        return TrayActionResult.Success(
            row.Name,
            updated?.StatusText ?? $"The {command} request was accepted.");
    }

    private async Task<TrayActionResult> RunTraySyncAsync(SyncRow row)
    {
        if (!row.IsBusy && _rcloneMutationBusy)
            return TrayActionResult.Failure("rclone is being updated", "Try again when the component operation finishes.");
        if (!row.IsBusy && row.IsMirror)
        {
            RestoreWindow();
            SelectPage("Jobs");
            return TrayActionResult.SilentSuccess();
        }

        var command = row.IsBusy ? "cancel-sync" : "run-sync";
        var response = await HostClient.SendAsync(new HostRequest(command, row.MountId, row.Id));
        if (!response.Succeeded)
        {
            return TrayActionResult.Failure(
                $"{row.Name} failed",
                response.ErrorMessage ?? "The background host rejected the request.");
        }

        _model.ApplySyncStatus(response.SyncJobs);
        UpdateTrayStatus();
        var updated = _model.Jobs.FirstOrDefault(job => job.Id == row.Id);
        return TrayActionResult.Success(row.Name, updated?.Result ?? "The request was accepted.");
    }

    private async Task<TrayActionResult> RefreshTrayAsync()
    {
        var response = await HostClient.SendAsync(new HostRequest("status"));
        if (!response.Succeeded)
        {
            return TrayActionResult.Failure(
                "Refresh failed",
                response.ErrorMessage ?? "The background host is unavailable.");
        }

        _model.ApplyStatus(response.Mounts);
        _model.ApplySyncStatus(response.SyncJobs);
        UpdateTrayStatus();
        return TrayActionResult.SilentSuccess();
    }

    private void UpdateTrayStatus() => _tray.UpdateStatus(
        _model.Mounts.Count(mount => mount.IsMounted),
        _model.Jobs.Count(job => job.IsBusy));

    private void OpenAbout_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private async void ExitApplication()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _exitChecking = true;
        _timer.Stop();
        try
        {
            var status = await HostClient.SendAsync(
                new HostRequest("status"),
                TimeSpan.FromMilliseconds(500));
            if (!status.Succeeded &&
                (status.ErrorCode is not "host.unavailable" || IsInstalledHostProcessRunning()))
            {
                _exitRequested = false;
                _timer.Start();
                RestoreWindow();
                ShowError(
                    $"{ProductInfo.Name} could not exit safely",
                    status.ErrorMessage ?? "The background host did not confirm its state.");
                return;
            }
            var activeMounts = status.Mounts?.Count(mount =>
                mount.Lifecycle is not "Stopped" and not "Failed") ?? 0;
            var activeSyncs = status.SyncJobs?.Count(job => job.Lifecycle == "Running") ?? 0;
            var confirmed = activeMounts == 0 && activeSyncs == 0;
            if (!confirmed)
            {
                RestoreWindow();
                var work = string.Join(
                    " and ",
                    new[]
                    {
                        activeMounts == 0 ? null : $"{activeMounts} mounted drive{(activeMounts == 1 ? string.Empty : "s")}",
                        activeSyncs == 0 ? null : $"{activeSyncs} running sync job{(activeSyncs == 1 ? string.Empty : "s")}"
                    }.Where(value => value is not null));
                confirmed = WpfMessageBox.Show(
                    this,
                    $"Exiting will stop {work}. Close open documents first. Upload status may be unavailable for some drives. Continue?",
                    $"Exit {ProductInfo.Name}?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes;
            }

            if (!confirmed)
            {
                _exitRequested = false;
                _timer.Start();
                return;
            }

            var shutdown = await HostClient.SendAsync(
                new HostRequest("shutdown", Confirmed: true),
                TimeSpan.FromSeconds(15));
            if (!shutdown.Succeeded &&
                (shutdown.ErrorCode is not "host.unavailable" || IsInstalledHostProcessRunning()))
            {
                _exitRequested = false;
                _timer.Start();
                ShowError($"{ProductInfo.Name} could not exit", shutdown.ErrorMessage ?? "The background host rejected the request.");
                return;
            }
        }
        catch (Exception exception)
        {
            _exitRequested = false;
            _timer.Start();
            ShowError(
                $"{ProductInfo.Name} could not exit",
                $"The background host could not be checked safely. {ProductInfo.Name} is still running.\n\n{exception.Message}");
        }
        finally
        {
            _exitChecking = false;
            if (_exitRequested)
            {
                Close();
            }
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.Application.MinimizeToTray)
        {
            HideToTray();
            return;
        }

        if (WindowState is WindowState.Normal or WindowState.Maximized)
        {
            _windowStateBeforeMinimize = WindowState;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitChecking)
        {
            e.Cancel = true;
            return;
        }
        if (!_exitRequested && _settings.Application.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (!_exitRequested)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(ExitApplication);
            return;
        }

        IsClosing = true;
        IsStartupReady = false;
    }

    private void Application_SessionEnding(object? sender, SessionEndingCancelEventArgs e) =>
        _exitRequested = true;

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreWindow()
    {
        var restoredState = _windowStateBeforeMinimize == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        WindowRestoration.PrepareForShow(this);
        Show();
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        WindowAppearance.Restore(handle);
        WindowState = restoredState;
        Activate();
        WindowAppearance.BringToForeground(handle);
        Focus();
    }

    private static bool IsInstalledHostProcessRunning()
    {
        var currentId = Environment.ProcessId;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            return true;

        try
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
            try
            {
                foreach (var process in processes)
                {
                    if (process.Id != currentId &&
                        process.MainModule?.FileName is { } candidate &&
                        string.Equals(
                            Path.GetFullPath(candidate),
                            Path.GetFullPath(executable),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or
            NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public bool RestoreFromExternalRequest()
    {
        RestoreWindow();
        return IsVisible && WindowState != WindowState.Minimized;
    }

    private static string CurrentExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "resodrive.exe");

    private static string MsiInstalledExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "rdrive",
        "resodrive.exe");

    private void ShowError(string title, string message) =>
        WpfMessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
