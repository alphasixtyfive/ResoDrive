using System.Xml.Linq;

namespace ResoDrive.App.Tests;

public sealed class InstallerPackageTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    [Fact]
    public void LegacyMigrationUsesExistingHelperAndSkipsFreshAndCurrentLayoutInstallations()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var migrationActions = document.Descendants(Wix + "CustomAction").Where(action =>
            ((string?)action.Attribute("Id"))?.EndsWith("InstallationDirectoryMigration", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(4, migrationActions.Length);
        foreach (var action in migrationActions)
        {
            Assert.Equal("ResoDriveInstallationHelper", (string?)action.Attribute("BinaryRef"));
            Assert.Equal("no", (string?)action.Attribute("Impersonate"));
            Assert.NotEqual("immediate", (string?)action.Attribute("Execute"));
            var scheduled = Assert.Single(document.Descendants(Wix + "Custom"), row =>
                (string?)row.Attribute("Action") == (string?)action.Attribute("Id"));
            Assert.Equal("WIX_UPGRADE_DETECTED AND NOT Installed AND RDRIVE_DIRECTORY_LAYOUT <> \"ResoDrive-v1\"", (string?)scheduled.Attribute("Condition"));
        }
    }

    [Fact]
    public void UpgradePreparationHonorsUploadProtectionAndRunsWithoutPowerShell()
    {
        var action = LoadAction("PrepareInstalledResoDriveForUpgrade");

        Assert.Null(action.Attribute("FileRef"));
        Assert.Equal("ResoDriveInstallationHelper", (string?)action.Attribute("BinaryRef"));
        Assert.Equal("--prepare-registered-install \"[INSTALLFOLDER].\" [UILevel] \"[RDRIVE_DATA_ROOT]\\.\"", (string?)action.Attribute("ExeCommand"));
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
    public void SetupUsesBrandedNativeTheme()
    {
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var application = Assert.Single(document.Descendants(bal + "WixStandardBootstrapperApplication"));
        Assert.Equal("SetupTheme.xml", (string?)application.Attribute("ThemeFile"));
        Assert.Equal("[ProgramFiles64Folder]ResoDrive\\resodrive.exe", (string?)application.Attribute("LaunchTarget"));
    }

    [Fact]
    public void SetupLeavesInstalledAppsAndProductMaintenanceToTheMsi()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var bundle = Assert.Single(document.Descendants(Wix + "Bundle"));
        var chain = Assert.Single(bundle.Elements(Wix + "Chain"));
        var packages = chain.Elements().Where(element => element.Name.LocalName.EndsWith("Package", StringComparison.Ordinal)).ToArray();
        var application = Assert.Single(packages, package => package.Name == Wix + "MsiPackage");

        Assert.Equal("yes", (string?)application.Attribute("Visible"));
        Assert.Equal("yes", (string?)application.Attribute("Vital"));
        Assert.Same(application, packages[^1]);
        Assert.All(packages, package => Assert.Equal("yes", (string?)package.Attribute("Permanent")));
        Assert.Null(bundle.Attribute("DisableModify"));
        Assert.Null(bundle.Attribute("DisableRemove"));

        var fromSetup = Assert.Single(application.Elements(Wix + "MsiProperty"), property =>
            (string?)property.Attribute("Name") == "RDRIVE_FROM_SETUP");
        Assert.Equal("1", (string?)fromSetup.Attribute("Value"));
        Assert.Null(fromSetup.Attribute("Condition"));
    }

    [Fact]
    public void SetupRejectsAppRemovalBeforePlanningAndExplainsWindowsRemoval()
    {
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var condition = Assert.Single(document.Descendants(bal + "Condition"));

        Assert.Equal("WixBundleCommandLineAction <> 4", (string?)condition.Attribute("Condition"));
        Assert.Contains("Windows Settings > Apps > Installed apps", (string?)condition.Attribute("Message"));
        Assert.Contains("Uninstall", (string?)condition.Attribute("Message"));
    }

    [Fact]
    public void SameVersionRecoveryIsLimitedToOwnerApprovedVersions()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ResoDrive.Installer.wixproj"));
        var settings = document.Descendants("ResoDriveAllowSameVersionUpgrades").ToArray();
        Assert.Equal("no", Assert.Single(settings, setting => setting.Attribute("Condition") is null).Value);

        var exceptions = settings.Where(setting => setting.Attribute("Condition") is not null).ToArray();
        Assert.Equal(
            ["'$(ResoDriveVersion)' == '0.3.7'", "'$(ResoDriveVersion)' == '0.3.19'"],
            exceptions.Select(setting => (string?)setting.Attribute("Condition")));
        Assert.All(exceptions, setting => Assert.Equal("yes", setting.Value));

        var package = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var upgrade = Assert.Single(package.Descendants(Wix + "MajorUpgrade"));
        Assert.Equal("$(var.ResoDriveAllowSameVersionUpgrades)", (string?)upgrade.Attribute("AllowSameVersionUpgrades"));
        Assert.Equal("afterInstallInitialize", (string?)upgrade.Attribute("Schedule"));
        Assert.Null(upgrade.Attribute("AllowDowngrades"));
    }

    [Fact]
    public void InclusiveUpgradeRangeValidationExceptionDoesNotSuppressOtherInstallerChecks()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ResoDrive.Installer.wixproj"));
        var suppressions = document.Descendants("SuppressIces").ToArray();
        Assert.Equal(
            ["'$(ResoDriveVersion)' == '0.3.7'", "'$(ResoDriveVersion)' == '0.3.19'"],
            suppressions.Select(setting => (string?)setting.Attribute("Condition")));
        Assert.All(suppressions, setting => Assert.Equal("$(SuppressIces);ICE61", setting.Value));
    }

    private static XElement LoadAction(string id)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        return Assert.Single(document.Descendants(Wix + "CustomAction"), action =>
            (string?)action.Attribute("Id") == id);
    }
}
