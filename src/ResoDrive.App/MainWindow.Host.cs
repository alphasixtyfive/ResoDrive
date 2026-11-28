using System.Diagnostics;
using System.IO;
using System.Windows;
using ResoDrive.Windows;

namespace ResoDrive.App;

public partial class MainWindow
{
    private Task<HostResponse> EnsureHostAsync()
    {
        return _hostConnection.EnsureAsync(
            (timeout, token) => HostClient.SendAsync(new HostRequest("status"), timeout, token),
            LaunchHost);
    }

    private static HostResponse? LaunchHost()
    {
        var hostPath = CurrentExecutablePath;
        if (!File.Exists(hostPath))
            return new HostResponse(
                false,
                "host.not_found",
                $"{ProductInfo.Name} was not found at '{hostPath}'."
            );
        using var hostProcess = Process.Start(new ProcessStartInfo(hostPath, "--host")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        }) ?? throw new InvalidOperationException("The background host could not be started.");
        return null;
    }

    private bool CanInteractWithHost => !IsClosing && !_settingsClosing &&
        !_exitRequested && !_exitChecking && !_hostConnection.IsSuspended;

    private async Task RefreshStatusAsync()
    {
        if (CloseForRemoteWipe()) return;
        if (_refreshing || !CanInteractWithHost)
            return;
        _refreshing = true;
        try
        {
            var response = await HostClient.SendAsync(new HostRequest("status"), _lifetimeCancellation.Token);
            if (!CanInteractWithHost) return;
            if (HostStatusPresentation.HasUsableMountStatus(response))
            {
                if (_hostUnavailableReported)
                {
                    _model.AddLogEntry("Background host connected", "Status updates resumed", LogSeverity.Success);
                    _hostUnavailableReported = false;
                }
                ShowHostConnected();
                ApplyTransferStatus(response);
                UpdateTrayStatus();
            }
            else
            {
                _model.ApplyHostUnavailable();
                MarkTransfersUnavailable();
                UpdateTrayStatus();
                var detail = response.InitializationErrorMessage ?? response.ErrorMessage;
                if (!_hostUnavailableReported)
                {
                    _model.AddLogEntry(
                        "Status delayed",
                        detail ?? "Host unavailable",
                        LogSeverity.Error
                    );
                    _hostUnavailableReported = true;
                }
                ShowHostInterrupted(detail, recoveryExhausted: false);
                if (response.InitializationErrorCode is not null ||
                    response.ErrorCode?.Equals("host.unavailable", StringComparison.OrdinalIgnoreCase) == true)
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
        if (_hostRecoveryBusy || !CanInteractWithHost)
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
        var recoveryToken = _hostConnection.Token;
        try
        {
            ShowHostInterrupted(null, recoveryExhausted: false);
            if (attempt > 1)
                await Task.Delay(TimeSpan.FromSeconds(attempt - 1), recoveryToken);
            var response = await EnsureHostAsync();
            if (!CanInteractWithHost) return;
            if (response.InitializationErrorCode is not null)
            {
                // The pipe is alive, but its first settings load failed. Retry that
                // load instead of launching a second host or sending a mount request.
                var reload = await HostClient.SendAsync(
                    new HostRequest("reload"), recoveryToken);
                response = reload.Succeeded
                    ? await HostClient.SendAsync(
                        new HostRequest("status"), recoveryToken)
                    : reload;
            }
            if (!CanInteractWithHost) return;
            if (HostStatusPresentation.HasUsableMountStatus(response))
            {
                ApplyTransferStatus(response);
                UpdateTrayStatus();
                if (_hostUnavailableReported)
                    _model.AddLogEntry("Background host connected", "Status updates resumed", LogSeverity.Success);
                _hostUnavailableReported = false;
                ShowHostConnected();
                return;
            }

            ShowHostInterrupted(
                response.InitializationErrorMessage ?? response.ErrorMessage,
                recoveryExhausted: attempt >= MaximumAutomaticHostRecoveryAttempts);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowHostInterrupted(
                exception.Message,
                recoveryExhausted: attempt >= MaximumAutomaticHostRecoveryAttempts);
            if (attempt >= MaximumAutomaticHostRecoveryAttempts)
                _model.AddLogEntry("Host recovery paused", exception.Message, LogSeverity.Error);
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
}
