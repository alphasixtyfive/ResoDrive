using ResoDrive.Core.Results;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Validation;

namespace ResoDrive.Core.Setup;

public sealed record ProfileSetupRequest
{
    public required string ProfileId { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public string RemotePath { get; init; } = string.Empty;
    public char DriveLetter { get; init; } = 'U';
    public bool NetworkMode { get; init; }
    public bool AutoMountOnApplicationStart { get; init; } = true;
    public bool StartWithWindows { get; init; } = true;
    public string SftpKeyFilePath { get; init; } = string.Empty;
    public IReadOnlyList<string>? MountArguments { get; init; }
}

public static class ProfileSetupPlan
{
    public static OperationResult<MountSettings> CreateMount(
        ProfileSetupRequest request,
        ISetupProfileCatalog profileCatalog,
        string remoteName)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profileCatalog);
        var profile = profileCatalog.Find(request.ProfileId);
        if (profile is null)
        {
            return Result.Failure<MountSettings>("setup.profile", "The selected setup profile is not available.");
        }

        try
        {
            SetupProfileValidator.ValidateUsername(request.Username);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<MountSettings>("setup.username", exception.Message);
        }

        if (request.DriveLetter is < 'D' or > 'Z')
        {
            return Result.Failure<MountSettings>("setup.drive", "Drive letters must be between D and Z.");
        }

        var remotePath = RemotePathUtility.Normalize(request.RemotePath);
        var displayName = request.DisplayName.Trim();
        var mountArguments = RcloneMountOptions.ForNewMount(request.MountArguments ?? profile.MountArguments);
        var argumentValidation = RcloneArgumentPolicy.ValidateMount(mountArguments);
        if (!argumentValidation.IsValid)
        {
            var issue = argumentValidation.Issues[0];
            return Result.Failure<MountSettings>(issue.Code, issue.Message);
        }

        var composedArguments = request.NetworkMode && !mountArguments.Any(argument =>
                argument.Equals("--network-mode", StringComparison.OrdinalIgnoreCase))
            ? mountArguments.Append("--network-mode").ToArray()
            : mountArguments.ToArray();
        var mount = new MountSettings
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            RemoteName = remoteName,
            ConnectionHost = ConnectionHost(profile.Connection),
            ConnectionType = ConnectionType(profile.Connection),
            RemotePath = remotePath,
            Target = new MountTargetSettings { Kind = "drive", DriveLetter = request.DriveLetter },
            AutoMount = request.AutoMountOnApplicationStart ? "OnApplicationStart" : "Never",
            Arguments = composedArguments
        };

        var mapped = new ResoDrive.Core.Domain.MountDefinition
        {
            Id = new ResoDrive.Core.Domain.MountId(mount.Id),
            DisplayName = mount.DisplayName,
            RemoteName = mount.RemoteName,
            RemotePath = mount.RemotePath,
            Target = new ResoDrive.Core.Domain.MountTarget.Drive(request.DriveLetter),
            Enabled = true,
            AutoMount = request.AutoMountOnApplicationStart
                ? ResoDrive.Core.Domain.AutoMountPolicy.OnApplicationStart
                : ResoDrive.Core.Domain.AutoMountPolicy.Never,
            Arguments = mount.Arguments,
            Restart = new ResoDrive.Core.Domain.RestartPolicy(),
            SyncJobs = Array.Empty<ResoDrive.Core.Domain.SyncJob>()
        };
        var mountValidation = new MountDefinitionValidator().Validate(mapped);
        if (!mountValidation.IsValid)
        {
            var issue = mountValidation.Issues[0];
            return Result.Failure<MountSettings>(issue.Code, issue.Message);
        }

        return Result.Success(mount);
    }

    private static string ConnectionHost(SetupConnectionDefinition connection) => connection switch
    {
        WebDavConnectionDefinition webDav => webDav.BaseUrl.IdnHost,
        SftpConnectionDefinition sftp => sftp.Host,
        _ => throw new InvalidOperationException("The connection type is not supported.")
    };

    private static string ConnectionType(SetupConnectionDefinition connection) => connection switch
    {
        WebDavConnectionDefinition => "WebDAV",
        SftpConnectionDefinition => "SFTP",
        _ => throw new InvalidOperationException("The connection type is not supported.")
    };
}
