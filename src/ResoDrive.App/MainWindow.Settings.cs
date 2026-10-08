using System.IO;
using System.Windows;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Validation;
using ResoDrive.Windows;
using WpfFrameworkElement = System.Windows.FrameworkElement;
using WpfMessageBox = ResoDrive.App.ModernMessageBox;

namespace ResoDrive.App;

public partial class MainWindow
{
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
        if (InstallationDirectories.SamePath(CurrentExecutablePath, InstallationDirectories.Executable))
        {
            var migration = autostart.MigrateFrom(InstallationDirectories.LegacyExecutable);
            if (!migration.Succeeded)
                _model.AddLogEntry("Windows startup task was not migrated", migration.Error?.Message ?? "The task was preserved.", LogSeverity.Error);
        }
        var current = await autostart.IsEnabledAsync(_lifetimeCancellation.Token);
        if (!current.Succeeded)
        {
            _model.AddLogEntry(
                "Windows startup task could not be checked",
                current.Error?.Message ?? "The startup task could not be read.",
                LogSeverity.Error);
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
                "Windows startup task was not reconciled",
                changed.Error?.Message ?? "The startup task could not be updated.",
                LogSeverity.Error);
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

            var status = await HostClient.SendAsync(new HostRequest("status"), _lifetimeCancellation.Token);
            LoadHostStatus(status);
            UpdateTrayStatus();
        }
        catch (Exception exception)
        {
            _model.AddLogEntry(
                "Connection details unavailable",
                exception.Message,
                LogSeverity.Error);
        }
    }

    private Task<bool> SaveAndReloadAsync(Func<ManagerSettings, ManagerSettings> edit) =>
        SettingsEditTransaction.ApplyAsync(
            EnterSettingsMutationAsync,
            () => _settingsMutationGate.Release(),
            () => _settings,
            edit,
            SaveAndReloadCoreAsync);

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
        if (!CanInteractWithHost) return true;
        var reload = await HostClient.SendAsync(new HostRequest("reload"));
        if (!CanInteractWithHost) return true;
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
            HostResponse? rollbackReload = null;
            if (rollback.Succeeded && rollback.Value is not null)
            {
                _settings = rollback.Value;
                rollbackReload = await HostClient.SendAsync(new HostRequest("reload"));
            }
            var activationWarning = rollback.Succeeded ? SettingsRollback.ActivationMessage(rollbackReload) : string.Empty;
            if (!restartDeclined || !rollback.Succeeded || activationWarning.Length > 0)
            {
                ShowError(
                    "Settings were not activated",
                    rollback.Succeeded
                        ? $"{reload.ErrorMessage ?? "The background host rejected the settings."}\n\nThe previous settings were restored. {activationWarning}".TrimEnd()
                        : $"{reload.ErrorMessage ?? "The background host rejected the settings."}\n\nThe previous settings could not be restored automatically."
                );
            }
        }
        // A failed reload response contains no mount or sync snapshots. Query the host
        // again after rollback so the UI does not briefly present every drive as stopped.
        var status = await HostClient.SendAsync(new HostRequest("status"), _lifetimeCancellation.Token);
        LoadHostStatus(status);
        UpdateTrayStatus();
        LoadSettingsControls();
        return reload.Succeeded;
    }

    private void LoadSettingsControls()
    {
        MinimizeToTrayBox.IsChecked = _settings.Application.MinimizeToTray;
        StartWithWindowsBox.IsChecked = _settings.Application.StartWithWindows;
    }

    private async Task<ResoDrive.Core.Results.OperationResult> SaveApplicationSettingsCoreAsync(ApplicationSettings application)
    {
        var result = await (
            _store ?? throw new InvalidOperationException("Settings are not ready."))
            .SaveAsync(_settings with { Application = application }, _settings.Revision);
        if (!result.Succeeded || result.Value is null)
        {
            return result.Succeeded
                ? ResoDrive.Core.Results.Result.Failure("settings.missing_saved_value", "Windows did not confirm the saved settings.")
                : result;
        }

        _settings = result.Value;
        LoadSettingsControls();
        return result;
    }

    private async Task<string?> RestoreStartupPreferenceAsync(ScheduledTaskAutostartService autostart, bool enabled)
    {
        var warning = await SettingsRollback.RestoreStartupAsync(() => autostart.SetEnabledAsync(enabled));
        if (warning is not null)
            _model.AddLogEntry("Startup setting needs attention", warning, LogSeverity.Error);
        return warning;
    }

    private void ReportRollbackActivation(HostResponse response)
    {
        var warning = SettingsRollback.ActivationMessage(response);
        if (warning.Length == 0) return;
        _model.AddLogEntry("Previous settings need an app restart", warning, LogSeverity.Error);
        ShowError("Previous settings need an app restart", warning);
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
            var save = await SaveApplicationSettingsCoreAsync(application);
            if (!save.Succeeded)
            {
                var rollbackWarning = autostart is not null && previousAutostart.HasValue
                    ? await RestoreStartupPreferenceAsync(autostart, previousAutostart.Value)
                    : null;
                ShowError("Settings were not saved",
                    SettingsRollback.WithRecovery(save.Error?.Message ?? "The settings could not be saved.", rollbackWarning));
            }
        }
        catch (Exception exception)
        {
            var rollbackWarning = autostart is not null && previousAutostart.HasValue
                ? await RestoreStartupPreferenceAsync(autostart, previousAutostart.Value)
                : null;
            ShowError("Settings were not saved", SettingsRollback.WithRecovery(exception.Message, rollbackWarning));
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
        if (!await EnterSettingsMutationAsync()) return;
        MountEditorWindow? editor = null;
        try
        {
            var current = _settings.Mounts.FirstOrDefault(mount => mount.Id == row.Id);
            if (current is null) return;
            editor = new MountEditorWindow(
                current, current.RemoteName, _paths,
                _settings.Mounts.Select(mount => mount.Target.DriveLetter)
                    .Where(letter => letter.HasValue).Select(letter => letter!.Value),
                _settings.Mounts.Where(mount => mount.Id != current.Id).Select(mount => mount.DisplayName))
            {
                Owner = this,
            };
        }
        finally
        {
            _settingsMutationGate.Release();
        }
        if (editor is null) return;
        if (!CanInteractWithHost)
        {
            editor.Close();
            return;
        }
        if (editor.ShowDialog() != true)
            return;
        await SaveAndReloadAsync(current => SettingsEdits.UpdateMount(
            current, row.Id, editor.Value, editor.DeleteRequested));
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
        var registeredMountIds = await LoadRemoteWipeMountIdsAsync();
        if (!CanInteractWithHost) return;
        var editor = new SyncEditorWindow(_paths, _settings.Mounts, registeredMountIds, null, null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Value is null || editor.SelectedMount is null)
            return;
        var id = editor.SelectedMount.Id;
        await SaveAndReloadAsync(current => SettingsEdits.AddSyncJob(current, id, editor.Value));
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
        var registeredMountIds = await LoadRemoteWipeMountIdsAsync();
        if (!CanInteractWithHost) return;
        var editor = new SyncEditorWindow(_paths, _settings.Mounts, registeredMountIds, row.MountId, row.Settings)
        {
            Owner = this,
        };
        if (editor.ShowDialog() != true)
            return;
        await SaveAndReloadAsync(current => SettingsEdits.UpdateSyncJob(
            current, row.MountId, row.Id, editor.Value, editor.DeleteRequested));
    }

    private async Task<IReadOnlySet<Guid>> LoadRemoteWipeMountIdsAsync()
    {
        var registrations = await new RemoteWipeStore(_paths).LoadAsync(_lifetimeCancellation.Token);
        return registrations.Select(registration => registration.MountId).ToHashSet();
    }

}
