using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ResoDrive.App.Controls;
using ResoDrive.Core.Settings;
using ResoDrive.Core.Validation;
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
                VerifyReconnectInputs(editor);
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

            var limitedSettings = settings with { Restart = settings.Restart with { MaximumAttempts = 37 } };
            var limited = new MountEditorWindow(limitedSettings, "cloud", paths, letters, ["Another drive"]);
            try
            {
                Assert.False(limited.TryBuildValue(out _, out _));
                Complete(limited.PrepareAsync());
                Assert.False(Control<CheckBox>(limited, "UnlimitedBox").IsChecked);
                Assert.Equal("37", Control<TextBox>(limited, "AttemptsBox").Text);
                Assert.True(limited.TryBuildValue(out var value, out var error), error);
                Assert.Equal(37, value!.Restart.MaximumAttempts);
                VerifyInvalidDriveValues(limited, limitedSettings);
            }
            finally { limited.Close(); }

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

    private static void VerifyReconnectInputs(MountEditorWindow editor)
    {
        var reconnect = Control<CheckBox>(editor, "RestartBox");
        var unlimited = Control<CheckBox>(editor, "UnlimitedBox");
        var attempts = Control<TextBox>(editor, "AttemptsBox");
        var error = Control<TextBlock>(editor, "AttemptsError");
        var save = Control<Button>(editor, "SaveButton");
        Assert.True(unlimited.IsChecked);
        Assert.False(attempts.IsEnabled);
        Assert.True(editor.TryGetRestartSettings(out var restart));
        Assert.Equal(0, restart.MaximumAttempts);
        unlimited.IsChecked = false;
        attempts.Text = "37";
        Assert.True(attempts.IsEnabled);
        Assert.True(editor.TryGetRestartSettings(out restart));
        Assert.Equal(37, restart.MaximumAttempts);
        unlimited.IsChecked = true;
        Assert.False(attempts.IsEnabled);
        unlimited.IsChecked = false;
        Assert.Equal("37", attempts.Text);

        // Exercise the routed paste handler without using the system clipboard.
        attempts.SelectAll();
        var rejected = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "Unlimited456"),
            false, DataFormats.UnicodeText);
        attempts.RaiseEvent(rejected);
        Assert.True(rejected.CommandCancelled);
        var accepted = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "1000"),
            false, DataFormats.UnicodeText);
        attempts.RaiseEvent(accepted);
        Assert.False(accepted.CommandCancelled); // Range checking must reject 1000, never truncate it to 100.
        Assert.Equal(0, attempts.MaxLength);

        foreach (var invalid in new[] { "", "0", "101", "1000", "2147483648", "Unlimited456", "+5", "1.5", "5 " })
        {
            attempts.Text = invalid;
            Assert.False(editor.TryGetRestartSettings(out _));
            Assert.False(editor.TryBuildValue(out _, out _));
            Assert.False(save.IsEnabled);
            Assert.Equal(Visibility.Visible, error.Visibility);
            reconnect.IsChecked = false;
            Assert.False(attempts.IsEnabled);
            Assert.False(unlimited.IsEnabled);
            Assert.True(save.IsEnabled);
            Assert.True(editor.TryGetRestartSettings(out restart));
            Assert.False(restart.Enabled);
            Assert.Equal(0, restart.MaximumAttempts);
            reconnect.IsChecked = true;
            Assert.Equal(invalid, attempts.Text);
            Assert.False(save.IsEnabled);
            unlimited.IsChecked = true;
            Assert.True(save.IsEnabled);
            Assert.Equal(Visibility.Collapsed, error.Visibility);
            Assert.True(editor.TryGetRestartSettings(out restart));
            Assert.Equal(0, restart.MaximumAttempts);
            unlimited.IsChecked = false;
        }
        attempts.Text = "100";
        Assert.True(editor.TryBuildValue(out var value, out var buildError), buildError);
        Assert.Equal(100, value!.Restart.MaximumAttempts);
        var preparation = editor.PrepareAsync();
        Complete(preparation);
        Assert.Equal("100", attempts.Text);

        // An invalid handler invocation must keep the editor open and produce
        // inline feedback even if a caller bypasses the disabled button.
        attempts.Text = "101";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(editor.Value);
        Assert.Null(editor.DialogResult);
        Assert.Equal(Visibility.Visible, Control<TextBlock>(editor, "FormError").Visibility);
        attempts.Text = "37";
        Assert.Equal(Visibility.Collapsed, Control<TextBlock>(editor, "FormError").Visibility);
    }

    private static void VerifyInvalidDriveValues(MountEditorWindow editor, MountSettings original)
    {
        var name = Control<TextBox>(editor, "NameBox");
        foreach (var invalid in new[] { "", new string('x', 129), "Another drive", "name\n" })
        {
            name.Text = invalid;
            Assert.False(editor.TryBuildValue(out _, out _));
        }
        name.Text = original.DisplayName;
        var folder = Control<TextBox>(editor, "RemotePathBox");
        foreach (var invalid in new[] { "../secret", "folder//file", "folder\\file", "folder\n" })
        {
            folder.Text = invalid;
            Assert.False(editor.TryBuildValue(out _, out _));
        }
        folder.Text = "Documents";
        Assert.True(editor.TryBuildValue(out var valid, out var error), error);
        Assert.Equal("Documents", valid!.RemotePath);
        var attempts = Control<TextBox>(editor, "AttemptsBox");
        attempts.Text = "101";
        Control<CheckBox>(editor, "RestartBox").IsChecked = false;
        Assert.True(editor.TryBuildValue(out var disabled, out error), error);
        Assert.Equal(37, disabled!.Restart.MaximumAttempts);
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

        foreach (var invalid in new[] { "", "banana", "2GB", "8E", "-1", "3G\n" })
        {
            size.Text = invalid;
            DrainDispatcher();
            Assert.False(options.TryGetArguments(false, out _, out error));
            Assert.Contains(invalid.Any(char.IsControl) ? "control characters" : "--vfs-cache-max-size", error!, StringComparison.Ordinal);
        }
        size.Text = "3.25GiB";
        foreach (var invalid in new[] { "", "banana", "1d2h", "9223372036854775808ns", "48h\n" })
        {
            age.Text = invalid;
            DrainDispatcher();
            Assert.False(options.TryGetArguments(false, out _, out error));
            Assert.Contains(invalid.Any(char.IsControl) ? "control characters" : "--vfs-cache-max-age", error!, StringComparison.Ordinal);
        }
        age.Text = "48h";
        Assert.True(options.TryGetArguments(false, out var custom, out error), error);
        Assert.Contains("--vfs-cache-max-size=3.25GiB", custom);
        Assert.Contains("--vfs-cache-max-age=48h", custom);
        size.Text = "banana";
        age.Text = "banana";
        mode.SelectedValue = "off";
        DrainDispatcher();
        Assert.True(options.TryGetArguments(false, out var disabled, out error), error);
        Assert.Equal(RcloneMountOptions.Value(original, RcloneMountOptions.CacheSizeOption),
            RcloneMountOptions.Value(disabled, RcloneMountOptions.CacheSizeOption));
        Assert.Equal(RcloneMountOptions.Value(original, RcloneMountOptions.CacheAgeOption),
            RcloneMountOptions.Value(disabled, RcloneMountOptions.CacheAgeOption));
        mode.SelectedValue = "full";
        DrainDispatcher();
        Assert.Equal("banana", size.Text);
        Assert.Equal("banana", age.Text);
        Assert.False(options.TryGetArguments(false, out _, out error));
        size.Text = "3.25GiB";
        age.Text = "48h";
        mode.SelectedIndex = -1;
        Assert.False(options.TryGetArguments(false, out _, out error));
        Assert.Equal("Choose a caching mode.", error);
        mode.SelectedValue = "full";
        DrainDispatcher();
        var additional = Control<TextBox>(options, "ArgumentsBox");
        var savedAdditional = additional.Text;
        additional.Text = string.Join(Environment.NewLine, Enumerable.Range(0, 64).Select(index => $"--unknown-option-{index}"));
        Assert.False(options.TryGetArguments(false, out _, out error));
        Assert.True(error!.Length < 120);
        Assert.DoesNotContain('\n', error);
        additional.Text = "--" + new string('x', 2046);
        Assert.False(options.TryGetArguments(false, out _, out error));
        Assert.True(error!.Length < 120);
        additional.Text = savedAdditional;
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
