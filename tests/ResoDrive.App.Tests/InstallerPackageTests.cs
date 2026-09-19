using System.Xml.Linq;

namespace ResoDrive.App.Tests;

public sealed class InstallerPackageTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    [Fact]
    public void UpgradePreparationHonorsUploadProtectionAndRunsWithoutPowerShell()
    {
        var action = LoadAction("PrepareInstalledResoDriveForUpgrade");

        Assert.Null(action.Attribute("FileRef"));
        Assert.Equal("ResoDriveInstallationHelper", (string?)action.Attribute("BinaryRef"));
        Assert.Equal("--prepare-install \"[INSTALLFOLDER].\" [UILevel] \"[RDRIVE_DATA_ROOT]\\.\"", (string?)action.Attribute("ExeCommand"));
        Assert.Equal("check", (string?)action.Attribute("Return"));
        Assert.Equal("yes", (string?)action.Attribute("Impersonate"));
    }

    [Fact]
    public void UpgradeDoesNotUseASeparateForceKillFallback()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        Assert.DoesNotContain(document.Descendants(Wix + "CustomAction"), action =>
            ((string?)action.Attribute("ExeCommand"))?.Contains("powershell", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void RemovalAlsoPreparesAndStopsTheInstalledApplication()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var prepare = Assert.Single(document.Descendants(Wix + "Custom"), action =>
            (string?)action.Attribute("Action") == "PrepareInstalledResoDriveForUpgrade");
        Assert.Equal("WIX_UPGRADE_DETECTED OR Installed", (string?)prepare.Attribute("Condition"));
        Assert.Equal("InstallValidate", (string?)prepare.Attribute("Before"));
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
