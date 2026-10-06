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
    public void SetupUsesBrandedNativeTheme()
    {
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var application = Assert.Single(document.Descendants(bal + "WixStandardBootstrapperApplication"));
        Assert.Equal("SetupTheme.xml", (string?)application.Attribute("ThemeFile"));
        Assert.Equal("[ProgramFiles64Folder]ResoDrive\\resodrive-launcher.exe", (string?)application.Attribute("LaunchTarget"));
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

    [Fact]
    public void NormalLaunchUsesNativeMonitorWhileMaintenanceStillUsesManagedApp()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var shortcut = Assert.Single(document.Descendants(Wix + "Shortcut"));
        Assert.Equal("ResoDriveLauncherFile", (string?)shortcut.Parent?.Attribute("Id"));
        Assert.Equal("ResoDriveLauncherFile", (string?)LoadAction("LaunchResoDrive").Attribute("FileRef"));
        Assert.Equal("ResoDriveExecutableFile", (string?)LoadAction("UnregisterResoDriveAutostart").Attribute("FileRef"));
        var helper = Assert.Single(document.Descendants(Wix + "Binary"));
        Assert.Equal("$(var.PackageSource)\\$(var.ResoDriveExecutableName)", (string?)helper.Attribute("SourceFile"));
        var folder = Assert.Single(document.Descendants(Wix + "Directory"), item => (string?)item.Attribute("Id") == "INSTALLFOLDER");
        Assert.Equal("ResoDrive", (string?)folder.Attribute("Name"));
    }

    [Fact]
    public void LegacyUpdaterPreservesRegisteredFolderAndModernUpdaterExplicitlyMigrates()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var migration = Assert.Single(document.Descendants(Wix + "Property"), item => (string?)item.Attribute("Id") == "RDRIVE_MIGRATE_INSTALL");
        Assert.Equal("yes", (string?)migration.Attribute("Secure"));
        var preserve = Assert.Single(document.Descendants(Wix + "SetProperty"), item => (string?)item.Attribute("Id") == "INSTALLFOLDER" && item.Attribute("Action") is null);
        Assert.Equal("[RDRIVE_PREVIOUS_INSTALL_ROOT]", (string?)preserve.Attribute("Value"));
        Assert.Equal("WIX_UPGRADE_DETECTED AND RDRIVE_PREVIOUS_INSTALL_ROOT AND RDRIVE_MIGRATE_INSTALL <> \"1\"", (string?)preserve.Attribute("Condition"));
        var search = Assert.Single(document.Descendants(Wix + "ComponentSearch"));
        Assert.Equal("2E1BB141-3D85-4177-8498-54A0833D2B23", (string?)search.Attribute("Guid"));
        Assert.Equal("file", (string?)search.Attribute("Type"));
        Assert.Empty(search.Elements());
        var profiles = Assert.Single(document.Descendants(Wix + "Component"), item => (string?)item.Attribute("Id") == "SampleProfiles");
        Assert.Equal("*", (string?)profiles.Attribute("Guid"));
        var location = Assert.Single(document.Descendants(Wix + "SetProperty"), item => (string?)item.Attribute("Id") == "ARPINSTALLLOCATION");
        Assert.Equal("[INSTALLFOLDER]", (string?)location.Attribute("Value"));
        Assert.Equal("CostFinalize", (string?)location.Attribute("After"));

        var bundle = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var optIn = Assert.Single(bundle.Descendants(Wix + "MsiProperty"), item => (string?)item.Attribute("Name") == "RDRIVE_MIGRATE_INSTALL");
        Assert.Equal("1", (string?)optIn.Attribute("Value"));
        var repair = Assert.Single(document.Descendants(Wix + "SetProperty"), item => (string?)item.Attribute("Action") == "RestoreInstalledFolder");
        Assert.Equal("[RDRIVE_INSTALLED_ROOT]", (string?)repair.Attribute("Value"));
        Assert.Equal("Installed AND RDRIVE_INSTALLED_ROOT", (string?)repair.Attribute("Condition"));
        var ownSearch = Assert.Single(document.Descendants(Wix + "RegistrySearch"), item => (string?)item.Attribute("Id") == "OwnRegisteredInstallFolder");
        Assert.Contains("[ProductCode]", (string?)ownSearch.Attribute("Key"));
        Assert.Equal("InstallLocation", (string?)ownSearch.Attribute("Name"));
        var installFolder = Assert.Single(document.Descendants(Wix + "Directory"), item => (string?)item.Attribute("Id") == "INSTALLFOLDER");
        Assert.Equal("$(var.ResoDriveComponentSeed)", (string?)installFolder.Attribute("ComponentGuidGenerationSeed"));
    }

    [Fact]
    public void RegisteredUpgradeLocationIsAvailableBeforeTheMissingLocationLaunchGuard()
    {
        // After the compatibility release, per-version component identities mean
        // AppSearch must supply the registered location before launch validation.
        // CostFinalize is too late, and a UI-only assignment breaks passive MSI updates.
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var resolve = Assert.Single(document.Descendants(Wix + "SetProperty"), item =>
            (string?)item.Attribute("Id") == "RDRIVE_PREVIOUS_INSTALL_ROOT");
        Assert.Equal("LaunchConditions", (string?)resolve.Attribute("Before"));
        Assert.Null(resolve.Attribute("After"));
        Assert.Equal("both", (string?)resolve.Attribute("Sequence"));
        Assert.Equal("[RDRIVE_REGISTERED_INSTALL_ROOT]", (string?)resolve.Attribute("Value"));
        Assert.Equal("WIX_UPGRADE_DETECTED AND RDRIVE_REGISTERED_INSTALL_ROOT", (string?)resolve.Attribute("Condition"));

        var search = Assert.Single(document.Descendants(Wix + "RegistrySearch"), item =>
            (string?)item.Attribute("Id") == "RegisteredInstallFolder");
        Assert.Equal("HKLM", (string?)search.Attribute("Root"));
        Assert.Equal("always64", (string?)search.Attribute("Bitness"));
        Assert.Equal("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\[WIX_UPGRADE_DETECTED]", (string?)search.Attribute("Key"));
        Assert.Equal("InstallLocation", (string?)search.Attribute("Name"));
        Assert.Contains(document.Descendants(Wix + "Launch"), item =>
            (string?)item.Attribute("Condition") == "Installed OR NOT WIX_UPGRADE_DETECTED OR RDRIVE_PREVIOUS_INSTALL_ROOT");
        Assert.Contains(document.Descendants(Wix + "Launch"), item =>
            (string?)item.Attribute("Condition") == "NOT (WIX_UPGRADE_DETECTED >< \";\")");
    }

    [Fact]
    public void RelocationDrainsBothDirectoriesAndRecordsMachineOwnedTaskMigrationProof()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var prepare = LoadAction("PreparePreviousResoDriveForUpgrade");
        Assert.Equal("ResoDriveInstallationHelper", (string?)prepare.Attribute("BinaryRef"));
        Assert.Equal("check", (string?)prepare.Attribute("Return"));
        var sequence = Assert.Single(document.Descendants(Wix + "Custom"), item => (string?)item.Attribute("Action") == "PreparePreviousResoDriveForUpgrade");
        Assert.Equal("PrepareInstalledResoDriveForUpgrade", (string?)sequence.Attribute("Before"));
        Assert.Equal("WIX_UPGRADE_DETECTED AND RDRIVE_PREVIOUS_INSTALL_ROOT AND RDRIVE_PREVIOUS_INSTALL_ROOT ~<> INSTALLFOLDER", (string?)sequence.Attribute("Condition"));
        var receipt = Assert.Single(document.Descendants(Wix + "Component"), item => (string?)item.Attribute("Id") == "ResoDriveInstallMigration");
        Assert.Equal("always64", (string?)receipt.Attribute("Bitness"));
        Assert.Null(receipt.Attribute("Permanent"));
        Assert.Equal("(Installed OR WIX_UPGRADE_DETECTED) AND RDRIVE_MIGRATED_FROM", (string?)receipt.Attribute("Condition"));
        Assert.All(receipt.Elements(Wix + "RegistryValue"), value =>
        {
            Assert.Equal("HKLM", (string?)value.Attribute("Root"));
            Assert.Equal("SOFTWARE\\ResoDrive\\Installation", (string?)value.Attribute("Key"));
        });
        Assert.Equal("[RDRIVE_MIGRATED_FROM]", (string?)Assert.Single(receipt.Elements(Wix + "RegistryValue"), item => (string?)item.Attribute("Name") == "LegacyInstallLocation").Attribute("Value"));
        Assert.Contains(document.Descendants(Wix + "Launch"), item =>
            (string?)item.Attribute("Condition") == "NOT (WIX_UPGRADE_DETECTED >< \";\")");
    }

    [Fact]
    public void LocalDumpsUseUserExpandedBoundedFullDumpsAndRespectExistingPolicy()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var component = Assert.Single(document.Descendants(Wix + "Component"), item => (string?)item.Attribute("Id") == "ResoDriveLocalDumps");
        Assert.Equal("always64", (string?)component.Attribute("Bitness"));
        Assert.Equal("yes", (string?)component.Attribute("NeverOverwrite"));
        Assert.Null(component.Attribute("Permanent"));
        Assert.Equal("RDRIVE_WER_OWNED = \"#1\" OR NOT (RDRIVE_WER_FOLDER OR RDRIVE_WER_COUNT OR RDRIVE_WER_TYPE OR RDRIVE_WER_FLAGS)", (string?)component.Attribute("Condition"));
        var values = component.Elements(Wix + "RegistryValue").ToArray();
        var folder = Assert.Single(values, item => (string?)item.Attribute("Name") == "DumpFolder");
        Assert.Equal("expandable", (string?)folder.Attribute("Type"));
        Assert.Equal("[\\%]LOCALAPPDATA[\\%]\\rdrive-diagnostics\\dumps", (string?)folder.Attribute("Value"));
        Assert.Equal("yes", (string?)folder.Attribute("KeyPath"));
        Assert.Equal("3", (string?)Assert.Single(values, item => (string?)item.Attribute("Name") == "DumpCount").Attribute("Value"));
        Assert.Equal("2", (string?)Assert.Single(values, item => (string?)item.Attribute("Name") == "DumpType").Attribute("Value"));
        Assert.All(values.Where(item => (string?)item.Attribute("Name") != "LocalDumpsOwned"), item =>
        {
            Assert.Equal("HKLM", (string?)item.Attribute("Root"));
            Assert.Equal("SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps\\resodrive.exe", (string?)item.Attribute("Key"));
        });
        foreach (var name in new[] { "DumpFolder", "DumpCount", "DumpType", "CustomDumpFlags" })
        {
            var search = Assert.Single(document.Descendants(Wix + "RegistrySearch"), item =>
                (string?)item.Attribute("Name") == name && ((string?)item.Attribute("Key"))?.EndsWith("\\resodrive.exe", StringComparison.Ordinal) == true);
            Assert.Equal("always64", (string?)search.Attribute("Bitness"));
            Assert.Equal("raw", (string?)search.Attribute("Type"));
        }
    }

    private static XElement LoadAction(string id)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        return Assert.Single(document.Descendants(Wix + "CustomAction"), action =>
            (string?)action.Attribute("Id") == id);
    }
}
