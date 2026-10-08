using ResoDrive.Core.Settings;

namespace ResoDrive.App;

/// <summary>Evaluates an edit against the latest settings only after acquiring mutation ownership.</summary>
internal static class SettingsEditTransaction
{
    internal static async Task<bool> ApplyAsync(
        Func<Task<bool>> acquire,
        Action release,
        Func<ManagerSettings> readCurrent,
        Func<ManagerSettings, ManagerSettings> edit,
        Func<ManagerSettings, Task<bool>> save)
    {
        if (!await acquire()) return false;
        try
        {
            return await save(edit(readCurrent()));
        }
        finally
        {
            release();
        }
    }
}
