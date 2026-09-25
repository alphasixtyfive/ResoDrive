using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ResoDrive.Windows;
using WpfMessageBox = ResoDrive.App.ModernMessageBox;

namespace ResoDrive.App;

#pragma warning disable CA1001 // WPF owns this window; the Closed handler cleans up its resources.
public partial class MainWindow
#pragma warning restore CA1001
{
    private void UpdateConnectionStatus()
    {
        if (!IsInitialized || _rcloneMutationBusy)
            return;
        StatusVisuals.ApplyPending(RcloneStatusIcon);
        SetLiveText(RcloneStatusText, "Checking…");
        RcloneHostIdentityStatusText.Visibility = Visibility.Collapsed;
        _rcloneRuntimeReady = false;
        AddMountButton.IsEnabled = false;
        var generation = ++_componentStatusGeneration;
        _ = ObserveComponentStatusAsync(UpdateLocalRcloneStatusAsync(generation), generation, rclone: true);
        SetLiveText(WinFspStatusText, "Checking WinFsp…");
        StatusVisuals.ApplyPending(WinFspStatusIcon);
        _ = ObserveComponentStatusAsync(UpdateWinFspStatusAsync(generation), generation, rclone: false);
    }

    private async Task ObserveComponentStatusAsync(Task inspection, int generation, bool rclone)
    {
        try
        {
            await inspection;
        }
        catch (Exception exception)
        {
            if (!IsInitialized || generation != _componentStatusGeneration)
                return;

            if (rclone)
            {
                StatusVisuals.Apply(RcloneStatusIcon, success: false, error: true);
                SetLiveText(RcloneStatusText, "Could not inspect");
                RcloneHostIdentityStatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusVisuals.Apply(WinFspStatusIcon, success: false, error: true);
                SetLiveText(WinFspStatusText, "WinFsp could not be inspected");
            }
            _model.AddLogEntry("\uE783", "Component check failed", exception.Message, true);
        }
    }

    private async Task UpdateWinFspStatusAsync(int? generation = null)
    {
        var result = await WinFspPrerequisiteService.InspectAsync();
        if (!IsInitialized || generation is int value && value != _componentStatusGeneration)
        {
            return;
        }

        if (!result.Succeeded || result.Value?.IsInstalled != true)
        {
            StatusVisuals.Apply(WinFspStatusIcon, success: false);
            SetLiveText(WinFspStatusText, "WinFsp is not detected; sync works, but mount drives need it");
            WinFspReleasesButton.Visibility = Visibility.Visible;
            return;
        }

        StatusVisuals.Apply(WinFspStatusIcon, success: true);
        WinFspReleasesButton.Visibility = Visibility.Hidden;
        SetLiveText(WinFspStatusText, string.IsNullOrWhiteSpace(result.Value.Version)
            ? "WinFsp is installed"
            : $"WinFsp {result.Value.Version} is installed");
    }

    private async Task UpdateLocalRcloneStatusAsync(int? generation = null)
    {
        var result = await new RcloneRuntimeLocator(_paths).InspectAsync();
        if (!IsInitialized || generation is int value && value != _componentStatusGeneration)
        {
            return;
        }
        // A generated inspection is background component UI work. Runtime mutation owns
        // the component action and status controls until its commit or cancellation ends.
        if (generation.HasValue && _rcloneMutationBusy)
        {
            return;
        }

        if (!result.Succeeded || result.Value?.Version is null)
        {
            StatusVisuals.Apply(RcloneStatusIcon, success: false, error: true);
            _rcloneInstalledVersion = null;
            RcloneHostIdentityStatusText.Visibility = Visibility.Collapsed;
            _rcloneRepairRequested = result.Error?.Code == "rclone.invalid";
            _rcloneRuntimeReady = false;
            var missing = result.Error?.Code == "rclone.not_installed";
            _rcloneUpdate = missing || _rcloneRepairRequested
                ? new RcloneUpdateCheck(string.Empty, RcloneBootstrapService.ReleaseVersion, true)
                : null;
            _rcloneUpdateCheckFailed = !missing && !_rcloneRepairRequested;
            SetLiveText(RcloneStatusText, _rcloneRepairRequested
                ? "The managed runtime is invalid and can be repaired"
                : missing
                    ? $"Download {RcloneBootstrapService.ReleaseVersion}"
                    : $"{result.Error?.Message ?? "rclone is unavailable"} · Retry the component check");
            UpdateRcloneButtonText.Text = _rcloneRepairRequested ? "Repair" : missing ? "Download" : "Update";
            RefreshRcloneUpdateAction();
            AddMountButton.IsEnabled = false;
            return;
        }

        StatusVisuals.Apply(RcloneStatusIcon, success: true);
        _rcloneRepairRequested = false;
        _rcloneRuntimeReady = true;
        _rcloneInstalledVersion = result.Value.Version;
        RcloneHostIdentityStatusText.Visibility = Visibility.Visible;
        AddMountButton.IsEnabled = !_rcloneMutationBusy;
        UpdateRcloneButtonText.Text = "Update";
        SetRcloneStatusDetail(_rcloneUpdate is null
            ? "Checking for updates…"
            : _rcloneUpdate.UpdateAvailable
                ? $"{_rcloneUpdate.AvailableVersion} available"
                : "Up to date");
    }

    private async Task CheckRcloneUpdateAsync()
    {
        if (_rcloneUpdateBusy)
        {
            return;
        }

        SetRcloneUpdateBusy(true);
        SetRcloneStatusDetail("Checking for a stable update…");
        try
        {
            var result = await new RcloneUpdateService(new RcloneRuntimeLocator(_paths))
                .CheckAsync(_lifetimeCancellation.Token);
            if (!result.Succeeded || result.Value is null)
            {
                if (!_rcloneRepairRequested)
                    _rcloneUpdate = null;
                _rcloneUpdateCheckFailed = true;
                SetRcloneStatusDetail(result.Error?.Message ?? "The update check failed.");
                return;
            }

            _rcloneUpdate = result.Value;
            _rcloneUpdateCheckFailed = false;
            _rcloneRepairRequested = false;
            _rcloneRuntimeReady = !string.IsNullOrEmpty(result.Value.CurrentVersion);
            var missing = string.IsNullOrEmpty(result.Value.CurrentVersion);
            _rcloneInstalledVersion = missing ? null : result.Value.CurrentVersion;
            RcloneHostIdentityStatusText.Visibility = missing ? Visibility.Collapsed : Visibility.Visible;
            UpdateRcloneButtonText.Text = missing ? "Download" : "Update";
            SetRcloneStatusDetail(missing
                ? $"Download {result.Value.AvailableVersion}"
                : result.Value.UpdateAvailable
                    ? $"{result.Value.AvailableVersion} available"
                    : "Up to date");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _rcloneUpdate = null;
            _rcloneUpdateCheckFailed = true;
            SetRcloneStatusDetail(exception.Message);
        }
        finally
        {
            SetRcloneUpdateBusy(false);
        }
    }

    private async Task CheckApplicationUpdateAsync()
    {
        if (_applicationUpdateBusy)
            return;

        SetApplicationUpdateBusy(true);
        StatusVisuals.ApplyPending(ApplicationUpdateStatusIcon);
        SetLiveText(ApplicationUpdateStatusText, "Checking for a stable release…");
        try
        {
            var result = await new ApplicationUpdateService()
                .CheckAsync(ProductInfo.Version, _lifetimeCancellation.Token);
            if (!result.Succeeded || result.Value is null)
            {
                _applicationUpdate = null;
                _applicationUpdateCheckFailed = true;
                StatusVisuals.Apply(ApplicationUpdateStatusIcon, success: false, error: true);
                SetLiveText(ApplicationUpdateStatusText, result.Error?.Message ?? "The update check failed.");
                return;
            }

            _applicationUpdate = result.Value;
            _applicationUpdateCheckFailed = false;
            StatusVisuals.Apply(
                ApplicationUpdateStatusIcon,
                success: !result.Value.UpdateAvailable);
            SetLiveText(ApplicationUpdateStatusText, result.Value.UpdateAvailable
                ? $"Version {result.Value.AvailableVersion} is available"
                : $"Installed {result.Value.CurrentVersion} · Up to date");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _applicationUpdate = null;
            _applicationUpdateCheckFailed = true;
            StatusVisuals.Apply(ApplicationUpdateStatusIcon, success: false, error: true);
            SetLiveText(ApplicationUpdateStatusText, exception.Message);
        }
        finally
        {
            SetApplicationUpdateBusy(false);
        }
    }

    private async void ApplicationUpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_applicationDownloadCancellation is { } download)
        {
            ApplicationUpdateActionButton.IsEnabled = false;
            SetLiveText(ApplicationUpdateStatusText, "Cancelling download…");
            download.Cancel();
            return;
        }
        await ExecuteUiActionAsync(
            "ResoDrive update failed",
            ApplicationUpdateActionAsync);
    }

    private async Task ApplicationUpdateActionAsync()
    {
        if (_applicationUpdateBusy)
            return;

        // Refresh the latest-release redirect before every update action so a
        // failed handoff cannot leave an older available version stuck in the UI.
        await CheckApplicationUpdateAsync();
        if (_applicationUpdate is not { UpdateAvailable: true })
            return;

        await InstallApplicationUpdateAsync();
    }

    private async void RcloneUpdateAction_Click(object sender, RoutedEventArgs e)
    {
        // Cancellation must bypass the duplicate-action guard protecting the download.
        if (_rcloneMutationBusy)
        {
            CancelRcloneOperation();
            return;
        }
        await ExecuteUiActionAsync("rclone operation failed", RcloneUpdateActionAsync);
    }

    private void CancelRcloneOperation()
    {
        UpdateRcloneButton.IsEnabled = false;
        SetRcloneStatusDetail("Cancelling…");
        _rcloneOperationCancellation?.Cancel();
    }

    private async Task RcloneUpdateActionAsync()
    {
        if (_rcloneMutationBusy)
        {
            await UpdateRcloneAsync();
            return;
        }
        if (_rcloneUpdateBusy)
            return;
        if (_rcloneUpdate is not { UpdateAvailable: true })
        {
            await CheckRcloneUpdateAsync();
            return;
        }

        await UpdateRcloneAsync();
    }

    private async Task InstallApplicationUpdateAsync()
    {
        if (_applicationUpdateBusy || _applicationUpdate is not { UpdateAvailable: true } update)
            return;

        var activeMounts = _model.Mounts.Count(mount => mount.ShouldStop || mount.IsTransient);
        var activeSyncs = _model.Jobs.Count(job => job.IsBusy);
        var activeItems = new[]
        {
            activeMounts == 0 ? null : $"{activeMounts} mounted drive{(activeMounts == 1 ? string.Empty : "s")}",
            activeSyncs == 0 ? null : $"{activeSyncs} running sync job{(activeSyncs == 1 ? string.Empty : "s")}",
        }.Where(value => value is not null);
        var activeWork = activeMounts + activeSyncs == 0
            ? string.Empty
            : $" Installing it will stop {string.Join(" and ", activeItems)}.";
        if (!WpfMessageBox.Confirm(
                this,
                $"Download and install ResoDrive {update.AvailableVersion}?{activeWork} Close open documents first. Upload status may be unavailable for some drives. ResoDrive will close and Windows will ask for permission.",
                "Install ResoDrive update?",
                "Install update"))
        {
            return;
        }

        _applicationDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        ApplicationDownloadProgress.Value = 0;
        ApplicationDownloadProgress.IsIndeterminate = true;
        ApplicationDownloadProgress.Visibility = Visibility.Visible;
        SetApplicationUpdateBusy(true);
        StatusVisuals.ApplyPending(ApplicationUpdateStatusIcon);
        SetLiveText(ApplicationUpdateStatusText, $"Downloading version {update.AvailableVersion}…");
        try
        {
            var progress = new Progress<ApplicationUpdateDownloadProgress>(value =>
            {
                if (_applicationDownloadCancellation is null || _applicationDownloadCancellation.IsCancellationRequested)
                    return;
                ApplicationDownloadProgress.IsIndeterminate = value.TotalBytes is not > 0;
                if (value.TotalBytes is > 0)
                    ApplicationDownloadProgress.Value = Math.Clamp(value.BytesReceived * 100d / value.TotalBytes.Value, 0, 100);
                SetProgressText(ApplicationUpdateStatusText, value.TotalBytes is > 0
                    ? $"Downloading version {update.AvailableVersion} · {value.BytesReceived * 100d / value.TotalBytes.Value:0}%"
                    : $"Downloading version {update.AvailableVersion} · {value.BytesReceived / 1024d / 1024d:0.0} MB");
            });
            var downloaded = await new ApplicationUpdateService().DownloadInstallerAsync(
                update,
                _paths.Updates,
                progress,
                _applicationDownloadCancellation.Token);
            if (!downloaded.Succeeded || downloaded.Value is null)
            {
                StatusVisuals.Apply(ApplicationUpdateStatusIcon, success: false, error: true);
                SetLiveText(
                    ApplicationUpdateStatusText,
                    downloaded.Error?.Message ?? "The update could not be downloaded.");
                return;
            }

            _applicationDownloadCancellation.Token.ThrowIfCancellationRequested();
            _applicationDownloadCancellation.Dispose();
            _applicationDownloadCancellation = null;
            RefreshApplicationUpdateAction();
            ApplicationDownloadProgress.IsIndeterminate = true;
            SetLiveText(ApplicationUpdateStatusText, "Checking pending uploads…");
            var uploads = await HostClient.SendAsync(new HostRequest("check-uploads"), _lifetimeCancellation.Token);
            if (!uploads.Succeeded && uploads.ErrorCode != "host.unavailable")
            {
                SetLiveText(ApplicationUpdateStatusText, uploads.ErrorMessage ?? "Could not check uploads. Try again.");
                return;
            }
            SetLiveText(ApplicationUpdateStatusText, "Stopping mounted drives and sync jobs…");
            var shutdown = await HostClient.SendAsync(
                new HostRequest("shutdown", Confirmed: true),
                TimeSpan.FromSeconds(15),
                _lifetimeCancellation.Token);
            if (!shutdown.Succeeded && shutdown.ErrorCode != "host.unavailable")
            {
                SetLiveText(
                    ApplicationUpdateStatusText,
                    shutdown.ErrorMessage ?? "ResoDrive could not safely stop its background work. Close open documents and try again.");
                return;
            }
            SetLiveText(ApplicationUpdateStatusText, "Preparing Windows Installer…");
            ApplicationUpdateHandoff.Start(
                downloaded.Value.Version,
                downloaded.Value.InstallerPath,
                _paths.Updates,
                CurrentExecutablePath,
                MsiInstalledExecutablePath,
                downloaded.Value.Sha256);

            _exitRequested = true;
            _timer.Stop();
            Close();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (_applicationDownloadCancellation?.IsCancellationRequested == true)
        {
            SetLiveText(ApplicationUpdateStatusText, "Download cancelled. Choose Update to resume.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            StatusVisuals.Apply(ApplicationUpdateStatusIcon, success: false, error: true);
            SetLiveText(ApplicationUpdateStatusText, "The update installer was not started.");
            ShowError("Could not install the ResoDrive update", exception.Message);
        }
        finally
        {
            _applicationDownloadCancellation?.Dispose();
            _applicationDownloadCancellation = null;
            ApplicationDownloadProgress.Visibility = Visibility.Collapsed;
            if (!_exitRequested)
                SetApplicationUpdateBusy(false);
        }
    }

    private void SetApplicationUpdateBusy(bool busy)
    {
        _applicationUpdateBusy = busy;
        RefreshApplicationUpdateAction();
    }

    private void RefreshApplicationUpdateAction()
    {
        var retry = _applicationUpdateCheckFailed && _applicationUpdate is null;
        var update = _applicationUpdate?.UpdateAvailable == true;
        ConfigureComponentAction(
            ApplicationUpdateActionButton,
            ApplicationUpdateActionGlyph,
            ApplicationUpdateActionText,
            _applicationUpdateBusy,
            _applicationDownloadCancellation is not null ? "Cancel" : retry ? "Retry" : update ? "Update" : null,
            update && _applicationDownloadCancellation is null,
            "ResoDrive",
            _applicationDownloadCancellation is not null);
    }

    private async Task UpdateRcloneAsync()
    {
        if (_rcloneMutationBusy)
        {
            CancelRcloneOperation();
            return;
        }

        if (_rcloneUpdateBusy || _rcloneUpdate?.UpdateAvailable != true)
        {
            return;
        }

        if (_model.Mounts.Any(mount => mount.ShouldStop || mount.IsTransient) ||
            _model.Jobs.Any(job => job.IsBusy))
        {
            ShowError(
                "Stop active work first",
                $"Stop all mounted drives and running sync jobs before changing the {ProductInfo.Name} rclone runtime.");
            return;
        }

        var installing = string.IsNullOrEmpty(_rcloneUpdate.CurrentVersion);
        var repairing = _rcloneRepairRequested;
        _rcloneOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var operationToken = _rcloneOperationCancellation.Token;
        var progress = new Progress<RcloneBootstrapProgress>(UpdateRcloneProgress);
        SetRcloneUpdateBusy(
            true,
            $"{(repairing ? "Repairing" : installing ? "Downloading" : "Updating to")} {_rcloneUpdate.AvailableVersion}…",
            mutation: true);
        UpdateRcloneButtonText.Text = "Cancel";
        UpdateRcloneButton.IsEnabled = true;
        try
        {
            var locator = new RcloneRuntimeLocator(_paths);
            var result = _rcloneRepairRequested
                ? await RepairRcloneAsync(locator, progress, operationToken)
                : await new RcloneUpdateService(locator).UpdateAsync(progress, operationToken);
            if (!result.Succeeded || result.Value is null)
            {
                SetRcloneStatusDetail(result.Error?.Message ?? "rclone could not be updated.");
                return;
            }

            _rcloneUpdate = null;
            _rcloneRepairRequested = false;
            var status = result.Value.Updated
                ? repairing
                    ? "Repair complete"
                    : installing ? "Install complete" : "Update complete"
                : "Up to date";
            _rcloneInstalledVersion = result.Value.CurrentVersion;
            await RefreshStatusAsync();
            await UpdateLocalRcloneStatusAsync();
            SetRcloneStatusDetail(status);
            if (result.Value.Updated)
            {
                var reload = await HostClient.SendAsync(
                    new HostRequest("activate-runtime"),
                    _lifetimeCancellation.Token);
                if (!reload.Succeeded)
                {
                    SetRcloneStatusDetail(
                        $"{status}. {reload.ErrorMessage ?? $"Restart {ProductInfo.Name} to activate it."}");
                }
                await RefreshStatusAsync();
            }
            if ((installing || repairing) && !File.Exists(_paths.ConfigFile))
            {
                await RunSetupAsync(firstRun: true, hostAlreadyRunning: true);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            SetRcloneStatusDetail("Component operation cancelled");
        }
        catch (Exception exception)
        {
            SetRcloneStatusDetail(exception.Message);
        }
        finally
        {
            var cancelled = operationToken.IsCancellationRequested &&
                !_lifetimeCancellation.IsCancellationRequested;
            _rcloneOperationCancellation.Dispose();
            _rcloneOperationCancellation = null;
            SetRcloneUpdateBusy(false);
            UpdateRcloneButtonText.Text = repairing ? "Repair" : installing ? "Download" : "Update";
            if (cancelled)
                SetRcloneStatusDetail("Component operation cancelled");
        }
    }

    private void SetRcloneUpdateBusy(bool busy, string? status = null, bool mutation = false)
    {
        if (busy && mutation && !_rcloneMutationBusy)
        {
            // Ignore any component inspection that began before the mutation acquired UI
            // ownership; it may have observed the intentionally absent/staged executable.
            _componentStatusGeneration++;
        }
        _rcloneUpdateBusy = busy;
        _rcloneMutationBusy = busy && mutation;
        RefreshRcloneUpdateAction();
        AddMountButton.IsEnabled = !_rcloneMutationBusy && _rcloneRuntimeReady;
        RcloneDownloadProgress.Visibility = _rcloneMutationBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_rcloneMutationBusy)
            RcloneDownloadProgress.Value = 0;
        if (status is not null)
        {
            SetRcloneStatusDetail(status);
        }
    }

    private void RefreshRcloneUpdateAction()
    {
        var retry = _rcloneUpdateCheckFailed && _rcloneUpdate is null;
        var update = _rcloneUpdate?.UpdateAvailable == true;
        var cancelling = _rcloneMutationBusy;
        var action = cancelling
            ? "Cancel"
            : retry
                ? "Retry"
                : update
                    ? _rcloneRepairRequested
                        ? "Repair"
                        : string.IsNullOrEmpty(_rcloneUpdate?.CurrentVersion) ? "Download" : "Update"
                    : null;
        ConfigureComponentAction(
            UpdateRcloneButton,
            UpdateRcloneButtonGlyph,
            UpdateRcloneButtonText,
            _rcloneUpdateBusy,
            action,
            update && !cancelling,
            "rclone",
            cancelling);
    }

    private void ConfigureComponentAction(
        System.Windows.Controls.Button button,
        TextBlock glyph,
        TextBlock text,
        bool busy,
        string? action,
        bool accent,
        string component,
        bool allowWhileBusy = false)
    {
        var compact = action is null;
        var accessibleName = compact
            ? busy ? $"Checking {component} for updates" : $"Check {component} for updates"
            : action == "Cancel" ? $"Cancel {component} operation" : $"{action} {component}";

        button.Visibility = Visibility.Visible;
        button.IsEnabled = allowWhileBusy || !busy;
        button.Width = compact ? 34 : double.NaN;
        button.MinWidth = compact ? 34 : 96;
        button.Style = (Style)FindResource(compact
            ? "IconOnlyButton"
            : accent ? "AccentButton" : "ButtonStyle");
        button.ToolTip = compact
            ? busy ? "Checking for updates" : "Check for updates"
            : accessibleName;
        AutomationProperties.SetName(button, accessibleName);

        glyph.Text = action == "Cancel" ? "\uE711" : compact || action == "Retry" ? "\uE72C" : "\uE896";
        glyph.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 7, 0);
        text.Text = action ?? string.Empty;
        text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateRcloneProgress(RcloneBootstrapProgress progress)
    {
        if (!_rcloneMutationBusy || _rcloneOperationCancellation?.IsCancellationRequested == true)
            return;

        if (progress.Percentage is { } percentage)
        {
            RcloneDownloadProgress.Value = percentage;
            SetRcloneStatusDetail($"{progress.Message} · {percentage:0}%", announce: false);
            return;
        }

        if (progress.BytesReceived > 0)
        {
            SetRcloneStatusDetail(
                $"{progress.Message} · {progress.BytesReceived / 1024d / 1024d:0.0} MB",
                announce: false);
            return;
        }

        if (!progress.Message.Equals("Downloading rclone", StringComparison.Ordinal))
            RcloneDownloadProgress.Value = 100;
        SetRcloneStatusDetail(progress.Message + "…");
    }

    private void SetRcloneStatusDetail(string detail, bool announce = true)
    {
        var text = string.IsNullOrWhiteSpace(_rcloneInstalledVersion)
            ? detail
            : $"{_rcloneInstalledVersion} · {detail}";
        if (announce)
            SetLiveText(RcloneStatusText, text);
        else
            SetProgressText(RcloneStatusText, text);
    }

    private static async Task<ResoDrive.Core.Results.OperationResult<RcloneUpdateResult>> RepairRcloneAsync(
        RcloneRuntimeLocator locator,
        IProgress<RcloneBootstrapProgress> progress,
        CancellationToken cancellationToken)
    {
        var installed = await new RcloneBootstrapService(locator).InstallAsync(
            progress,
            cancellationToken: cancellationToken);
        if (!installed.Succeeded || installed.Value?.Version is null)
        {
            var error = installed.Error!;
            return ResoDrive.Core.Results.Result.Failure<RcloneUpdateResult>(
                error.Code,
                error.Message,
                error.IsTransient);
        }
        return ResoDrive.Core.Results.Result.Success(new RcloneUpdateResult(
            string.Empty,
            installed.Value.Version,
            true));
    }

}
