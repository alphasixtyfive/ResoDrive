using System.Windows;
using System.Windows.Threading;

namespace ResoDrive.App;

#pragma warning disable CA1001 // The Closed handler owns cancellation and timer cleanup.
public partial class SetupWindow
#pragma warning restore CA1001
{
    private readonly WinFspStatusRefresh _winFspRefresh;
    private readonly DispatcherTimer _prerequisiteTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private async Task RefreshWinFspAsync(bool force = false, bool initial = false)
    {
        if (!IsLoaded || _running && !initial) return;
        try
        {
            var result = await _winFspRefresh.CheckAsync(force, _lifetimeCancellation.Token);
            if (result is null || !IsLoaded || _lifetimeCancellation.IsCancellationRequested) return;
            var installed = result.Succeeded && result.Value?.IsInstalled == true;
            var version = result.Value?.Version;
            WinFspText.Text = !result.Succeeded
                ? "WinFsp could not be checked. Setup can continue; mounting may be unavailable."
                : installed
                    ? $"WinFsp{(string.IsNullOrWhiteSpace(version) ? string.Empty : " " + version)} is installed."
                    : "WinFsp is not installed. Setup can continue; mounting needs WinFsp.";
            WinFspText.ToolTip = null;
            StatusVisuals.Apply(WinFspIcon, installed);
            WinFspButton.Visibility = installed ? Visibility.Hidden : Visibility.Visible;
            // Refreshing an external installation must never reset an editing choice.
            if (initial && !installed) AutoMountBox.IsChecked = false;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsLoaded) return;
            WinFspText.Text = "WinFsp could not be checked. Setup can continue; mounting may be unavailable.";
            WinFspText.ToolTip = exception.Message;
            StatusVisuals.Apply(WinFspIcon, success: false);
            WinFspButton.Visibility = Visibility.Visible;
            if (initial) AutoMountBox.IsChecked = false;
        }
    }
}
