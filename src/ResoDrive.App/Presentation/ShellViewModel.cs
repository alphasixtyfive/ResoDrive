using System.Collections.ObjectModel;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App;

public sealed partial class ShellViewModel : NotifyBase
{
    private string _mountSummary = "Loading…";
    private string _jobSummary = "Loading…";
    private bool _isInitialized;
    public ObservableCollection<MountRow> Mounts { get; } = [];
    public ObservableCollection<SyncRow> Jobs { get; } = [];
    public ObservableCollection<LogRow> Log { get; } = [];
    public string MountSummary
    {
        get => _mountSummary;
        private set => Set(ref _mountSummary, value);
    }
    public string JobSummary
    {
        get => _jobSummary;
        private set => Set(ref _jobSummary, value);
    }
    public bool IsInitialized
    {
        get => _isInitialized;
        private set => Set(ref _isInitialized, value);
    }

    public void Load(
        ManagerSettings settings,
        IReadOnlyList<HostMountStatus>? statuses,
        IReadOnlyList<HostSyncStatus>? syncStatuses = null,
        bool hostUnavailable = false,
        bool mountStatusTruncated = false,
        bool syncStatusTruncated = false
    )
    {
        var statusMap = (statuses ?? []).ToDictionary(status => status.MountId);
        var syncMap = (syncStatuses ?? []).ToDictionary(status =>
            (status.MountId, status.SyncJobId)
        );
        Mounts.Clear();
        Jobs.Clear();
        var mountIds = settings.Mounts.Select(mount => mount.Id).ToHashSet();
        foreach (var id in _mountLogObservations.Keys.Where(id => !mountIds.Contains(id)).ToArray())
            _mountLogObservations.Remove(id);
        var jobIds = settings.Mounts.SelectMany(mount => mount.SyncJobs).Select(job => job.Id).ToHashSet();
        foreach (var id in _loggedSyncRuns.Keys.Where(id => !jobIds.Contains(id)).ToArray())
            _loggedSyncRuns.Remove(id);
        foreach (var mount in settings.Mounts)
        {
            statusMap.TryGetValue(mount.Id, out var status);
            Mounts.Add(new MountRow(mount, status, hostUnavailable || (mountStatusTruncated && status is null)));
            ObserveMountStatus(mount, hostUnavailable ? null : status);
            foreach (var job in mount.SyncJobs)
            {
                syncMap.TryGetValue((mount.Id, job.Id), out var syncStatus);
                var row = new SyncRow(mount, job, syncStatus);
                if (hostUnavailable || (syncStatusTruncated && syncStatus is null)) row.MarkStatusUnavailable();
                Jobs.Add(row);
            }
        }
        foreach (var status in (syncStatuses ?? []).Where(IsTerminalSyncStatus))
        {
            AddSyncOutcome(status);
        }
        Refresh();
        IsInitialized = true;
    }

    public void ReorderMounts(IReadOnlyList<Guid> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Count != Mounts.Count || order.Distinct().Count() != order.Count ||
            !order.ToHashSet().SetEquals(Mounts.Select(row => row.Id)))
            throw new ArgumentException("Drive order must include each existing drive exactly once.", nameof(order));
        var mountRows = Mounts.ToDictionary(row => row.Id);
        for (var position = 0; position < order.Count; position++)
        {
            var current = Mounts.IndexOf(mountRows[order[position]]);
            if (current != position)
                Mounts.Move(current, position);
        }
        var positions = order.Select((id, position) => (id, position)).ToDictionary(item => item.id, item => item.position);
        var syncRows = Jobs.OrderBy(row => positions[row.MountId]).ToArray();
        for (var position = 0; position < syncRows.Length; position++)
        {
            var current = Jobs.IndexOf(syncRows[position]);
            if (current != position)
                Jobs.Move(current, position);
        }
    }

    public void ApplyStatus(IReadOnlyList<HostMountStatus>? statuses, bool statusTruncated = false)
    {
        var map = (statuses ?? []).ToDictionary(status => status.MountId);
        foreach (var mount in Mounts)
        {
            map.TryGetValue(mount.Id, out var status);
            if (statusTruncated && status is null) mount.MarkHostUnavailable();
            else mount.ApplyStatus(status);
            ObserveMountStatus(mount.Settings, status);
        }
        Refresh();
    }

    public void ApplyHostUnavailable()
    {
        foreach (var observation in _mountLogObservations.Values)
            _ = observation.Attention.Observe(attention: false, confirmedHealthy: false);
        foreach (var mount in Mounts)
            mount.MarkHostUnavailable();
        foreach (var job in Jobs)
            job.MarkStatusUnavailable();
        Refresh();
    }

    public void ApplySyncStatus(IReadOnlyList<HostSyncStatus>? statuses, bool statusTruncated = false)
    {
        var map = (statuses ?? []).ToDictionary(status => (status.MountId, status.SyncJobId));
        foreach (var job in Jobs)
        {
            map.TryGetValue((job.MountId, job.Id), out var status);
            if (statusTruncated && status is null) job.MarkStatusUnavailable();
            else job.ApplyStatus(status);
        }
        foreach (var status in (statuses ?? []).Where(IsTerminalSyncStatus))
        {
            AddSyncOutcome(status);
        }
        Refresh();
    }

    public void Refresh()
    {
        if (Mounts.Count == 0)
        {
            MountSummary = "No drives configured";
        }
        else
        {
            var transientCount = Mounts.Count(mount => mount.IsTransient);
            MountSummary = $"{Mounts.Count} {(Mounts.Count == 1 ? "drive" : "drives")} · {Mounts.Count(mount => mount.IsMounted)} mounted";
            if (transientCount > 0)
            {
                MountSummary += $" · {transientCount} in progress";
            }
        }
        JobSummary = $"{Jobs.Count(job => job.Enabled)} enabled · {Jobs.Count} total";
    }

}

