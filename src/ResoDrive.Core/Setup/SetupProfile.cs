using ResoDrive.Core.Validation;

namespace ResoDrive.Core.Setup;

public abstract record SetupConnectionDefinition;

public enum WebDavVendor { Nextcloud, Owncloud, SharePoint, Fastmail, Other }

public sealed record WebDavConnectionDefinition : SetupConnectionDefinition
{
    public required Uri BaseUrl { get; init; }
    public required string PathTemplate { get; init; }
    public WebDavVendor Vendor { get; init; } = WebDavVendor.Other;
}

public enum SftpAuthenticationMethod { Password, PrivateKey }

public sealed record SftpConnectionDefinition : SetupConnectionDefinition
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public string KnownHost { get; init; } = string.Empty;
    public SftpAuthenticationMethod Authentication { get; init; } = SftpAuthenticationMethod.Password;
}

public sealed record SetupProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string RemoteName { get; init; }
    public required SetupConnectionDefinition Connection { get; init; }
    public string DefaultRemotePath { get; init; } = string.Empty;
    public char DefaultDriveLetter { get; init; } = 'U';
    public bool StartWithWindowsByDefault { get; init; } = true;
    public IReadOnlyList<string> MountArguments { get; init; } = Array.Empty<string>();

}

public static class SetupProfileValidator
{
    public static ValidationResult Validate(SetupProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = new List<ValidationIssue>();
        ValidateSlug(profile.Id, "profile.id", issues);
        ValidationRules.ValidateRemoteName(profile.RemoteName, issues);
        ValidateDisplayText(profile.DisplayName, 128, "profile.displayName", issues);
        ValidateDisplayText(profile.Description, 512, "profile.description", issues);
        switch (profile.Connection)
        {
            case WebDavConnectionDefinition webDav: ValidateWebDav(webDav, issues); break;
            case SftpConnectionDefinition sftp: ValidateSftp(sftp, issues); break;
            case null: issues.Add(new("profile.connection.null", "A connection definition is required.", "connection")); break;
            default: issues.Add(new("profile.connection.unsupported", "The connection type is not supported.", "connection.type")); break;
        }
        if (profile.DefaultDriveLetter is < 'D' or > 'Z')
            issues.Add(new("profile.drive", "The default drive letter must be between D and Z.", "defaultDriveLetter"));
        ValidationRules.ValidateRemotePath(profile.DefaultRemotePath, "defaultRemotePath", issues);
        foreach (var issue in RcloneArgumentPolicy.ValidateMount(profile.MountArguments).Issues)
            issues.Add(issue with { Field = $"mountArguments.{issue.Field}" });
        return issues.Count == 0 ? ValidationResult.Valid : new ValidationResult(issues);
    }

    public static Uri CreateWebDavUri(SetupProfile profile, string username)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Connection is not WebDavConnectionDefinition webDav)
            throw new ArgumentException("The setup profile is not a WebDAV profile.", nameof(profile));
        if (!Validate(profile).IsValid)
            throw new ArgumentException("The setup profile is invalid.", nameof(profile));
        ValidateUsername(username);
        var path = webDav.PathTemplate.Replace("{username}", Uri.EscapeDataString(username), StringComparison.Ordinal);
        var endpoint = new Uri(webDav.BaseUrl, path);
        if (!endpoint.Scheme.Equals(webDav.BaseUrl.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !endpoint.Host.Equals(webDav.BaseUrl.Host, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Port != webDav.BaseUrl.Port)
            throw new ArgumentException("The WebDAV path resolves outside the configured service.", nameof(profile));
        return endpoint;
    }

    public static void ValidateUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (username.Length > 256 || !username.Equals(username.Trim(), StringComparison.Ordinal) || username.Any(char.IsControl))
            throw new ArgumentException("The username is not valid.", nameof(username));
    }

    public static string ToRcloneVendor(WebDavVendor vendor) => vendor switch
    {
        WebDavVendor.Nextcloud => "nextcloud",
        WebDavVendor.Owncloud => "owncloud",
        WebDavVendor.SharePoint => "sharepoint",
        WebDavVendor.Fastmail => "fastmail",
        WebDavVendor.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(vendor))
    };

    private static void ValidateWebDav(WebDavConnectionDefinition connection, List<ValidationIssue> issues)
    {
        if (!Enum.IsDefined(connection.Vendor))
            issues.Add(new("profile.webdav.vendor", "The WebDAV vendor is not supported.", "connection.vendor"));
        var url = connection.BaseUrl;
        if (url is null || !url.IsAbsoluteUri || !url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) ||
            !string.IsNullOrEmpty(url.Fragment) || url.AbsolutePath != "/")
            issues.Add(new("profile.endpoint", "The WebDAV base URL must be an HTTPS origin without credentials, a query, or a fragment.", "connection.baseUrl"));
        var template = connection.PathTemplate;
        var usernameCount = string.IsNullOrEmpty(template) ? 0 : template.Split("{username}", StringSplitOptions.None).Length - 1;
        var requiresUsername = connection.Vendor is WebDavVendor.Nextcloud or WebDavVendor.Owncloud;
        var unknownPlaceholder = template?.Replace("{username}", string.Empty, StringComparison.Ordinal)
            .IndexOfAny(['{', '}']) >= 0;
        if (string.IsNullOrEmpty(template) || template[0] != '/' || template.StartsWith("//", StringComparison.Ordinal) ||
            template.IndexOfAny(['\r', '\n', '\0', '?', '#', '\\']) >= 0 ||
            template.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..") ||
            unknownPlaceholder || usernameCount > 1 || (requiresUsername && usernameCount != 1))
            issues.Add(new("profile.webDavPath", "The WebDAV path is invalid or has an invalid {username} placeholder.", "connection.pathTemplate"));
    }

    private static void ValidateSftp(SftpConnectionDefinition connection, List<ValidationIssue> issues)
    {
        if (!IsValidSftpHost(connection.Host))
            issues.Add(new("profile.sftp.host", "The SFTP host is not a valid DNS name or IP address.", "connection.host"));
        if (connection.Port is < 1 or > 65_535)
            issues.Add(new("profile.sftp.port", "The SFTP port must be between 1 and 65535.", "connection.port"));
        if (!Enum.IsDefined(connection.Authentication))
            issues.Add(new("profile.sftp.authentication", "The SFTP authentication method is not supported.", "connection.authentication"));
        if (!string.IsNullOrEmpty(connection.KnownHost) && !SftpHostKeyValidator.IsValid(connection.KnownHost))
            issues.Add(new("profile.sftp.knownHost", "The SFTP server host key must contain a supported OpenSSH public key.", "connection.knownHost"));
    }

    public static bool IsValidSftpHost(string? host)
    {
        var hostType = string.IsNullOrWhiteSpace(host) ? UriHostNameType.Unknown : Uri.CheckHostName(host);
        return !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
            host.Equals(host.Trim(), StringComparison.Ordinal) && !host.Any(char.IsControl) &&
            host.IndexOfAny(['/', '\\', '@', '[', ']']) < 0 &&
            (!host.Contains(':', StringComparison.Ordinal) || hostType == UriHostNameType.IPv6) &&
            hostType != UriHostNameType.Unknown;
    }

    private static void ValidateDisplayText(string? value, int maximumLength, string field, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !value.Equals(value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
            issues.Add(new(field, "The display text is empty, too long, or contains control characters.", field));
    }

    private static void ValidateSlug(string value, string field, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !value.Equals(value.Trim(), StringComparison.Ordinal) ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            issues.Add(new(field, "The profile identifier is invalid.", field));
    }
}
