using ResoDrive.Core.Settings;

namespace ResoDrive.App;

/// <summary>Applies editor intent by stable identity without replacing unrelated newer settings.</summary>
internal static class SettingsEdits
{
    internal static ManagerSettings UpdateMount(ManagerSettings current, Guid mountId, MountSettings? edited, bool delete)
    {
        var mount = RequireMount(current, mountId);
        if (!delete && (edited is null || edited.Id != mountId))
            throw new InvalidOperationException("The drive edit is no longer valid. Reopen its settings and try again.");
        return current with
        {
            Mounts = delete
                ? current.Mounts.Where(item => item.Id != mountId).ToArray()
                : current.Mounts.Select(item => item.Id == mountId
                    ? edited! with
                    {
                        RemoteName = mount.RemoteName,
                        ConnectionHost = mount.ConnectionHost,
                        ConnectionType = mount.ConnectionType,
                        SyncJobs = mount.SyncJobs,
                    }
                    : item).ToArray(),
        };
    }

    internal static ManagerSettings AddSyncJob(ManagerSettings current, Guid mountId, SyncJobSettings job)
    {
        var mount = RequireMount(current, mountId);
        if (current.Mounts.SelectMany(item => item.SyncJobs).Any(item => item.Id == job.Id))
            throw new InvalidOperationException("This sync job already exists. Reopen its settings and try again.");
        return ReplaceJobs(current, mount, mount.SyncJobs.Append(job).ToArray());
    }

    internal static ManagerSettings UpdateSyncJob(
        ManagerSettings current, Guid mountId, Guid jobId, SyncJobSettings? edited, bool delete)
    {
        var mount = RequireMount(current, mountId);
        if (!mount.SyncJobs.Any(job => job.Id == jobId))
            throw new InvalidOperationException("This sync job was removed while its settings were open. Reopen the Sync page and try again.");
        if (!delete && (edited is null || edited.Id != jobId))
            throw new InvalidOperationException("The sync job edit is no longer valid. Reopen its settings and try again.");
        var jobs = delete
            ? mount.SyncJobs.Where(job => job.Id != jobId).ToArray()
            : mount.SyncJobs.Select(job => job.Id == jobId ? edited! : job).ToArray();
        return ReplaceJobs(current, mount, jobs);
    }

    private static MountSettings RequireMount(ManagerSettings current, Guid mountId) =>
        current.Mounts.FirstOrDefault(mount => mount.Id == mountId)
        ?? throw new InvalidOperationException("This drive was removed while its settings were open. Reopen the Drives page and try again.");

    private static ManagerSettings ReplaceJobs(ManagerSettings current, MountSettings mount, SyncJobSettings[] jobs) =>
        current with { Mounts = current.Mounts.Select(item => item.Id == mount.Id ? item with { SyncJobs = jobs } : item).ToArray() };
}
