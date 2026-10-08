using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ResoDrive.Windows;

/// <summary>Allowlisted build and runtime metadata, safe to include in a shared report.</summary>
public static partial class DiagnosticIdentity
{
    public static string ApplicationVersion => VersionText(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string VersionText(string? value) =>
        value is { Length: <= 160 } && VersionPattern().IsMatch(value) ? value : "Unknown";

    public static string CommitText(string? applicationVersion)
    {
        var version = VersionText(applicationVersion);
        var separator = version.LastIndexOf('+');
        return separator < 0 ? "Unknown" : version[(separator + 1)..];
    }

    public static string RuntimeText => Environment.Version.ToString();
    public static string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();
    public static string OperatingSystemArchitecture => RuntimeInformation.OSArchitecture.ToString();

    // Only a hexadecimal source identity is admitted after '+'. Arbitrary build metadata
    // can contain paths or user-supplied strings and must never be copied into exports.
    [GeneratedRegex(@"\Av?\d+\.\d+(?:\.\d+){0,2}(?:-[A-Za-z0-9][A-Za-z0-9._-]*)?(?:\+[A-Fa-f0-9]{7,64})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
