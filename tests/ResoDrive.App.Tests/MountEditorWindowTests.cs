using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ResoDrive.App.Controls;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

internal static class MountEditorWindowTests
{
    // Reuse the STA application fixture: WPF permits one Application per process.
    internal static void VerifyWithApplicationResources()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            var letters = Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => (char)value).ToArray();
            var settings = new MountSettings
            {
                Id = Guid.NewGuid(), DisplayName = "Shared files", RemoteName = "cloud", ConnectionHost = "files.example.test",
                Target = new MountTargetSettings { DriveLetter = 'S' },
                Arguments = ["--vfs-cache-mode=full", "--vfs-cache-max-size=3G", "--vfs-cache-max-age=48h", "--timeout=2m"],
            };
            var editor = new MountEditorWindow(settings, "cloud", paths, letters.Select(char.ToLowerInvariant));
            try
            {
                var drive = Control<ComboBox>(editor, "DriveBox");
                Assert.Equal('S', drive.SelectedItem);
                Assert.False(drive.IsEnabled);
                Assert.False(Control<ScrollViewer>(editor, "EditorScrollViewer").IsEnabled);
                Assert.False(Control<Button>(editor, "SaveButton").IsEnabled);
                Assert.False(Control<Button>(editor, "DeleteButton").IsEnabled);
                Assert.Equal("Loading…", Control<TextBox>(editor, "ServerAddressBox").Text);
                var preparation = editor.PrepareAsync();
                Assert.Same(preparation, editor.PrepareAsync());
                Complete(preparation);
                Assert.Equal('S', Assert.Single(drive.Items.Cast<char>()));
                Assert.Equal('S', drive.SelectedItem);
                Assert.True(Control<Button>(editor, "SaveButton").IsEnabled);
                Assert.True(Control<ScrollViewer>(editor, "EditorScrollViewer").IsEnabled);
                Assert.Equal(Visibility.Hidden, Control<TextBlock>(editor, "LoadingText").Visibility);
                var server = Control<TextBox>(editor, "ServerAddressBox");
                Assert.Equal(settings.ConnectionHost, server.Text);
                Assert.False(server.IsEnabled);
                var beforeLoaded = server.Text;
                editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                DrainDispatcher();
                Assert.Equal(beforeLoaded, server.Text);
                Assert.Equal('S', drive.SelectedItem);

                VerifyCustomCacheValues(Control<MountOptionsControl>(editor, "OptionsEditor"), settings.Arguments);
            }
            finally { editor.Close(); }

            var add = new MountEditorWindow(null, "cloud", paths, letters);
            try
            {
                Complete(add.PrepareAsync());
                Assert.Empty(Control<ComboBox>(add, "DriveBox").Items);
                Assert.False(Control<Button>(add, "SaveButton").IsEnabled);
                Assert.Equal("No free drive letters are available.", Control<TextBlock>(add, "LoadingText").Text);
                Assert.Equal("Unavailable", Control<TextBox>(add, "ServerAddressBox").Text);
            }
            finally { add.Close(); }

            var cancelled = new MountEditorWindow(settings, "cloud", paths);
            try
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => Complete(cancelled.PrepareAsync(cancellation.Token)));
                Assert.False(Control<Button>(cancelled, "SaveButton").IsEnabled);
            }
            finally { cancelled.Close(); }
            var closed = new MountEditorWindow(settings, "cloud", paths);
            var closingPreparation = closed.PrepareAsync();
            closed.Close();
            Assert.ThrowsAny<OperationCanceledException>(() => Complete(closingPreparation));
            Assert.False(Control<Button>(closed, "SaveButton").IsEnabled);
            Assert.Equal("Loading…", Control<TextBox>(closed, "ServerAddressBox").Text);
            Assert.False(Directory.Exists(paths.Root));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void VerifyCustomCacheValues(MountOptionsControl options, IReadOnlyList<string> original)
    {
        options.Measure(new System.Windows.Size(400, 500));
        options.Arrange(new Rect(0, 0, 400, 500));
        options.UpdateLayout();
        options.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        DrainDispatcher();
        var size = Control<ComboBox>(options, "SizeBox");
        var age = Control<ComboBox>(options, "AgeBox");
        Assert.Equal("3G", size.Text);
        Assert.Equal("48h", age.Text);
        Assert.True(options.TryGetArguments(false, out var unchanged, out var error), error);
        Assert.Equal(original, unchanged);

        size.SelectedIndex = 1; // 2 GiB; Text is synchronized after SelectionChanged.
        DrainDispatcher();
        Assert.True(options.TryGetArguments(false, out var selected, out error), error);
        Assert.Contains("--vfs-cache-max-size=2G", selected);
        age.Text = "49h";
        DrainDispatcher();
        Assert.True(options.TryGetArguments(false, out var changed, out error), error);
        Assert.Contains("--vfs-cache-max-size=2G", changed);
        Assert.Contains("--vfs-cache-max-age=49h", changed);

        var mode = Control<ComboBox>(options, "ModeBox");
        mode.SelectedValue = "off";
        DrainDispatcher();
        Assert.False(size.IsEnabled);
        Assert.False(age.IsEnabled);
        mode.SelectedValue = "full";
        DrainDispatcher();
        Assert.Equal("2 GiB", size.Text);
        Assert.Equal("49h", age.Text);
    }

    private static T Control<T>(FrameworkElement owner, string name) where T : FrameworkElement =>
        Assert.IsType<T>(owner.FindName(name));

    private static void Complete(Task task)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        timeout.Tick += (_, _) => frame.Continue = false;
        timeout.Start();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert.True(task.IsCompleted, "Drive settings preparation did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
