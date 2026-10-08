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
    private const int MaximumAutomaticHostRecoveryAttempts = 3;
    private readonly ApplicationPaths _paths = new();
    private readonly AccountDataGuard _accountData = new(new ApplicationPaths());
    private readonly ShellViewModel _model = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly HostConnectionRecovery _hostConnection;
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
        _hostConnection = new(_lifetimeCancellation.Token);
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
            exception => _model.AddLogEntry("Tray action failed", exception.Message, LogSeverity.Error),
            ShowTransfers);
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
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
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
            outcome.Finalized ? "Application update result" : "Application update interrupted",
            message,
            failed || !outcome.Finalized ? LogSeverity.Error : LogSeverity.Success);
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
            _model.AddLogEntry("Welcome state was not saved", exception.Message, LogSeverity.Error);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        IsClosing = true;
        IsStartupReady = false;
        _timer.Stop();
        _settingsClosing = true;
        _lifetimeCancellation.Cancel();
        _hostConnection.Dispose();
        _componentStatusGeneration++;
        System.Windows.Application.Current.SessionEnding -= Application_SessionEnding;
        CloseTransfersWindow();
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
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _model.ApplyHostUnavailable();
            MarkTransfersUnavailable();
            UpdateTrayStatus();
            if (!_hostUnavailableReported)
            {
                _model.AddLogEntry("Status refresh failed", exception.Message, LogSeverity.Error);
                _hostUnavailableReported = true;
            }
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
        var hostReady = HostStatusPresentation.HasUsableMountStatus(response);
        LoadHostStatus(response);
        UpdateTrayStatus();
        LoadSettingsControls();
        UpdateConnectionStatus();
        if (!hostReady)
        {
            var detail = response.InitializationErrorMessage ?? response.ErrorMessage;
            ShowHostInterrupted(detail, recoveryExhausted: false);
            _model.AddLogEntry(
                "Background host unavailable",
                detail ?? "The host did not respond.",
                LogSeverity.Error
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
        var activeJobs = Math.Max(foreignHost.ActiveSyncJobs, foreignHost.SyncJobs?.Count(status =>
            status.Lifecycle is "Running" or "Queued") ?? 0);
        var hasActiveWork = activeDrives > 0 || activeJobs > 0 || foreignHost.MountStatusTruncated || foreignHost.SyncStatusTruncated;
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
            hasActiveWork,
            _lifetimeCancellation.Token
        );
        if (!CanInteractWithHost) return null;
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
                confirmed: true,
                _lifetimeCancellation.Token
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
            await Task.Delay(500, _lifetimeCancellation.Token);
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
        if (row.NeedsHostRecovery)
        {
            await ExecuteUiActionAsync(
                "Host recovery failed",
                () => TryRecoverHostAsync(userInitiated: true));
            return;
        }
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
        var response = await SendInteractiveMountActionAsync(row, command);
        if (response is null) return;
        if (!response.Succeeded)
            throw new InvalidOperationException(RcloneErrorMessage.Clean(
                response.ErrorMessage,
                "The host rejected the request."));
        ApplyTransferStatus(response);
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
        var response = await HostClient.SendAsync(new HostRequest(command, row.MountId, row.Id), _lifetimeCancellation.Token);
        if (!CanInteractWithHost) return;
        if (!response.Succeeded)
            throw new InvalidOperationException(
                response.ErrorMessage ?? "The host rejected the request.");
        ApplyTransferStatus(response);
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

    private async Task ExecuteUiActionAsync(
        string heading,
        Func<Task> action)
    {
        if (!CanInteractWithHost || !_activeUiActions.Add(heading))
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
            if (!CanInteractWithHost) return;
            _model.AddLogEntry(heading, exception.Message, LogSeverity.Error);
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
        if (row.NeedsHostRecovery)
        {
            await TryRecoverHostAsync(userInitiated: true);
            return TrayActionResult.SilentSuccess();
        }
        var command = row.ShouldStop ? "stop" : "start";
        if (command == "start" && _rcloneMutationBusy)
            return TrayActionResult.Failure("rclone is being updated", "Try again when the component operation finishes.");
        var response = await SendInteractiveMountActionAsync(row, command);
        if (response is null) return TrayActionResult.SilentSuccess();
        if (!response.Succeeded)
        {
            return TrayActionResult.Failure(
                $"{row.Name} failed",
                RcloneErrorMessage.Clean(
                    response.ErrorMessage,
                    "The background host rejected the request."));
        }

        ApplyTransferStatus(response);
        UpdateTrayStatus();
        var updated = _model.Mounts.FirstOrDefault(mount => mount.Id == row.Id);
        return TrayActionResult.Success(
            row.Name,
            updated?.StatusText ?? $"The {command} request was accepted.");
    }

    private async Task<TrayActionResult> RunTraySyncAsync(SyncRow row)
    {
        if (!CanInteractWithHost) return TrayActionResult.SilentSuccess();
        if (!row.IsBusy && _rcloneMutationBusy)
            return TrayActionResult.Failure("rclone is being updated", "Try again when the component operation finishes.");
        if (!row.IsBusy && row.IsMirror)
        {
            RestoreWindow();
            SelectPage("Jobs");
            return TrayActionResult.SilentSuccess();
        }

        var command = row.IsBusy ? "cancel-sync" : "run-sync";
        var response = await HostClient.SendAsync(new HostRequest(command, row.MountId, row.Id), _lifetimeCancellation.Token);
        if (!CanInteractWithHost) return TrayActionResult.SilentSuccess();
        if (!response.Succeeded)
        {
            return TrayActionResult.Failure(
                $"{row.Name} failed",
                response.ErrorMessage ?? "The background host rejected the request.");
        }

        ApplyTransferStatus(response);
        UpdateTrayStatus();
        var updated = _model.Jobs.FirstOrDefault(job => job.Id == row.Id);
        return TrayActionResult.Success(row.Name, updated?.Result ?? "The request was accepted.");
    }

    private async Task<TrayActionResult> RefreshTrayAsync()
    {
        if (!CanInteractWithHost) return TrayActionResult.SilentSuccess();
        var response = await HostClient.SendAsync(new HostRequest("status"), _lifetimeCancellation.Token);
        if (!CanInteractWithHost) return TrayActionResult.SilentSuccess();
        if (!HostStatusPresentation.HasUsableMountStatus(response))
        {
            _model.ApplyHostUnavailable();
            MarkTransfersUnavailable();
            UpdateTrayStatus();
            return TrayActionResult.Failure(
                "Refresh failed",
                response.InitializationErrorMessage ?? response.ErrorMessage ??
                "The background host is unavailable.");
        }

        ApplyTransferStatus(response);
        UpdateTrayStatus();
        return TrayActionResult.SilentSuccess();
    }

    private void UpdateTrayStatus()
    {
        RefreshTransfersPresentation();
        var mounts = UploadMountRows();
        var uploadWarning = _powerProtectionUnavailable ||
            (_mountUploadStatusUnavailable && _model.Mounts.Any(mount => mount.ShouldStop || mount.NeedsHostRecovery)) ||
            mounts.Any(mount => mount.UploadNeedsAttention &&
                ((mount.UploadStatus?.UploadStatusStale == true && !mount.UploadStatus.UploadStatusChecking) ||
                    UploadPresentation.Errors(mount.UploadStatus) > 0 || mount.UploadStatus?.UploadRecoveryRequired == true));
        _tray.UpdateStatus(_model.Mounts.Count(mount => mount.IsMounted),
            ActiveSyncJobCount,
            UploadPresentation.PendingCount(mounts),
            uploadWarning,
            attentionStatusConfirmed: !_mountUploadStatusUnavailable && _syncStatusAvailable &&
                mounts.All(mount => mount.UploadStatus?.UploadStatusStale != true &&
                    mount.UploadStatus?.UploadStatusChecking != true && !UploadPresentation.HasPending(mount.UploadStatus)));
    }

    private void OpenAbout_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

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

    private void Application_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        var pending = UploadMountRows().Any(mount => mount.UploadNeedsAttention) ||
            _model.Jobs.Any(job => job.IsBusy) ||
            (_mountUploadStatusUnavailable && _model.Mounts.Any(mount => mount.ShouldStop || mount.NeedsHostRecovery));
        e.Cancel = pending;
        _exitRequested = !pending;
    }

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

    private static string MsiInstalledExecutablePath =>
        InstalledApplicationLocator.ResolveExecutablePath() ?? InstallationDirectories.Executable;

    private void ShowError(string title, string message)
    {
        if (IsClosing || _settingsClosing || _lifetimeCancellation.IsCancellationRequested) return;
        WpfMessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
