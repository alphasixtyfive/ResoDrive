namespace ResoDrive.Windows;

/// <summary>
/// Atomically publishes setup files while retaining enough state to restore the
/// previous installation until the caller confirms the wider setup operation.
/// </summary>
public sealed class SetupFileTransaction : IDisposable
{
    private readonly List<Entry> _entries;
    private TransactionState _state;
    private readonly AccountDataGuard? _accountData;

    public SetupFileTransaction(IEnumerable<(string StagedPath, string DestinationPath)> files, AccountDataGuard? accountData = null)
    {
        _accountData = accountData;
        ArgumentNullException.ThrowIfNull(files);
        _entries = files.Select(file => new Entry(
            Path.GetFullPath(file.StagedPath),
            Path.GetFullPath(file.DestinationPath))).ToList();
        if (_entries.Count == 0)
        {
            throw new ArgumentException("At least one staged file is required.", nameof(files));
        }

        if (_entries.Select(entry => entry.DestinationPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != _entries.Count)
        {
            throw new ArgumentException("Setup destinations must be unique.", nameof(files));
        }
    }

    public void Apply()
    {
        using var lease = _accountData?.AcquireAsync().GetAwaiter().GetResult();
        EnsureState(TransactionState.Prepared);
        try
        {
            foreach (var entry in _entries)
            {
                if (!File.Exists(entry.StagedPath))
                {
                    throw new FileNotFoundException("A staged setup file is missing.", entry.StagedPath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(entry.DestinationPath)!);
                if (File.Exists(entry.DestinationPath))
                {
                    entry.BackupPath = entry.DestinationPath + $".{Guid.NewGuid():N}.setup-backup";
                    File.Replace(entry.StagedPath, entry.DestinationPath, entry.BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(entry.StagedPath, entry.DestinationPath);
                }

                entry.Applied = true;
            }

            _state = TransactionState.Applied;
        }
        catch (Exception applyFailure)
        {
            try
            {
                RestoreAppliedEntries();
                _state = TransactionState.RolledBack;
                CleanupArtifacts();
            }
            catch (Exception rollbackFailure)
                when (rollbackFailure is IOException or UnauthorizedAccessException or AggregateException)
            {
                _state = TransactionState.RecoveryRequired;
                throw new AggregateException(
                    "Publishing setup files failed and automatic recovery was incomplete.",
                    applyFailure,
                    rollbackFailure);
            }

            throw;
        }
    }

    public void Complete()
    {
        EnsureState(TransactionState.Applied);
        _state = TransactionState.Completed;
        CleanupArtifacts();
    }

    public void Rollback()
    {
        if (_state is TransactionState.Completed or TransactionState.RolledBack)
        {
            return;
        }

        // A remote wipe supersedes rollback: restoring backups would recreate revoked credentials.
        if (_accountData?.IsBlocked == true)
        {
            _state = TransactionState.RolledBack;
            CleanupArtifacts();
            return;
        }
        using var lease = _accountData?.AcquireAsync().GetAwaiter().GetResult();

        if (_state is TransactionState.Applied or TransactionState.RecoveryRequired)
        {
            try
            {
                RestoreAppliedEntries();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AggregateException)
            {
                _state = TransactionState.RecoveryRequired;
                throw;
            }
        }

        _state = TransactionState.RolledBack;
        CleanupArtifacts();
    }

    public void Dispose()
    {
        try
        {
            Rollback();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (AggregateException)
        {
        }
    }

    private void RestoreAppliedEntries()
    {
        var applied = _entries.AsEnumerable().Reverse().Where(entry => entry.Applied).ToArray();
        // Setup files can form an encrypted config/password pair. If an original
        // backup is missing, keep the entire current set and remaining backups
        // intact rather than knowingly creating a mixture of old and new files.
        var missingBackups = applied.Where(entry => entry.BackupPath is not null && !File.Exists(entry.BackupPath))
            .Select(_ => new IOException("A setup rollback backup is missing. The current files were preserved for manual recovery."))
            .ToArray();
        if (missingBackups.Length != 0)
            throw new AggregateException("Setup files could not be fully restored.", missingBackups);

        List<Exception>? failures = null;
        foreach (var entry in applied)
        {
            try
            {
                if (entry.BackupPath is not null)
                {
                    if (!File.Exists(entry.BackupPath))
                        throw new IOException("A setup rollback backup is missing. The current file was preserved for manual recovery.");
                    if (File.Exists(entry.DestinationPath))
                    {
                        File.Replace(entry.BackupPath, entry.DestinationPath, null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(entry.BackupPath, entry.DestinationPath);
                    }
                }
                else if (File.Exists(entry.DestinationPath))
                {
                    File.Delete(entry.DestinationPath);
                }

                entry.Applied = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Setup files could not be fully restored.", failures);
        }
    }

    private void CleanupArtifacts()
    {
        foreach (var path in _entries.SelectMany(entry => new[] { entry.StagedPath, entry.BackupPath }))
        {
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception) when (_state is TransactionState.RolledBack or TransactionState.Completed)
            {
                // The live files are already consistent; stale setup files are harmless.
            }
        }
    }

    private void EnsureState(TransactionState expected)
    {
        if (_state != expected)
        {
            throw new InvalidOperationException($"The setup file transaction is {_state}.");
        }
    }

    private sealed class Entry(string stagedPath, string destinationPath)
    {
        public string StagedPath { get; } = stagedPath;
        public string DestinationPath { get; } = destinationPath;
        public string? BackupPath { get; set; }
        public bool Applied { get; set; }
    }

    private enum TransactionState
    {
        Prepared,
        Applied,
        Completed,
        RolledBack,
        RecoveryRequired
    }
}
