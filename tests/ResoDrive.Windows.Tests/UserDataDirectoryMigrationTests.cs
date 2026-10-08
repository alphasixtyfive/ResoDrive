using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResoDrive.Windows.Tests;

public sealed class UserDataDirectoryMigrationTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "resodrive-migration-tests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_parent, "rdrive");
    private string Destination => Path.Combine(_parent, "ResoDrive");
    private string Journal => Path.Combine(_parent, "ResoDriveMigration", "user-data-migration.json");

    [Fact]
    public async Task FreshInstallation_DoesNotCreateMigrationStateOrPrepareApplication()
    {
        await new UserDataDirectoryMigration(Source, Destination).MigrateAsync(_ =>
            throw new InvalidOperationException("A fresh installation has nothing to migrate."));
        Assert.False(Directory.Exists(_parent));
    }

    [Fact]
    public async Task Migration_PreservesCredentialsCacheManagedCopiesAndRewritesOnlyOwnedPaths()
    {
        Directory.CreateDirectory(Path.Combine(Source, "cache"));
        Directory.CreateDirectory(Path.Combine(Source, "managed-sync", "job"));
        var paths = new ApplicationPaths(Source);
        var secret = new DpapiSecretStore(paths);
        await secret.SaveProtectedFileAsync("disposable-secret", paths.ConfigSecretFile);
        var encrypted = await File.ReadAllBytesAsync(paths.ConfigSecretFile);
        await File.WriteAllBytesAsync(Path.Combine(Source, "cache", "marker"), [1, 2, 3, 255]);
        await File.WriteAllTextAsync(Path.Combine(Source, "managed-sync", "job", "copy"), "local edits");
        var settings = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = 17,
            mounts = new[] { new { target = new { directoryPath = Path.Combine(Source, "mount") }, syncJobs = new[] {
                new { localPath = Path.Combine(Source, "managed-sync", "job") }, new { localPath = @"D:\Personal\rdrive-documents" } } } }
        });
        await File.WriteAllTextAsync(paths.SettingsFile, settings);
        await File.WriteAllTextAsync(Path.Combine(Source, "mount-upload-recovery.json"), JsonSerializer.Serialize(new[] {
            new { metadataPath = Path.Combine(Source, "cache", "vfsMeta", "entry"), arguments = new[] {
                "--config", paths.ConfigFile, "--password-command", $"\"{InstallationDirectories.LegacyExecutable}\" password",
                "remote:rdrive/archive" } } }));
        File.Copy(Path.Combine(Source, "mount-upload-recovery.json"), Path.Combine(Source, "mount-upload-recovery.json.bak"));
        await File.WriteAllTextAsync(paths.OwnershipFile, JsonSerializer.Serialize(new[] {
            new { executablePath = paths.RcloneExecutable, target = Path.Combine(Source, "mount"), source = "remote:rdrive/archive" } }));
        var prepared = false;
        await new UserDataDirectoryMigration(Source, Destination).MigrateAsync(_ => { prepared = true; return Task.CompletedTask; });
        Assert.True(prepared);
        Assert.False(Directory.Exists(Source));
        var migrated = new ApplicationPaths(Destination);
        Assert.Equal(encrypted, await File.ReadAllBytesAsync(migrated.ConfigSecretFile));
        Assert.Equal("disposable-secret", await new DpapiSecretStore(migrated).LoadAsync());
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, await File.ReadAllBytesAsync(Path.Combine(Destination, "cache", "marker")));
        Assert.Equal("local edits", await File.ReadAllTextAsync(Path.Combine(Destination, "managed-sync", "job", "copy")));
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(migrated.SettingsFile))!;
        Assert.Equal(17, saved["revision"]!.GetValue<int>());
        Assert.Equal(Path.Combine(Destination, "managed-sync", "job"), saved["mounts"]![0]!["syncJobs"]![0]!["localPath"]!.GetValue<string>());
        Assert.Equal(@"D:\Personal\rdrive-documents", saved["mounts"]![0]!["syncJobs"]![1]!["localPath"]!.GetValue<string>());
        var recovery = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Destination, "mount-upload-recovery.json")))!;
        Assert.Equal(migrated.ConfigFile, recovery[0]!["arguments"]![1]!.GetValue<string>());
        Assert.Equal($"\"{InstallationDirectories.Executable}\" password", recovery[0]!["arguments"]![3]!.GetValue<string>());
        Assert.Equal("remote:rdrive/archive", recovery[0]!["arguments"]![4]!.GetValue<string>());
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Destination, "mount-upload-recovery.json")),
            await File.ReadAllTextAsync(Path.Combine(Destination, "mount-upload-recovery.json.bak")));
        var ownership = JsonNode.Parse(await File.ReadAllTextAsync(migrated.OwnershipFile))!;
        Assert.Equal(Path.Combine(Destination, "mount"), ownership[0]!["target"]!.GetValue<string>());
        Assert.Equal(migrated.RcloneExecutable, ownership[0]!["executablePath"]!.GetValue<string>());
        Assert.Equal("remote:rdrive/archive", ownership[0]!["source"]!.GetValue<string>());
        await new UserDataDirectoryMigration(Source, Destination).MigrateAsync(_ => throw new InvalidOperationException("Completed migration must not stop work again."));
        Assert.False(Directory.Exists(Source));
    }

    [Fact]
    public async Task Migration_RejectsIndependentDestinationBeforePreparation()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
        await File.WriteAllTextAsync(Path.Combine(Source, "settings.json"), "{\"revision\":1}");
        await File.WriteAllTextAsync(Path.Combine(Destination, "settings.json"), "{\"revision\":2}");
        await Assert.ThrowsAsync<IOException>(() => new UserDataDirectoryMigration(Source, Destination)
            .MigrateAsync(_ => throw new InvalidOperationException("Must not stop either app.")));
        Assert.Contains("1", await File.ReadAllTextAsync(Path.Combine(Source, "settings.json")), StringComparison.Ordinal);
        Assert.Contains("2", await File.ReadAllTextAsync(Path.Combine(Destination, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_RejectedShutdownLeavesOriginalDataUntouched()
    {
        Directory.CreateDirectory(Source);
        var path = Path.Combine(Source, "settings.json");
        await File.WriteAllTextAsync(path, "{\"revision\":17}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserDataDirectoryMigration(Source, Destination)
            .MigrateAsync(_ => throw new InvalidOperationException("Uploads pending.")));
        Assert.Equal("{\"revision\":17}", await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(Destination));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task Migration_ResumesCrashAfterDirectoryRenameAndBetweenPathRewrites()
    {
        Directory.CreateDirectory(Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(Journal)!);
        var journal = JsonSerializer.Serialize(new { Id = Guid.NewGuid(), Source, Destination, Phase = "Prepared" });
        await File.WriteAllTextAsync(Journal, journal);
        await File.WriteAllTextAsync(Path.Combine(Destination, ".directory-migration.json"), journal);
        await File.WriteAllTextAsync(Path.Combine(Destination, "settings.json"), JsonSerializer.Serialize(new {
            revision = 2, mounts = new[] { new { syncJobs = new[] { new { localPath = Path.Combine(Destination, "managed-sync", "job") } } } } }));
        await File.WriteAllTextAsync(Path.Combine(Destination, "mount-upload-recovery.json"), JsonSerializer.Serialize(new[] {
            new { metadataPath = Path.Combine(Source, "cache", "metadata"), arguments = new[] { Path.Combine(Source, "rclone.conf") } } }));
        await new UserDataDirectoryMigration(Source, Destination).MigrateAsync(_ => throw new InvalidOperationException("Already stopped before the crash."));
        Assert.False(File.Exists(Journal));
        Assert.Equal("Complete", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(Journal)!, "user-data-migration.complete.json")))!["Phase"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(Destination, ".directory-migration.json")));
        Assert.Contains(Destination.Replace("\\", "\\\\", StringComparison.Ordinal), await File.ReadAllTextAsync(Path.Combine(Destination, "mount-upload-recovery.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_DoesNotAdoptUnrelatedDestinationWithForgedJournal()
    {
        Directory.CreateDirectory(Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(Journal)!);
        await File.WriteAllTextAsync(Journal, JsonSerializer.Serialize(new { Id = Guid.NewGuid(), Source, Destination, Phase = "Renamed" }));
        await Assert.ThrowsAsync<IOException>(() => new UserDataDirectoryMigration(Source, Destination).MigrateAsync(_ => Task.CompletedTask));
        Assert.True(Directory.Exists(Destination));
    }

    [Fact]
    public async Task Migration_PendingWipeIsPreservedAndBlocksRelocation()
    {
        Directory.CreateDirectory(Source);
        var paths = new ApplicationPaths(Source);
        // An unreadable durable wipe state is never treated as absent.
        await File.WriteAllTextAsync(paths.RemoteWipeStateFile, "unreadable-disposable-state");
        await Assert.ThrowsAsync<IOException>(() => new UserDataDirectoryMigration(Source, Destination).MigrateAsync(_ => Task.CompletedTask));
        Assert.True(File.Exists(paths.RemoteWipeStateFile));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task Migration_InvalidSettingsArePreservedBeforeRename()
    {
        Directory.CreateDirectory(Source);
        await File.WriteAllTextAsync(Path.Combine(Source, "settings.json"), "not-json");
        await Assert.ThrowsAnyAsync<JsonException>(() => new UserDataDirectoryMigration(Source, Destination).MigrateAsync(_ => Task.CompletedTask));
        Assert.True(Directory.Exists(Source));
        Assert.False(Directory.Exists(Destination));
    }

    [Theory]
    [InlineData("settings.json", "{\"mounts\":[{\"syncJobs\":[{\"localPath\":42}]}]}")]
    [InlineData("ownership.json", "[{\"arguments\":[42]}]")]
    public async Task Migration_InvalidPathTypesAreRejectedBeforePreparation(string name, string contents)
    {
        Directory.CreateDirectory(Source);
        await File.WriteAllTextAsync(Path.Combine(Source, name), contents);
        await Assert.ThrowsAsync<IOException>(() => new UserDataDirectoryMigration(Source, Destination)
            .MigrateAsync(_ => throw new InvalidOperationException("Must validate paths before shutdown.")));
        Assert.Equal(contents, await File.ReadAllTextAsync(Path.Combine(Source, name)));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task Migration_RejectsNestedJunctionWithoutFollowingIt()
    {
        Directory.CreateDirectory(Source);
        var external = Path.Combine(_parent, "outside");
        Directory.CreateDirectory(external);
        var junction = Path.Combine(Source, "cache");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") {
            Arguments = $"/c mklink /J \"{junction}\" \"{external}\"", UseShellExecute = false, CreateNoWindow = true });
        await process!.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new UserDataDirectoryMigration(Source, Destination).MigrateAsync(_ => Task.CompletedTask));
            Assert.True(Directory.Exists(external));
            Assert.False(Directory.Exists(Destination));
        }
        finally { Directory.Delete(junction); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true);
    }
}
