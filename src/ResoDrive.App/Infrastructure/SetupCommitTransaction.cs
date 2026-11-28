using ResoDrive.Core.Results;

namespace ResoDrive.App;

/// <summary>Completes setup recovery before returning an outcome for presentation.</summary>
internal static class SetupCommitTransaction
{
    internal static async Task<SetupCommitOutcome> ApplyAsync(SetupCommitOperations operations)
    {
        var stage = CommitStage.Prepared;
        string primaryFailure;
        try
        {
            stage = CommitStage.PublishingFiles;
            operations.PublishFiles();
            if (operations.ChangeStartup is not null)
            {
                stage = CommitStage.ChangingStartup;
                RequireSuccess(await operations.ChangeStartup(), "The Windows startup task could not be changed.");
            }
            RequireSuccess(await operations.SaveSettings(), "The settings could not be saved.");
            stage = CommitStage.SettingsSaved;
            if (operations.ActivateSettings is not null)
                RequireSuccess(await operations.ActivateSettings(), "The background host rejected the new connection.");
            operations.CompleteFiles();
            stage = CommitStage.Committed;
        }
        catch (Exception exception)
        {
            primaryFailure = exception.Message;
            var recovery = new List<string>();
            if (stage >= CommitStage.SettingsSaved && !await RecoverAsync(
                    operations.RestoreSettings,
                    "The previous settings could not be restored. The saved connection files were kept together; restart ResoDrive to activate the saved configuration.", recovery))
            {
                // Keep the new settings/config pair if reverting the settings failed.
                RecoverFiles(operations.CompleteFiles, "The saved connection files could not be finalized. Keep the recovery files and restart ResoDrive before continuing.", recovery);
            }
            else
            {
                if (operations.ChangeStartup is not null && stage >= CommitStage.ChangingStartup)
                    await RecoverAsync(operations.RestoreStartup!,
                        "The Windows startup setting could not be restored. Review Start with Windows before restarting.", recovery);
                var filesRestored = RecoverFiles(operations.RestoreFiles,
                    "The connection files could not be fully restored. Keep the recovery files and contact your administrator before continuing.", recovery);
                if (filesRestored && stage >= CommitStage.SettingsSaved && operations.ReactivateSettings is not null)
                    await RecoverAsync(operations.ReactivateSettings,
                        "The background host could not reactivate the previous settings. Restart ResoDrive before continuing.", recovery);
            }
            return new(false, SettingsRollback.WithRecovery(primaryFailure,
                recovery.Count == 0 ? null : string.Join("\n\n", recovery)), null);
        }

        // The connection is committed. A mount rejection or lost acknowledgement must
        // neither revert its settings nor retry a start that the host may have accepted.
        string? mountWarning = null;
        if (operations.StartMount is not null)
        {
            try
            {
                var start = await operations.StartMount();
                if (!start.Succeeded) mountWarning = MountWarning(start.Error?.Message);
            }
            catch (Exception exception) { mountWarning = MountWarning(exception.Message); }
        }
        return new(true, null, mountWarning);
    }

    private static string MountWarning(string? reason) =>
        "The connection was saved, but its mount request was not confirmed. Check the drive status before trying Mount again. " +
        (reason ?? "The background host did not confirm the request.");

    private static void RequireSuccess(OperationResult result, string fallback)
    {
        if (!result.Succeeded) throw new InvalidOperationException(result.Error?.Message ?? fallback);
    }

    private static async Task<bool> RecoverAsync(Func<Task<OperationResult>> operation, string guidance, List<string> warnings)
    {
        try
        {
            var result = await operation();
            if (result.Succeeded) return true;
            warnings.Add(guidance + " " + (result.Error?.Message ?? "The change was not confirmed."));
        }
        catch (Exception exception) { warnings.Add(guidance + " " + exception.Message); }
        return false;
    }

    private static bool RecoverFiles(Action operation, string guidance, List<string> warnings)
    {
        try { operation(); return true; }
        catch (Exception exception) { warnings.Add(guidance + " " + exception.Message); return false; }
    }

    private enum CommitStage { Prepared, PublishingFiles, ChangingStartup, SettingsSaved, Committed }
}

internal sealed record SetupCommitOutcome(bool Committed, string? Failure, string? MountWarning);

internal sealed record SetupCommitOperations(
    Action PublishFiles,
    Func<Task<OperationResult>>? ChangeStartup,
    Func<Task<OperationResult>> SaveSettings,
    Func<Task<OperationResult>>? ActivateSettings,
    Action CompleteFiles,
    Func<Task<OperationResult>> RestoreSettings,
    Func<Task<OperationResult>>? RestoreStartup,
    Action RestoreFiles,
    Func<Task<OperationResult>>? ReactivateSettings,
    Func<Task<OperationResult>>? StartMount);
