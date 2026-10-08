namespace ResoDrive.Windows.Tests;

public sealed class InstallationDirectoriesTests
{
    [Fact]
    public void MsiAndRegisteredSpellingsProduceOnePreparationDirectory()
    {
        var directories = InstallationDirectories.DistinctPaths([
            @"C:\Program Files\ResoDrive",
            @"c:\Program Files\ResoDrive\.",
            @"C:\Program Files\ResoDrive\"
        ]);

        Assert.Equal([@"C:\Program Files\ResoDrive"], directories);
    }

    [Fact]
    public void LegacyAndCurrentInstallationsRemainSeparateInOrder()
    {
        var directories = InstallationDirectories.DistinctPaths([
            @"C:\Program Files\rdrive\.",
            @"C:\Program Files\ResoDrive\",
            @"C:\Program Files\rdrive"
        ]);

        Assert.Equal([@"C:\Program Files\rdrive", @"C:\Program Files\ResoDrive"], directories);
    }

    [Fact]
    public void RelativePathsAreRejectedBeforeAnyPreparationCanStart()
    {
        Assert.Throws<ArgumentException>(() => InstallationDirectories.DistinctPaths([
            @"C:\Program Files\ResoDrive", @".\ResoDrive"
        ]));
    }
}
