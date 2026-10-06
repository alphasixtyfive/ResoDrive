namespace ResoDrive.Windows.Tests;

public sealed class InstalledApplicationLocatorTests
{
    private static readonly string InstalledRoot = Path.GetFullPath(@"C:\Program Files\ResoDrive");
    private static InstalledApplicationRegistration Registration => new(
        "{4A06E48C-562B-49DF-92B9-C0751C885FAD}", "ResoDrive", "Alexey Ivanov", "0.3.31", InstalledRoot);

    [InstalledMsiFixtureFact]
    [Trait("Category", "InstalledMsiIntegration")]
    public void NativeWindowsInstallerCatalogFindsExactIsolatedInstalledApplication()
    {
        var expected = Environment.GetEnvironmentVariable("RDRIVE_TEST_INSTALLED_MSI_PATH");
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        Assert.Equal("github-hosted", Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"));
        var version = Environment.GetEnvironmentVariable("RDRIVE_TEST_INSTALLED_MSI_VERSION");
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(Path.GetFullPath(expected!), InstalledApplicationLocator.ResolveExecutablePath(version),
            ignoreCase: true);
    }

    [Fact]
    public void MissingRegistrationDoesNotGuessFromExistingFolders()
    {
        Assert.Null(InstalledApplicationLocator.ResolveExecutablePath(new Catalog([]), static (_, _) =>
            throw new InvalidOperationException("No unregistered path may be inspected.")));
    }

    [Fact]
    public void RegisteredLocationAndExpectedVersionDetermineTheExecutable()
    {
        var resolved = InstalledApplicationLocator.ResolveExecutablePath(new Catalog([Registration]),
            (path, version) => path == Path.Combine(InstalledRoot, "resodrive.exe") && version == new Version("0.3.31"), "0.3.31");
        Assert.Equal(Path.Combine(InstalledRoot, "resodrive.exe"), resolved);
    }

    [Fact]
    public void LegacyComponentRegistrationSuppliesTheActualOldOrCustomLocation()
    {
        var oldRoot = Path.GetFullPath(@"D:\Company Applications\rdrive");
        var product = Registration with { InstallDirectory = null, LegacyComponentPath = Path.Combine(oldRoot, "profiles.sample.json") };
        Assert.Equal(Path.Combine(oldRoot, "resodrive.exe"), InstalledApplicationLocator.ResolveExecutablePath(
            new Catalog([product]), static (_, _) => true));
    }

    [Fact]
    public void CurrentRegistrationWinsOverAnOldComponentPath()
    {
        var product = Registration with { LegacyComponentPath = @"C:\Program Files\rdrive\profiles.sample.json" };
        Assert.Equal(Path.Combine(InstalledRoot, "resodrive.exe"), InstalledApplicationLocator.ResolveExecutablePath(
            new Catalog([product]), static (_, _) => true));
    }

    [Fact]
    public void AmbiguousProductsFailClosedInsteadOfPickingTheNewest()
    {
        Assert.Throws<InvalidOperationException>(() => InstalledApplicationLocator.ResolveExecutablePath(
            new Catalog([Registration, Registration with { Version = "0.3.30" }]), static (_, _) => true));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("publisher")]
    [InlineData("product-code")]
    [InlineData("version")]
    [InlineData("location")]
    [InlineData("relative-location")]
    [InlineData("network-location")]
    [InlineData("wrong-component")]
    public void InvalidProductIdentityOrLocationFailsClosed(string field)
    {
        var product = field switch
        {
            "name" => Registration with { ProductName = "Other application" },
            "publisher" => Registration with { Publisher = "Other publisher" },
            "product-code" => Registration with { ProductCode = "not-a-product-code" },
            "version" => Registration with { Version = "0.3+secret" },
            "location" => Registration with { InstallDirectory = null },
            "relative-location" => Registration with { InstallDirectory = "rdrive" },
            "network-location" => Registration with { InstallDirectory = @"\\server\share\ResoDrive" },
            "wrong-component" => Registration with { InstallDirectory = null, LegacyComponentPath = @"C:\Elsewhere\arbitrary.exe" },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidOperationException>(() => InstalledApplicationLocator.ResolveExecutablePath(
            new Catalog([product]), static (_, _) => throw new InvalidOperationException("Do not inspect invalid registrations.")));
    }

    [Fact]
    public void WrongRequestedVersionAndExecutableIdentityCannotConfirmUpdate()
    {
        Assert.Throws<InvalidOperationException>(() => InstalledApplicationLocator.ResolveExecutablePath(
            new Catalog([Registration]), static (_, _) => true, "0.3.32"));
        Assert.Throws<InvalidOperationException>(() => InstalledApplicationLocator.ResolveExecutablePath(
            new Catalog([Registration]), static (_, _) => false, "0.3.31"));
    }

    private sealed class InstalledMsiFixtureFactAttribute : FactAttribute
    {
        public InstalledMsiFixtureFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RDRIVE_TEST_INSTALLED_MSI_PATH")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RDRIVE_TEST_INSTALLED_MSI_VERSION")))
                Skip = "Requires disposable installed MSI acceptance.";
        }
    }

    private sealed class Catalog(IReadOnlyList<InstalledApplicationRegistration> products) : IInstalledApplicationCatalog
    {
        public IReadOnlyList<InstalledApplicationRegistration> ReadMachineInstallations() => products;
    }
}
