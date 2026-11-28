using System.Reflection;
using ResoDrive.Core;

namespace ResoDrive.App;

/// <summary>User-facing product metadata sourced from the build configuration.</summary>
public static class ProductInfo
{
    private static readonly Assembly Assembly = typeof(ProductInfo).Assembly;

    public static string Name { get; } =
        Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "ResoDrive";

    public static string DeveloperName { get; } =
        Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Alexey Ivanov";

    public static Uri DeveloperPage => ProductLinks.Developer;
    public static Uri RepositoryPage => ProductLinks.Repository;
    public static Uri ReleasesPage => ProductLinks.Releases;
    public static Uri IssuesPage => ProductLinks.Issues;
    public static Uri LicensePage { get; } = new(
        $"{ProductLinks.Repository.AbsoluteUri.TrimEnd('/')}/blob/main/LICENSE");

    public static string Version
    {
        get
        {
            var informational = Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            return !string.IsNullOrWhiteSpace(informational)
                ? informational.Split('+', 2)[0]
                : Assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
    }

    public static string AboutTitle => $"About {Name}";
    public static string OpenLabel => $"Open {Name}";
    public static string ExitLabel => $"Exit {Name}";
    public static string WelcomeTitle => $"Welcome to {Name}";
    public static string StartWithWindowsText => $"Start {Name} with Windows";
    public static string MountOnStartText => $"Mount automatically when {Name} starts";
    public static string RunSyncOnStartText => $"Run once when {Name} starts";
    public static string WelcomeTrayText =>
        $"Start drives on demand, mount selected drives at sign-in, and create simple one-way copy or mirror jobs. {Name} stays available in the notification area.";
    public static string WelcomeRcloneText =>
        $"{Name} uses the free, open-source rclone engine. It is not bundled with the app: {Name} downloads and verifies a private copy for you, without changing any system rclone installation.";
}
