using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace ResoDrive.App;

#pragma warning disable CA1001 // WPF calls OnExit, where all process coordination handles are disposed.
public partial class App : System.Windows.Application
#pragma warning restore CA1001
{
    private static readonly string[] AccessibilityBrushKeys =
    [
        "WindowBrush", "NavBrush", "CardBrush", "ControlBrush", "ControlHoverBrush",
        "ControlPressedBrush", "PopupBrush", "BorderBrush", "BorderHoverBrush", "TextBrush",
        "MutedBrush", "DisabledTextBrush", "AccentBrush", "AccentHoverBrush", "AccentTextBrush", "FocusBrush",
        "DangerBrush", "DangerBorderBrush", "DangerHoverBrush", "DangerPressedBrush",
        "DangerTextBrush", "BadgeBrush", "BadgeBorderBrush", "BadgeTextBrush",
        "SftpBadgeBrush", "SftpBadgeBorderBrush", "SftpBadgeTextBrush", "WarningBrush"
    ];
    private readonly Dictionary<string, System.Windows.Media.Color> _defaultPalette = [];
    private static readonly string InstanceScope = CreateInstanceScope(AppContext.BaseDirectory);
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMinutes(2);
    private SingleInstanceActivation? _activation;
    private readonly ActivationRequestState _activationState = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<
        SingleInstanceActivation.ActivationRequest> _pendingShowRequests = new();

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        RegisterGlobalExceptionLogging();
        var explicitShow = e.Args.Any(argument =>
            argument.Equals("--show", StringComparison.OrdinalIgnoreCase));
        var startInBackground = !explicitShow &&
            e.Args.Any(ResoDrive.Windows.AutostartCommand.IsBackgroundArgument);
        UiDiagnosticLog.Current.Information(
            "startup.begin",
            startInBackground ? "mode=background" : explicitShow ? "mode=show" : "mode=foreground");
        _activation = new SingleInstanceActivation(InstanceScope);
        if (!_activation.IsFirstInstance)
        {
            UiDiagnosticLog.Current.Information("activation.secondary_instance");
            var activated = startInBackground || _activation.RequestShow(ActivationTimeout);
            UiDiagnosticLog.Current.Information(
                activated ? "activation.request_acknowledged" : "activation.request_timed_out");
            _activation.Dispose();
            _activation = null;
            Shutdown(activated ? 0 : 2);
            return;
        }

        _activation.Listen(ShowRequested);
        UiDiagnosticLog.Current.Information("activation.listener_ready");

        var wipeState = new ResoDrive.Windows.RemoteWipeStateStore(new ResoDrive.Windows.ApplicationPaths()).Read();
        if (wipeState is { Phase: not ResoDrive.Windows.RemoteWipePhase.Completed })
        {
            Program.TryStartHostEarly();
            if (!startInBackground)
                System.Windows.MessageBox.Show(
                    "Nextcloud requested removal of local ResoDrive data. Cleanup is continuing in the background. " +
                    "Close documents opened from ResoDrive and keep this computer online. Reopen ResoDrive after cleanup completes.",
                    "ResoDrive remote wipe", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Let the host load settings and queue automatic mounts while WPF builds the
        // main window and inspects optional components.
        Program.TryStartHostEarly();
        UiDiagnosticLog.Current.Information("startup.host_requested");

        base.OnStartup(e);
        ApplyAccessibilityPalette();
        System.Windows.SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        UiDiagnosticLog.Current.Information("startup.wpf_ready");
        ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        var mainWindow = new MainWindow(startInBackground);
        mainWindow.StartupReady += (_, _) =>
        {
            UiDiagnosticLog.Current.Information("startup.ready");
            ProcessPendingShowRequest();
        };
        MainWindow = mainWindow;
        if (startInBackground)
        {
            mainWindow.ShowActivated = false;
            mainWindow.ShowInTaskbar = false;
            mainWindow.WindowState = System.Windows.WindowState.Minimized;
            mainWindow.Loaded += (_, _) => CompleteBackgroundStartup(mainWindow);
            mainWindow.Show();
        }
        else
        {
            mainWindow.Show();
        }
        ProcessPendingShowRequest();
        UiDiagnosticLog.Current.Information("startup.window_shown");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        UiDiagnosticLog.Current.Information("shutdown", $"exitCode={e.ApplicationExitCode}");
        _activation?.Dispose();
        _activation = null;
        System.Windows.SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        UnregisterGlobalExceptionLogging();
        base.OnExit(e);
    }

    private void ShowRequested(SingleInstanceActivation.ActivationRequest request)
    {
        UiDiagnosticLog.Current.Information("activation.request_received");
        _activationState.RequestShow();
        _pendingShowRequests.Enqueue(request);
        try
        {
            Dispatcher.BeginInvoke(ProcessPendingShowRequest);
        }
        catch (InvalidOperationException)
        {
            UiDiagnosticLog.Current.Information("activation.dispatcher_unavailable");
            // The requesting process will observe the missing acknowledgement.
        }
    }

    private void CompleteBackgroundStartup(MainWindow mainWindow)
    {
        if (_activationState.ShouldHideForBackgroundStartup)
        {
            mainWindow.Hide();
            return;
        }

        ProcessPendingShowRequest();
    }

    private void ProcessPendingShowRequest()
    {
        if (_pendingShowRequests.IsEmpty)
            return;

        var mainWindow = MainWindow as MainWindow;
        var dispatcherLive = mainWindow is not null &&
            !mainWindow.Dispatcher.HasShutdownStarted &&
            !mainWindow.Dispatcher.HasShutdownFinished;
        if (mainWindow is null || !mainWindow.IsLoaded || !mainWindow.IsStartupReady ||
            mainWindow.IsClosing || !dispatcherLive)
            return;

        bool visible;
        try
        {
            visible = mainWindow.RestoreFromExternalRequest();
        }
        catch (InvalidOperationException)
        {
            // A closing window cannot satisfy the activation request. The caller
            // receives a non-zero exit code when acknowledgement times out.
            return;
        }
        if (!_activationState.CanAcknowledge(
                windowAvailable: true,
                loaded: mainWindow.IsLoaded,
                startupReady: mainWindow.IsStartupReady,
                closing: mainWindow.IsClosing,
                dispatcherLive: dispatcherLive,
                windowVisible: visible))
            return;

        while (_pendingShowRequests.TryDequeue(out var request))
        {
            request.Acknowledge();
            _activationState.CompleteRequest();
            UiDiagnosticLog.Current.Information("activation.request_completed");
        }
    }

    private void RegisterGlobalExceptionLogging()
    {
        DispatcherUnhandledException += Application_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private void UnregisterGlobalExceptionLogging()
    {
        DispatcherUnhandledException -= Application_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
    }

    private static void Application_DispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) =>
        UiDiagnosticLog.Current.Exception("exception.dispatcher", e.Exception);

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ??
            new InvalidOperationException("An unknown unhandled exception occurred.");
        UiDiagnosticLog.Current.Exception("exception.app_domain", exception);
    }

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        UiDiagnosticLog.Current.Exception("exception.unobserved_task", e.Exception);
        e.SetObserved();
    }

    private void SystemParameters_StaticPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(System.Windows.SystemParameters.HighContrast))
            ApplyAccessibilityPalette();
    }

    private void ApplyAccessibilityPalette()
    {
        foreach (var key in AccessibilityBrushKeys)
        {
            if (!_defaultPalette.ContainsKey(key) &&
                TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
                _defaultPalette[key] = brush.Color;
        }
        if (!System.Windows.SystemParameters.HighContrast)
        {
            foreach (var pair in _defaultPalette)
                SetPaletteColor(pair.Key, pair.Value);
            return;
        }

        var window = System.Windows.SystemColors.WindowBrush;
        var control = System.Windows.SystemColors.ControlBrush;
        var border = System.Windows.SystemColors.ActiveBorderBrush;
        var text = System.Windows.SystemColors.WindowTextBrush;
        var muted = System.Windows.SystemColors.GrayTextBrush;
        var highlight = System.Windows.SystemColors.HighlightBrush;
        var highlightText = System.Windows.SystemColors.HighlightTextBrush;
        foreach (var key in new[] { "WindowBrush", "NavBrush", "CardBrush", "PopupBrush" })
            SetPaletteColor(key, window.Color);
        foreach (var key in new[] { "ControlBrush", "ControlHoverBrush", "ControlPressedBrush" })
            SetPaletteColor(key, control.Color);
        foreach (var key in new[] { "BorderBrush", "BorderHoverBrush" })
            SetPaletteColor(key, border.Color);
        SetPaletteColor("TextBrush", text.Color);
        SetPaletteColor("MutedBrush", muted.Color);
        SetPaletteColor("DisabledTextBrush", muted.Color);
        foreach (var key in new[]
                 {
                     "AccentBrush", "AccentHoverBrush", "FocusBrush", "DangerBrush",
                     "DangerBorderBrush", "DangerHoverBrush", "DangerPressedBrush", "BadgeBrush",
                     "BadgeBorderBrush", "SftpBadgeBrush", "SftpBadgeBorderBrush", "WarningBrush"
                 })
            SetPaletteColor(key, highlight.Color);
        foreach (var key in new[] { "AccentTextBrush", "DangerTextBrush", "BadgeTextBrush", "SftpBadgeTextBrush" })
            SetPaletteColor(key, highlightText.Color);
    }

    private void SetPaletteColor(string key, System.Windows.Media.Color color)
    {
        if (TryFindResource(key) is System.Windows.Media.SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }
        Resources[key] = new System.Windows.Media.SolidColorBrush(color);
    }

    internal static string CreateInstanceScope(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath))
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(directory));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
