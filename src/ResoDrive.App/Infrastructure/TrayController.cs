using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ResoDrive.App;

internal sealed record TrayActionResult(bool Succeeded, string Title, string Message, bool Notify = true)
{
    public static TrayActionResult Success(string title, string message) => new(true, title, message);
    public static TrayActionResult Failure(string title, string message) => new(false, title, message);
    public static TrayActionResult SilentSuccess() => new(true, string.Empty, string.Empty, false);
}

/// <summary>Owns the notification-area icon and its native Windows context menu.</summary>
/// <remarks>All supplied providers and actions are invoked on the WPF dispatcher.</remarks>
internal sealed partial class TrayController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _exit;
    private readonly System.Drawing.Icon? _icon;
    private readonly Func<IReadOnlyList<MountRow>> _mountProvider;
    private readonly Func<MountRow, Task<TrayActionResult>> _mountAction;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Action<MountRow> _openMount;
    private readonly Func<Task<TrayActionResult>> _refresh;
    private readonly Action<Exception>? _reportError;
    private readonly Action _restoreWindow;
    private readonly Action? _showTransfers;
    private readonly string _productName;
    private Action? _balloonAction;
    private readonly DispatcherTimer _animation;
    private readonly System.Drawing.Icon[] _uploadIcons;
    private readonly System.Drawing.Icon _warningIcon;
    private int _animationFrame;
    private bool _uploadWarning;
    private readonly Func<IReadOnlyList<SyncRow>> _syncProvider;
    private readonly Func<SyncRow, Task<TrayActionResult>> _syncAction;
    private bool _disposed;

    internal TrayController(
        Dispatcher dispatcher,
        Func<IReadOnlyList<MountRow>> mountProvider,
        Func<IReadOnlyList<SyncRow>> syncProvider,
        Func<MountRow, Task<TrayActionResult>> mountAction,
        Func<SyncRow, Task<TrayActionResult>> syncAction,
        Action<MountRow> openMount,
        Func<Task<TrayActionResult>> refresh,
        Action restoreWindow,
        Action exit,
        Action<Exception>? reportError = null,
        Action? showTransfers = null,
        string? productName = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _mountProvider = mountProvider ?? throw new ArgumentNullException(nameof(mountProvider));
        _syncProvider = syncProvider ?? throw new ArgumentNullException(nameof(syncProvider));
        _mountAction = mountAction ?? throw new ArgumentNullException(nameof(mountAction));
        _syncAction = syncAction ?? throw new ArgumentNullException(nameof(syncAction));
        _openMount = openMount ?? throw new ArgumentNullException(nameof(openMount));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _restoreWindow = restoreWindow ?? throw new ArgumentNullException(nameof(restoreWindow));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _reportError = reportError;
        _showTransfers = showTransfers;
        _productName = productName ?? ProductInfo.Name;

        _icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _uploadIcons = [CreateUploadIcon(0), CreateUploadIcon(1)];
        _warningIcon = CreateUploadIcon(0, warning: true);
        _notifyIcon = new Forms.NotifyIcon { Icon = _icon, Text = _productName, Visible = true };
        _animation = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background,
            (_, _) => _notifyIcon.Icon = _uploadIcons[++_animationFrame % _uploadIcons.Length], _dispatcher);
        _animation.Stop();
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        _notifyIcon.BalloonTipClicked += NotifyIcon_BalloonTipClicked;
    }

    internal void UpdateStatus(int mountedCount, int runningSyncCount, long pendingUploads = 0, bool uploadWarning = false)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => UpdateStatus(mountedCount, runningSyncCount, pendingUploads, uploadWarning));
            return;
        }
        var uploading = pendingUploads > 0;
        var transferring = uploading || runningSyncCount > 0;
        var uploadText = uploadWarning ? "uploads need attention"
            : uploading ? $"{pendingUploads} uploads pending" : "no uploads pending";
        _notifyIcon.Text = Truncate($"{_productName}: {mountedCount} mounted, {runningSyncCount} syncing · {uploadText}", 127);
        if (transferring || uploadWarning)
        {
            _notifyIcon.Icon = uploadWarning ? _warningIcon : _uploadIcons[_animationFrame % _uploadIcons.Length];
            if (transferring && !uploadWarning && SystemParameters.ClientAreaAnimation) _animation.Start();
            else _animation.Stop();
        }
        else
        {
            _animation.Stop();
            _notifyIcon.Icon = _icon;
        }
        if (uploadWarning && !_uploadWarning)
            ShowResult(TrayActionResult.Failure("Uploads need attention", "Keep ResoDrive running. Open Transfers to check pending files and connection status."), _showTransfers);
        _uploadWarning = uploadWarning;
    }

    internal void ShowMountResult(MountRow mount, TrayActionResult result) =>
        ShowResult(result with { Title = string.IsNullOrWhiteSpace(result.Title) ? mount.Name : result.Title });

    internal void ShowSyncResult(SyncRow sync, TrayActionResult result) =>
        ShowResult(result with { Title = string.IsNullOrWhiteSpace(result.Title) ? sync.Name : result.Title });

    internal void ShowResult(TrayActionResult result, Action? onClick = null)
    {
        if (_disposed || !result.Notify) return;
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => ShowResult(result, onClick));
            return;
        }
        _balloonAction = onClick ?? _restoreWindow;
        _notifyIcon.ShowBalloonTip(
            result.Succeeded ? 3000 : 5000,
            Truncate(result.Title, 63),
            Truncate(result.Message, 255),
            result.Succeeded ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _animation.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.MouseUp -= NotifyIcon_MouseUp;
        _notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked;
        _notifyIcon.Dispose();
        _icon?.Dispose();
        foreach (var icon in _uploadIcons) icon.Dispose();
        _warningIcon.Dispose();
        GC.SuppressFinalize(this);
    }

    private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e) => Dispatch(_balloonAction ?? _restoreWindow);

    private void NotifyIcon_MouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
            Dispatch(_showTransfers ?? _restoreWindow);
        else if (e.Button == Forms.MouseButtons.Right) Dispatch(ShowMenu);
    }

    private void Dispatch(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    ReportError(exception);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // The dispatcher can finish shutting down between the guard and BeginInvoke.
        }
    }

    private void ShowMenu()
    {
        using var menu = new NativePopupMenu();
        menu.Add(ProductInfo.OpenLabel, _restoreWindow);
        if (_showTransfers is not null) menu.Add("Transfers…", _showTransfers);
        menu.AddSeparator();

        var mounts = _mountProvider();
        foreach (var mount in mounts)
        {
            var submenu = menu.AddSubmenu($"{mount.Name}\t{mount.Drive}:");
            if (mount.UploadActivityText.Length > 0) submenu.Add(Truncate(mount.UploadActivityText, 100), null, false);
            if (mount.CanOpen) submenu.Add("Open", () => _openMount(mount));
            submenu.Add(
                mount.ActionText,
                () => _ = RunActionAsync(() => _mountAction(mount), result => ShowMountResult(mount, result)),
                mount.CanAct);
        }

        var syncJobs = _syncProvider();
        if (mounts.Count > 0 && syncJobs.Count > 0) menu.AddSeparator();
        foreach (var sync in syncJobs)
        {
            menu.Add(
                $"{sync.ActionText} {sync.Name}\t{sync.MountName}",
                () => _ = RunActionAsync(() => _syncAction(sync), result => ShowSyncResult(sync, result)),
                sync.CanAct);
        }

        if (mounts.Count == 0 && syncJobs.Count == 0)
            menu.Add("No drives configured", null, false);

        menu.AddSeparator();
        menu.Add("Refresh status", () => _ = RunActionAsync(_refresh, result => ShowResult(result)));
        menu.Add(ProductInfo.ExitLabel, _exit);

        var owner = System.Windows.Application.Current?.MainWindow is { } window
            ? new WindowInteropHelper(window).Handle
            : IntPtr.Zero;
        menu.Show(owner);
    }

    private async Task RunActionAsync(Func<Task<TrayActionResult>> action, Action<TrayActionResult> notify)
    {
        try
        {
            var result = await action().ConfigureAwait(true);
            if (!_disposed) notify(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ReportError(exception);
            if (!_disposed)
                ShowResult(TrayActionResult.Failure("Action failed", $"The operation could not be completed. Open {ProductInfo.Name} to see details."));
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _reportError?.Invoke(exception);
        }
        catch (Exception reportingException)
        {
            UiDiagnosticLog.Current.Exception("tray.error_reporting_failed", reportingException);
        }
        UiDiagnosticLog.Current.Exception("tray.action_failed", exception);
    }

    private static string Truncate(string value, int maximumLength) => value.Length <= maximumLength
        ? value : string.Concat(value.AsSpan(0, maximumLength - 1), "…");

    private System.Drawing.Icon CreateUploadIcon(int frame, bool warning = false)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        if (_icon is not null) graphics.DrawIcon(_icon, new System.Drawing.Rectangle(0, 0, 32, 32));
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new System.Drawing.SolidBrush(warning
            ? System.Drawing.Color.FromArgb(255, 204, 102) : System.Drawing.Color.FromArgb(0, 103, 192));
        using var arrow = new System.Drawing.Pen(warning ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.White, 2);
        graphics.FillEllipse(background, 14, 14, 18, 18);
        if (warning)
        {
            graphics.DrawLine(arrow, 23, 18, 23, 24);
            graphics.DrawLine(arrow, 23, 26, 23, 28);
        }
        else
        {
            var tip = 18 + frame;
            graphics.DrawLine(arrow, 23, tip, 23, 28);
            graphics.DrawLine(arrow, 19, tip + 4, 23, tip);
            graphics.DrawLine(arrow, 27, tip + 4, 23, tip);
        }
        var handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);
}
