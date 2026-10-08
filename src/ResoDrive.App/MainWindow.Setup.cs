using ResoDrive.Core.Results;
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
        SetupCommitOutcome? outcome;
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

            outcome = await CommitProvisioningAsync(
                provisioning,
                reloadHost,
                applyStartupPreference);
        }
        // Disposal also completes the file transaction's final recovery attempt.
        // Present the result only after every recovery operation has finished.
        if (outcome is null) return false;
        LoadSettingsControls();
        if (!outcome.Committed)
        {
            _model.AddLogEntry("Setup was not completed", outcome.Failure!, LogSeverity.Error);
            ShowError("Setup was not completed", outcome.Failure!);
            return false;
        }

        _model.AddLogEntry("Drive added",
            $"{provisioning.NewMount.DisplayName} · {provisioning.ConnectionSummary}", LogSeverity.Success);
        if (reloadHost) await RefreshStatusAsync();
        if (outcome.MountWarning is not null)
        {
            _model.AddLogEntry("Drive mount needs attention", outcome.MountWarning, LogSeverity.Warning);
            ModernMessageBox.Show(this, outcome.MountWarning, "Drive mount needs attention",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        return true;
    }

    private async Task<SetupCommitOutcome?> CommitProvisioningAsync(
        ProfileProvisioningResult provisioning,
        bool reloadHost,
        bool applyStartupPreference)
    {
        if (!await EnterSettingsMutationAsync())
        {
            return null;
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

    private async Task<SetupCommitOutcome> CommitProvisioningCoreAsync(
        ProfileProvisioningResult provisioning,
        bool reloadHost,
        bool applyStartupPreference)
    {
        var autostart = new ScheduledTaskAutostartService(CurrentExecutablePath);
        var previousStartup = await autostart.IsEnabledAsync();
        if (!previousStartup.Succeeded)
        {
            return new(false, previousStartup.Error?.Message ?? "The Windows startup task could not be read.", null);
        }

        var previousSettings = _settings;
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
        var store = _store ?? throw new InvalidOperationException("Settings are not ready.");
        async Task<OperationResult> SaveAsync(ManagerSettings settings)
        {
            var saved = await store.SaveAsync(settings, _settings.Revision);
            if (!saved.Succeeded) return saved;
            if (saved.Value is null)
                return Result.Failure("settings.missing_saved_value", "Windows did not confirm the saved settings.");
            _settings = saved.Value;
            return saved;
        }
        async Task<OperationResult> ReloadAsync()
        {
            var response = await HostClient.SendAsync(new HostRequest("reload"));
            if (response.Succeeded) LoadHostStatus(response);
            return SetupHostResult(response);
        }
        var startRequested = reloadHost && provisioning.NewMount.AutoMount.Equals(
            "OnApplicationStart", StringComparison.OrdinalIgnoreCase);
        return await SetupCommitTransaction.ApplyAsync(new(
            provisioning.Files.Apply,
            applyStartupPreference ? () => autostart.SetEnabledAsync(provisioning.StartWithWindows) : null,
            () => SaveAsync(updated),
            reloadHost ? ReloadAsync : null,
            provisioning.Files.Complete,
            () => SaveAsync(previousSettings),
            applyStartupPreference ? () => autostart.SetEnabledAsync(previousStartup.Value == true) : null,
            provisioning.Files.Rollback,
            reloadHost ? ReloadAsync : null,
            startRequested ? async () => SetupHostResult(await HostClient.SendAsync(new HostRequest(
                "start", provisioning.NewMount.Id))) : null));

    }

    private static OperationResult SetupHostResult(HostResponse response) => response.Succeeded
        ? Result.Success()
        : Result.Failure(response.ErrorCode ?? "host.request_failed",
            response.ErrorMessage ?? "The background host did not confirm the request.");
}
