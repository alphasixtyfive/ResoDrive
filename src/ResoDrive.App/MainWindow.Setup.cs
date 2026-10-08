using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App;

public partial class MainWindow
{
    private async Task<bool> RunSetupAsync(bool firstRun, bool hostAlreadyRunning = false)
    {
        var reservedDriveLetters = _settings.Mounts
            .Select(mount => mount.Target.DriveLetter)
            .Where(letter => letter.HasValue)
            .Select(letter => letter!.Value);
        var wizard = new SetupWindow(_paths, firstRun, reservedDriveLetters,
            _settings.Mounts.Select(mount => mount.DisplayName)) { Owner = this };
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
                LoadHostStatus(reload);
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
                "Drive added",
                $"{provisioning.NewMount.DisplayName} · {provisioning.ConnectionSummary}", LogSeverity.Success);
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
                        var warning = await RestoreStartupPreferenceAsync(autostart, previous.Value == true);
                        if (warning is not null) ShowError("Startup setting needs attention", warning);
                    }
                    provisioning.Files.Rollback();
                    if (reloadHost && settingsApplied)
                    {
                        var restored = await HostClient.SendAsync(new HostRequest("reload"));
                        ReportRollbackActivation(restored);
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

}
