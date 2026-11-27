using System.Text;

namespace ResoDrive.Windows.Tests;

public sealed class ProfileCatalogInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "profile-install-" + Guid.NewGuid().ToString("N"));
    private ApplicationPaths Paths => new(Path.Combine(_root, "active"));
    private const string Catalog = """
        {"schemaVersion":2,"profiles":[{"id":"example","displayName":"Files","description":"Test profile","defaultRemoteName":"Files",
        "connection":{"type":"webdav","baseUrl":"https://example.test/","pathTemplate":"/remote.php/dav/files/{username}","vendor":"nextcloud"},
        "defaultRemotePath":"","defaultDriveLetter":"U","startWithWindowsByDefault":false,"mountArguments":[]}]}
        """;

    private async Task<string> SourceAsync(string content = Catalog)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "source.json");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task InstallsIntoActiveRootAndPreservesSettingsAndCache()
    {
        var source = await SourceAsync();
        var paths = Paths;
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, "settings sentinel");
        await File.WriteAllTextAsync(Path.Combine(paths.Cache, "sentinel"), "cached upload");
        var result = await new ProfileCatalogInstaller(paths).InstallAsync(source);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.False(result.Value);
        Assert.Equal(Catalog, await File.ReadAllTextAsync(paths.ProfilesFile));
        Assert.Equal("settings sentinel", await File.ReadAllTextAsync(paths.SettingsFile));
        Assert.Equal("cached upload", await File.ReadAllTextAsync(Path.Combine(paths.Cache, "sentinel")));
    }

    [Fact]
    public async Task PreservesAnExistingValidCatalog()
    {
        var source = await SourceAsync();
        var paths = Paths;
        paths.EnsureCreated();
        var existing = Catalog.Replace("Files", "My files", StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.ProfilesFile, existing);
        var result = await new ProfileCatalogInstaller(paths).InstallAsync(source);
        Assert.True(result.Succeeded);
        Assert.True(result.Value);
        Assert.Equal(existing, await File.ReadAllTextAsync(paths.ProfilesFile));
        Assert.Empty(Directory.GetFiles(paths.Root, "*.invalid-*"));
    }

    [Fact]
    public async Task BacksUpInvalidCatalogBeforeAtomicReplacement()
    {
        var source = await SourceAsync();
        var paths = Paths;
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.ProfilesFile, "broken original");
        var result = await new ProfileCatalogInstaller(paths).InstallAsync(source);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(Catalog, await File.ReadAllTextAsync(paths.ProfilesFile));
        Assert.Equal("broken original", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(paths.Root, "*.invalid-*"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{bad")]
    [InlineData("{\"schemaVersion\":2,\"profiles\":[]}")]
    public async Task InvalidCatalogDoesNotCreateAccountData(string json)
    {
        var source = await SourceAsync(json);
        Assert.False((await new ProfileCatalogInstaller(Paths).InstallAsync(source)).Succeeded);
        Assert.False(Directory.Exists(Paths.Root));
    }

    [Fact]
    public async Task RejectsOversizedInputBeforeWriting()
    {
        var source = await SourceAsync(new string('x', 512 * 1024 + 1));
        Assert.False((await new ProfileCatalogInstaller(Paths).InstallAsync(source)).Succeeded);
        Assert.False(Directory.Exists(Paths.Root));
    }

    [Fact]
    public async Task RejectsNetworkDataRootWithoutAccessingIt()
    {
        var result = await new ProfileCatalogInstaller(new ApplicationPaths(@"\\not-contacted.invalid\share"))
            .InstallAsync(await SourceAsync());
        Assert.False(result.Succeeded);
        Assert.Equal("profiles.data_root_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task ALockedExistingCatalogIsNotOverwritten()
    {
        var source = await SourceAsync();
        var paths = Paths;
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.ProfilesFile, "original");
        using (var held = File.Open(paths.ProfilesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False((await new ProfileCatalogInstaller(paths).InstallAsync(source)).Succeeded);
        Assert.Equal("original", await File.ReadAllTextAsync(paths.ProfilesFile));
    }

    [Fact]
    public async Task RespectsRemoteWipeWriteGuard()
    {
        var source = await SourceAsync();
        var paths = Paths;
        await new RemoteWipeStateStore(paths).SaveAsync(new RemoteWipeState(Guid.NewGuid(), RemoteWipePhase.Requested,
            new RemoteWipeRegistration(Guid.NewGuid(), "https://example.test/dav", "https://example.test/", "user", "test-token")));
        var result = await new ProfileCatalogInstaller(paths).InstallAsync(source);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(paths.ProfilesFile));
        Assert.Equal(RemoteWipePhase.Requested, new RemoteWipeStateStore(paths).Read()!.Phase);
    }

    [Fact]
    public async Task CancellationWhileWaitingForAccountWriteDoesNotPublish()
    {
        var source = await SourceAsync();
        var paths = Paths;
        await using var lease = await new AccountDataGuard(paths).AcquireAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProfileCatalogInstaller(paths).InstallAsync(source, cancellation.Token));
        Assert.False(File.Exists(paths.ProfilesFile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
