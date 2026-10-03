namespace ResoDrive.Windows;

internal static class RcloneRuntimeMutationLock
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);

    public static async Task<IAsyncDisposable> AcquireAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(runtimeDirectory);
            var lockPath = Path.Combine(runtimeDirectory, "runtime.lock");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.Asynchronous);
                    return new Releaser(stream, lockPath);
                }
                catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            ProcessGate.Release();
            throw;
        }
    }

    private sealed class Releaser(FileStream stream, string lockPath) : IAsyncDisposable
    {
        private FileStream? _stream = stream;

        public async ValueTask DisposeAsync()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
                return;
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                try
                {
                    // The file is only a cross-process mutex, not durable state. A waiter that
                    // acquired it after disposal denies deletion and remains correctly locked.
                    File.Delete(lockPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Another process owns the lock or cleanup can be retried next time.
                }
            }
            finally
            {
                ProcessGate.Release();
            }
        }
    }
}
