using System.Windows;
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

    private void ShowUploads_Click(object sender, RoutedEventArgs e) => ShowUploads();

    private void ShowUploads(bool fromTray = false)
    {
        if (IsClosing || _exitRequested || _exitChecking) return;
        RefreshUploadsPresentation();
        if (_uploadsWindow is null)
        {
            _uploadsWindow = new UploadsWindow(_uploadsModel);
            _uploadsWindow.Closed += (_, _) => _uploadsWindow = null;
        }
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).Bounds;
        var anchor = fromTray ? System.Windows.Forms.Cursor.Position
            : new System.Drawing.Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
        _uploadsWindow.ShowFlyout(anchor);
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
