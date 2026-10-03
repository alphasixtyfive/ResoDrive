using ResoDrive.Windows;

namespace ResoDrive.App;

public partial class MainWindow
{
    private static bool IsUploadGuard(HostResponse response) => response.ErrorCode is
        "mount.uploads_pending" or "mount.upload_recovery_pending" or "mount.upload_status_unknown";

    private async Task<HostResponse?> SendInteractiveMountActionAsync(MountRow row, string command)
    {
        var response = await HostClient.SendAsync(new HostRequest(command, row.Id), _lifetimeCancellation.Token);
        if (IsClosing || _settingsClosing || _lifetimeCancellation.IsCancellationRequested) return null;
        if (command != "stop" || response.Succeeded || !IsUploadGuard(response)) return response;
        RestoreWindow();
        if (!ModernMessageBox.Confirm(this,
                "Some files have not reached the server, or upload status could not be checked. Disconnecting pauses uploads. Files already cached on this PC will be kept for the next connection. Close open documents first.",
                $"Disconnect {row.Name}?", "Disconnect anyway", "Keep connected", affirmativeIsDefault: false))
            return null;
        return await HostClient.SendAsync(new HostRequest("stop", row.Id, Confirmed: true, AllowPendingUploads: true), _lifetimeCancellation.Token);
    }

    private async void ExitApplication()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _exitChecking = true;
        _hostConnection.Suspend();
        _timer.Stop();
        try
        {
            var status = await HostClient.SendAsync(new HostRequest("status"), TimeSpan.FromSeconds(2));
            if (!status.Succeeded && status.ErrorCode == "host.unavailable" && !IsInstalledHostProcessRunning())
                return;
            var activeSyncs = Math.Max(status.ActiveSyncJobs, status.SyncJobs?.Count(job => job.Lifecycle is "Running" or "Queued") ?? 0);
            var uploadsPending = !HostStatusPresentation.HasUsableMountStatus(status) || status.MountStatusTruncated ||
                status.Mounts?.Any(mount => mount.UploadRecoveryRequired || mount.UploadStatusStale || mount.UploadStatusChecking ||
                mount.Lifecycle is "Starting" or "Stopping" or "WaitingToRestart" ||
                mount.UploadsQueued > 0 || mount.UploadsInProgress > 0 || mount.UploadsDirty > 0 || mount.UploadErrors > 0) == true;
            if (activeSyncs > 0 && !uploadsPending)
            {
                RestoreWindow();
                if (!ModernMessageBox.Confirm(this,
                        "Exiting will stop active sync jobs. You can run them again after restarting ResoDrive.",
                        $"Exit {ProductInfo.Name}?", "Exit anyway", "Keep running", affirmativeIsDefault: false))
                {
                    _exitRequested = false;
                    return;
                }
            }
            var response = await HostClient.SendAsync(new HostRequest("exit", Confirmed: true), TimeSpan.FromSeconds(15));
            if (!response.Succeeded && IsUploadGuard(response))
            {
                RestoreWindow();
                if (!ModernMessageBox.Confirm(this,
                        "Some files have not reached the server, or upload status could not be checked. Exiting pauses uploads and stops sync jobs. Files already cached on this PC will be kept for the next connection. Close open documents first.",
                        $"Exit {ProductInfo.Name}?", "Exit anyway", "Keep running", affirmativeIsDefault: false))
                {
                    _exitRequested = false;
                    return;
                }
                response = await HostClient.SendAsync(new HostRequest("exit", Confirmed: true, AllowPendingUploads: true), TimeSpan.FromSeconds(15));
            }
            if (!response.Succeeded && (response.ErrorCode != "host.unavailable" || IsInstalledHostProcessRunning()))
            {
                RestoreWindow();
                _exitRequested = ModernMessageBox.Confirm(this,
                    "The background host could not be stopped. Closing this window will leave background work running.\n\n" +
                    (response.ErrorMessage ?? "The host did not respond."),
                    "Close ResoDrive window?", "Close window", "Keep running", affirmativeIsDefault: false);
            }
        }
        catch (Exception exception)
        {
            RestoreWindow();
            _exitRequested = ModernMessageBox.Confirm(this,
                "The background host could not be checked. Closing this window may leave background work running.\n\n" + exception.Message,
                "Close ResoDrive window?", "Close window", "Keep running", affirmativeIsDefault: false);
        }
        finally
        {
            _exitChecking = false;
            if (_exitRequested) Close();
            else
            {
                _hostConnection.Resume();
                _timer.Start();
            }
        }
    }
}
