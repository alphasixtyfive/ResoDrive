using ResoDrive.Core.Results;
using ResoDrive.Windows;
using System.ComponentModel;
using System.IO;

namespace ResoDrive.App;

internal static class SettingsRollback
{
    internal static string WithRecovery(string failure, string? recovery) =>
        recovery is null ? failure : failure + "\n\n" + recovery;

    internal static async Task<string?> RestoreStartupAsync(Func<Task<OperationResult>> restore)
    {
        OperationResult result;
        try { result = await restore(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            result = Result.Failure("settings.startup_rollback_failed", exception.Message);
        }
        return result.Succeeded ? null :
            "The Windows startup setting could not be restored. Review Start with Windows before restarting. " +
            (result.Error?.Message ?? "Windows did not confirm the change.");
    }

    internal static string ActivationMessage(HostResponse? response) =>
        response?.Succeeded == true ? string.Empty :
            "The background host could not reactivate the previous settings. Restart ResoDrive before continuing. " +
            (response?.ErrorMessage ?? "The host did not confirm the change.");
}
