using System.Diagnostics;
using System.IO;
using System.Windows;
using ResoDrive.Core.Setup;
using ResoDrive.Core.Validation;
using ResoDrive.Windows;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfSelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using WpfMessageBox = ResoDrive.App.ModernMessageBox;
using WpfWindow = System.Windows.Window;

namespace ResoDrive.App;

#pragma warning disable CA1001 // WPF owns the window lifetime; the active source is disposed when the operation completes.
public partial class SetupWindow : WpfWindow
{
    private const string ManualProfileId = "manual";
    private static readonly TimeSpan DriveInventoryTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] ConnectionTypes = ["Nextcloud", "WebDAV", "SFTP"];
    private static readonly string[] AuthenticationMethods = ["Password", "Private key"];
    private readonly ApplicationPaths _paths;
    private readonly bool _firstRun;
    private readonly IReadOnlySet<char> _reservedDriveLetters;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private SetupProfileCatalog? _catalog;
    private string _profileId = string.Empty;
    private bool _prerequisitesReady;
    private bool _running;
    private bool _manual;
    private CancellationTokenSource? _operationCancellation;
    private bool _closeAfterCancellation;

    public SetupWindow(
        ApplicationPaths paths,
        bool firstRun = false,
        IEnumerable<char>? reservedDriveLetters = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _firstRun = firstRun;
        _reservedDriveLetters = (reservedDriveLetters ?? [])
            .Select(char.ToUpperInvariant)
            .ToHashSet();
        InitializeComponent();
        ConnectionTypeBox.ItemsSource = ConnectionTypes;
        AuthenticationBox.ItemsSource = AuthenticationMethods;
        AuthenticationBox.SelectedIndex = 0;
        StartWithWindowsBox.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;
        WindowAppearance.PrepareDialog(this);
        Loaded += SetupWindow_Loaded;
        Closing += SetupWindow_Closing;
        Closed += (_, _) => _lifetimeCancellation.Dispose();
    }

    public ProfileProvisioningResult? Result { get; private set; }

    private async void SetupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Closing the dialog cancels background detection without surfacing an error.
        }
        catch (Exception exception)
        {
            if (!IsLoaded)
                return;

            StatusText.Text = "Storage setup could not be initialized.";
            StatusText.ToolTip = exception.Message;
            _prerequisitesReady = false;
            SetRunning(false);
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        StatusText.Text = "Loading profiles…";
        _catalog = await Task.Run(
            () => AdjacentProfileCatalogLoader.Load(AppContext.BaseDirectory, _paths.ProfilesFile),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLoaded)
            return;

        var catalog = _catalog;
        await PopulateDrivesAsync(cancellationToken);
        var manualChoice = new SetupChoice(
            "Manual",
            "Choose the storage type and enter its connection details.",
            null);
        if (catalog.Profiles.Count == 0)
        {
            ProfilePanel.Visibility = Visibility.Collapsed;
            SetupSubtitle.Text = "Enter the connection details for your storage provider.";
            ApplyChoice(manualChoice);
        }
        else
        {
            var choices = catalog.Profiles
                .Select(profile => new SetupChoice(profile.DisplayName, profile.Description, profile))
                .Append(manualChoice)
                .ToArray();
            ProfileCombo.ItemsSource = choices;
            ProfileCombo.SelectedIndex = 0;
            ProfileSourceText.Text = catalog.Source == ProfileCatalogSource.UserFile
                ? $"Using custom profiles from {catalog.SourcePath}"
                : catalog.Diagnostic ?? string.Empty;
            ProfileSourceText.Visibility = string.IsNullOrWhiteSpace(ProfileSourceText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            ProfileSourceText.ToolTip = catalog.Diagnostic ?? catalog.SourcePath;
        }
        if (DriveBox.Items.Count > 0)
        {
            StatusText.Text = catalog.Profiles.Count == 0
                ? catalog.Diagnostic ?? string.Empty
                : string.Empty;
        }
        try
        {
            var inspected = await WinFspPrerequisiteService.InspectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLoaded)
                return;
            var winFspInstalled = inspected.Succeeded && inspected.Value?.IsInstalled == true;
            var version = inspected.Value?.Version;
            WinFspText.Text = winFspInstalled
                ? $"WinFsp{(string.IsNullOrWhiteSpace(version) ? string.Empty : " " + version)} is installed and mount drives are available."
                : "WinFsp is not detected. Setup can continue, but mounting needs WinFsp.";
            StatusVisuals.Apply(WinFspIcon, winFspInstalled);
            WinFspButton.Visibility = winFspInstalled ? Visibility.Hidden : Visibility.Visible;
            if (!winFspInstalled)
                AutoMountBox.IsChecked = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WinFspText.Text = "WinFsp could not be checked. Setup can continue, but mounting may be unavailable.";
            WinFspText.ToolTip = exception.Message;
            StatusVisuals.Apply(WinFspIcon, success: false);
            WinFspButton.Visibility = Visibility.Visible;
            AutoMountBox.IsChecked = false;
        }
        finally
        {
            _prerequisitesReady = true;
            SetRunning(false);
        }
    }

    private void ProfileSelection_Changed(
        object sender,
        WpfSelectionChangedEventArgs e)
    {
        var selectedProfile = sender is WpfComboBox combo ? combo.SelectedItem : null;
        if (selectedProfile is SetupChoice choice)
        {
            ApplyChoice(choice);
        }
    }

    private void ApplyChoice(SetupChoice choice)
    {
        PasswordBox.Clear();
        _manual = choice.Profile is null;
        if (_manual)
        {
            _profileId = ManualProfileId;
            ConnectionTypeBox.SelectedItem = ConnectionTypes[0];
            ServerBox.Text = string.Empty;
            PortBox.Text = "22";
            HostKeyBox.Text = string.Empty;
            KeyFileBox.Text = string.Empty;
            AuthenticationBox.SelectedIndex = 0;
            UsernameBox.Clear();
            UsernameLabel.Text = "Username";
            UsernameBox.ToolTip = null;
            PasswordBox.ToolTip = null;
            DisplayNameBox.Text = string.Empty;
            RemotePathBox.Text = string.Empty;
            OptionsEditor.LoadArguments([], newMount: true);
            NetworkModeBox.IsChecked = false;
            SetConnectionFieldsEditable(true);
            UpdateConnectionType();
            return;
        }

        var profile = choice.Profile!;
        _profileId = profile.Id;
        switch (profile.Connection)
        {
            case WebDavConnectionDefinition webDav:
                ConnectionTypeBox.SelectedItem = webDav.Vendor == WebDavVendor.Nextcloud
                    ? "Nextcloud"
                    : "WebDAV";
                ServerBox.Text = webDav.BaseUrl.AbsoluteUri;
                break;
            case SftpConnectionDefinition sftp:
                ConnectionTypeBox.SelectedItem = "SFTP";
                ServerBox.Text = sftp.Host;
                PortBox.Text = sftp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                HostKeyBox.Text = sftp.KnownHost;
                AuthenticationBox.SelectedItem = sftp.Authentication == SftpAuthenticationMethod.PrivateKey
                    ? "Private key"
                    : "Password";
                break;
        }
        StartWithWindowsBox.IsChecked = profile.StartWithWindowsByDefault;
        RemotePathBox.Text = profile.DefaultRemotePath;
        NetworkModeBox.IsChecked = RcloneMountOptions.HasOption(profile.MountArguments, "--network-mode");
        OptionsEditor.LoadArguments(profile.MountArguments, newMount: true);
        UpdateConnectionType();
        SetConnectionFieldsEditable(false);

    }

    private void ConnectionType_Changed(object sender, WpfSelectionChangedEventArgs e) =>
        UpdateConnectionType();

    private void UpdateConnectionType()
    {
        var connectionType = ConnectionTypeBox.SelectedItem as string;
        var isSftp = connectionType == "SFTP";
        ServerLabel.Text = connectionType switch
        {
            "Nextcloud" => "Server URL",
            "WebDAV" => "WebDAV URL",
            _ => "Server address",
        };
        ServerBox.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            ServerLabel.Text);
        PortLabel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        PortBox.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        HostKeyLabel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        HostKeyBox.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        AuthenticationLabel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        AuthenticationPanel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        UsernameLabel.Text = "Username";
        UsernameBox.ToolTip = null;
        UpdateAuthentication();
    }

    private void Authentication_Changed(object sender, WpfSelectionChangedEventArgs e) =>
        UpdateAuthentication();

    private void UpdateAuthentication()
    {
        var connectionType = ConnectionTypeBox.SelectedItem as string;
        var usesKey = connectionType == "SFTP" &&
            AuthenticationBox.SelectedItem as string == "Private key";
        KeyFileBox.Visibility = usesKey ? Visibility.Visible : Visibility.Collapsed;
        BrowseKeyButton.Visibility = usesKey ? Visibility.Visible : Visibility.Collapsed;
        PasswordLabel.Text = usesKey
            ? "Key passphrase"
            : connectionType == "Nextcloud" ? "App password" : "Password";
        PasswordBox.ToolTip = usesKey
            ? "Optional passphrase for an encrypted private key"
            : connectionType == "Nextcloud"
                ? "Use a dedicated Nextcloud app password for this ResoDrive client. A server-requested wipe removes all local ResoDrive accounts and cache on this PC."
                : null;
        PasswordBox.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            usesKey ? "Private key passphrase, optional" : PasswordLabel.Text);
    }

    private void SetConnectionFieldsEditable(bool editable)
    {
        ConnectionTypeBox.IsEnabled = editable && !_running;
        ServerBox.IsReadOnly = !editable;
        PortBox.IsReadOnly = !editable;
        HostKeyBox.IsReadOnly = !editable;
        AuthenticationBox.IsEnabled = editable && !_running;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose SFTP private key",
            Filter = "Private key files|id_*;*.pem;*.key|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            KeyFileBox.Text = dialog.FileName;
    }

    private async Task PopulateDrivesAsync(CancellationToken cancellationToken)
    {
        HashSet<char> occupied;
        try
        {
            occupied = await Task.Run(
                    () => DriveInfo.GetDrives()
                        .Select(drive => char.ToUpperInvariant(drive.Name[0]))
                        .ToHashSet(),
                    cancellationToken)
                .WaitAsync(DriveInventoryTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or TimeoutException or
                System.Security.SecurityException)
        {
            StatusText.Text = "Windows drive letters could not be checked.";
            StatusText.ToolTip = exception.Message;
            return;
        }
        occupied.UnionWith(_reservedDriveLetters);
        foreach (var letter in Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => (char)value))
        {
            if (!occupied.Contains(letter))
            {
                DriveBox.Items.Add(letter);
            }
        }

        if (DriveBox.Items.Count == 0)
        {
            StatusText.Text = "No free drive letters are available between D: and Z:.";
            ConnectButton.IsEnabled = false;
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        var usesSftpKey = ConnectionTypeBox.SelectedItem as string == "SFTP" &&
            AuthenticationBox.SelectedItem as string == "Private key";
        if (!OptionsEditor.TryGetArguments(NetworkModeBox.IsChecked == true, out var mountArguments, out var argumentError))
        {
            WpfMessageBox.Show(
                this,
                argumentError ?? "Check the mount options.",
                "Invalid advanced options",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text) ||
            username.Length == 0 || (!usesSftpKey && password.Length == 0) ||
            (usesSftpKey && string.IsNullOrWhiteSpace(KeyFileBox.Text)) ||
            DriveBox.SelectedItem is not char drive)
        {
            WpfMessageBox.Show(
                this,
                "Enter a drive name and connection details, then choose a free drive letter.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetRunning(true);
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var catalog = _catalog ?? throw new InvalidOperationException("Profiles are not loaded.");
            ISetupProfileCatalog provisioningCatalog = catalog;
            if (_manual)
            {
                var manual = CreateManualProfile();
                provisioningCatalog = new SetupProfileCatalog(
                    [manual],
                    ProfileCatalogSource.UserFile);
            }
            var request = new ProfileSetupRequest
            {
                ProfileId = _profileId,
                Username = username,
                DisplayName = DisplayNameBox.Text.Trim(),
                RemotePath = RemotePathBox.Text.Trim(),
                DriveLetter = drive,
                NetworkMode = NetworkModeBox.IsChecked == true,
                AutoMountOnApplicationStart = AutoMountBox.IsChecked == true,
                StartWithWindows = _firstRun && StartWithWindowsBox.IsChecked == true,
                SftpKeyFilePath = usesSftpKey ? KeyFileBox.Text : string.Empty,
                MountArguments = mountArguments
            };
            var progress = new Progress<string>(message => StatusText.Text = message + "…");
            var result = await new ProfileProvisioningService(_paths, provisioningCatalog).ProvisionAsync(
                request,
                password,
                progress,
                _operationCancellation.Token);
            if (!result.Succeeded || result.Value is null)
            {
                var detail = RcloneErrorMessage.Clean(
                    result.Error?.Message,
                    "The connection could not be created.");
                StatusText.Text = "Connection failed";
                ModernMessageBox.Show(
                    this,
                    detail,
                    "Connection failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            Result = result.Value;
            SetRunning(false);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
            StatusText.Text = "Connection setup cancelled.";
        }
        catch (Exception exception)
        {
            StatusText.Text = "Connection failed";
            ModernMessageBox.Show(
                this,
                RcloneErrorMessage.Clean(exception.Message, "The connection could not be created."),
                "Connection failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            password = string.Empty;
            PasswordBox.Clear();
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetRunning(false);
            if (_closeAfterCancellation && IsLoaded)
            {
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_running)
            return;
        _closeAfterCancellation = true;
        StatusText.Text = "Cancelling…";
        _operationCancellation?.Cancel();
    }

    private SetupProfile CreateManualProfile()
    {
        var connectionType = ConnectionTypeBox.SelectedItem as string ?? ConnectionTypes[0];
        SetupConnectionDefinition connection = connectionType switch
        {
            "SFTP" => new SftpConnectionDefinition
            {
                Host = ServerBox.Text.Trim(),
                Port = int.TryParse(
                    PortBox.Text,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var port)
                    ? port
                    : 0,
                KnownHost = HostKeyBox.Text.Trim(),
                Authentication = AuthenticationBox.SelectedItem as string == "Private key"
                    ? SftpAuthenticationMethod.PrivateKey
                    : SftpAuthenticationMethod.Password,
            },
            "WebDAV" => CreateManualWebDav(nextcloud: false),
            _ => CreateManualWebDav(nextcloud: true),
        };
        var displayName = DisplayNameBox.Text.Trim();
        var profile = new SetupProfile
        {
            Id = ManualProfileId,
            DisplayName = "Manual",
            Description = "Manually configured storage connection.",
            RemoteName = CreateRemoteName(displayName, connectionType),
            Connection = connection,
            DefaultRemotePath = string.Empty,
            DefaultDriveLetter = DriveBox.SelectedItem is char drive ? drive : 'U',
            StartWithWindowsByDefault = StartWithWindowsBox.IsChecked == true,
        };
        var validation = SetupProfileValidator.Validate(profile);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Issues[0].Message);
        return profile;
    }

    private WebDavConnectionDefinition CreateManualWebDav(bool nextcloud)
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var entered))
            throw new ArgumentException("Enter a valid HTTPS server address.");
        if (!entered.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(entered.UserInfo) ||
            !string.IsNullOrEmpty(entered.Query) ||
            !string.IsNullOrEmpty(entered.Fragment))
        {
            throw new ArgumentException(
                "Use an HTTPS server address without credentials, a query, or a fragment.");
        }
        if (nextcloud && entered.AbsolutePath != "/")
        {
            throw new ArgumentException(
                "For Nextcloud, enter only the server address, for example https://nextcloud.example.com/.");
        }
        var origin = new Uri(entered.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return new WebDavConnectionDefinition
        {
            BaseUrl = origin,
            PathTemplate = nextcloud
                ? "/remote.php/dav/files/{username}"
                : string.IsNullOrEmpty(entered.AbsolutePath) ? "/" : entered.AbsolutePath,
            Vendor = nextcloud ? WebDavVendor.Nextcloud : WebDavVendor.Other,
        };
    }

    private static string CreateRemoteName(string displayName, string connectionType)
    {
        var source = string.IsNullOrWhiteSpace(displayName) ? connectionType : displayName;
        var cleaned = new string(source
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_' or '.')
            .Take(96)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Storage" : cleaned;
    }

    private void OpenWinFsp_Click(object sender, RoutedEventArgs e)
    {
        OpenWinFspReleases();
    }

    internal static void OpenWinFspReleases()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                WinFspPrerequisiteService.OfficialReleasesUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The caller remains usable if Windows has no browser association.
        }
    }

    private void SetupWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            _closeAfterCancellation = true;
            StatusText.Text = "Cancelling…";
            _operationCancellation?.Cancel();
            return;
        }

        _lifetimeCancellation.Cancel();
    }

    private void SetRunning(bool running)
    {
        _running = running;
        ConnectButton.IsEnabled = !running && _prerequisitesReady && DriveBox.Items.Count > 0;
        CancelButton.IsEnabled = true;
        ProfileCombo.IsEnabled = !running;
        ConnectionTypeBox.IsEnabled = !running && _manual;
        ServerBox.IsEnabled = !running;
        PortBox.IsEnabled = !running;
        HostKeyBox.IsEnabled = !running;
        AuthenticationBox.IsEnabled = !running && _manual;
        KeyFileBox.IsEnabled = !running;
        BrowseKeyButton.IsEnabled = !running;
        UsernameBox.IsEnabled = !running;
        PasswordBox.IsEnabled = !running;
        DisplayNameBox.IsEnabled = !running;
        DriveBox.IsEnabled = !running;
        NetworkModeBox.IsEnabled = !running;
        AdvancedBox.IsEnabled = !running;
        AutoMountBox.IsEnabled = !running;
        StartWithWindowsBox.IsEnabled = !running;
        Cursor = running ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private sealed record SetupChoice(
        string DisplayName,
        string Description,
        SetupProfile? Profile);
}
