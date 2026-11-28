using System.ComponentModel;
using System.Reflection;
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
        VerifyHiddenPresentationDefersWork();
    }

    private static void VerifyHiddenPresentationDefersWork()
    {
        var previousData = Environment.GetEnvironmentVariable("RDRIVE_DATA_DIR");
        Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", Path.Combine(Path.GetTempPath(), "resodrive-hidden-transfers-" + Guid.NewGuid().ToString("N")));
        try
        {
            var main = new MainWindow(); // Keep startup/host IO out of this rendering fixture.
            var model = Assert.IsType<ShellViewModel>(main.DataContext);
            var mount = new MountSettings { Id = Guid.NewGuid(), DisplayName = "Cloud", RemoteName = "fixture" };
            var status = new HostMountStatus(mount.Id, "Mounted", "Mounted", 20, 1)
            {
                Uploads = [new MountUploadFile { RelativePath = "active.bin", State = MountUploadState.Uploading,
                    BytesTransferred = 40, TotalBytes = 100 },
                    .. Enumerable.Range(0, 20).Select(index => new MountUploadFile { RelativePath = $"queued-{index}.bin" })],
            };
            model.Load(new ManagerSettings { Mounts = [mount] }, [status]);
            var apply = MainMethod("ApplyTransferStatus").CreateDelegate<Action<HostResponse>>(main);
            var refresh = MainMethod("RefreshTransfersPresentation").CreateDelegate<Action<bool>>(main);
            var transfers = (TransfersViewModel)MainField("_transfersModel").GetValue(main)!;
            try
            {
                apply(new HostResponse(true, Mounts: [status], SyncJobs: []));
                refresh(false);
                Assert.Empty(transfers.Transfers);

                // A previously opened but dismissed flyout should also skip work.
                MainField("_transfersWindow").SetValue(main, new TransfersWindow(transfers, Noop, Noop));
                refresh(false);
                Assert.Empty(transfers.Transfers);
                for (var warmup = 0; warmup < 10; warmup++) refresh(false);
                var allocatedBeforeHidden = GC.GetAllocatedBytesForCurrentThread();
                for (var poll = 0; poll < 100; poll++) refresh(false);
                var hiddenBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeHidden;

                refresh(true);
                var active = Assert.Single(transfers.Transfers, row => row.Key == $"file:{mount.Id}:active.bin");
                Assert.Equal(40, active.Percent);
                var allocatedBeforeForced = GC.GetAllocatedBytesForCurrentThread();
                for (var poll = 0; poll < 100; poll++) refresh(true);
                var forcedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeForced;
                Assert.True(hiddenBytes * 10 < forcedBytes,
                    $"Hidden refreshes should avoid row-building allocations: hidden={hiddenBytes}, forced={forcedBytes}.");
                var captureDirectory = Environment.GetEnvironmentVariable("RESODRIVE_PERF_CAPTURE_DIR");
                if (!string.IsNullOrEmpty(captureDirectory))
                {
                    Directory.CreateDirectory(captureDirectory);
                    File.WriteAllText(Path.Combine(captureDirectory, "hidden-flyout.json"),
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            refreshes = 100, hiddenAllocatedBytes = hiddenBytes, forcedAllocatedBytes = forcedBytes,
                            reductionPercent = 100 * (1 - hiddenBytes / (double)forcedBytes),
                        }));
                }

                var latest = status with { Uploads = [status.Uploads[0] with { BytesTransferred = 70 }] };
                apply(new HostResponse(true, Mounts: [latest], SyncJobs: []));
                refresh(false);
                Assert.Equal(70, Assert.Single(model.Mounts).UploadStatus!.Uploads[0].BytesTransferred);
                Assert.Equal(40, active.Percent);
                // The same forced refresh used before opening must use already-observed
                // status immediately; it must not wait for another host poll.
                refresh(true);
                Assert.Same(active, Assert.Single(transfers.Transfers));
                Assert.Equal(70, active.Percent);

                model.ApplyHostUnavailable();
                MainMethod("MarkTransfersUnavailable").CreateDelegate<Action>(main)();
                refresh(true);
                Assert.Equal(Visibility.Collapsed, active.ProgressVisibility);
                Assert.Equal(Visibility.Collapsed, active.ProgressTextVisibility);
                Assert.Equal("Transfer status unavailable.", transfers.Summary);
            }
            finally
            {
                MainField("_exitRequested").SetValue(main, true);
                main.Close();
            }
        }
        finally { Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", previousData); }
    }

    private static MethodInfo MainMethod(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static FieldInfo MainField(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

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
