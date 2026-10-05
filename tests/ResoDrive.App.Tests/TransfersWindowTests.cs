using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

[Collection(WpfUiTestsGroup.Name)]
public sealed class TransfersWindowTests
{
    [Theory]
    [InlineData("model")]
    [InlineData("openMainWindow")]
    [InlineData("openSettings")]
    public void MissingDependencyIsRejectedBeforeTheWindowLoads(string missing)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var error = Assert.Throws<ArgumentNullException>(() => new TransfersWindow(
                    missing == "model" ? null! : new TransfersViewModel(),
                    missing == "openMainWindow" ? null! : Noop,
                    missing == "openSettings" ? null! : Noop));
                Assert.Equal(missing, error.ParamName);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Noop() { }

    // WPF permits only one Application per process, so the shared STA application
    // fixture calls this while its resources and dispatcher are still alive.
    internal static void VerifyWithApplicationResources()
    {
        var opened = 0;
        var settingsOpened = 0;
        var model = new TransfersViewModel();
        var transfers = new TransfersWindow(model, () => opened++, () => settingsOpened++);
        var open = Assert.IsType<Button>(transfers.FindName("OpenMainWindowButton"));
        var settings = Assert.IsType<Button>(transfers.FindName("OpenSettingsButton"));
        var dismiss = Assert.IsType<Button>(transfers.FindName("DismissButton"));
        try
        {
            Assert.Equal("Transfers", transfers.Title);
            Assert.Same(model, transfers.DataContext);
            VerifySummaryLayout(transfers, model);

            Click(open);
            Assert.Equal(1, opened);
            Assert.Equal(0, settingsOpened);
            Click(settings);
            Assert.Equal(1, opened);
            Assert.Equal(1, settingsOpened);

            CancelEventHandler cancelClose = (_, args) =>
            {
                args.Cancel = true;
                Click(dismiss);
                Click(settings);
            };
            transfers.Closing += cancelClose;
            try
            {
                transfers.Close();
                Assert.Equal(1, settingsOpened);
                Click(open);
                Assert.Equal(2, opened);
            }
            finally { transfers.Closing -= cancelClose; }
        }
        finally { transfers.Close(); }

        Click(dismiss);
        Click(open);
        Click(settings);
        transfers.ShowFlyout(System.Drawing.Point.Empty);
        Assert.Equal(2, opened);
        Assert.Equal(1, settingsOpened);
    }

    private static void VerifySummaryLayout(TransfersWindow transfers, TransfersViewModel model)
    {
        var layout = Assert.IsType<Grid>(transfers.Content);
        var summary = Assert.Single(layout.Children.OfType<TextBlock>());
        var files = Assert.Single(layout.Children.OfType<ScrollViewer>());
        model.Update([], false);
        RefreshLayout(layout);
        Assert.Equal(Visibility.Visible, summary.Visibility);
        Assert.Equal(Visibility.Collapsed, files.Visibility);
        Assert.Equal("No active transfers.", summary.Text);

        var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "cloud" };
        var row = new MountRow(mount, new HostMountStatus(mount.Id, "Mounted", "Mounted", 1, 0));
        model.Update([row], false);
        RefreshLayout(layout);
        Assert.Equal(Visibility.Collapsed, summary.Visibility);
        Assert.Equal(Visibility.Visible, files.Visibility);
        Assert.Equal(0, layout.RowDefinitions[1].ActualHeight);

        row.ApplyStatus(row.UploadStatus! with { UploadStatusChecking = true });
        model.Update([row], false);
        RefreshLayout(layout);
        Assert.Equal(Visibility.Collapsed, summary.Visibility);
        Assert.Equal(Visibility.Visible, files.Visibility);
        Assert.Equal("Uploads pending. Waiting for upload status.", Assert.Single(model.Transfers).Detail);

        row.ApplyStatus(new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0) { UploadErrors = 1 });
        model.Update([row], false);
        RefreshLayout(layout);
        Assert.Equal(Visibility.Visible, summary.Visibility);
        Assert.Equal("Some transfers need attention.", summary.Text);

        var job = new SyncJobSettings { Id = Guid.NewGuid(), DisplayName = "Photo library", LocalPath = @"C:\Demo\Photos", Mode = nameof(SyncMode.CopyFromRemote) };
        var sync = new SyncRow(mount, job, new HostSyncStatus(mount.Id, job.Id,
            nameof(SyncLifecycle.Running), "Syncing", null, BytesTransferred: 100, TotalBytes: 200,
            ProgressPercent: 50, SpeedBytesPerSecond: 10));
        model.Update([], false, [sync], activeSyncJobs: 1);
        RefreshLayout(layout);
        Assert.Equal(Visibility.Collapsed, summary.Visibility);
        Assert.Equal(Visibility.Visible, files.Visibility);
        Assert.Equal(0, layout.RowDefinitions[1].ActualHeight);
        Assert.Single(model.Transfers);

        sync.ApplyStatus(sync.TransferStatus! with { Lifecycle = nameof(SyncLifecycle.Succeeded) });
        model.Update([], false, [sync], activeSyncJobs: 0);
        RefreshLayout(layout);
        Assert.Equal(Visibility.Visible, summary.Visibility);
        Assert.Equal(Visibility.Collapsed, files.Visibility);
        Assert.Equal("No active transfers.", summary.Text);
    }

    private static void RefreshLayout(Grid layout)
    {
        // A constructed, hidden Window has not yet drained its deferred bindings.
        // Process the same binding/render work that a running UI dispatcher would.
        layout.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        layout.Measure(new System.Windows.Size(360, 400));
        layout.Arrange(new Rect(0, 0, 360, layout.DesiredSize.Height));
        layout.UpdateLayout();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
