using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
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
                VerifyStrictSelectionsAtSave(editor, settings.Arguments);
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

    private static void VerifyStrictSelectionsAtSave(MountEditorWindow editor, IReadOnlyList<string> original)
    {
        var options = Control<MountOptionsControl>(editor, "OptionsEditor");
        options.LoadArguments(original);
        var size = Control<ComboBox>(options, "SizeBox");
        size.SelectedIndex = -1;
        Assert.False(editor.TryBuildValue(out _, out var error));
        Assert.Equal("Choose a cache size target.", error);
        size.SelectedValue = "2G";
        Assert.True(editor.TryBuildValue(out var saved, out error), error);
        Assert.Contains("--vfs-cache-max-size=2G", saved!.Arguments);
        Assert.Contains("--vfs-cache-max-age=48h", saved.Arguments);

        Assert.IsType<Border>(options.Parent);
        editor.Show();
        size.SelectedIndex = -1;
        Control<Button>(editor, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DrainDispatcher();
        Assert.True(editor.IsVisible);
        Assert.True(size.IsKeyboardFocused);
        Assert.Equal("Choose a cache size target.", Control<TextBlock>(editor, "FormError").Text);
        size.SelectedValue = "2G";
        Assert.Equal(Visibility.Collapsed, Control<TextBlock>(editor, "FormError").Visibility);
        editor.Width = 540;
        editor.Height = 640;
        editor.UpdateLayout();
        var previewRoot = Assert.IsAssignableFrom<FrameworkElement>(editor.Content);
        var scroll = Control<ScrollViewer>(editor, "EditorScrollViewer");
        scroll.ScrollToTop();
        DrainDispatcher();
        editor.UpdateLayout();
        MainWindowUpdateTests.Capture(previewRoot, 1.5d, new Size(editor.Width, editor.Height), "drive-layout-top");
        scroll.ScrollToBottom();
        DrainDispatcher();
        editor.UpdateLayout();
        MainWindowUpdateTests.Capture(previewRoot, 1.5d, new Size(editor.Width, editor.Height), "drive-layout-options");
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        {
            VisualTreeHelper.SetRootDpi(editor, new DpiScale(scale, scale));
            editor.Width = editor.MinWidth;
            editor.Height = editor.MinHeight;
            editor.UpdateLayout();
            Control<ScrollViewer>(editor, "EditorScrollViewer").ScrollToBottom();
            DrainDispatcher();
            editor.UpdateLayout();
            size.Focus();
            size.BringIntoView();
            DrainDispatcher();
            Control<ScrollViewer>(editor, "EditorScrollViewer").ScrollToBottom();
            DrainDispatcher();
            editor.UpdateLayout();
            Assert.False(System.Windows.Input.ApplicationCommands.Paste.CanExecute(null, size));
            var root = Assert.IsAssignableFrom<FrameworkElement>(editor.Content);
            var bounds = size.TransformToAncestor(root).TransformBounds(new Rect(size.RenderSize));
            var saveBounds = Control<Button>(editor, "SaveButton").TransformToAncestor(root)
                .TransformBounds(new Rect(Control<Button>(editor, "SaveButton").RenderSize));
            Assert.True(bounds.Top >= 0 && bounds.Bottom < saveBounds.Top);
            MainWindowUpdateTests.Capture(root, scale, new Size(editor.Width, editor.Height), "strict-selects");
            size.IsDropDownOpen = true;
            DrainDispatcher();
            var popup = Assert.IsType<Popup>(size.Template.FindName("PART_Popup", size));
            var content = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            content.UpdateLayout();
            MainWindowUpdateTests.Capture(content, scale, new Size(editor.Width, editor.Height), "cache-choices");
            size.IsDropDownOpen = false;
            Control<ComboBox>(options, "ModeBox").SelectedValue = "off";
            DrainDispatcher();
            MainWindowUpdateTests.Capture(root, scale, new Size(editor.Width, editor.Height), "disabled-selects");
            Control<ComboBox>(options, "ModeBox").SelectedValue = "full";
        }
        options.LoadArguments(original);
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
        Assert.False(size.IsEditable);
        Assert.False(age.IsEditable);
        Assert.Equal("3G", size.SelectedValue);
        Assert.Equal("48h", age.SelectedValue);
        Assert.Null(size.Template.FindName("PART_EditableTextBox", size));
        Assert.Null(age.Template.FindName("PART_EditableTextBox", age));
        Assert.True(options.TryGetArguments(false, out var unchanged, out var error), error);
        Assert.Equal(original, unchanged);

        size.SelectedIndex = 1; // 2 GiB; Text is synchronized after SelectionChanged.
        DrainDispatcher();
        Assert.True(options.TryGetArguments(false, out var selected, out error), error);
        Assert.Contains("--vfs-cache-max-size=2G", selected);
        age.SelectedValue = "72h";
        DrainDispatcher();
        Assert.True(options.TryGetArguments(false, out var changed, out error), error);
        Assert.Contains("--vfs-cache-max-size=2G", changed);
        Assert.Contains("--vfs-cache-max-age=72h", changed);

        var mode = Control<ComboBox>(options, "ModeBox");
        mode.SelectedValue = "off";
        DrainDispatcher();
        Assert.False(size.IsEnabled);
        Assert.False(age.IsEnabled);
        mode.SelectedValue = "full";
        DrainDispatcher();
        Assert.Equal("2 GiB", size.Text);
        Assert.Equal("72h", age.SelectedValue);

        foreach (var invalid in new[] { "", "banana", "2GB", "8E", "-1", "3G\n" })
        {
            options.LoadArguments(["--vfs-cache-mode=full", "--vfs-cache-max-size=" + invalid, "--vfs-cache-max-age=48h"]);
            DrainDispatcher();
            Assert.False(options.TryGetArguments(false, out _, out error));
            Assert.Null(size.SelectedItem);
            Assert.Equal("Choose a cache size target.", error);
        }
        foreach (var invalid in new[] { "", "banana", "1d2h", "9223372036854775808ns", "48h\n" })
        {
            options.LoadArguments(["--vfs-cache-mode=full", "--vfs-cache-max-size=3.25GiB", "--vfs-cache-max-age=" + invalid]);
            DrainDispatcher();
            Assert.False(options.TryGetArguments(false, out _, out error));
            Assert.Null(age.SelectedItem);
            Assert.Equal("Choose when to remove unused cached files.", error);
        }
        options.LoadArguments(["--vfs-cache-mode=full", "--vfs-cache-max-size=3.25GiB", "--vfs-cache-max-age=48h"]);
        Assert.True(options.TryGetArguments(false, out var custom, out error), error);
        Assert.Contains("--vfs-cache-max-size=3.25GiB", custom);
        Assert.Contains("--vfs-cache-max-age=48h", custom);
        options.LoadArguments(original);
        size.SelectedIndex = -1;
        age.SelectedIndex = -1;
        size.Text = "3.5G";
        age.Text = "49h";
        DrainDispatcher();
        Assert.False(options.TryGetArguments(false, out _, out error));
        mode.SelectedValue = "off";
        DrainDispatcher();
        Assert.True(options.TryGetArguments(false, out var disabled, out error), error);
        Assert.Equal(RcloneMountOptions.Value(original, RcloneMountOptions.CacheSizeOption),
            RcloneMountOptions.Value(disabled, RcloneMountOptions.CacheSizeOption));
        Assert.Equal(RcloneMountOptions.Value(original, RcloneMountOptions.CacheAgeOption),
            RcloneMountOptions.Value(disabled, RcloneMountOptions.CacheAgeOption));
        mode.SelectedValue = "full";
        DrainDispatcher();
        Assert.False(options.TryGetArguments(false, out _, out error));
        size.SelectedValue = "3G";
        age.SelectedValue = "48h";
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
