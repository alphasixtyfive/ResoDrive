using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;
using MediaBrush = System.Windows.Media.Brush;

namespace ResoDrive.App;

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
