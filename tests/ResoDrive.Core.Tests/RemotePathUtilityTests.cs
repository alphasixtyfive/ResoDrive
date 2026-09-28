using ResoDrive.Core.Domain;

namespace ResoDrive.Core.Tests;

public sealed class RemotePathUtilityTests
{
    [Theory]
    [InlineData(" /srv/harbour/ ", "/srv/harbour")]
    [InlineData("documents/", "documents")]
    [InlineData("/", "")]
    public void Normalize_PreservesOneLeadingSlash(string path, string expected) =>
        Assert.Equal(expected, RemotePathUtility.Normalize(path));

    [Theory]
    [InlineData("../private")]
    [InlineData("folder/./child")]
    [InlineData("folder\\child")]
    [InlineData("//srv/harbour")]
    [InlineData("srv//harbour")]
    public void IsWellFormed_RejectsUnsafeOrAmbiguousPaths(string path) =>
        Assert.False(RemotePathUtility.IsWellFormed(path));

    [Theory]
    [InlineData("", "storage:")]
    [InlineData("documents", "storage:documents")]
    [InlineData("/srv/harbour", "storage:/srv/harbour")]
    public void FormatSource_PreservesRootedRemotePath(string path, string expected) =>
        Assert.Equal(expected, RemotePathUtility.FormatSource("storage", path));

    [Theory]
    [InlineData("", "Archive")]
    [InlineData("team/reports", "Archive · team/reports")]
    [InlineData("/Fleet Reference", "Archive · /Fleet Reference")]
    public void Display_ShowsPathFromConnectionRoot(string path, string expected) =>
        Assert.Equal(expected, RemotePathUtility.Display("Archive", path));
}
