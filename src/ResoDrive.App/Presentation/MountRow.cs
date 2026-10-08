using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;
using MediaBrush = System.Windows.Media.Brush;

namespace ResoDrive.App;

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
