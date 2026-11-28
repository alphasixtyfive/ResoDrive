using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.Windows.Tests;

public sealed class AtomicSettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_FallsBackToSemanticallyValidBackup()
    {
        var paths = TestPaths();
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, """
            { "schemaVersion": 1, "revision": 3, "application": {}, "mounts": null }
            """);
        await File.WriteAllTextAsync(paths.SettingsFile + ".bak", """
            { "schemaVersion": 1, "revision": 2, "application": {}, "mounts": [] }
            """);
        using var store = new AtomicSettingsStore(paths);

        var result = await store.LoadAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value?.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairingCorruptPrimaryPreservesTheRecoveredBackup(bool blockReplacement)
    {
        var paths = TestPaths();
        try
        {
            paths.EnsureCreated();
            const string corrupt = "{ incomplete settings";
            const string backup = "{\"schemaVersion\":1,\"revision\":2,\"application\":{},\"mounts\":[]}";
            await File.WriteAllTextAsync(paths.SettingsFile, corrupt);
            await File.WriteAllTextAsync(paths.SettingsFile + ".bak", backup);
            using var store = new AtomicSettingsStore(paths);
            var recovered = await store.LoadAsync();
            Assert.True(recovered.Succeeded);
            var candidate = recovered.Value! with
            {
                Application = recovered.Value!.Application with { MinimizeToTray = false },
            };

            using (var held = blockReplacement
                ? new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null)
            {
                var saved = await store.SaveAsync(candidate, recovered.Value.Revision);
                Assert.Equal(!blockReplacement, saved.Succeeded);
                if (blockReplacement)
                {
                    Assert.Equal("settings.io", saved.Error?.Code);
                    Assert.Equal(corrupt, await File.ReadAllTextAsync(paths.SettingsFile));
                }
                else
                {
                    Assert.Equal(3, saved.Value!.Revision);
                    Assert.False(saved.Value.Application.MinimizeToTray);
                }
                Assert.Equal(backup, await File.ReadAllTextAsync(paths.SettingsFile + ".bak"));
            }

            await File.WriteAllTextAsync(paths.SettingsFile, corrupt);
            var stillRecoverable = await store.LoadAsync();
            Assert.True(stillRecoverable.Succeeded);
            Assert.Equal(2, stillRecoverable.Value!.Revision);
            Assert.Empty(Directory.EnumerateFiles(paths.Root, ".settings.json.*.tmp"));
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPrimaryUsesExistingBackupAndRejectsUnreadableBackup(bool corruptBackup)
    {
        var paths = TestPaths();
        try
        {
            paths.EnsureCreated();
            var backup = corruptBackup ? "{ incomplete settings" :
                "{\"schemaVersion\":1,\"revision\":2,\"application\":{},\"mounts\":[]}";
            await File.WriteAllTextAsync(paths.SettingsFile + ".bak", backup);
            using var store = new AtomicSettingsStore(paths);

            var loaded = await store.LoadAsync();
            Assert.Equal(!corruptBackup, loaded.Succeeded);
            if (corruptBackup)
            {
                Assert.Equal("settings.corrupt", loaded.Error?.Code);
                var saved = await store.SaveAsync(new ManagerSettings(), 0);
                Assert.False(saved.Succeeded);
                Assert.Equal("settings.corrupt", saved.Error?.Code);
                Assert.False(File.Exists(paths.SettingsFile));
            }
            else
            {
                Assert.Equal(2, loaded.Value!.Revision);
                var saved = await store.SaveAsync(loaded.Value, loaded.Value.Revision);
                Assert.True(saved.Succeeded);
                Assert.Equal(3, saved.Value!.Revision);
            }
            Assert.Equal(backup, await File.ReadAllTextAsync(paths.SettingsFile + ".bak"));
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true); }
    }

    [Fact]
    public async Task SaveAsync_RejectsInvalidSettingsWithoutChangingFile()
    {
        var paths = TestPaths();
        using var store = new AtomicSettingsStore(paths);
        var initial = await store.SaveAsync(new ManagerSettings(), 0);
        Assert.True(initial.Succeeded);
        var before = await File.ReadAllTextAsync(paths.SettingsFile);

        var invalid = initial.Value! with { Mounts = null! };
        var result = await store.SaveAsync(invalid, initial.Value!.Revision);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.invalid", result.Error?.Code);
        Assert.Equal(before, await File.ReadAllTextAsync(paths.SettingsFile));
    }

    [Fact]
    public async Task ImportAsync_ValidatesFileAndPreservesCurrentSettings()
    {
        var paths = TestPaths();
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, """
            { "schemaVersion": 1, "revision": 9, "application": {}, "mounts": [] }
            """);
        var importedPath = Path.Combine(Path.GetTempPath(), $"resodrive-import-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(importedPath, """
            { "schemaVersion": 1, "revision": 4, "application": { "minimizeToTray": false }, "mounts": [] }
            """);
        using var store = new AtomicSettingsStore(paths);

        var result = await store.ImportAsync(importedPath);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(10, result.Value?.Revision);
        Assert.False(result.Value?.Application.MinimizeToTray);
        Assert.Contains("\"revision\": 10", await File.ReadAllTextAsync(paths.SettingsFile));
        var preserved = Assert.Single(Directory.EnumerateFiles(paths.Root, "settings.pre-import-*.json"));
        Assert.Contains("\"revision\": 9", await File.ReadAllTextAsync(preserved));
        Assert.Empty(Directory.EnumerateFiles(paths.Root, "*.import"));
    }

    [Fact]
    public async Task ImportAsync_RejectsInvalidFileWithoutChangingCurrentSettings()
    {
        var paths = TestPaths();
        paths.EnsureCreated();
        const string current = "{ \"schemaVersion\": 1, \"revision\": 9, \"application\": {}, \"mounts\": [] }";
        await File.WriteAllTextAsync(paths.SettingsFile, current);
        var importedPath = Path.Combine(Path.GetTempPath(), $"resodrive-import-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(importedPath, "{ not-json }");
        using var store = new AtomicSettingsStore(paths);

        var result = await store.ImportAsync(importedPath);

        Assert.False(result.Succeeded);
        Assert.Equal("settings.import_invalid", result.Error?.Code);
        Assert.Equal(current, await File.ReadAllTextAsync(paths.SettingsFile));
        Assert.Empty(Directory.EnumerateFiles(paths.Root, "settings.pre-import-*.json"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task InvalidRevisionCannotBeLoadedOrSaved(long revision)
    {
        var paths = TestPaths();
        try
        {
            paths.EnsureCreated();
            var json = $"{{\"schemaVersion\":1,\"revision\":{revision},\"application\":{{}},\"mounts\":[]}}";
            await File.WriteAllTextAsync(paths.SettingsFile, json);
            using var store = new AtomicSettingsStore(paths);

            Assert.False((await store.LoadAsync()).Succeeded);
            Assert.False((await store.SaveAsync(new ManagerSettings { Revision = revision }, revision)).Succeeded);
            Assert.Equal(json, await File.ReadAllTextAsync(paths.SettingsFile));
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true); }
    }

    private static ApplicationPaths TestPaths() => new(
        Path.Combine(Path.GetTempPath(), "rdrive-tests", Guid.NewGuid().ToString("N")));
}
