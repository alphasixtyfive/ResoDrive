namespace ResoDrive.Windows;

/// <summary>Serializes account writes with wipe and rejects writes from pre-wipe UI sessions.</summary>
public sealed class AccountDataGuard
{
    private readonly ApplicationPaths _paths;
    private readonly Guid? _generation;

    public AccountDataGuard(ApplicationPaths paths)
    {
        _paths = paths;
        _generation = new RemoteWipeStateStore(paths).Read()?.Id;
    }

    public bool IsBlocked
    {
        get
        {
            var state = new RemoteWipeStateStore(_paths).Read();
            return state?.Id != _generation || state is { Phase: not RemoteWipePhase.Completed };
        }
    }

    public async Task<FileStream> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var lease = await LockAsync(_paths, cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsBlocked)
                throw new IOException("Nextcloud requested removal of local ResoDrive data. Restart ResoDrive after cleanup completes.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static async Task<FileStream> LockAsync(ApplicationPaths paths, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(paths.Root, ".account-data.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
