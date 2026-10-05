using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Validation;
using ResoDrive.Windows;
using WpfMessageBox = ResoDrive.App.ModernMessageBox;
using WpfWindow = System.Windows.Window;

namespace ResoDrive.App;

#pragma warning disable CA1001 // WPF owns the dialog lifetime; Closed cancels and disposes loading.
public partial class MountEditorWindow : WpfWindow
#pragma warning restore CA1001
{
    private static readonly TimeSpan DriveInventoryTimeout = TimeSpan.FromSeconds(5);

    private readonly MountSettings? _existing;
    private readonly char? _currentDrive;
    private readonly string _remoteName;
    private readonly ApplicationPaths _paths;
    private readonly HashSet<char> _reservedDriveLetters;
    private readonly CancellationTokenSource _loadingCancellation = new();
    private Task? _preparation;
    private bool _closed;
    private bool _ready;
    private bool _loadingInteraction;

    public MountEditorWindow(MountSettings? existing, string remoteName, ApplicationPaths paths,
        IEnumerable<char>? reservedDriveLetters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
        ArgumentNullException.ThrowIfNull(paths);
        InitializeComponent();
        WindowAppearance.PrepareDialog(this);
        _existing = existing;
        _currentDrive = existing?.Target.DriveLetter;
        _remoteName = remoteName.Trim().TrimEnd(':');
        _paths = paths;
        _reservedDriveLetters = (reservedDriveLetters ?? []).Select(char.ToUpperInvariant).ToHashSet();

        DriveBox.IsEnabled = false;
        SaveButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        EditorScrollViewer.IsEnabled = false;
        Loaded += Editor_Loaded;
        PreviewKeyDown += (_, _) => { if (!_ready) _loadingInteraction = true; };
        PreviewMouseDown += (_, _) => { if (!_ready) _loadingInteraction = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _loadingCancellation.Cancel();
            _loadingCancellation.Dispose();
        };
        OptionsEditor.LoadArguments(existing?.Arguments ?? [], newMount: existing is null);
        DeleteButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = existing is null ? "Add drive" : "Edit drive";
        ConnectionText.Text = $"Storage connection: {_remoteName}";
        ServerAddressBox.Text = "Loading…";
        if (_currentDrive is char current)
        {
            DriveBox.Items.Add(current);
            DriveBox.SelectedItem = current;
        }

        if (existing is null)
        {
            EnabledBox.IsChecked = true;
            RestartBox.IsChecked = true;
            AttemptsBox.Text = "Unlimited";
        }
        else
        {
            NameBox.Text = existing.DisplayName;
            RemotePathBox.Text = existing.RemotePath;
            AutoMountBox.IsChecked =
                existing.AutoMount.Equals("OnApplicationStart", StringComparison.OrdinalIgnoreCase);
            EnabledBox.IsChecked = existing.Enabled;
            RestartBox.IsChecked = existing.Restart.Enabled;
            AttemptsBox.Text = existing.Restart.MaximumAttempts == 0 ? "Unlimited" :
                existing.Restart.MaximumAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
            NetworkModeBox.IsChecked = RcloneMountOptions.HasOption(existing.Arguments, "--network-mode");
        }

        UpdateRestartControls();
    }

    public MountSettings? Value { get; private set; }
    public bool DeleteRequested { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || DriveBox.SelectedItem is not char drive)
        {
            WpfMessageBox.Show(
                this,
                "Enter a name and choose a free drive letter.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var path = RemotePathUtility.Normalize(RemotePathBox.Text);
        if (!RemotePathUtility.IsWellFormed(path))
        {
            WpfMessageBox.Show(
                this,
                "Folder paths may start with one forward slash, but cannot contain backslashes, repeated slashes, or dot traversal segments.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var reconnect = RestartBox.IsChecked == true;
        var attempts = _existing?.Restart.MaximumAttempts ?? 0;
        var attemptsText = AttemptsBox.Text.Trim();
        if (reconnect && attemptsText.Equals("Unlimited", StringComparison.OrdinalIgnoreCase)) attempts = 0;
        else if (reconnect && (!int.TryParse(attemptsText, out attempts) || attempts is < 0 or > 100))
        {
            WpfMessageBox.Show(
                this,
                "Enter 1 to 100 reconnect attempts, or Unlimited to keep trying.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!OptionsEditor.TryGetArguments(NetworkModeBox.IsChecked == true, out var arguments, out var argumentError))
        {
            WpfMessageBox.Show(
                this,
                argumentError ?? "Check the mount options.",
                "Invalid advanced options",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Value = new MountSettings
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            DisplayName = NameBox.Text.Trim(),
            RemoteName = _remoteName,
            ConnectionHost = _existing?.ConnectionHost,
            ConnectionType = _existing?.ConnectionType,
            RemotePath = path,
            Target = new MountTargetSettings { Kind = "drive", DriveLetter = drive },
            Enabled = EnabledBox.IsChecked == true,
            AutoMount = AutoMountBox.IsChecked == true ? "OnApplicationStart" : "Never",
            Restart = (_existing?.Restart ?? new RestartSettings()) with
            {
                Enabled = reconnect,
                MaximumAttempts = attempts,
            },
            Arguments = arguments,
            SyncJobs = _existing?.SyncJobs ?? [],
        };
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (WpfMessageBox.Confirm(
                this,
                "Delete this drive? Remote data will not be changed.",
                Title,
                "Delete drive"))
        {
            DeleteRequested = true;
            DialogResult = true;
        }
    }

    private void Restart_Changed(object sender, RoutedEventArgs e) => UpdateRestartControls();

    private void UpdateRestartControls()
    {
        if (AttemptsBox is not null)
        {
            AttemptsBox.IsEnabled = RestartBox.IsChecked == true;
        }
    }

    private async void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await PrepareAsync();
            if (!_closed && _ready && !_loadingInteraction) NameBox.Focus();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            if (_closed) return;
            UiDiagnosticLog.Current.Exception("drive-settings.load", exception);
            SetLoadingStatus("Settings could not be loaded. Close and try again.");
            LoadingText.Foreground = StatusPalette.Warning;
        }
    }

    // Start after the first render. Apply detection once, before editing becomes
    // available, and ignore completion after Cancel/Close.
    public Task PrepareAsync(CancellationToken cancellationToken = default) =>
        _preparation ??= PrepareCoreAsync(cancellationToken);

    private async Task PrepareCoreAsync(CancellationToken cancellationToken)
    {
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _loadingCancellation.Token);
        await Dispatcher.Yield(DispatcherPriority.Background);
        loading.Token.ThrowIfCancellationRequested();
        var inventoryTask = ReadDriveInventoryAsync(loading.Token);
        var addressTask = ReadServerAddressAsync(loading.Token);
        await Task.WhenAll(inventoryTask, addressTask);
        loading.Token.ThrowIfCancellationRequested();
        var inventory = await inventoryTask;
        var address = await addressTask;
        ServerAddressBox.Text = address ?? (string.IsNullOrWhiteSpace(_existing?.ConnectionHost)
            ? "Unavailable" : _existing.ConnectionHost);
        ServerAddressBox.ToolTip = $"{ServerAddressBox.Text}\n" +
            (address is null ? "Full server address unavailable." : "Managed by the storage connection.");

        string message;
        if (inventory is not null)
        {
            PopulateDrives(inventory, _currentDrive);
            message = DriveBox.Items.Count == 0 ? "No free drive letters are available." : string.Empty;
        }
        else
        {
            DriveBox.ToolTip = "Windows drive letters could not be checked.";
            DriveBox.IsEnabled = false; // Keep the current letter; do not offer unverified alternatives.
            message = _currentDrive.HasValue
                ? "Drive letters unavailable. Keeping the current letter."
                : "Drive letters unavailable. Close and try again.";
        }
        _ready = true;
        EditorScrollViewer.IsEnabled = true;
        DeleteButton.IsEnabled = true;
        SaveButton.IsEnabled = DriveBox.Items.Count > 0;
        SetLoadingStatus(message);
    }

    private void SetLoadingStatus(string message)
    {
        LoadingText.Text = message;
        LoadingText.Visibility = message.Length == 0 ? Visibility.Hidden : Visibility.Visible;
        if (message.Length > 0)
        {
            var peer = UIElementAutomationPeer.FromElement(LoadingText) ??
                UIElementAutomationPeer.CreatePeerForElement(LoadingText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        else
        {
            var peer = UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent, "Settings ready.", "drive-settings-loading");
        }
    }

    private async Task<string?> ReadServerAddressAsync(CancellationToken cancellationToken)
    {
        var metadata = await RcloneConnectionMetadataService.ReadAsync(
            new RcloneRuntimeLocator(_paths).ExecutablePath, _paths, _remoteName, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return metadata.Succeeded && metadata.Value is not null &&
            metadata.Value.TryGetValue(_remoteName, out var connection) ? connection.Address : null;
    }

    private static async Task<HashSet<char>?> ReadDriveInventoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var occupied = await Task.Run(GetOccupiedDriveLetters, cancellationToken)
                .WaitAsync(DriveInventoryTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return occupied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or TimeoutException or
                System.Security.SecurityException)
        {
            return null;
        }
    }

    private static HashSet<char> GetOccupiedDriveLetters() => DriveInfo.GetDrives()
        .Select(drive => char.ToUpperInvariant(drive.Name[0]))
        .ToHashSet();

    private void PopulateDrives(HashSet<char> occupied, char? currentDrive)
    {
        DriveBox.Items.Clear();
        foreach (var letter in Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => (char)value))
        {
            if ((!occupied.Contains(letter) && !_reservedDriveLetters.Contains(letter)) || letter == currentDrive)
            {
                DriveBox.Items.Add(letter);
            }
        }

        if (DriveBox.Items.Count == 0)
        {
            DriveBox.ToolTip = "No free drive letters are available.";
            DriveBox.IsEnabled = false;
            return;
        }

        if (currentDrive is char current && DriveBox.Items.Contains(current))
        {
            DriveBox.SelectedItem = current;
        }
        DriveBox.ToolTip = null;
        DriveBox.IsEnabled = true;
    }

}
