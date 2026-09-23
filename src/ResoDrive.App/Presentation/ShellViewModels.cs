using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;
using MediaBrush = System.Windows.Media.Brush;

namespace ResoDrive.App;

public sealed class ShellViewModel : NotifyBase
{
    private readonly HashSet<(Guid JobId, DateTimeOffset CompletedAt)> _loggedSyncRuns = [];
    private readonly Queue<(Guid JobId, DateTimeOffset CompletedAt)> _loggedSyncRunOrder = [];
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
        IReadOnlyList<HostSyncStatus>? syncStatuses = null
    )
    {
        var statusMap = (statuses ?? []).ToDictionary(status => status.MountId);
        var syncMap = (syncStatuses ?? []).ToDictionary(status =>
            (status.MountId, status.SyncJobId)
        );
        Mounts.Clear();
        Jobs.Clear();
        foreach (var mount in settings.Mounts)
        {
            statusMap.TryGetValue(mount.Id, out var status);
            Mounts.Add(new MountRow(mount, status));
            foreach (var job in mount.SyncJobs)
            {
                syncMap.TryGetValue((mount.Id, job.Id), out var syncStatus);
                Jobs.Add(new SyncRow(mount, job, syncStatus));
            }
        }
        foreach (var status in (syncStatuses ?? [])
                     .Where(IsTerminalSyncStatus)
                     .OrderBy(item => item.CompletedAt))
        {
            AddSyncOutcome(status);
        }
        Refresh();
        IsInitialized = true;
    }

    public void ApplyStatus(IReadOnlyList<HostMountStatus>? statuses)
    {
        var map = (statuses ?? []).ToDictionary(status => status.MountId);
        foreach (var mount in Mounts)
        {
            map.TryGetValue(mount.Id, out var status);
            mount.ApplyStatus(status);
        }
        Refresh();
    }

    public void ApplySyncStatus(IReadOnlyList<HostSyncStatus>? statuses)
    {
        var map = (statuses ?? []).ToDictionary(status => (status.MountId, status.SyncJobId));
        foreach (var job in Jobs)
        {
            map.TryGetValue((job.MountId, job.Id), out var status);
            job.ApplyStatus(status);
        }
        foreach (var status in (statuses ?? []).Where(IsTerminalSyncStatus))
        {
            AddSyncOutcome(status);
        }
        Refresh();
    }

    public void AddLogEntry(
        string glyph,
        string title,
        string detail,
        bool error = false,
        DateTimeOffset? occurredAt = null,
        MediaBrush? brush = null)
    {
        Log.Insert(
            0,
            new LogRow(
                glyph,
                title,
                detail,
                DisplayFormatting.Timestamp(occurredAt?.LocalDateTime ?? DateTime.Now),
                brush ?? (error ? StatusPalette.Error : StatusPalette.Success)
            )
        );
        while (Log.Count > 100)
            Log.RemoveAt(Log.Count - 1);
    }

    private static bool IsTerminalSyncStatus(HostSyncStatus status) =>
        status.CompletedAt is not null &&
        Enum.TryParse(status.Lifecycle, true, out SyncLifecycle lifecycle) &&
        lifecycle is SyncLifecycle.Succeeded or SyncLifecycle.Failed or SyncLifecycle.Cancelled;

    private void AddSyncOutcome(HostSyncStatus status)
    {
        if (status.CompletedAt is not { } completedAt ||
            !_loggedSyncRuns.Add((status.SyncJobId, completedAt)))
        {
            return;
        }
        _loggedSyncRunOrder.Enqueue((status.SyncJobId, completedAt));
        while (_loggedSyncRunOrder.Count > 256)
            _loggedSyncRuns.Remove(_loggedSyncRunOrder.Dequeue());

        var job = Jobs.FirstOrDefault(item => item.Id == status.SyncJobId);
        if (job is null || !Enum.TryParse(status.Lifecycle, true, out SyncLifecycle lifecycle))
        {
            return;
        }

        var title = lifecycle switch
        {
            SyncLifecycle.Succeeded => $"{job.Name} completed",
            SyncLifecycle.Failed => $"{job.Name} failed",
            SyncLifecycle.Cancelled => $"{job.Name} cancelled",
            _ => job.Name
        };
        var details = new List<string> { job.MountName };
        if (!string.IsNullOrWhiteSpace(status.Status))
            details.Add(status.Status);
        if (status.TransfersCompleted is > 0)
            details.Add($"{status.TransfersCompleted} file{(status.TransfersCompleted == 1 ? string.Empty : "s")}");
        else if (status.ChecksCompleted is > 0)
            details.Add($"{status.ChecksCompleted} checked");
        if (status.BytesTransferred is > 0)
            details.Add($"{DisplayFormatting.Bytes(status.BytesTransferred.Value)} transferred");
        if (status.Errors is > 0)
            details.Add($"{status.Errors} error{(status.Errors == 1 ? string.Empty : "s")}");

        AddLogEntry(
            job.DirectionGlyph,
            title,
            string.Join(" · ", details),
            error: lifecycle == SyncLifecycle.Failed,
            occurredAt: completedAt,
            brush: lifecycle switch
            {
                SyncLifecycle.Succeeded => StatusPalette.Success,
                SyncLifecycle.Failed => StatusPalette.Error,
                _ => StatusPalette.Muted
            });
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

internal static class StatusPalette
{
    public static MediaBrush Success => Select("#6CCB5F", System.Windows.SystemColors.WindowTextBrush);
    public static MediaBrush Error => Select("#FF7878", System.Windows.SystemColors.WindowTextBrush);
    public static MediaBrush Warning => Select("#FFB946", System.Windows.SystemColors.WindowTextBrush);
    public static MediaBrush Info => Select("#60CDFF", System.Windows.SystemColors.WindowTextBrush);
    public static MediaBrush Muted => Select("#A9A9A9", System.Windows.SystemColors.GrayTextBrush);
    public static MediaBrush Disabled => Select("#919191", System.Windows.SystemColors.GrayTextBrush);

    private static MediaBrush Select(string value, MediaBrush highContrast) =>
        System.Windows.SystemParameters.HighContrast ? highContrast : Create(value);

    private static SolidColorBrush Create(string value)
    {
        var brush = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}

public sealed class MountRow : NotifyBase
{
    private long? _uploadsQueued;
    private long? _uploadsInProgress;
    private bool _uploadStatusStale;
    private MountLifecycle _lifecycle = MountLifecycle.Stopped;
    private string _status = "Not mounted";
    private string _errorDetail = string.Empty;

    public MountRow(MountSettings settings, HostMountStatus? status)
    {
        Settings = settings;
        ApplyStatus(status);
    }

    public MountSettings Settings { get; }
    public Guid Id => Settings.Id;
    public string Name => Settings.DisplayName;
    public string Source => $"{Settings.RemoteName}:{Settings.RemotePath}";
    public bool Enabled => Settings.Enabled;
    public char Drive => Settings.Target.DriveLetter ?? '?';
    public string DriveDisplay => $"{Drive}:";
    public string ConnectionHostDisplay => Settings.ConnectionHost?.Trim() ?? string.Empty;
    public string UploadActivityText => !ShouldStop
        ? string.Empty
        : _uploadStatusStale ? "Upload status unavailable"
        : _uploadsQueued is not > 0 && _uploadsInProgress is not > 0 ? string.Empty
        : "↑ " + string.Join(" · ", new[]
        {
            _uploadsInProgress > 0 ? $"{_uploadsInProgress} uploading" : null,
            _uploadsQueued > 0 ? $"{_uploadsQueued} queued" : null
        }.Where(value => value is not null));
    public string UploadActivitySuffix => UploadActivityText.Length == 0 ? string.Empty
        : (ConnectionHostDisplay.Length > 0 ? "  ·  " : string.Empty) + UploadActivityText;
    public System.Windows.Visibility ConnectionDetailVisibility =>
        ConnectionHostDisplay.Length > 0 || UploadActivityText.Length > 0
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string ConnectionTypeDisplay => Settings.ConnectionType?.Trim().ToLowerInvariant() switch
    {
        "webdav" => "WebDAV",
        "sftp" => "SFTP",
        _ => string.Empty,
    };
    public System.Windows.Visibility ConnectionHostVisibility =>
        ConnectionHostDisplay.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility ConnectionTypeVisibility =>
        ConnectionTypeDisplay.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public string LocationDisplay => string.IsNullOrWhiteSpace(Settings.ConnectionHost)
        ? string.IsNullOrWhiteSpace(Settings.ConnectionType)
            ? DriveDisplay
            : $"{DriveDisplay}  ·  {ConnectionTypeDisplay}"
        : string.IsNullOrWhiteSpace(Settings.ConnectionType)
            ? $"{DriveDisplay}  ·  {ConnectionHostDisplay}"
            : $"{DriveDisplay}  ·  {ConnectionHostDisplay}  ·  {ConnectionTypeDisplay}";
    public bool IsMounted => _lifecycle == MountLifecycle.Mounted;
    public bool IsTransient =>
        _lifecycle is MountLifecycle.Starting or MountLifecycle.Stopping or MountLifecycle.WaitingToRestart;
    public bool ShouldStop =>
        IsMounted || _lifecycle is MountLifecycle.Starting or MountLifecycle.Degraded or MountLifecycle.WaitingToRestart;
    public string StatusText => _status;
    public string ErrorDetail => _errorDetail;
    public System.Windows.Visibility ErrorVisibility =>
        _lifecycle == MountLifecycle.Failed && _errorDetail.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility StatusVisibility =>
        _lifecycle is MountLifecycle.Failed or MountLifecycle.Degraded or MountLifecycle.WaitingToRestart
        || (!Enabled && !ShouldStop)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public MediaBrush StatusBrush =>
        _lifecycle switch
        {
            MountLifecycle.Mounted => StatusPalette.Success,
            MountLifecycle.Starting or MountLifecycle.Stopping => StatusPalette.Info,
            MountLifecycle.WaitingToRestart or MountLifecycle.Degraded => StatusPalette.Warning,
            MountLifecycle.Failed => StatusPalette.Error,
            _ => StatusPalette.Disabled,
        };
    public string ActionText =>
        !Enabled && !ShouldStop
            ? "Disabled"
            : _lifecycle switch
            {
                MountLifecycle.Mounted => "Unmount",
                MountLifecycle.Starting => "Starting…",
                MountLifecycle.Stopping => "Stopping…",
                MountLifecycle.Degraded or MountLifecycle.WaitingToRestart => "Stop",
                MountLifecycle.Failed => "Retry",
                _ => "Mount",
            };
    public string ActionGlyph =>
        !Enabled && !ShouldStop
            ? "\uE711" // Cancel
            : _lifecycle switch
            {
                MountLifecycle.Mounted => "\uE8CD", // DisconnectDrive
                MountLifecycle.Starting or MountLifecycle.Stopping => "\uE71A", // Stop
                MountLifecycle.Degraded or MountLifecycle.WaitingToRestart => "\uE71A",
                MountLifecycle.Failed => "\uE72C", // Refresh
                _ => "\uE8CE", // MapDrive
            };
    public bool CanOpen => IsMounted;
    public bool CanAct =>
        (Enabled || ShouldStop) && _lifecycle is not MountLifecycle.Starting and not MountLifecycle.Stopping;
    public string DetailText => $"{StatusText}  ·  {LocationDisplay}  ·  {Source}";
    public string OptionsAccessibleName => $"Open settings for {Name}";
    public string OpenAccessibleName => $"Open {Name} ({DriveDisplay}) in File Explorer";
    public string ActionAccessibleName => $"{ActionText} {Name}";

    public void ApplyStatus(HostMountStatus? status)
    {
        var recognized = Enum.TryParse(status?.Lifecycle, true, out MountLifecycle lifecycle);
        var nextLifecycle = recognized ? lifecycle : MountLifecycle.Stopped;
        var previousLifecycle = _lifecycle;
        _lifecycle = nextLifecycle;
        var detail = status is null ? string.Empty : RcloneErrorMessage.Clean(status.Status);
        var nextStatus =
            !Enabled && !ShouldStop
                ? "Disabled. Enable it in drive settings."
                : status is null
                    ? "Not mounted"
                    : recognized
                        ? nextLifecycle == MountLifecycle.Failed ? "Mount failed" : detail
                        : "Unknown mount state";
        var nextErrorDetail = nextLifecycle == MountLifecycle.Failed ? detail : string.Empty;
        if (previousLifecycle == nextLifecycle && _status == nextStatus &&
            _errorDetail == nextErrorDetail && _uploadsQueued == status?.UploadsQueued &&
            _uploadsInProgress == status?.UploadsInProgress && _uploadStatusStale == (status?.UploadStatusStale ?? false))
            return;
        _status = nextStatus;
        _errorDetail = nextErrorDetail;
        _uploadsQueued = status?.UploadsQueued;
        _uploadsInProgress = status?.UploadsInProgress;
        _uploadStatusStale = status?.UploadStatusStale ?? false;
        ChangedState();
    }

    private void ChangedState()
    {
        Changed(nameof(StatusText));
        Changed(nameof(UploadActivityText));
        Changed(nameof(UploadActivitySuffix));
        Changed(nameof(ConnectionDetailVisibility));
        Changed(nameof(StatusVisibility));
        Changed(nameof(ErrorDetail));
        Changed(nameof(ErrorVisibility));
        Changed(nameof(DetailText));
        Changed(nameof(StatusBrush));
        Changed(nameof(ActionText));
        Changed(nameof(ActionGlyph));
        Changed(nameof(CanOpen));
        Changed(nameof(CanAct));
        Changed(nameof(IsMounted));
        Changed(nameof(IsTransient));
        Changed(nameof(ActionAccessibleName));
    }
}

public sealed class SyncRow : NotifyBase
{
    private readonly SyncMode? _mode;
    private readonly string _mountRemotePath;
    private SyncLifecycle _lifecycle = SyncLifecycle.Idle;
    private string _statusPrimary = string.Empty;
    private string _statusSecondary = string.Empty;

    public SyncRow(MountSettings mount, SyncJobSettings settings, HostSyncStatus? status)
    {
        MountId = mount.Id;
        Settings = settings;
        MountName = mount.DisplayName;
        _mountRemotePath = mount.RemotePath;
        _mode = Enum.TryParse<SyncMode>(settings.Mode, true, out var mode) && mode.IsSupported()
            ? mode
            : null;
        ApplyStatus(status);
    }

    public Guid MountId { get; }
    public SyncJobSettings Settings { get; }
    public Guid Id => Settings.Id;
    public string MountName { get; }
    public string Name => Settings.DisplayName;
    public bool Enabled => Settings.Enabled;
    public string Route
    {
        get
        {
            var remote = RemotePathUtility.Display(MountName, _mountRemotePath, Settings.RemotePath);
            return _mode?.IsFromRemote() == true
                ? $"{remote}  →  {Settings.LocalPath}"
                : $"{Settings.LocalPath}  →  {remote}";
        }
    }
    public bool IsMirror => _mode?.IsMirror() == true;
    public string ModeLabel =>
        _mode switch
        {
            SyncMode.CopyToRemote => "Copy to remote",
            SyncMode.CopyFromRemote => "Copy from remote",
            SyncMode.SyncToRemote => "Mirror to remote",
            SyncMode.SyncFromRemote => "Mirror from remote",
            _ => "Invalid mode",
        };
    public string DirectionGlyph =>
        _mode switch
        {
            SyncMode.CopyFromRemote => "\uE896",
            SyncMode.CopyToRemote => "\uE898",
            SyncMode.SyncFromRemote or SyncMode.SyncToRemote => "\uE895",
            _ => "\uE783",
        };
    public string Result => _statusPrimary;
    public string StatusPrimary => _statusPrimary;
    public string StatusSecondary => _statusSecondary;
    public string StatusLine => string.Join(
        "  ·  ",
        new[] { _statusPrimary, _statusSecondary }.Where(value => value.Length > 0));
    public System.Windows.Visibility StatusVisibility =>
        StatusLine.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public string DetailText => string.Join(
        " · ",
        new[] { ModeLabel, Route, StatusPrimary, StatusSecondary }.Where(value => value.Length > 0));
    public bool IsRunning => _lifecycle == SyncLifecycle.Running;
    public bool IsBusy => _lifecycle is SyncLifecycle.Queued or SyncLifecycle.Running;
    public bool CanAct => _mode is not null && (Enabled || IsBusy);
    public string ActionText => IsBusy ? "Stop" : Enabled ? "Run" : "Disabled";
    public string ActionGlyph => IsBusy ? "\uE71A" : Enabled ? "\uE768" : "\uE711";
    public MediaBrush ResultBrush =>
        _lifecycle switch
        {
            SyncLifecycle.Succeeded => StatusPalette.Success,
            SyncLifecycle.Failed => StatusPalette.Error,
            SyncLifecycle.Queued or SyncLifecycle.Running => StatusPalette.Info,
            _ => StatusPalette.Muted,
        };
    public string OptionsAccessibleName => $"Open settings for {Name}";
    public string ActionAccessibleName => $"{ActionText} {Name}";

    public void ApplyStatus(HostSyncStatus? status)
    {
        var recognized = Enum.TryParse(status?.Lifecycle, true, out SyncLifecycle lifecycle);
        var nextLifecycle = recognized ? lifecycle : SyncLifecycle.Idle;
        var presentation = SyncStatusPresentation.Create(
            _mode,
            Enabled,
            nextLifecycle is SyncLifecycle.Queued or SyncLifecycle.Running,
            nextLifecycle,
            status,
            recognized);
        if (_lifecycle == nextLifecycle &&
            _statusPrimary == presentation.Primary &&
            _statusSecondary == presentation.Secondary)
        {
            return;
        }
        _lifecycle = nextLifecycle;
        (_statusPrimary, _statusSecondary) = (presentation.Primary, presentation.Secondary);
        Changed(nameof(Result));
        Changed(nameof(StatusPrimary));
        Changed(nameof(StatusSecondary));
        Changed(nameof(StatusLine));
        Changed(nameof(StatusVisibility));
        Changed(nameof(DetailText));
        Changed(nameof(IsRunning));
        Changed(nameof(IsBusy));
        Changed(nameof(CanAct));
        Changed(nameof(ActionText));
        Changed(nameof(ActionGlyph));
        Changed(nameof(ResultBrush));
        Changed(nameof(ActionAccessibleName));
    }
}

public sealed record LogRow(
    string Glyph,
    string Title,
    string Detail,
    string Time,
    MediaBrush Brush
);

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Changed(name);
        return true;
    }

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
