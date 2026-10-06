using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ResoDrive.Windows;

internal sealed record InstalledApplicationRegistration(string ProductCode, string? ProductName,
    string? Publisher, string? Version, string? InstallDirectory, string? LegacyComponentPath = null);

internal interface IInstalledApplicationCatalog
{
    IReadOnlyList<InstalledApplicationRegistration> ReadMachineInstallations();
}

/// <summary>Locates one installed MSI product using its registration, never by probing similarly named folders.</summary>
public static partial class InstalledApplicationLocator
{
    public const string UpgradeCode = "{8D0BD004-119E-4589-B816-7D5A27D94561}";
    internal const string LegacyProfilesComponentCode = "{2E1BB141-3D85-4177-8498-54A0833D2B23}";
    private static readonly string ProductName = typeof(InstalledApplicationLocator).Assembly
        .GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "ResoDrive";
    private static readonly string Publisher = typeof(InstalledApplicationLocator).Assembly
        .GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Alexey Ivanov";

    /// <summary>Returns null only when no installed per-machine product is registered.</summary>
    public static string? ResolveExecutablePath(string? expectedVersion = null) =>
        ResolveExecutablePath(new WindowsInstallerCatalog(), MatchesExecutable, expectedVersion);

    internal static string? ResolveExecutablePath(IInstalledApplicationCatalog catalog,
        Func<string, Version, bool> matchesExecutable, string? expectedVersion = null)
    {
        var products = catalog.ReadMachineInstallations();
        if (products.Count == 0) return null;
        if (products.Count != 1)
            throw new InvalidOperationException("Windows registered more than one ResoDrive installation. Run the latest ResoDrive Setup to repair it.");
        var product = products[0];
        if (!Guid.TryParseExact(product.ProductCode, "B", out _) ||
            product.ProductName != ProductName || product.Publisher != Publisher ||
            !Version.TryParse(product.Version, out var version) || version.Build < 0 || version.Revision > 0 ||
            expectedVersion is not null && !string.Equals(product.Version, expectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed ResoDrive identity could not be verified. Run the latest ResoDrive Setup.");

        var directory = product.InstallDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            // Public legacy packages did not publish ARPINSTALLLOCATION. MSI still
            // records this frozen component's actual path, including custom roots.
            if (product.LegacyComponentPath is { } component &&
                Path.IsPathFullyQualified(component) &&
                Path.GetFileName(component).Equals("profiles.sample.json", StringComparison.OrdinalIgnoreCase))
                directory = Path.GetDirectoryName(component);
        }
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Windows did not record a verifiable local ResoDrive installation folder. Run the latest ResoDrive Setup.");
        var executable = Path.Combine(Path.GetFullPath(directory), "resodrive.exe");
        if (!matchesExecutable(executable, version))
            throw new InvalidOperationException("The registered ResoDrive executable does not match its installed product. Run the latest ResoDrive Setup.");
        return executable;
    }

    private static bool MatchesExecutable(string path, Version expected)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
        for (var directory = Directory.GetParent(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName == ProductName && info.CompanyName == Publisher &&
            info.FileMajorPart == expected.Major && info.FileMinorPart == expected.Minor &&
            info.FileBuildPart == expected.Build && info.FilePrivatePart == Math.Max(expected.Revision, 0);
    }

    private sealed partial class WindowsInstallerCatalog : IInstalledApplicationCatalog
    {
        private const uint MachineContext = 4;
        private const uint MoreData = 234;
        private const uint NoMoreItems = 259;
        private const uint UnknownProduct = 1605;
        private const uint UnknownProperty = 1608;
        private const int MaximumCharacters = 32 * 1024;

        public unsafe IReadOnlyList<InstalledApplicationRegistration> ReadMachineInstallations()
        {
            var products = new List<InstalledApplicationRegistration>();
            var code = stackalloc char[39];
            for (uint index = 0; index < 64; index++)
            {
                var result = MsiEnumRelatedProducts(UpgradeCode, 0, index, code);
                if (result == NoMoreItems) return products;
                if (result != 0) throw new Win32Exception((int)result, "Windows Installer product discovery failed.");
                var productCode = new string(code);
                if (Property(productCode, "State") != "5") continue;
                products.Add(new(productCode, Property(productCode, "InstalledProductName"),
                    Property(productCode, "Publisher"), Property(productCode, "VersionString"),
                    Property(productCode, "InstallLocation"), LegacyPath(productCode)));
            }
            throw new InvalidOperationException("Windows Installer returned too many related ResoDrive products.");
        }

        private static unsafe string? Property(string productCode, string name)
        {
            uint count = 0;
            var result = MsiGetProductInfoEx(productCode, null, MachineContext, name, null, ref count);
            if (result is UnknownProduct or UnknownProperty) return null;
            if (result != 0 && result != MoreData)
                throw new Win32Exception((int)result, "Windows Installer product information could not be read.");
            if (count >= MaximumCharacters)
                throw new InvalidOperationException("Windows Installer product information exceeded its supported size.");
            var buffer = new char[count + 1];
            count++;
            fixed (char* value = buffer)
            {
                result = MsiGetProductInfoEx(productCode, null, MachineContext, name, value, ref count);
                if (result != 0) throw new Win32Exception((int)result, "Windows Installer product information changed during discovery.");
                return new string(value, 0, (int)count);
            }
        }

        private static unsafe string? LegacyPath(string productCode)
        {
            var buffer = new char[MaximumCharacters];
            uint count = MaximumCharacters;
            fixed (char* path = buffer)
            {
                var state = MsiGetComponentPath(productCode, LegacyProfilesComponentCode, path, ref count);
                return state == 3 && count < MaximumCharacters ? new string(path, 0, (int)count) : null;
            }
        }

        [LibraryImport("msi.dll", EntryPoint = "MsiEnumRelatedProductsW", StringMarshalling = StringMarshalling.Utf16)]
        private static unsafe partial uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, char* productCode);
        [LibraryImport("msi.dll", EntryPoint = "MsiGetProductInfoExW", StringMarshalling = StringMarshalling.Utf16)]
        private static unsafe partial uint MsiGetProductInfoEx(string productCode, string? userSid, uint context,
            string property, char* value, ref uint characters);
        [LibraryImport("msi.dll", EntryPoint = "MsiGetComponentPathW", StringMarshalling = StringMarshalling.Utf16)]
        private static unsafe partial int MsiGetComponentPath(string productCode, string componentCode,
            char* path, ref uint characters);
    }
}
