using ResoDrive.Windows;
using ResoDrive.Core.Settings;

namespace ResoDrive.App;

public partial class MainWindow
{
    private readonly UploadsViewModel _uploadsModel = new();
    private readonly List<MountRow> _orphanUploadMounts = [];
    private UploadsWindow? _uploadsWindow;
    private bool _uploadsHostUnavailable = true;
    private bool _powerProtectionUnavailable;
    private int _hostActiveSyncJobs;

    private void ShowUploads()
    {
        if (!CanInteractWithHost || _lifetimeCancellation.IsCancellationRequested) return;
        RefreshUploadsPresentation();
        if (_uploadsWindow is null)
        {
            _uploadsWindow = new UploadsWindow(_uploadsModel,
                () => OpenUploadsDestination(), () => OpenUploadsDestination("Settings"));
            _uploadsWindow.Closed += (_, _) => _uploadsWindow = null;
        }
        _uploadsWindow.ShowFlyout(System.Windows.Forms.Cursor.Position);
    }

    private void OpenUploadsDestination(string? page = null)
    {
        if (!CanInteractWithHost || _lifetimeCancellation.IsCancellationRequested) return;
        if (page is not null) SelectPage(page);
        RestoreWindow();
    }

    private void ApplyUploadStatus(HostResponse response)
    {
        if (!HostStatusPresentation.HasUsableMountStatus(response)) return;
        _uploadsHostUnavailable = response.MountStatusTruncated;
        _hostActiveSyncJobs = response.ActiveSyncJobs;
        _powerProtectionUnavailable = response.SessionProtectionError is not null;
        _orphanUploadMounts.Clear();
        foreach (var mount in (response.Mounts ?? []).Where(status => status.UploadRecoveryRequired &&
                     !_model.Mounts.Any(row => row.Id == status.MountId)))
            _orphanUploadMounts.Add(new MountRow(new MountSettings
            {
                Id = mount.MountId, DisplayName = "Drive needing recovery", RemoteName = "unavailable", Enabled = false,
            }, mount));
        RefreshUploadsPresentation();
    }

    private void MarkUploadsUnavailable()
    {
        _uploadsHostUnavailable = true;
        RefreshUploadsPresentation();
    }

    private void RefreshUploadsPresentation() =>
        _uploadsModel.Update(UploadMountRows(), _uploadsHostUnavailable,
            _hostActiveSyncJobs > 0 || _model.Jobs.Any(job => job.IsBusy), _powerProtectionUnavailable);

    private MountRow[] UploadMountRows() => [.. _model.Mounts, .. _orphanUploadMounts];

    private void CloseUploadWindows() => _uploadsWindow?.Close();
}
