using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Validation;
using ResoDrive.Windows;
using WpfMessageBox = ResoDrive.App.ModernMessageBox;
using WpfWindow = System.Windows.Window;

namespace ResoDrive.App;

public partial class SyncEditorWindow : WpfWindow
{
    private sealed record SyncModeOption(SyncMode Value, string Label);

    private static readonly IReadOnlyList<SyncModeOption> Modes =
    [
        new(SyncMode.CopyFromRemote, "Copy remote to local"),
        new(SyncMode.CopyToRemote, "Copy local to remote"),
        new(SyncMode.SyncFromRemote, "Mirror remote to local"),
        new(SyncMode.SyncToRemote, "Mirror local to remote"),
    ];

    private readonly SyncJobSettings? _existing;
    private readonly IReadOnlySet<Guid> _registeredMountIds;
    private readonly Guid _jobId;
    private readonly string _managedLocalPath;
    private string _externalLocalPath = string.Empty;
    private bool _initializing = true;
    private bool _updatingLocalCopyControls;
    private bool _managedCopyActive;
    private bool _managedChoiceChanged;

    public SyncEditorWindow(
        ApplicationPaths paths,
        IReadOnlyList<MountSettings> mounts,
        IReadOnlySet<Guid> registeredMountIds,
        Guid? mountId,
        SyncJobSettings? existing
    )
    {
        ArgumentNullException.ThrowIfNull(paths);
        _existing = existing;
        _registeredMountIds = registeredMountIds;
        _jobId = existing?.Id ?? Guid.NewGuid();
        _managedLocalPath = paths.ManagedSyncFolder(_jobId);
        InitializeComponent();
        WindowAppearance.PrepareDialog(this);
        MountBox.ItemsSource = mounts;
        ModeBox.ItemsSource = Modes;
        ModeBox.DisplayMemberPath = nameof(SyncModeOption.Label);
        MountBox.SelectedItem =
            mounts.FirstOrDefault(mount => mount.Id == mountId)
            ?? (mounts.Count > 0 ? mounts[0] : null);
        DeleteButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = existing is null ? "New sync job" : "Edit sync job";
        if (existing is null)
        {
            ModeBox.SelectedItem = Modes[0];
            EnabledBox.IsChecked = true;
            IntervalBox.Text = "60";
        }
        else
        {
            NameBox.Text = existing.DisplayName;
            RemotePathBox.Text = existing.RemotePath;
            LocalPathBox.Text = existing.LocalPath;
            ModeBox.SelectedItem = Enum.TryParse<SyncMode>(existing.Mode, true, out var existingMode)
                ? Modes.FirstOrDefault(mode => mode.Value == existingMode) ?? Modes[0]
                : Modes[0];
            EnabledBox.IsChecked = existing.Enabled;
            ScheduleBox.IsChecked = existing.Schedule.Enabled;
            RunOnStartBox.IsChecked = existing.Schedule.RunOnApplicationStart;
            IntervalBox.Text = existing.Schedule.IntervalMinutes.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
            ArgumentsBox.Text = RcloneArgumentTextCodec.Format(existing.Arguments);
            MountBox.IsEnabled = false;
        }
        ManagedLocalCopyBox.IsChecked = existing?.ManagedLocalCopy ?? CanManageLocalCopy;
        _initializing = false;
        UpdateLocalCopyControls();
        UpdateMirrorWarning();
        UpdateScheduleControls();
    }

    public MountSettings? SelectedMount => MountBox.SelectedItem as MountSettings;
    public SyncJobSettings? Value { get; private set; }
    public bool DeleteRequested { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMount is null)
        {
            WpfMessageBox.Show(
                this,
                "Choose a drive for this sync job.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        var scheduled = ScheduleBox.IsChecked == true;
        var interval = 60;
        if (scheduled && (!int.TryParse(IntervalBox.Text, out interval) || interval is < 5 or > 1440))
        {
            WpfMessageBox.Show(
                this,
                "The interval must be between 5 and 1440 minutes.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        var selectedMode = ModeBox.SelectedItem as SyncModeOption ?? Modes[0];
        var managedLocalCopy = ManagedLocalCopyBox.IsChecked == true && CanManageLocalCopy;
        if (!managedLocalCopy && IsManagedStoragePath(LocalPathBox.Text.Trim()))
        {
            WpfMessageBox.Show(
                this,
                "Choose a folder outside ResoDrive's managed copies. Files in the managed folder remain included in Nextcloud remote wipe.",
                "Choose a different local folder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        var arguments = RcloneArgumentTextCodec.Parse(ArgumentsBox.Text);
        var job = new SyncJob
        {
            Id = new SyncJobId(_jobId),
            DisplayName = NameBox.Text.Trim(),
            Enabled = EnabledBox.IsChecked == true,
            RemotePath = RemotePathUtility.Normalize(RemotePathBox.Text),
            LocalPath = managedLocalCopy ? _managedLocalPath : LocalPathBox.Text.Trim(),
            ManagedLocalCopy = managedLocalCopy,
            Mode = selectedMode.Value,
            Schedule = new SyncSchedule
            {
                Enabled = scheduled,
                Interval = TimeSpan.FromMinutes(interval),
                RunOnApplicationStart = RunOnStartBox.IsChecked == true,
            },
            Arguments = arguments,
        };
        var validation = new SyncJobValidator().Validate(job);
        if (!validation.IsValid)
        {
            WpfMessageBox.Show(
                this,
                string.Join(
                    Environment.NewLine,
                    validation.Issues.Select(issue => "• " + issue.Message)
                ),
                "Check sync job",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        var mirror = job.Mode.IsMirror();
        if (
            mirror
            && (job.Schedule.Enabled || job.Schedule.RunOnApplicationStart)
            && !WpfMessageBox.Confirm(
                this,
                $"This automatic mirror may delete destination-only files in:\n\n{MirrorDestination(selectedMode)}\n\nEnable automatic runs?",
                "Confirm automatic mirror",
                "Enable automatic runs"
            )
        )
            return;
        Value = new SyncJobSettings
        {
            Id = job.Id.Value,
            DisplayName = job.DisplayName,
            Enabled = job.Enabled,
            RemotePath = job.RemotePath,
            LocalPath = job.LocalPath,
            ManagedLocalCopy = job.ManagedLocalCopy,
            Mode = job.Mode.ToString(),
            Schedule = new SyncScheduleSettings
            {
                Enabled = job.Schedule.Enabled,
                IntervalMinutes = checked((int)job.Schedule.Interval.TotalMinutes),
                RunOnApplicationStart = job.Schedule.RunOnApplicationStart,
            },
            Arguments = job.Arguments.ToArray(),
        };
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (
            WpfMessageBox.Confirm(
                this,
                "Delete this sync job? Local and remote files stay in place. Any managed copies remain included in Nextcloud remote wipe.",
                Title,
                "Delete sync job"
            )
        )
        {
            DeleteRequested = true;
            DialogResult = true;
        }
    }

    private void Mode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        ApplyNewJobLocalCopyDefault();
        UpdateLocalCopyControls();
        UpdateMirrorWarning();
    }

    private void Mount_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        ApplyNewJobLocalCopyDefault();
        UpdateLocalCopyControls();
    }

    private void ManagedLocalCopy_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingLocalCopyControls)
            return;
        _managedChoiceChanged = true;
        UpdateLocalCopyControls();
    }

    private bool IsDownload =>
        (ModeBox.SelectedItem as SyncModeOption)?.Value is SyncMode.CopyFromRemote or SyncMode.SyncFromRemote;

    private bool IsEnrolled => SelectedMount is not null && _registeredMountIds.Contains(SelectedMount.Id);

    private bool CanManageLocalCopy => IsDownload && IsEnrolled;

    private void ApplyNewJobLocalCopyDefault()
    {
        if (_existing is not null || _managedChoiceChanged)
            return;
        _updatingLocalCopyControls = true;
        ManagedLocalCopyBox.IsChecked = CanManageLocalCopy;
        _updatingLocalCopyControls = false;
    }

    private void UpdateLocalCopyControls()
    {
        _updatingLocalCopyControls = true;
        try
        {
            ManagedLocalCopyBox.Visibility = IsDownload ? Visibility.Visible : Visibility.Collapsed;
            ManagedLocalCopyBox.IsEnabled = CanManageLocalCopy;
            if (!CanManageLocalCopy)
                ManagedLocalCopyBox.IsChecked = false;

            var managed = ManagedLocalCopyBox.IsChecked == true;
            if (managed)
            {
                if (!_managedCopyActive && !IsManagedStoragePath(LocalPathBox.Text.Trim()))
                    _externalLocalPath = LocalPathBox.Text;
                LocalPathBox.Text = _managedLocalPath;
            }
            else if (_managedCopyActive || IsManagedStoragePath(LocalPathBox.Text.Trim()))
            {
                LocalPathBox.Text = _externalLocalPath;
            }
            _managedCopyActive = managed;
            LocalPathBox.IsReadOnly = managed;
            BrowseLocalFolderButton.IsEnabled = !managed || Directory.Exists(_managedLocalPath);
            LocalFolderButtonLabel.Text = managed ? "Open" : "Browse…";
            BrowseLocalFolderButton.ToolTip = managed
                ? BrowseLocalFolderButton.IsEnabled
                    ? "Open the managed local folder"
                    : "The managed folder is created when this job first runs."
                : "Choose a local folder";
            BrowseLocalFolderButton.SetValue(
                System.Windows.Automation.AutomationProperties.NameProperty,
                managed ? "Open managed local folder" : "Browse for local folder");

            LocalCopyNotice.Text = managed
                ? "Stored in ResoDrive's folder and included in Nextcloud remote wipe. External copies and upload originals are not covered."
                : IsDownload && !IsEnrolled
                    ? "Reconnect this drive through Nextcloud setup to use managed copies. External folders are not included in remote wipe."
                    : "External folders and upload originals are not included in Nextcloud remote wipe.";
            if (managed && _existing is { ManagedLocalCopy: false })
                LocalCopyNotice.Text += " Existing files stay in the old folder; new downloads use the managed folder.";
            else if (managed && _existing is not null
                && !_existing.LocalPath.Equals(_managedLocalPath, StringComparison.OrdinalIgnoreCase))
                LocalCopyNotice.Text += " Files at the previous location stay there; this job uses the managed folder shown above.";
            else if (!managed && _existing is { ManagedLocalCopy: true })
                LocalCopyNotice.Text += " Choose a different folder. Previous managed copies stay in place and remain covered.";
        }
        finally
        {
            _updatingLocalCopyControls = false;
        }
    }

    private bool IsManagedStoragePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            return false;
        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var managedRoot = Path.GetDirectoryName(_managedLocalPath)!;
            return fullPath.Equals(managedRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(managedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void UpdateMirrorWarning() =>
        MirrorWarning.Visibility =
            (ModeBox.SelectedItem as SyncModeOption)?.Value.IsMirror() == true
                ? Visibility.Visible
                : Visibility.Collapsed;

    private void Schedule_Changed(object sender, RoutedEventArgs e) => UpdateScheduleControls();

    private void BrowseLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_managedCopyActive)
        {
            try
            {
                if (!Directory.Exists(_managedLocalPath))
                    throw new DirectoryNotFoundException("Run this job once to create its managed folder.");
                using var process = Process.Start(new ProcessStartInfo(_managedLocalPath)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                WpfMessageBox.Show(this, exception.Message, "Could not open managed folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateLocalCopyControls();
            }
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose a local folder",
            Multiselect = false,
        };
        var currentPath = LocalPathBox.Text.Trim();
        if (Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = Path.GetFullPath(currentPath);
        }

        if (dialog.ShowDialog(this) == true)
        {
            LocalPathBox.Text = dialog.FolderName;
            LocalPathBox.Focus();
            LocalPathBox.CaretIndex = LocalPathBox.Text.Length;
        }
    }

    private void UpdateScheduleControls()
    {
        if (!IsInitialized)
            return;
        var enabled = ScheduleBox.IsChecked == true;
        IntervalBox.IsEnabled = enabled;
        IntervalLabel.Opacity = enabled ? 1 : 0.6;
    }

    private string MirrorDestination(SyncModeOption mode) =>
        mode.Value == SyncMode.SyncFromRemote
            ? LocalPathBox.Text.Trim()
            : SelectedMount is null
                ? "the selected remote folder"
                : RemotePathUtility.Display(
                    SelectedMount.DisplayName,
                    SelectedMount.RemotePath,
                    RemotePathBox.Text.Trim());
}
