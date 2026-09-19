namespace ResoDrive.Windows;

public sealed class ApplicationPaths
{
    public ApplicationPaths(string? rootPath = null)
    {
        var configuredRoot = rootPath ?? Environment.GetEnvironmentVariable("RDRIVE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configuredRoot) && !Path.IsPathFullyQualified(configuredRoot))
            configuredRoot = Path.Combine(AppContext.BaseDirectory, configuredRoot);

        Root = Path.GetFullPath(
            configuredRoot ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "rdrive"));
        SettingsFile = Path.Combine(Root, "settings.json");
        ConfigFile = Path.Combine(Root, "rclone.conf");
        OwnershipFile = Path.Combine(Root, "ownership.json");
        ConfigSecretFile = Path.Combine(Root, "config-pass.dpapi");
        RemoteWipeFile = Path.Combine(Root, "remote-wipe.dpapi");
        ProfilesFile = Path.Combine(Root, "profiles.json");
        SyncRunStateFile = Path.Combine(Root, "sync-run-state.json");
        WelcomeCompletedFile = Path.Combine(Root, "welcome.complete");
        Logs = Path.Combine(Root, "logs");
        UiLogFile = Path.Combine(Logs, "resodrive-ui.log");
        Cache = Path.Combine(Root, "cache");
        Components = Path.Combine(Root, "components");
        Updates = Path.Combine(Root, "updates");
        Rclone = Path.Combine(Components, "rclone");
        RcloneExecutable = Path.Combine(Rclone, "rclone.exe");
    }

    public string Root { get; }
    public string SettingsFile { get; }
    public string ConfigFile { get; }
    public string OwnershipFile { get; }
    public string ConfigSecretFile { get; }
    public string RemoteWipeFile { get; }
    public string ProfilesFile { get; }
    public string SyncRunStateFile { get; }
    public string WelcomeCompletedFile { get; }
    public string Logs { get; }
    public string UiLogFile { get; }
    public string Cache { get; }
    public string Components { get; }
    public string Updates { get; }
    public string Rclone { get; }
    public string RcloneExecutable { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Rclone);
        Directory.CreateDirectory(Updates);
    }
}
