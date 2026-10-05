using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using ResoDrive.Core.Setup;
using ResoDrive.Core.Validation;
using ResoDrive.Windows;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfSelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
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
    private readonly HashSet<string> _reservedDriveNames;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private SetupProfileCatalog? _catalog;
    private string _profileId = string.Empty;
    private bool _prerequisitesReady;
    private bool _running;
    private bool _manual;
    private bool _inputError;
    private FrameworkElement? _invalidInput;
    private CancellationTokenSource? _operationCancellation;
    private bool _closeAfterCancellation;

    public SetupWindow(
        ApplicationPaths paths,
        bool firstRun = false,
        IEnumerable<char>? reservedDriveLetters = null,
        IEnumerable<string>? reservedDriveNames = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _firstRun = firstRun;
        _reservedDriveLetters = (reservedDriveLetters ?? [])
            .Select(char.ToUpperInvariant)
            .ToHashSet();
        _reservedDriveNames = (reservedDriveNames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        ConnectionTypeBox.ItemsSource = ConnectionTypes;
        AuthenticationBox.ItemsSource = AuthenticationMethods;
        AuthenticationBox.SelectedIndex = 0;
        StartWithWindowsBox.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;
        WindowAppearance.PrepareDialog(this);
        SetupScrollViewer.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler(Input_Changed));
        SetupScrollViewer.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new System.Windows.Controls.SelectionChangedEventHandler(Input_Changed));
        SetupScrollViewer.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler(Input_Changed));
        SetupScrollViewer.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler(Input_Changed));
        PasswordBox.PasswordChanged += Input_Changed;
        System.Windows.DataObject.AddPastingHandler(PortBox, Port_Pasting);
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
                ? "Use a dedicated Nextcloud app password for this ResoDrive client. A server-requested wipe removes all local ResoDrive accounts, cache and managed local copies in this data directory. External folders and upload originals are not covered."
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

        if (!TryPrepareSetup(out var provisioningCatalog, out var request)) return;
        var password = PasswordBox.Password;

        SetRunning(true);
        _operationCancellation = new CancellationTokenSource();
        try
        {
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

    internal bool TryPrepareSetup(
        [NotNullWhen(true)] out ISetupProfileCatalog? provisioningCatalog,
        [NotNullWhen(true)] out ProfileSetupRequest? request)
    {
        provisioningCatalog = null;
        request = null;
        if (_catalog is null)
            return ShowInputError(ProfileCombo, "Storage profiles are still loading.");
        if (DriveBox.SelectedItem is not char drive || drive is < 'D' or > 'Z' ||
            !DriveBox.Items.Contains(drive))
            return ShowInputError(DriveBox, "Choose a free drive letter.");
        if (UsernameBox.Text.Any(char.IsControl))
            return ShowInputError(UsernameBox, "The username cannot contain control characters.");
        if (DisplayNameBox.Text.Any(char.IsControl))
            return ShowInputError(DisplayNameBox, "The drive name cannot contain control characters.");
        if (RemotePathBox.Text.Any(char.IsControl))
            return ShowInputError(RemotePathBox, "The folder cannot contain control characters.");

        var catalog = _catalog;
        if (_manual)
        {
            if (ServerBox.Text.Any(char.IsControl))
                return ShowInputError(ServerBox, "The server address cannot contain control characters.");
            if (ConnectionTypeBox.SelectedItem is not string connectionType ||
                !ConnectionTypes.Contains(connectionType, StringComparer.Ordinal))
                return ShowInputError(ConnectionTypeBox, "Choose a storage type.");
            if (connectionType == "SFTP" &&
                (AuthenticationBox.SelectedItem is not string authentication ||
                 !AuthenticationMethods.Contains(authentication, StringComparer.Ordinal)))
                return ShowInputError(AuthenticationBox, "Choose an authentication method.");
            if (connectionType == "SFTP" && !SetupProfileValidator.IsValidSftpHost(ServerBox.Text.Trim()))
                return ShowInputError(ServerBox, "Enter a valid SFTP host name or IP address.");
            if (connectionType == "SFTP" && !TryReadPort(PortBox.Text, out _))
                return ShowInputError(PortBox, "Enter a whole-number port from 1 to 65535.");
            if (connectionType == "SFTP" && HostKeyBox.Text.Any(char.IsControl))
                return ShowInputError(HostKeyBox, "The server host key cannot contain control characters.");
            try
            {
                catalog = new SetupProfileCatalog([CreateManualProfile()], ProfileCatalogSource.UserFile);
            }
            catch (ArgumentException exception)
            {
                var input = connectionType == "SFTP" ? HostKeyBox : ServerBox;
                return ShowInputError(input, exception.Message);
            }
        }
        var profile = catalog.Find(_profileId);
        if (profile is null)
            return ShowInputError(ProfileCombo, "Choose a connection profile.");
        var username = UsernameBox.Text.Trim();
        try { SetupProfileValidator.ValidateUsername(username); }
        catch (ArgumentException)
        {
            return ShowInputError(UsernameBox, "Enter a valid username, up to 256 characters.");
        }
        var usesSftpKey = profile.Connection is SftpConnectionDefinition
            { Authentication: SftpAuthenticationMethod.PrivateKey };
        if (usesSftpKey && string.IsNullOrWhiteSpace(KeyFileBox.Text))
            return ShowInputError(BrowseKeyButton, "Choose a private key file.");
        try
        {
            _ = profile.Connection switch
            {
                SftpConnectionDefinition { Authentication: SftpAuthenticationMethod.PrivateKey } =>
                    ProfileProvisioningService.NormalizeOptionalSecret(PasswordBox.Password),
                WebDavConnectionDefinition { Vendor: WebDavVendor.Nextcloud } =>
                    ProfileProvisioningService.NormalizeAppPassword(PasswordBox.Password),
                _ => ProfileProvisioningService.NormalizeExactPassword(PasswordBox.Password),
            };
        }
        catch (ArgumentException)
        {
            return ShowInputError(PasswordBox, usesSftpKey
                ? "Enter a valid key passphrase, up to 2048 characters."
                : "Enter a valid password, up to 2048 characters.");
        }
        if (!OptionsEditor.TryGetArguments(NetworkModeBox.IsChecked == true, out var mountArguments, out var argumentError))
        {
            AdvancedBox.IsExpanded = true;
            return ShowInputError(OptionsEditor, argumentError ?? "Check the mount options.");
        }
        var candidate = new ProfileSetupRequest
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
            MountArguments = mountArguments,
        };
        var plan = ProfileSetupPlan.CreateMount(candidate, catalog, profile.RemoteName);
        if (!plan.Succeeded)
        {
            var input = plan.Error?.Code.StartsWith("mount.displayName", StringComparison.Ordinal) == true
                ? DisplayNameBox : RemotePathBox;
            return ShowInputError(input, plan.Error?.Message ?? "Check the drive settings.");
        }
        if (_reservedDriveNames.Contains(candidate.DisplayName))
            return ShowInputError(DisplayNameBox, "A drive with this name already exists.");
        ClearInputError();
        provisioningCatalog = catalog;
        request = candidate;
        return true;
    }

    private bool ShowInputError(FrameworkElement input, string message)
    {
        _inputError = true;
        _invalidInput = input;
        SetInputErrorMessage(message);
        input.BringIntoView();
        input.Focus();
        return false;
    }

    private void SetInputErrorMessage(string message)
    {
        var changed = StatusText.Text != message;
        StatusText.Text = message;
        StatusText.Foreground = StatusPalette.Warning;
        if (!changed) return;
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(StatusText) ??
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(StatusText);
        peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        // Expanding/collapsing a section changes its template's toggle button,
        // not the input. Keep actionable feedback visible until input changes.
        if (e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton
            { TemplatedParent: System.Windows.Controls.Expander }) return;
        // Editable combo-box templates can initialize when Advanced first
        // expands. Preserve its error while its actual values remain invalid.
        if (_inputError && _invalidInput == OptionsEditor &&
            !OptionsEditor.TryGetArguments(NetworkModeBox.IsChecked == true, out _, out var error))
        {
            SetInputErrorMessage(error ?? "Check the mount options.");
            return;
        }
        ClearInputError();
    }

    private void ClearInputError()
    {
        if (!_inputError) return;
        _inputError = false;
        _invalidInput = null;
        StatusText.Text = string.Empty;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private static bool TryReadPort(string text, out int port) =>
        NumericInput.TryGetPositiveInteger(text, 65_535, out port);

    private void Port_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e) =>
        e.Handled = !NumericInput.IsAsciiDigits(NumericInput.ReplaceSelection(
            PortBox.Text, PortBox.SelectionStart, PortBox.SelectionLength, e.Text));

    private void Port_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.UnicodeText) ||
            e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) is not string text ||
            !NumericInput.IsAsciiDigits(NumericInput.ReplaceSelection(
                PortBox.Text, PortBox.SelectionStart, PortBox.SelectionLength, text)))
            e.CancelCommand();
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
        var connectionType = ConnectionTypeBox.SelectedItem as string ??
            throw new ArgumentException("Choose a storage type.");
        SetupConnectionDefinition connection = connectionType switch
        {
            "SFTP" => new SftpConnectionDefinition
            {
                Host = ServerBox.Text.Trim(),
                Port = TryReadPort(PortBox.Text, out var port)
                    ? port
                    : throw new ArgumentException("Enter a whole-number port from 1 to 65535."),
                KnownHost = HostKeyBox.Text.Trim(),
                Authentication = AuthenticationBox.SelectedItem as string == "Private key"
                    ? SftpAuthenticationMethod.PrivateKey
                    : SftpAuthenticationMethod.Password,
            },
            "WebDAV" => CreateManualWebDav(nextcloud: false),
            "Nextcloud" => CreateManualWebDav(nextcloud: true),
            _ => throw new ArgumentException("Choose a storage type."),
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
            DefaultDriveLetter = DriveBox.SelectedItem is char drive ? drive :
                throw new ArgumentException("Choose a free drive letter."),
            StartWithWindowsByDefault = StartWithWindowsBox.IsChecked == true,
        };
        var validation = SetupProfileValidator.Validate(profile);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Issues[0].Message);
        return profile;
    }

    private WebDavConnectionDefinition CreateManualWebDav(bool nextcloud)
    {
        var server = ServerBox.Text.Trim();
        if (server.Any(char.IsControl) || server.Contains('\\') ||
            !Uri.TryCreate(server, UriKind.Absolute, out var entered))
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
        RemotePathBox.IsEnabled = !running;
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
