using ResoDrive.Windows;
using ResoDrive.Core.Settings;

namespace ResoDrive.App;

public partial class MainWindow
{
    private readonly TransfersViewModel _transfersModel = new();
    private readonly List<MountRow> _orphanUploadMounts = [];
    private TransfersWindow? _transfersWindow;
    private bool _mountUploadStatusUnavailable = true;
    private bool _syncStatusAvailable;
    private bool _powerProtectionUnavailable;
    private int _hostActiveSyncJobs;

    private void ShowTransfers()
    {
        if (!CanInteractWithHost || _lifetimeCancellation.IsCancellationRequested) return;
        RefreshTransfersPresentation();
        if (_transfersWindow is null)
        {
            _transfersWindow = new TransfersWindow(_transfersModel,
                () => OpenTransfersDestination(), () => OpenTransfersDestination("Settings"));
            _transfersWindow.Closed += (_, _) => _transfersWindow = null;
        }
        _transfersWindow.ShowFlyout(System.Windows.Forms.Cursor.Position);
    }

    private void OpenTransfersDestination(string? page = null)
    {
        if (!CanInteractWithHost || _lifetimeCancellation.IsCancellationRequested) return;
        if (page is not null) SelectPage(page);
        RestoreWindow();
    }

    private void ApplyTransferStatus(HostResponse response)
    {
        if (!HostStatusPresentation.HasUsableMountStatus(response)) return;
        _model.ApplyStatus(response.Mounts, response.MountStatusTruncated);
        _mountUploadStatusUnavailable = response.MountStatusTruncated;
        _syncStatusAvailable = HostStatusPresentation.HasUsableSyncStatus(response);
        if (_syncStatusAvailable)
        {
            _model.ApplySyncStatus(response.SyncJobs, response.SyncStatusTruncated);
            _hostActiveSyncJobs = Math.Max(0, response.ActiveSyncJobs);
        }
        else _model.ApplySyncStatus(null, statusTruncated: true);
        _powerProtectionUnavailable = response.SessionProtectionError is not null;
        _orphanUploadMounts.Clear();
        foreach (var mount in (response.Mounts ?? []).Where(status => status.UploadRecoveryRequired &&
                     !_model.Mounts.Any(row => row.Id == status.MountId)))
            _orphanUploadMounts.Add(new MountRow(new MountSettings
            {
                Id = mount.MountId, DisplayName = "Drive needing recovery", RemoteName = "unavailable", Enabled = false,
            }, mount));
        RefreshTransfersPresentation();
    }

    private void LoadHostStatus(HostResponse? response)
    {
        var hostReady = response is not null && HostStatusPresentation.HasUsableMountStatus(response);
        var syncReady = response is not null && HostStatusPresentation.HasUsableSyncStatus(response);
        _syncStatusAvailable = false;
        _model.Load(_settings, hostReady ? response!.Mounts : null, syncReady ? response!.SyncJobs : null,
            hostUnavailable: !hostReady,
            mountStatusTruncated: response?.MountStatusTruncated == true,
            syncStatusTruncated: !syncReady || response?.SyncStatusTruncated == true);
        if (hostReady) ApplyTransferStatus(response!);
        else MarkTransfersUnavailable();
    }

    private void MarkTransfersUnavailable()
    {
        _mountUploadStatusUnavailable = true;
        _syncStatusAvailable = false;
        RefreshTransfersPresentation();
    }

    private void RefreshTransfersPresentation()
    {
        if (!_model.IsInitialized) return;
        _transfersModel.Update(UploadMountRows(), _mountUploadStatusUnavailable,
            _model.Jobs, _hostActiveSyncJobs, _powerProtectionUnavailable, _syncStatusAvailable);
    }

    private int ActiveSyncJobCount => Math.Max(_hostActiveSyncJobs,
        _model.Jobs.Count(job => job.IsBusy && (!_syncStatusAvailable || !job.StatusUnavailable)));

    private MountRow[] UploadMountRows() => [.. _model.Mounts, .. _orphanUploadMounts];

    private void CloseTransfersWindow() => _transfersWindow?.Close();
}
