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
        var applications = packages.Where(package => package.Name == Wix + "MsiPackage").ToArray();
        Assert.Equal(2, applications.Length);
        Assert.All(applications, application =>
        {
            Assert.Equal("yes", (string?)application.Attribute("Visible"));
            Assert.Equal("yes", (string?)application.Attribute("Vital"));
            Assert.Equal("ResoDrive", (string?)application.Attribute("DisplayName"));
            var fromSetup = Assert.Single(application.Elements(Wix + "MsiProperty"), property =>
                (string?)property.Attribute("Name") == "RDRIVE_FROM_SETUP");
            Assert.Equal("1", (string?)fromSetup.Attribute("Value"));
            Assert.Null(fromSetup.Attribute("Condition"));
        });
        Assert.Equal(applications, packages.TakeLast(2));
        Assert.All(packages, package => Assert.Equal("yes", (string?)package.Attribute("Permanent")));
        Assert.Null(bundle.Attribute("DisableModify"));
        Assert.Null(bundle.Attribute("DisableRemove"));

    }

    [Fact]
    public void SetupRejectsAppRemovalBeforePlanningAndExplainsWindowsRemoval()
    {
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var condition = Assert.Single(document.Descendants(bal + "Condition"), item =>
            (string?)item.Attribute("Condition") == "WixBundleCommandLineAction <> 4");

        Assert.Equal("WixBundleCommandLineAction <> 4", (string?)condition.Attribute("Condition"));
        Assert.Contains("Windows Settings > Apps > Installed apps", (string?)condition.Attribute("Message"));
        Assert.Contains("Uninstall", (string?)condition.Attribute("Message"));
    }

    [Fact]
    public void SetupSelectsExactlyOneImmutableVariantForInstallAndRepair()
    {
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var packages = document.Descendants(Wix + "MsiPackage").ToArray();
        Assert.Equal(2, packages.Length);
        Assert.Equal(["$(var.StandardMsiSource)", "$(var.CompatibilityMsiSource)"], packages.Select(package => (string?)package.Attribute("SourceFile")));
        Assert.Equal(["ResoDriveCompatibilityMode = 0", "ResoDriveCompatibilityMode = 1"], packages.Select(package => (string?)package.Attribute("InstallCondition")));
        Assert.All(packages, package => Assert.Equal((string?)package.Attribute("InstallCondition"), (string?)package.Attribute("RepairCondition")));
        var selection = Assert.Single(document.Descendants(Wix + "Variable"), item => (string?)item.Attribute("Name") == "ResoDriveCompatibilityMode");
        Assert.Equal("-1", (string?)selection.Attribute("Value"));
        Assert.Equal("yes", (string?)selection.Attribute(bal + "Overridable"));
        var extension = Assert.Single(document.Descendants(Wix + "Payload"), item => (string?)item.Attribute(bal + "BAFunctions") == "yes");
        Assert.Equal("$(var.SetupFunctionsSource)", (string?)extension.Attribute("SourceFile"));
        Assert.Contains(document.Descendants(bal + "Condition"), item => (string?)item.Attribute("Condition") == "ResoDriveSelectionValid = 1");
    }

    [Fact]
    public void EqualVersionDistinctProductsAreRejectedBeforePreparation()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var range = Assert.Single(document.Descendants(Wix + "UpgradeVersion"), item => (string?)item.Attribute("Property") == "RDRIVE_SAME_VERSION_PRODUCT");
        Assert.Equal("$(var.ResoDriveVersion)", (string?)range.Attribute("Minimum"));
        Assert.Equal((string?)range.Attribute("Minimum"), (string?)range.Attribute("Maximum"));
        Assert.Equal("yes", (string?)range.Attribute("IncludeMinimum"));
        Assert.Equal("yes", (string?)range.Attribute("IncludeMaximum"));
        Assert.Equal("yes", (string?)range.Attribute("OnlyDetect"));
        Assert.Contains(document.Descendants(Wix + "Launch"), item => (string?)item.Attribute("Condition") == "Installed OR NOT RDRIVE_SAME_VERSION_PRODUCT");
    }

    [Fact]
    public void CoordinatingInstallerCanDeferLaunchWithoutChangingPublicDefaults()
    {
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        XNamespace theme = "http://wixtoolset.org/schemas/v4/thmutil";
        var bundle = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var suppression = Assert.Single(bundle.Descendants(Wix + "Variable"), item => (string?)item.Attribute("Name") == "ResoDriveSuppressLaunch");
        Assert.Equal("0", (string?)suppression.Attribute("Value"));
        Assert.Equal("yes", (string?)suppression.Attribute(bal + "Overridable"));
        Assert.Equal("yes", (string?)suppression.Attribute("Persisted"));
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SetupTheme.xml"));
        var launch = Assert.Single(document.Descendants(theme + "Button"), item => (string?)item.Attribute("Name") == "LaunchButton");
        Assert.Equal("yes", (string?)launch.Attribute("HideWhenDisabled"));
    }

    [Fact]
    public void SetupShowsOptInProtectionWarningAndRealPrerequisiteStatus()
    {
        XNamespace theme = "http://wixtoolset.org/schemas/v4/thmutil";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SetupTheme.xml"));
        var checkbox = Assert.Single(document.Descendants(theme + "Checkbox"));
        Assert.Equal("ResoDriveCompatibilityMode", (string?)checkbox.Attribute("Name"));
        Assert.Equal("ResoDriveCanChangeMode = 1", (string?)checkbox.Attribute("EnableCondition"));
        Assert.Equal("ResoDriveShowCompatibilityChoice = 1", (string?)checkbox.Attribute("VisibleCondition"));
        Assert.Contains("reduced protection", checkbox.Value);
        XNamespace bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var bundle = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bundle.wxs"));
        var visibility = Assert.Single(bundle.Descendants(Wix + "Variable"), variable => (string?)variable.Attribute("Name") == "ResoDriveShowCompatibilityChoice");
        Assert.Equal("0", (string?)visibility.Attribute("Value"));
        Assert.Equal("numeric", (string?)visibility.Attribute("Type"));
        Assert.Null(visibility.Attribute(bal + "Overridable"));
        Assert.Null(visibility.Attribute("Persisted"));
        Assert.Contains(document.Descendants(theme + "Label"), label =>
            (string?)label.Attribute("VisibleCondition") == "ResoDriveShowCompatibilityChoice = 1" && label.Value.Contains("Standard is recommended", StringComparison.Ordinal));
        Assert.Contains(document.Descendants(theme + "Text"), text =>
            (string?)text.Attribute("Condition") == "DotNetDesktopRuntimeVersion >= DotNetDesktopRuntimeMinimumVersion" && text.Value.Contains("download skipped", StringComparison.Ordinal));
        Assert.Contains(document.Descendants(theme + "Label"), label =>
            (string?)label.Attribute("VisibleCondition") == "ResoDriveMissingCetCapability = 1");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Name") == "CacheProgressPackageText");
        Assert.Contains(document.Descendants(), element => (string?)element.Attribute("Name") == "ExecuteProgressPackageText");
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
        var optIns = bundle.Descendants(Wix + "MsiProperty").Where(item => (string?)item.Attribute("Name") == "RDRIVE_MIGRATE_INSTALL").ToArray();
        Assert.Equal(2, optIns.Length);
        Assert.All(optIns, optIn => Assert.Equal("1", (string?)optIn.Attribute("Value")));
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
    public void MigrationReceiptSourceIsCalculatedBeforeCostingFromTheFinalSelectedDirectory()
    {
        // Component conditions are evaluated during costing. Computing the source
        // afterward silently omits the receipt even when relocation succeeds.
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var canonical = Assert.Single(document.Descendants(Wix + "SetProperty"), item =>
            (string?)item.Attribute("Action") == "SetCanonicalInstallFolder");
        Assert.Equal("[ProgramFiles64Folder]ResoDrive\\", (string?)canonical.Attribute("Value"));
        Assert.Equal("SetINSTALLFOLDER", (string?)canonical.Attribute("After"));
        Assert.Equal("both", (string?)canonical.Attribute("Sequence"));
        Assert.Equal("NOT Installed AND WIX_UPGRADE_DETECTED AND RDRIVE_PREVIOUS_INSTALL_ROOT AND RDRIVE_MIGRATE_INSTALL = \"1\" AND ProgramFiles64Folder", (string?)canonical.Attribute("Condition"));
        var repair = Assert.Single(document.Descendants(Wix + "SetProperty"), item =>
            (string?)item.Attribute("Action") == "RestoreInstalledFolder");
        Assert.Equal("SetCanonicalInstallFolder", (string?)repair.Attribute("After"));
        var source = Assert.Single(document.Descendants(Wix + "SetProperty"), item =>
            (string?)item.Attribute("Id") == "RDRIVE_MIGRATED_FROM");
        Assert.Equal("RestoreInstalledFolder", (string?)source.Attribute("After"));
        Assert.Equal("both", (string?)source.Attribute("Sequence"));
        Assert.Equal("[RDRIVE_PREVIOUS_INSTALL_ROOT]", (string?)source.Attribute("Value"));
        Assert.Equal("WIX_UPGRADE_DETECTED AND RDRIVE_PREVIOUS_INSTALL_ROOT AND INSTALLFOLDER AND RDRIVE_PREVIOUS_INSTALL_ROOT ~<> INSTALLFOLDER", (string?)source.Attribute("Condition"));
        // The chain is anchored before launch validation, hence before costing.
        var anchor = Assert.Single(document.Descendants(Wix + "SetProperty"), item =>
            (string?)item.Attribute("Id") == "RDRIVE_PREVIOUS_INSTALL_ROOT");
        Assert.Equal("LaunchConditions", (string?)anchor.Attribute("Before"));
        // A later upgrade at the same target leaves the existing legacy source
        // loaded by AppSearch; it must not replace that source with the current path.
        var existing = Assert.Single(document.Descendants(Wix + "RegistrySearch"), item =>
            (string?)item.Attribute("Id") == "PreviousInstallMigration");
        Assert.Equal("LegacyInstallLocation", (string?)existing.Attribute("Name"));
        Assert.Equal("HKLM", (string?)existing.Attribute("Root"));
        Assert.Equal("always64", (string?)existing.Attribute("Bitness"));
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
    public void LocalDumpsRestoreOwnedPolicyAcrossEarlyMajorUpgradesAndRespectExternalPolicy()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
        var component = Assert.Single(document.Descendants(Wix + "Component"), item => (string?)item.Attribute("Id") == "ResoDriveLocalDumps");
        Assert.Equal("always64", (string?)component.Attribute("Bitness"));
        // CostFinalize runs before RemoveExistingProducts. NeverOverwrite would
        // skip the new component while the old policy still exists, then the
        // old product would remove that policy without a new registry write.
        Assert.Null(component.Attribute("NeverOverwrite"));
        Assert.Equal("4FD2C558-1556-4B77-A6A3-BB16B2CCF592", (string?)component.Attribute("Guid"));
        var upgrade = Assert.Single(document.Descendants(Wix + "MajorUpgrade"));
        Assert.Equal("afterInstallInitialize", (string?)upgrade.Attribute("Schedule"));
        Assert.Null(component.Attribute("Permanent"));
        Assert.Equal("RDRIVE_WER_OWNED = \"#1\" OR NOT (RDRIVE_WER_FOLDER OR RDRIVE_WER_COUNT OR RDRIVE_WER_TYPE OR RDRIVE_WER_FLAGS)", (string?)component.Attribute("Condition"));
        var values = component.Elements(Wix + "RegistryValue").ToArray();
        var folder = Assert.Single(values, item => (string?)item.Attribute("Name") == "DumpFolder");
        Assert.Equal("expandable", (string?)folder.Attribute("Type"));
        Assert.Equal("[\\%]LOCALAPPDATA[\\%]\\rdrive-diagnostics\\dumps", (string?)folder.Attribute("Value"));
        Assert.Equal("yes", (string?)folder.Attribute("KeyPath"));
        Assert.Equal("3", (string?)Assert.Single(values, item => (string?)item.Attribute("Name") == "DumpCount").Attribute("Value"));
        Assert.Equal("2", (string?)Assert.Single(values, item => (string?)item.Attribute("Name") == "DumpType").Attribute("Value"));
        Assert.All(values.Where(item => (string?)item.Attribute("Name") is "DumpCount" or "DumpType" or "LocalDumpsOwned"), item =>
            Assert.Equal("integer", (string?)item.Attribute("Type")));
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
