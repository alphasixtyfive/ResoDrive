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
internal sealed class TrayController : IDisposable
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
        Action<Exception>? reportError = null)
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

        _icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _notifyIcon = new Forms.NotifyIcon { Icon = _icon, Text = ProductInfo.Name, Visible = true };
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        _notifyIcon.BalloonTipClicked += NotifyIcon_BalloonTipClicked;
    }

    internal void UpdateStatus(int mountedCount, int runningSyncCount)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => UpdateStatus(mountedCount, runningSyncCount));
            return;
        }
        _notifyIcon.Text = Truncate($"{ProductInfo.Name}: {mountedCount} mounted, {runningSyncCount} syncing", 127);
    }

    internal void ShowMountResult(MountRow mount, TrayActionResult result) =>
        ShowResult(result with { Title = string.IsNullOrWhiteSpace(result.Title) ? mount.Name : result.Title });

    internal void ShowSyncResult(SyncRow sync, TrayActionResult result) =>
        ShowResult(result with { Title = string.IsNullOrWhiteSpace(result.Title) ? sync.Name : result.Title });

    internal void ShowResult(TrayActionResult result)
    {
        if (_disposed || !result.Notify) return;
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => ShowResult(result));
            return;
        }
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
        _notifyIcon.Visible = false;
        _notifyIcon.MouseUp -= NotifyIcon_MouseUp;
        _notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked;
        _notifyIcon.Dispose();
        _icon?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e) => Dispatch(_restoreWindow);

    private void NotifyIcon_MouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) Dispatch(_restoreWindow);
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
        menu.AddSeparator();

        var mounts = _mountProvider();
        foreach (var mount in mounts)
        {
            var submenu = menu.AddSubmenu($"{mount.Name}\t{mount.Drive}:");
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
        menu.Add("Refresh status", () => _ = RunActionAsync(_refresh, ShowResult));
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
}
