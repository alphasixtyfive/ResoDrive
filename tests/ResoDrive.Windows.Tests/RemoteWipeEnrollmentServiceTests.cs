using System.Security.AccessControl;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;

namespace ResoDrive.Windows.Tests;

public sealed class RemoteWipeEnrollmentServiceTests
{
    private const string AppPassword = "potato";
    // Upstream rclone known-answer vector for the disposable password "potato".
    private const string ObscuredPassword = "YWFhYWFhYWFhYWFhYWFhYXMaGgIlEQ";
    private const string EncryptedConfig = "# Encrypted rclone configuration File\n\nRCLONE_ENCRYPT_V0:\nfixture-ciphertext";

    [Fact]
    public async Task SavedAppPasswordEnrollsEveryMountLocallyAndPreservesOtherAccounts()
    {
        using var fixture = await Fixture.CreateAsync(mountCount: 2);
        var unrelated = new RemoteWipeRegistration(Guid.NewGuid(),
            "https://other.example/remote.php/dav/files/other", "https://other.example/", "other", "other-token");
        await fixture.SaveRegistrationsAsync([unrelated]);
        var configBefore = await File.ReadAllBytesAsync(fixture.Paths.ConfigFile);
        var secretBefore = await File.ReadAllBytesAsync(fixture.Paths.ConfigSecretFile);
        var service = fixture.CreateService();

        var result = await service.EnrollAsync(fixture.Mounts);

        Assert.True(result.RegistrationsChanged);
        Assert.All(fixture.Mounts, mount => Assert.Equal(RemoteWipeEnrollmentService.Configured, result.Statuses[mount.Id.Value]));
        var registrations = await fixture.Registrations.LoadAsync();
        Assert.Equal(3, registrations.Count);
        Assert.Contains(unrelated, registrations);
        foreach (var mount in fixture.Mounts)
        {
            var registration = Assert.Single(registrations, item => item.MountId == mount.Id.Value);
            Assert.Equal(AppPassword, registration.AppToken);
            Assert.Equal("test", registration.Username);
            Assert.Equal("https://cloud.example/nextcloud/", registration.ServerBaseUrl);
            Assert.Equal("https://cloud.example/nextcloud/remote.php/dav/files/test", registration.ProbeEndpoint);
        }
        Assert.Equal(configBefore, await File.ReadAllBytesAsync(fixture.Paths.ConfigFile));
        Assert.Equal(secretBefore, await File.ReadAllBytesAsync(fixture.Paths.ConfigSecretFile));
        Assert.True(new FileInfo(fixture.Paths.RemoteWipeFile).GetAccessControl().AreAccessRulesProtected);
        Assert.DoesNotContain(AppPassword, await File.ReadAllTextAsync(fixture.Paths.RemoteWipeFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "https://cloud.example/nextcloud/remote.php/webdav")]
    [InlineData("other", "https://cloud.example/nextcloud/remote.php/dav/files/test/documents")]
    [InlineData("nextcloud", "https://cloud.example:8443/nextcloud/remote.php/dav/files/test")]
    public async Task ImportedDavConnectionsKeepTheirSubdirectoryPortAndSavedPassword(string vendor, string endpoint)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Configuration.Values["vendor"] = vendor;
        fixture.Configuration.Values["url"] = endpoint;

        var result = await fixture.CreateService().EnrollAsync(fixture.Mounts);

        Assert.Equal(RemoteWipeEnrollmentService.Configured, result.Statuses[fixture.Mounts[0].Id.Value]);
        var registration = Assert.Single(await fixture.Registrations.LoadAsync());
        Assert.Equal(endpoint, registration.ProbeEndpoint);
        Assert.Equal(new Uri(endpoint).GetLeftPart(UriPartial.Authority) + "/nextcloud/", registration.ServerBaseUrl);
        Assert.Equal(AppPassword, registration.AppToken);
        Assert.Equal(EncryptedConfig, await File.ReadAllTextAsync(fixture.Paths.ConfigFile));
    }

    [Theory]
    [InlineData("url", "http://cloud.example/remote.php/dav/files/test")]
    [InlineData("url", "https://user:secret@cloud.example/remote.php/dav/files/test")]
    [InlineData("url", "https://cloud.example/remote.php/dav/files/test?query")]
    [InlineData("url", "https://cloud.example/remote.php/dav/files/test#fragment")]
    [InlineData("url", "https://cloud.example/public.php/webdav")]
    [InlineData("url", "https://cloud.example/remote.php/dav/public-files/share")]
    [InlineData("url", "https://cloud.example/remote.php/dav/files/")]
    [InlineData("url", "https://cloud.example/next%2Fcloud/remote.php/dav/files/test")]
    [InlineData("url", "https://cloud.example/remote.php/webdav-other")]
    [InlineData("bearer_token", "disposable-bearer")]
    [InlineData("bearer_token_command", "disposable-command")]
    [InlineData("headers", "Authorization,disposable-value")]
    [InlineData("pass", "not-an-obscured-password")]
    [InlineData("pass", "")]
    [InlineData("user", "invalid:user")]
    [InlineData("user", "")]
    public async Task UnsupportedCredentialsOrEndpointsAreNotEnrolled(string key, string value)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Configuration.Values[key] = value;

        var result = await fixture.CreateService().EnrollAsync(fixture.Mounts);

        Assert.NotEqual(RemoteWipeEnrollmentService.Configured, result.Statuses[fixture.Mounts[0].Id.Value]);
        Assert.Empty(await fixture.Registrations.LoadAsync());
        Assert.Equal(EncryptedConfig, await File.ReadAllTextAsync(fixture.Paths.ConfigFile));
    }

    [Fact]
    public async Task GenericWebDavIsNotPresentedAsConfiguredForRemoteWipe()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Configuration.Values["vendor"] = "other";
        fixture.Configuration.Values["url"] = "https://storage.example/dav/";

        var result = await fixture.CreateService().EnrollAsync(fixture.Mounts);

        Assert.Equal(RemoteWipeEnrollmentService.NotConfigured, result.Statuses[fixture.Mounts[0].Id.Value]);
        Assert.Empty(await fixture.Registrations.LoadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleMountDefinitionsCannotRecreateRemovedOrReassignedEnrollment(bool reassigned)
    {
        using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await fixture.SaveMountsAsync(reassigned ? [fixture.Mounts[0] with { RemoteName = "replacement" }] : []);

        var result = await service.EnrollAsync(fixture.Mounts);

        Assert.False(result.Statuses.TryGetValue(fixture.Mounts[0].Id.Value, out var status) &&
            status == RemoteWipeEnrollmentService.Configured);
        Assert.Empty(await fixture.Registrations.LoadAsync());
    }

    [Theory]
    [InlineData(RemoteWipePhase.Requested)]
    [InlineData(RemoteWipePhase.Cleaned)]
    [InlineData(RemoteWipePhase.Completed)]
    public async Task WipeGenerationChangeBlocksAnOlderEnrollmentService(RemoteWipePhase phase)
    {
        using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var registration = new RemoteWipeRegistration(fixture.Mounts[0].Id.Value,
            "https://cloud.example/nextcloud/remote.php/dav/files/test", "https://cloud.example/nextcloud/", "test", AppPassword);
        await new RemoteWipeStateStore(fixture.Paths).SaveAsync(new RemoteWipeState(
            Guid.NewGuid(), phase, phase == RemoteWipePhase.Completed ? null : registration));

        var result = await service.EnrollAsync(fixture.Mounts);

        Assert.Equal(RemoteWipeEnrollmentService.Attention, result.Statuses[fixture.Mounts[0].Id.Value]);
        Assert.Equal(0, fixture.Configuration.ReadCalls);
        Assert.Empty(await fixture.Registrations.LoadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedEnrollmentRestoresADeletedOrOverwrittenRegistrationList(bool overwrite)
    {
        using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        Assert.True((await service.EnrollAsync(fixture.Mounts)).RegistrationsChanged);
        Assert.False((await service.EnrollAsync(fixture.Mounts)).RegistrationsChanged);
        Assert.Equal(1, fixture.Configuration.ReadCalls);
        var unrelated = new RemoteWipeRegistration(Guid.NewGuid(),
            "https://other.example/remote.php/dav/files/other", "https://other.example/", "other", "other-token");
        if (overwrite) await fixture.SaveRegistrationsAsync([unrelated]);
        else File.Delete(fixture.Paths.RemoteWipeFile);

        var result = await service.EnrollAsync(fixture.Mounts);

        Assert.Equal(RemoteWipeEnrollmentService.Configured, result.Statuses[fixture.Mounts[0].Id.Value]);
        Assert.Equal(2, fixture.Configuration.ReadCalls);
        var registrations = await fixture.Registrations.LoadAsync();
        Assert.Equal(overwrite ? 2 : 1, registrations.Count);
        Assert.Equal(AppPassword, Assert.Single(registrations,
            item => item.MountId == fixture.Mounts[0].Id.Value).AppToken);
        if (overwrite) Assert.Contains(unrelated, registrations);
    }

    [Fact]
    public async Task ConfigChangesInvalidateCachedEnrollmentAndReplaceOnlyTheMatchingAccount()
    {
        using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.EnrollAsync(fixture.Mounts);
        fixture.Configuration.Values["user"] = "replacement";
        fixture.Configuration.Values["url"] = "https://cloud.example/nextcloud/remote.php/dav/files/replacement";
        await File.AppendAllTextAsync(fixture.Paths.ConfigFile, "replacement-encrypted-fixture");
        var updatedBytes = await File.ReadAllBytesAsync(fixture.Paths.ConfigFile);

        var result = await service.EnrollAsync(fixture.Mounts);

        Assert.Equal(RemoteWipeEnrollmentService.Configured, result.Statuses[fixture.Mounts[0].Id.Value]);
        Assert.Equal(2, fixture.Configuration.ReadCalls);
        Assert.Equal("replacement", Assert.Single(await fixture.Registrations.LoadAsync()).Username);
        Assert.Equal(updatedBytes, await File.ReadAllBytesAsync(fixture.Paths.ConfigFile));
    }

    [Fact]
    public async Task ReadFailuresReportAttentionWithoutExposingCredentialBearingErrors()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Configuration.ReadFailure = new IOException("Disposable source password: " + AppPassword);

        var result = await fixture.CreateService().EnrollAsync(fixture.Mounts);

        Assert.Equal(RemoteWipeEnrollmentService.Attention, result.Statuses[fixture.Mounts[0].Id.Value]);
        Assert.All(result.Statuses.Values, status => Assert.DoesNotContain(AppPassword, status, StringComparison.Ordinal));
        Assert.Empty(await fixture.Registrations.LoadAsync());
    }

    [Fact]
    public async Task RepeatedEnrollmentAfterRestartDoesNotRewriteTheConnectionOrProtectedCatalog()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CreateService().EnrollAsync(fixture.Mounts);
        var registrationBytes = await File.ReadAllBytesAsync(fixture.Paths.RemoteWipeFile);
        var configBytes = await File.ReadAllBytesAsync(fixture.Paths.ConfigFile);

        var result = await fixture.CreateService().EnrollAsync(fixture.Mounts);

        Assert.Equal(RemoteWipeEnrollmentService.Configured, result.Statuses[fixture.Mounts[0].Id.Value]);
        Assert.Single(await fixture.Registrations.LoadAsync());
        Assert.Equal(registrationBytes, await File.ReadAllBytesAsync(fixture.Paths.RemoteWipeFile));
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(fixture.Paths.ConfigFile));
    }

    [Fact]
    public async Task RegisteredSavedPasswordsRemainInsideTheExistingWipeScope()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CreateService().EnrollAsync(fixture.Mounts);
        Assert.Single(await fixture.Registrations.LoadAsync());

        RemoteWipeCleanup.DeleteAccountData(fixture.Paths);

        Assert.False(File.Exists(fixture.Paths.RemoteWipeFile));
        Assert.False(File.Exists(fixture.Paths.ConfigFile));
        Assert.False(File.Exists(fixture.Paths.ConfigSecretFile));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture()
        {
            Paths = new(Path.Combine(Path.GetTempPath(), $"resodrive-enrollment-{Guid.NewGuid():N}"));
            Paths.EnsureCreated();
        }

        public ApplicationPaths Paths { get; }
        public FakeConfiguration Configuration { get; } = new();
        public MountDefinition[] Mounts { get; private set; } = [];
        public RemoteWipeStore Registrations => new(Paths);

        public static async Task<Fixture> CreateAsync(int mountCount = 1)
        {
            var fixture = new Fixture();
            await new DpapiSecretStore(fixture.Paths).SaveAsync("disposable-encrypted-config-password");
            await File.WriteAllTextAsync(fixture.Paths.ConfigFile, EncryptedConfig);
            await File.WriteAllTextAsync(fixture.Paths.RcloneExecutable, "Disposable executable placeholder, never launched");
            fixture.Mounts = Enumerable.Range(0, mountCount).Select(index => new MountDefinition
            {
                Id = MountId.New(), DisplayName = $"Cloud {index}", RemoteName = "cloud",
                Target = new MountTarget.Drive((char)('R' + index)), ConnectionType = "WebDAV",
            }).ToArray();
            await fixture.SaveMountsAsync(fixture.Mounts);
            return fixture;
        }

        public async Task SaveMountsAsync(MountDefinition[] mounts)
        {
            using var store = new AtomicSettingsStore(Paths);
            var loaded = await store.LoadAsync();
            Assert.True(loaded.Succeeded);
            var saved = await store.SaveAsync(new ManagerSettings
            {
                Mounts = mounts.Select(MountDefinitionMapper.ToSettings).ToArray(),
            }, loaded.Value!.Revision);
            Assert.True(saved.Succeeded, saved.Error?.Message);
        }

        public async Task SaveRegistrationsAsync(IEnumerable<RemoteWipeRegistration> registrations)
        {
            var staged = await Registrations.CreateStagedAsync(registrations);
            File.Move(staged, Paths.RemoteWipeFile, overwrite: true);
        }

        public RemoteWipeEnrollmentService CreateService() => new(Paths, Configuration, Paths.RcloneExecutable);

        public void Dispose()
        {
            var root = Path.GetFullPath(Paths.Root);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(root));
            Assert.StartsWith("resodrive-enrollment-", Path.GetFileName(root), StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeConfiguration : IRcloneRemoteConfigurationAccess
    {
        public int ReadCalls { get; private set; }
        public Exception? ReadFailure { get; set; }
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal)
        {
            ["type"] = "webdav", ["vendor"] = "nextcloud", ["user"] = "test", ["pass"] = ObscuredPassword,
            ["url"] = "https://cloud.example/nextcloud/remote.php/dav/files/test",
        };

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadAsync(
            string executablePath, string configPath, string passwordCommand,
            IReadOnlyCollection<string> remoteNames, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ReadCalls++;
            if (ReadFailure is not null) throw ReadFailure;
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = remoteNames.Contains("cloud")
                ? new Dictionary<string, IReadOnlyDictionary<string, string>> { ["cloud"] = new Dictionary<string, string>(Values) }
                : new Dictionary<string, IReadOnlyDictionary<string, string>>();
            return Task.FromResult(result);
        }

    }
}
