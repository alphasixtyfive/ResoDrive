using System.Reflection;

namespace ResoDrive.Windows;

/// <summary>Keeps a deliberately selected CET compatibility build on its matching update package.</summary>
public static class ApplicationUpdateAssets
{
    public static bool UsesCetCompatibility { get; } = typeof(ApplicationUpdateAssets).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(value => value.Key == "ResoDriveCETCompatibility" && value.Value == "true");

    public static string InstallerFileName(string version, bool? compatibilityMode = null) =>
        $"resodrive-win-x64-{version}{((compatibilityMode ?? UsesCetCompatibility) ? "-compatibility" : string.Empty)}.msi";
}
