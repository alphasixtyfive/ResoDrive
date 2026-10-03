using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

[Collection("Sync editor application")]
public sealed class UploadsWindowTests
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
                var error = Assert.Throws<ArgumentNullException>(() => new UploadsWindow(
                    missing == "model" ? null! : new UploadsViewModel(),
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
        var model = new UploadsViewModel();
        var uploads = new UploadsWindow(model, () => opened++, () => settingsOpened++);
        var open = Assert.IsType<Button>(uploads.FindName("OpenMainWindowButton"));
        var settings = Assert.IsType<Button>(uploads.FindName("OpenSettingsButton"));
        var dismiss = Assert.IsType<Button>(uploads.FindName("DismissButton"));
        try
        {
            Assert.Equal("Uploads", uploads.Title);
            Assert.Same(model, uploads.DataContext);
            VerifySummaryLayout(uploads, model);

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
            uploads.Closing += cancelClose;
            try
            {
                uploads.Close();
                Assert.Equal(1, settingsOpened);
                Click(open);
                Assert.Equal(2, opened);
            }
            finally { uploads.Closing -= cancelClose; }
        }
        finally { uploads.Close(); }

        Click(dismiss);
        Click(open);
        Click(settings);
        uploads.ShowFlyout(System.Drawing.Point.Empty);
        Assert.Equal(2, opened);
        Assert.Equal(1, settingsOpened);
    }

    private static void VerifySummaryLayout(UploadsWindow uploads, UploadsViewModel model)
    {
        var layout = Assert.IsType<Grid>(uploads.Content);
        var summary = Assert.Single(layout.Children.OfType<TextBlock>());
        var files = Assert.Single(layout.Children.OfType<ScrollViewer>());
        model.Update([], false);
        RefreshLayout(layout);
        Assert.Equal(Visibility.Visible, summary.Visibility);
        Assert.Equal(Visibility.Collapsed, files.Visibility);
        Assert.Equal("No active uploads.", summary.Text);

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
        Assert.Equal(Visibility.Visible, summary.Visibility);
        Assert.Equal("Checking uploads…", summary.Text);

        row.ApplyStatus(new HostMountStatus(mount.Id, "Mounted", "Mounted", 0, 0) { UploadErrors = 1 });
        model.Update([row], false);
        RefreshLayout(layout);
        Assert.Equal(Visibility.Visible, summary.Visibility);
        Assert.Equal("Some uploads need attention.", summary.Text);
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
