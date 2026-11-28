namespace ResoDrive.Windows.Tests;

public sealed class SetupFileTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rdrive-setup-{Guid.NewGuid():N}");

    public SetupFileTransactionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PreparedTransaction_DoesNotChangeLiveFile_AndDisposeRemovesStage()
    {
        var live = Write("rclone.conf", "old");
        var staged = Write("rclone.conf.stage", "new");

        using (new SetupFileTransaction([(staged, live)]))
        {
            Assert.Equal("old", File.ReadAllText(live));
        }

        Assert.Equal("old", File.ReadAllText(live));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void ApplyThenComplete_PublishesAllFilesAndRemovesTransactionArtifacts()
    {
        var config = Write("rclone.conf", "old-config");
        var secret = Path.Combine(_root, "config-pass.dpapi");
        var stagedConfig = Write("config.stage", "new-config");
        var stagedSecret = Write("secret.stage", "new-secret");
        using var transaction = new SetupFileTransaction(
            [(stagedSecret, secret), (stagedConfig, config)]);

        transaction.Apply();
        transaction.Complete();

        Assert.Equal("new-config", File.ReadAllText(config));
        Assert.Equal("new-secret", File.ReadAllText(secret));
        Assert.Empty(Directory.GetFiles(_root, "*.setup-backup"));
        Assert.Empty(Directory.GetFiles(_root, "*.stage"));
    }

    [Fact]
    public void Rollback_RestoresReplacedFileAndRemovesNewFile()
    {
        var config = Write("rclone.conf", "old-config");
        var secret = Path.Combine(_root, "config-pass.dpapi");
        var stagedConfig = Write("config.stage", "new-config");
        var stagedSecret = Write("secret.stage", "new-secret");
        using var transaction = new SetupFileTransaction(
            [(stagedSecret, secret), (stagedConfig, config)]);

        transaction.Apply();
        transaction.Rollback();

        Assert.Equal("old-config", File.ReadAllText(config));
        Assert.False(File.Exists(secret));
        Assert.Empty(Directory.GetFiles(_root, "*.setup-backup"));
    }

    [Fact]
    public void PartialApplyFailure_RestoresFilesAlreadyPublished()
    {
        var config = Write("rclone.conf", "old-config");
        var stagedConfig = Write("config.stage", "new-config");
        var missingStage = Path.Combine(_root, "missing.stage");
        var secret = Path.Combine(_root, "config-pass.dpapi");
        using var transaction = new SetupFileTransaction(
            [(stagedConfig, config), (missingStage, secret)]);

        Assert.Throws<FileNotFoundException>(() => transaction.Apply());

        Assert.Equal("old-config", File.ReadAllText(config));
        Assert.False(File.Exists(secret));
        Assert.Empty(Directory.GetFiles(_root, "*.setup-backup"));
    }

    [Fact]
    public void MissingRollbackBackupPreservesCurrentFileAndRequiresRecovery()
    {
        var config = Write("rclone.conf", "old-config");
        var staged = Write("config.stage", "new-config");
        using var transaction = new SetupFileTransaction([(staged, config)]);
        transaction.Apply();
        File.Delete(Assert.Single(Directory.GetFiles(_root, "*.setup-backup")));

        var failure = Assert.Throws<AggregateException>(transaction.Rollback);

        Assert.Contains("backup is missing", Assert.Single(failure.InnerExceptions).Message, StringComparison.Ordinal);
        Assert.Equal("new-config", File.ReadAllText(config));
        Assert.Contains("RecoveryRequired", Assert.Throws<InvalidOperationException>(transaction.Complete).Message,
            StringComparison.Ordinal);
        Assert.Throws<AggregateException>(transaction.Rollback);
        transaction.Dispose();
        Assert.Equal("new-config", File.ReadAllText(config));
    }

    [Fact]
    public void MissingBackupPreventsAnyRestorationOfAnEncryptedFilePair()
    {
        var config = Write("rclone.conf", "old-config");
        var secret = Write("config-pass.dpapi", "old-secret");
        var stagedConfig = Write("config.stage", "new-config");
        var stagedSecret = Write("secret.stage", "new-secret");
        using var transaction = new SetupFileTransaction([(stagedConfig, config), (stagedSecret, secret)]);
        transaction.Apply();
        File.Delete(Assert.Single(Directory.GetFiles(_root, "rclone.conf.*.setup-backup")));
        var remainingBackup = Assert.Single(Directory.GetFiles(_root, "config-pass.dpapi.*.setup-backup"));

        Assert.Throws<AggregateException>(transaction.Rollback);
        Assert.Equal("new-config", File.ReadAllText(config));
        Assert.Equal("new-secret", File.ReadAllText(secret));
        Assert.Equal("old-secret", File.ReadAllText(remainingBackup));
        Assert.Throws<InvalidOperationException>(transaction.Complete);
        transaction.Dispose();
        Assert.Equal("new-config", File.ReadAllText(config));
        Assert.Equal("new-secret", File.ReadAllText(secret));
        Assert.Equal("old-secret", File.ReadAllText(remainingBackup));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string fileName, string contents)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, contents);
        return path;
    }
}
