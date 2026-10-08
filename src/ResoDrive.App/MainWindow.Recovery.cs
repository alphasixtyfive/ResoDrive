using System.IO;
using System.Windows;
using ResoDrive.Core.Domain;
using ResoDrive.Windows;
using WpfMessageBox = ResoDrive.App.ModernMessageBox;

namespace ResoDrive.App;

public partial class MainWindow
{
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
                LoadHostStatus(rollbackStatus);
                UpdateTrayStatus();
                ShowError(
                    "Settings were not activated",
                    rollback.Succeeded
                        ? $"{reload.ErrorMessage ?? "The background host rejected the imported settings."}\n\nThe previous settings were restored. {SettingsRollback.ActivationMessage(rollbackReload)}".TrimEnd()
                        : $"{reload.ErrorMessage ?? "The background host rejected the imported settings."}\n\nThe previous settings could not be restored automatically.");
                return;
            }

            LoadSettingsControls();
            await ReconcileAutostartAsync();
            LoadHostStatus(reload);
            UpdateTrayStatus();
            UiDiagnosticLog.Current.Information("recovery.settings_imported");
        }
        finally
        {
            _settingsMutationGate.Release();
        }
    }

    private static bool HasActiveHostWork(HostResponse response) =>
        response.MountStatusTruncated || response.SyncStatusTruncated || response.ActiveSyncJobs > 0 ||
        response.Mounts?.Any(mount =>
            !Enum.TryParse(mount.Lifecycle, true, out MountLifecycle lifecycle) ||
            lifecycle is not MountLifecycle.Stopped and not MountLifecycle.Failed) == true ||
        response.SyncJobs?.Any(job =>
            !Enum.TryParse(job.Lifecycle, true, out SyncLifecycle lifecycle) ||
            lifecycle is SyncLifecycle.Queued or SyncLifecycle.Running) == true;

}
