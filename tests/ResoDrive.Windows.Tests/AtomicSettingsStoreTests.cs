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
