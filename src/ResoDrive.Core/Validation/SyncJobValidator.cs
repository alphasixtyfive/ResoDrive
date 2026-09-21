using ResoDrive.Core.Domain;

namespace ResoDrive.Core.Validation;

public sealed class SyncJobValidator : IValidator<SyncJob>
{
    private static readonly TimeSpan MinimumScheduleInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumScheduleInterval = TimeSpan.FromDays(1);

    public ValidationResult Validate(SyncJob value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var issues = new List<ValidationIssue>();

        ValidationRules.ValidateId(value.Id.Value, "sync.id", "id", issues);
        ValidationRules.ValidateDisplayName(value.DisplayName, "sync", issues);
        ValidationRules.ValidateLocalPath(value.LocalPath, "localPath", issues);
        ValidationRules.ValidateRemotePath(value.RemotePath, "remotePath", issues);

        if (value.ManagedLocalCopy && !value.Mode.IsFromRemote())
            issues.Add(new("sync.managed_direction", "Managed local copies require a remote-to-local operation.", "mode"));

        if (!Enum.IsDefined(value.Mode))
        {
            issues.Add(new("sync.mode", "The sync mode is invalid.", "mode"));
        }
        else if (value.Mode == SyncMode.Bisync)
        {
            issues.Add(new(
                "sync.mode.bisync",
                "Bidirectional sync is not available until its recovery workflow is configured.",
                "mode"));
        }

        if (value.Schedule is null)
        {
            issues.Add(new("sync.schedule.null", "A sync schedule is required.", "schedule"));
        }
        else if (value.Schedule.Enabled &&
                 (value.Schedule.Interval < MinimumScheduleInterval || value.Schedule.Interval > MaximumScheduleInterval))
        {
            issues.Add(new(
                "sync.schedule.interval",
                "Scheduled sync intervals must be between 5 minutes and 24 hours.",
                "schedule.interval"));
        }

        AddArgumentIssues(RcloneArgumentPolicy.ValidateSync(value.Arguments), issues);
        return issues.Count == 0 ? ValidationResult.Valid : new ValidationResult(issues);
    }

    private static void AddArgumentIssues(ValidationResult result, List<ValidationIssue> issues)
    {
        foreach (var issue in result.Issues)
        {
            issues.Add(issue);
        }
    }
}
