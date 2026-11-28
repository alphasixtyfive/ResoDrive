namespace ResoDrive.Windows.Tests;

public sealed class InstallerDirectoryMigrationTests
{
    [Fact]
    public void Cleanup_ProfileFailureKeepsTheJournalAndRetryTaskPending()
    {
        var completed = false;
        var taskPresent = true;
        var profilePresent = true;
        Assert.Throws<IOException>(() => InstallerDirectoryMigration.FinishCleanupArtifacts(completed,
            () => throw new IOException("The staging profile could not be verified."),
            () => completed = true, () => taskPresent = false));
        Assert.False(completed);
        Assert.True(taskPresent);

        InstallerDirectoryMigration.FinishCleanupArtifacts(completed,
            () => profilePresent = false, () => completed = true, () => taskPresent = false);
        Assert.False(profilePresent);
        Assert.True(completed);
        Assert.False(taskPresent);
    }

    [Fact]
    public void Cleanup_CommitFailureKeepsTheTaskForARepeatAfterProfileRemoval()
    {
        var profilePresent = true;
        var taskPresent = true;
        var completed = false;
        Assert.Throws<IOException>(() => InstallerDirectoryMigration.FinishCleanupArtifacts(completed,
            () => profilePresent = false, () => throw new IOException("Completion could not be recorded."),
            () => taskPresent = false));
        Assert.False(profilePresent);
        Assert.True(taskPresent);

        InstallerDirectoryMigration.FinishCleanupArtifacts(completed,
            () => profilePresent = false, () => completed = true, () => taskPresent = false);
        Assert.True(completed);
        Assert.False(taskPresent);
    }

    [Fact]
    public void Cleanup_TaskFailureCanResumeFromCompleteWithoutRewritingTheJournal()
    {
        var profilePresent = true;
        var taskPresent = true;
        var completed = false;
        Assert.Throws<IOException>(() => InstallerDirectoryMigration.FinishCleanupArtifacts(completed,
            () => profilePresent = false, () => completed = true,
            () => throw new IOException("Windows Task Scheduler was unavailable.")));
        Assert.False(profilePresent);
        Assert.True(completed);
        Assert.True(taskPresent);

        InstallerDirectoryMigration.FinishCleanupArtifacts(completed,
            () => profilePresent = false,
            () => throw new InvalidOperationException("Completed journal must not be rewritten."),
            () => taskPresent = false);
        Assert.False(profilePresent);
        Assert.False(taskPresent);
    }
}
