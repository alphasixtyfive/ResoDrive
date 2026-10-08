using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;
using MediaBrush = System.Windows.Media.Brush;

namespace ResoDrive.App;

internal static class HostStatusPresentation
{
    public static bool HasUsableMountStatus(HostResponse response) =>
        response.Succeeded && response.InitializationErrorCode is null;

    public static bool HasUsableSyncStatus(HostResponse response) =>
        HasUsableMountStatus(response) && response.SyncJobs is not null;
}

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
    public HostMountStatus? UploadStatus { get; private set; }
    private MountLifecycle _lifecycle = MountLifecycle.Stopped;
    private string _status = "Not mounted";
    private string _errorDetail = string.Empty;
    private bool _hasStatus;
    private bool _hasRecognizedStatus;
    private bool _hostUnavailable;
    private string _statusLine = string.Empty;
    private MediaBrush _statusLineBrush = StatusPalette.Muted;

    public MountRow(MountSettings settings, HostMountStatus? status, bool hostUnavailable = false)
    {
        Settings = settings;
        ApplyStatus(status);
        if (hostUnavailable)
            MarkHostUnavailable();
    }

    public MountSettings Settings { get; }
    public Guid Id => Settings.Id;
    public string Name => Settings.DisplayName;
    public string Source => $"{Settings.RemoteName}:{Settings.RemotePath}";
    public bool Enabled => Settings.Enabled;
    public char Drive => Settings.Target.DriveLetter ?? '?';
    public string DriveDisplay => $"{Drive}:";
    public string ConnectionHostDisplay => Settings.ConnectionHost?.Trim() ?? string.Empty;
    public string UploadActivityText
    {
        get
        {
            if (UploadStatus?.UploadRecoveryRequired == true) return "Cache recovery required";
            if (!ShouldStop) return HasPendingUploads ? UploadAttentionText : string.Empty;
            var activity = UploadPresentation.Activity(UploadStatus);
            if (UploadStatus?.UploadStatusChecking == true || UploadStatus?.UploadStatusStale == true)
                return UploadPresentation.Errors(UploadStatus) > 0
                    ? JoinStatus(UploadAttentionText, activity) : activity;
            return activity.Length > 0 ? "↑ " + activity : HasPendingUploads ? "Uploads pending" : string.Empty;
        }
    }
    public bool HasPendingUploads => UploadPresentation.HasPending(UploadStatus);
    public bool UploadNeedsAttention => HasPendingUploads || IsTransient ||
        (ShouldStop && (UploadStatus is null || UploadStatus.UploadStatusStale));
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
            : $"{DriveDisplay}  {ConnectionTypeDisplay}"
        : string.IsNullOrWhiteSpace(Settings.ConnectionType)
            ? $"{DriveDisplay}  {ConnectionHostDisplay}"
            : $"{DriveDisplay}  {ConnectionHostDisplay}  ·  {ConnectionTypeDisplay}";
    public bool IsMounted => _lifecycle == MountLifecycle.Mounted;
    public bool IsTransient =>
        (Enabled && IsAutomaticMount && !_hasStatus && !_hostUnavailable) ||
        _lifecycle is MountLifecycle.Starting or MountLifecycle.Stopping or MountLifecycle.WaitingToRestart;
    public bool NeedsHostRecovery => Enabled && !_hasStatus && _hostUnavailable;
    public bool ShouldStop =>
        IsMounted || _lifecycle is MountLifecycle.Starting or MountLifecycle.Degraded or MountLifecycle.WaitingToRestart;
    public string StatusText => _status;
    public string StatusLine => _statusLine;
    public MediaBrush StatusLineBrush => _statusLineBrush;
    public string StatusToolTip => _lifecycle == MountLifecycle.Degraded && StatusText != StatusLine
        ? $"{StatusLine}\n{StatusText}" : StatusLine;
    public string ErrorDetail => _errorDetail;
    public System.Windows.Visibility ErrorVisibility =>
        _lifecycle == MountLifecycle.Failed && _errorDetail.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility StatusVisibility =>
        _statusLine.Length > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public MediaBrush StatusBrush =>
        _lifecycle == MountLifecycle.Failed ? StatusPalette.Error
            : HostOutageAffectsDrive ? StatusPalette.Warning : !_hasStatus && Enabled
            ? StatusPalette.Info
            : _lifecycle switch
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
            : !_hasStatus
                ? _hostUnavailable ? "Retry" : IsAutomaticMount ? "Starting…" : "Waiting…"
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
            : !_hasStatus
                ? _hostUnavailable ? "\uE72C" : "\uE71A" // Refresh or pending
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
        NeedsHostRecovery ||
        (_hasStatus && _hasRecognizedStatus && (Enabled || ShouldStop) &&
            _lifecycle is not MountLifecycle.Starting and not MountLifecycle.Stopping);
    public string DetailText => $"{(StatusLine.Length > 0 ? StatusLine : StatusText)}  ·  {LocationDisplay}  ·  {Source}";
    public string OptionsAccessibleName => $"Open settings for {Name}";
    public string OpenAccessibleName => $"Open {Name} ({DriveDisplay}) in File Explorer";
    public string ActionAccessibleName => $"{ActionText} {Name}";
    private bool IsAutomaticMount => Settings.AutoMount.Equals(
        "OnApplicationStart", StringComparison.OrdinalIgnoreCase);

    public void ApplyStatus(HostMountStatus? status)
    {
        var uploadChanged = (UploadStatus is null ? null : UploadStatus with { RemoteWipeStatus = null }) !=
            (status is null ? null : status with { RemoteWipeStatus = null });
        UploadStatus = status;
        var hadStatus = _hasStatus;
        var hostWasUnavailable = _hostUnavailable;
        _hasStatus = status is not null;
        _hostUnavailable = false;
        var recognized = Enum.TryParse(status?.Lifecycle, true, out MountLifecycle lifecycle) && Enum.IsDefined(lifecycle);
        _hasRecognizedStatus = recognized;
        var nextLifecycle = recognized ? lifecycle : MountLifecycle.Stopped;
        var previousLifecycle = _lifecycle;
        _lifecycle = nextLifecycle;
        var detail = status is null ? string.Empty : RcloneErrorMessage.Clean(status.Status);
        var nextStatus =
            !Enabled && !ShouldStop
                ? "Disabled. Enable it in drive settings."
                : status is null
                    ? IsAutomaticMount ? "Preparing automatic mount" : "Waiting for background host"
                    : recognized
                        ? nextLifecycle == MountLifecycle.Failed ? "Mount failed" : detail
                        : "Unknown mount state";
        var nextErrorDetail = nextLifecycle == MountLifecycle.Failed ? detail : string.Empty;
        if (!uploadChanged && hadStatus == _hasStatus && hostWasUnavailable == _hostUnavailable &&
            previousLifecycle == nextLifecycle && _status == nextStatus &&
            _errorDetail == nextErrorDetail)
            return;
        _status = nextStatus;
        _errorDetail = nextErrorDetail;
        ChangedState();
    }

    public void MarkHostUnavailable()
    {
        if (_hostUnavailable) return;
        _hostUnavailable = true;
        if (_hasStatus)
        {
            if (UploadStatus is not null) UploadStatus = UploadStatus with { UploadStatusStale = true };
        }
        else _status = "Background host unavailable";
        ChangedState();
    }

    private void ChangedState()
    {
        (_statusLine, _statusLineBrush) = PresentStatusLine();
        Changed(nameof(StatusText));
        Changed(nameof(StatusLine));
        Changed(nameof(StatusLineBrush));
        Changed(nameof(StatusToolTip));
        Changed(nameof(UploadActivityText));
        Changed(nameof(UploadStatus));
        Changed(nameof(HasPendingUploads));
        Changed(nameof(UploadNeedsAttention));
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
        Changed(nameof(NeedsHostRecovery));
        Changed(nameof(ActionAccessibleName));
    }

    private string UploadAttentionText => UploadStatus?.UploadRecoveryRequired == true
        ? "Cache recovery required"
        : UploadPresentation.Errors(UploadStatus) is > 0 and var errors
            ? UploadPresentation.ErrorCount(errors)
            : HasPendingUploads ? "Uploads pending" : string.Empty;

    private bool HostOutageAffectsDrive => _hostUnavailable &&
        (ShouldStop || IsTransient || NeedsHostRecovery || HasPendingUploads ||
         _lifecycle == MountLifecycle.Failed || (_hasStatus && !_hasRecognizedStatus));

    private (string Text, MediaBrush Brush) PresentStatusLine()
    {
        if (_hostUnavailable)
        {
            if (_lifecycle == MountLifecycle.Failed)
                return (JoinStatus(JoinStatus("Mount failed", "Background host unavailable"), UploadAttentionText), StatusPalette.Error);
            if (_hasStatus && !_hasRecognizedStatus)
                return (JoinStatus(JoinStatus("Unknown mount state", "Background host unavailable"), UploadAttentionText), StatusPalette.Warning);
            return HostOutageAffectsDrive
                ? (JoinStatus("Background host unavailable", UploadAttentionText), StatusPalette.Warning)
                : (string.Empty, StatusPalette.Muted);
        }
        if (!_hasStatus)
            return (string.Empty, StatusPalette.Muted);
        if (!_hasRecognizedStatus)
            return (JoinStatus("Unknown mount state", UploadAttentionText), StatusPalette.Warning);
        if (_lifecycle == MountLifecycle.Degraded)
        {
            var reason = StatusText.Split('·', 2)[0].Trim();
            if (reason.Contains("Checking", StringComparison.OrdinalIgnoreCase))
                reason = HasPendingUploads ? UploadAttentionText : "Uploads could not be verified";
            else if (reason.Length == 0 || reason == "Recovered drive")
                reason = "Drive is not ready";
            var warning = UploadStatus?.UploadRecoveryRequired == true || UploadPresentation.Errors(UploadStatus) > 0
                ? UploadAttentionText : string.Empty;
            return (JoinStatus(reason, warning), StatusPalette.Warning);
        }
        if (_lifecycle is MountLifecycle.Failed or MountLifecycle.WaitingToRestart)
            return (JoinStatus(_lifecycle == MountLifecycle.Failed ? "Mount failed" : StatusText, UploadAttentionText),
                _lifecycle == MountLifecycle.Failed ? StatusPalette.Error : StatusPalette.Warning);
        if (_lifecycle is MountLifecycle.Starting or MountLifecycle.Stopping)
            return (JoinStatus(StatusText.Length > 0 ? StatusText : ActionText, UploadAttentionText),
                UploadStatus?.UploadRecoveryRequired == true || UploadPresentation.Errors(UploadStatus) > 0
                    ? StatusPalette.Warning : StatusPalette.Info);

        var needsAttention = !ShouldStop || UploadStatus?.UploadRecoveryRequired == true ||
            UploadPresentation.Errors(UploadStatus) > 0 || UploadStatus?.UploadStatusStale == true;
        return (UploadActivityText, needsAttention ? StatusPalette.Warning : StatusPalette.Info);
    }

    private static string JoinStatus(string primary, string secondary) =>
        secondary.Length == 0 || primary == secondary ? primary
            : primary.Length == 0 ? secondary : $"{primary} · {secondary}";
}

public sealed class SyncRow : NotifyBase
{
    private readonly SyncMode? _mode;
    private bool _statusUnavailable;
    private HostSyncStatus? _transferStatus;
    private SyncLifecycle _lifecycle = SyncLifecycle.Idle;
    private string _statusPrimary = string.Empty;
    private string _statusSecondary = string.Empty;

    public SyncRow(MountSettings mount, SyncJobSettings settings, HostSyncStatus? status)
    {
        MountId = mount.Id;
        Settings = settings;
        MountName = mount.DisplayName;
        RemoteName = mount.RemoteName;
        _mode = Enum.TryParse<SyncMode>(settings.Mode, true, out var mode) && mode.IsSupported()
            ? mode
            : null;
        ApplyStatus(status);
    }

    public Guid MountId { get; }
    public SyncJobSettings Settings { get; }
    public Guid Id => Settings.Id;
    public string MountName { get; }
    public string RemoteName { get; }
    public string Name => Settings.DisplayName;
    public bool Enabled => Settings.Enabled;
    public string Route
    {
        get
        {
            var remote = RemotePathUtility.Display(RemoteName, Settings.RemotePath);
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
    public HostSyncStatus? TransferStatus => _transferStatus;
    public bool StatusUnavailable => _statusUnavailable;
    public bool CanAct => _mode is not null && (IsBusy || (Enabled && !_statusUnavailable));
    public string ActionText => IsBusy ? "Stop" : _statusUnavailable ? "Waiting…" : Enabled ? "Run" : "Disabled";
    public string ActionGlyph => IsBusy ? "\uE71A" : _statusUnavailable ? "\uE895" : Enabled ? "\uE768" : "\uE711";
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

    public void MarkStatusUnavailable()
    {
        if (_statusUnavailable) return;
        _statusUnavailable = true;
        _statusPrimary = "Status unavailable";
        _statusSecondary = string.Empty;
        Changed(string.Empty);
    }

    public void ApplyStatus(HostSyncStatus? status)
    {
        // Keep the raw counters current even when their formatted text has not changed.
        Set(ref _transferStatus, status, nameof(TransferStatus));
        var wasUnavailable = _statusUnavailable;
        _statusUnavailable = false;
        if (wasUnavailable) Changed(nameof(StatusUnavailable));
        var recognized = Enum.TryParse(status?.Lifecycle, true, out SyncLifecycle lifecycle);
        var nextLifecycle = recognized ? lifecycle : SyncLifecycle.Idle;
        var presentation = SyncStatusPresentation.Create(
            _mode,
            Enabled,
            nextLifecycle is SyncLifecycle.Queued or SyncLifecycle.Running,
            nextLifecycle,
            status,
            recognized);
        if (!wasUnavailable && _lifecycle == nextLifecycle &&
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

public enum LogSeverity { Information, Success, Warning, Error }

public sealed record LogRow(
    string Title,
    string Detail,
    DateTimeOffset OccurredAt,
    LogSeverity Severity
)
{
    public string Time => OccurredAt.LocalDateTime.ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture);
    public string FullTime => OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    public string AccessibleName => $"{FullTime} · {Severity} · {Title} · {Detail}";
    public MediaBrush Brush => Severity switch
    {
        LogSeverity.Error => StatusPalette.Error,
        LogSeverity.Warning => StatusPalette.Warning,
        LogSeverity.Success => StatusPalette.Success,
        _ => StatusPalette.Muted
    };
    public string SeverityGlyph => Severity switch
    {
        LogSeverity.Error => "\uE783",
        LogSeverity.Warning => "\uE7BA",
        LogSeverity.Success => "\uE73E",
        _ => "\uE946"
    };
}

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
