using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace ResoDrive.Windows;

/// <summary>Non-identifying client and operating-system versions for storage requests.</summary>
public static class ClientUserAgent
{
    private static readonly Lazy<string> Current = new(Create);

    public static string Value => Current.Value;

    private static string Create()
    {
        var assembly = typeof(ClientUserAgent).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3);
        var osVersion = Environment.OSVersion.Version;
        string? installationType = null, edition = null, release = null;
        int? revision = null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var windows = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            installationType = windows?.GetValue("InstallationType") as string;
            edition = windows?.GetValue("EditionID") as string;
            // A staged Windows upgrade can expose metadata for a different build.
            // Only combine the release/revision with the running OS when they match.
            if (int.TryParse(windows?.GetValue("CurrentBuildNumber") as string, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var build) && build == osVersion.Build)
            {
                release = windows?.GetValue("DisplayVersion") as string ?? windows?.GetValue("ReleaseId") as string;
                if (windows?.GetValue("UBR") is int updateRevision && updateRevision >= 0)
                    revision = updateRevision;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Optional registry details must not prevent setup, requests or wipe recovery.
        }
        return Format(version, osVersion, installationType, edition, release, revision,
            RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);
    }

    internal static string Format(string? applicationVersion, Version osVersion, string? installationType,
        string? edition, string? release, int? updateRevision, Architecture clientArchitecture,
        Architecture osArchitecture, string? rcloneVersion = null)
    {
        var version = SafeValue(applicationVersion?.Split('+', 2)[0], allowSpaces: false) ?? "unknown";
        var platform = installationType switch
        {
            "Client" when osVersion.Major == 10 => osVersion.Build >= 22000 ? "Windows 11" : "Windows 10",
            "Server" or "Server Core" => "Windows Server",
            _ => "Windows"
        };
        List<string> details = [platform];
        if (SafeValue(edition) is { } safeEdition) details.Add($"Edition: {safeEdition}");
        if (SafeValue(release) is { } safeRelease) details.Add($"Release: {safeRelease}");
        var build = osVersion.ToString(3);
        if (updateRevision is >= 0) build += "." + updateRevision.Value.ToString(CultureInfo.InvariantCulture);
        details.Add($"Build: {build}");
        details.Add($"ClientArchitecture: {clientArchitecture.ToString().ToLowerInvariant()}");
        details.Add($"OSArchitecture: {osArchitecture.ToString().ToLowerInvariant()}");
        if (RcloneVersion.IsReportable(rcloneVersion))
            details.Add($"Rclone: {rcloneVersion}");
        return $"ResoDrive/{version} ({string.Join("; ", details)})";
    }

    public static string WithRcloneVersion(string? rcloneVersion)
    {
        return !RcloneVersion.IsReportable(rcloneVersion) || !Value.EndsWith(')')
            ? Value
            : $"{Value[..^1]}; Rclone: {rcloneVersion})";
    }

    private static string? SafeValue(string? value, bool allowSpaces = true)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ||
                allowSpaces && character == ' ')))
            return null;
        return value.Trim();
    }
}
