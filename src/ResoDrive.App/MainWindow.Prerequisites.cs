using System.Windows;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;

namespace ResoDrive.App;

public partial class MainWindow
{
    private readonly WinFspStatusRefresh _winFspRefresh;

    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (IsStartupReady) await UpdateWinFspStatusAsync(force: true);
    }

    private async Task UpdateWinFspStatusAsync(bool force = false)
    {
        if (IsClosing) return;
        try
        {
            var result = await _winFspRefresh.CheckAsync(force, _lifetimeCancellation.Token);
            if (result is not null && !IsClosing) ApplyWinFspStatus(result);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsClosing) return;
            StatusVisuals.Apply(WinFspStatusIcon, success: false, error: true);
            SetLiveText(WinFspStatusText, "WinFsp could not be checked. Return to Settings to try again.");
            _model.AddLogEntry("WinFsp check failed", exception.Message, LogSeverity.Error);
        }
    }

    private void ApplyWinFspStatus(OperationResult<PrerequisiteStatus> result)
    {
        if (!result.Succeeded)
        {
            StatusVisuals.Apply(WinFspStatusIcon, success: false, error: true);
            SetLiveText(WinFspStatusText, "WinFsp could not be checked. Return to Settings to try again.");
            return;
        }
        var installed = result.Value?.IsInstalled == true;
        StatusVisuals.Apply(WinFspStatusIcon, success: installed);
        WinFspReleasesButton.Visibility = installed ? Visibility.Hidden : Visibility.Visible;
        SetLiveText(WinFspStatusText, installed
            ? string.IsNullOrWhiteSpace(result.Value?.Version) ? "WinFsp is installed" : $"WinFsp {result.Value.Version} is installed"
            : "WinFsp is not installed. Sync works; mounting drives needs WinFsp.");
    }
}
