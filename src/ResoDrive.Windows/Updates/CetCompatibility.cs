using System.Diagnostics;
using Microsoft.Win32;

namespace ResoDrive.Windows;

/// <summary>Preserves the MSI installation's explicit CET compatibility choice for copied update helpers.</summary>
public static class CetCompatibility
{
    public const string RegistryPath = @"SOFTWARE\ResoDrive\Installation";
    public const string RegistryValueName = "DisableCet";

    public static IDisposable? StartInstalledHelper(ProcessStartInfo startInfo, string installedExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return CetProcessLauncher.Start(startInfo, IsEnabledForInstalledApplication(installedExecutablePath));
    }

    public static bool IsEnabledForInstalledApplication(string executablePath)
    {
        var registered = InstalledApplicationLocator.ResolveExecutablePath();
        if (registered is null || !InstallationDirectories.SamePath(executablePath, registered)) return false;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var installation = machine.OpenSubKey(RegistryPath);
        var value = installation?.GetValue(RegistryValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return IsEnabled(executablePath, registered, value,
            value is null ? RegistryValueKind.String : installation!.GetValueKind(RegistryValueName));
    }

    internal static bool IsEnabled(string executablePath, string? registeredExecutablePath, object? value,
        RegistryValueKind kind = RegistryValueKind.String)
    {
        if (registeredExecutablePath is null ||
            !InstallationDirectories.SamePath(executablePath, registeredExecutablePath)) return false;
        return (kind, value) switch
        {
            (_, null) or (RegistryValueKind.String, "0") => false,
            (RegistryValueKind.String, "1") => true,
            _ => throw new InvalidDataException("The installed ResoDrive CET compatibility setting is invalid. Run ResoDrive Setup to repair it."),
        };
    }
}
