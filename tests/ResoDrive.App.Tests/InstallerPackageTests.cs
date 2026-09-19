using System.Xml.Linq;

namespace ResoDrive.App.Tests;

public sealed class InstallerPackageTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    [Fact]
    public void UpgradePreparationHonorsUploadProtectionAndRunsWithoutPowerShell()
    {
        var action = LoadAction("PrepareInstalledResoDriveForUpgrade");

        Assert.Equal("ResoDriveExecutableFile", (string?)action.Attribute("FileRef"));
        Assert.Equal("--prepare-update", (string?)action.Attribute("ExeCommand"));
        Assert.Equal("check", (string?)action.Attribute("Return"));
        Assert.Equal("yes", (string?)action.Attribute("Impersonate"));
    }

    [Fact]
    public void UpgradeStopFallbackIsHiddenPathScopedAndWaitsForExit()
    {
        var action = LoadAction("StopResoDriveForUpgrade");
        var command = Assert.IsType<XAttribute>(action.Attribute("ExeCommand")).Value;

        Assert.Equal("check", (string?)action.Attribute("Return"));
        Assert.Equal("no", (string?)action.Attribute("Impersonate"));
        Assert.Contains("-WindowStyle Hidden", command, StringComparison.Ordinal);
        Assert.Contains("Path -eq $p", command, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(10000)", command, StringComparison.Ordinal);
        Assert.Contains(";exit 0", command, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovalAlsoPreparesAndStopsTheInstalledApplication()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        foreach (var id in new[] { "PrepareInstalledResoDriveForUpgrade", "StopResoDriveForUpgrade" })
        {
            var scheduled = Assert.Single(document.Descendants(Wix + "Custom"), action =>
                (string?)action.Attribute("Action") == id);
            Assert.Equal("WIX_UPGRADE_DETECTED OR Installed", (string?)scheduled.Attribute("Condition"));
        }
    }

    [Fact]
    public void SetupUsesBrandedNativeThemeAndOwnsInstalledAppsEntry()
    {
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var application = Assert.Single(document.Descendants(bal + "WixStandardBootstrapperApplication"));
        Assert.Equal("SetupTheme.xml", (string?)application.Attribute("ThemeFile"));
        Assert.Equal("[ProgramFiles64Folder]rdrive\\resodrive.exe", (string?)application.Attribute("LaunchTarget"));
        Assert.Equal("no", (string?)Assert.Single(document.Descendants(Wix + "MsiPackage")).Attribute("Visible"));
    }

    private static XElement LoadAction(string id)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        return Assert.Single(document.Descendants(Wix + "CustomAction"), action =>
            (string?)action.Attribute("Id") == id);
    }
}
