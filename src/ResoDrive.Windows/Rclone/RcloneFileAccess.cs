using System.ComponentModel;

namespace ResoDrive.Windows;

internal static class RcloneFileAccess
{
    // Five seconds in total. Retry sharing/lock violations only; permissions,
    // invalid packages and security-policy refusals need their own resolution.
    private static readonly int[] RetryDelays = [200, 400, 800, 1600, 2000];

    internal static bool IsLocked(Exception exception) => exception switch
    {
        Win32Exception native => native.NativeErrorCode is 32 or 33,
        IOException io => (io.HResult & 0xFFFF) is 32 or 33,
        _ => false,
    };

    internal static string DescribeLock(string file) =>
        $"{file} is still locked by another program after several retries. " +
        "Close programs using it, wait for security scanning to finish, then try again. " +
        "If it stays locked, restart Windows.";

    internal static async Task<T> RunAsync<T>(Func<Task<T>> action,
        CancellationToken token, Action? retrying = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await action().ConfigureAwait(false); }
            catch (Exception exception) when (IsLocked(exception) && attempt < RetryDelays.Length)
            {
                if (attempt == 0) retrying?.Invoke();
                await Task.Delay(RetryDelays[attempt], token).ConfigureAwait(false);
            }
        }
    }

    internal static Task<T> OpenAsync<T>(Func<T> action, CancellationToken token, Action? retrying = null) =>
        RunAsync(() => Task.FromResult(action()), token, retrying);

    internal static Task ChangeAsync(Action action, CancellationToken token, Action? retrying = null) =>
        OpenAsync(() => { action(); return true; }, token, retrying);
}
