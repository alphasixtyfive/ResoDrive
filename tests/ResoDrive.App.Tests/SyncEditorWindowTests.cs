using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

[Collection(WpfUiTestsGroup.Name)]
public sealed class SyncEditorWindowTests
{
    [Fact]
    public void ManagedCopySelection_PreservesExternalPathsAndRequiresEnrolledDownloads()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            try
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/resodrive;component/Themes/Controls.xaml", UriKind.Relative),
                });
                var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
                var enrolled = Mount("Enrolled");
                var external = Mount("External");
                var registered = new HashSet<Guid> { enrolled.Id };
                VerifyNewJob(paths, enrolled, external, registered);
                VerifyExistingExternalJob(paths, enrolled, registered);
                VerifyExistingManagedJob(paths, enrolled, registered);
                VerifySyncInputBoundary(paths, external);
                VerifyUnsupportedSyncModes(paths, external);
                TransfersWindowTests.VerifyWithApplicationResources();
                MountEditorWindowTests.VerifyWithApplicationResources();
                LogWindowTests.VerifyWithApplicationResources();
                SetupWindowTests.VerifyWithApplicationResources();
                MainWindowUpdateTests.VerifyWithApplicationResources();
                Assert.False(Directory.Exists(paths.Root));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                application.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyNewJob(
        ApplicationPaths paths, MountSettings enrolled, MountSettings external, IReadOnlySet<Guid> registered)
    {
        var editor = new SyncEditorWindow(paths, [enrolled, external], registered, enrolled.Id, null);
        try
        {
            var managed = Control<CheckBox>(editor, "ManagedLocalCopyBox");
            var local = Control<TextBox>(editor, "LocalPathBox");
            var browse = Control<Button>(editor, "BrowseLocalFolderButton");
            var mode = Control<ComboBox>(editor, "ModeBox");
            Assert.True(managed.IsChecked);
            Assert.True(local.IsReadOnly);
            Assert.False(browse.IsEnabled);
            Assert.Equal(paths.ManagedSyncRoot, Path.GetDirectoryName(local.Text));
            var managedPath = local.Text;

            mode.SelectedIndex = 1; // Copy local to remote.
            Assert.False(managed.IsChecked);
            Assert.Equal(Visibility.Collapsed, managed.Visibility);
            Assert.False(local.IsReadOnly);
            Assert.True(browse.IsEnabled);
            Assert.Empty(local.Text);

            mode.SelectedIndex = 0;
            Assert.True(managed.IsChecked);
            Assert.Equal(managedPath, local.Text);

            Control<ComboBox>(editor, "MountBox").SelectedItem = external;
            Assert.False(managed.IsChecked);
            Assert.False(managed.IsEnabled);
            Assert.Empty(local.Text);
            Assert.Contains("requires Nextcloud remote wipe setup", Control<TextBlock>(editor, "LocalCopyNotice").Text, StringComparison.Ordinal);

            Control<ComboBox>(editor, "MountBox").SelectedItem = enrolled;
            Assert.True(managed.IsChecked);
            Assert.Equal(managedPath, local.Text);
            managed.IsChecked = false;
            local.Text = @"C:\Users\Example\Downloads";
            managed.IsChecked = true;
            Assert.Equal(managedPath, local.Text);
            managed.IsChecked = false;
            Assert.Equal(@"C:\Users\Example\Downloads", local.Text);
        }
        finally
        {
            editor.Close();
        }
    }

    private static void VerifyExistingExternalJob(
        ApplicationPaths paths, MountSettings enrolled, IReadOnlySet<Guid> registered)
    {
        var existing = Job(@"C:\Users\Example\Documents", managed: false);
        var editor = new SyncEditorWindow(paths, [enrolled], registered, enrolled.Id, existing);
        try
        {
            var managed = Control<CheckBox>(editor, "ManagedLocalCopyBox");
            var local = Control<TextBox>(editor, "LocalPathBox");
            Assert.False(managed.IsChecked);
            Assert.Equal(existing.LocalPath, local.Text);
            managed.IsChecked = true;
            Assert.Equal(paths.ManagedSyncFolder(existing.Id), local.Text);
            Assert.Contains("Files in the old folder stay there", Control<TextBlock>(editor, "LocalCopyNotice").Text, StringComparison.Ordinal);
            Control<ComboBox>(editor, "ModeBox").SelectedIndex = 3; // Mirror local to remote.
            Assert.False(managed.IsChecked);
            Assert.Equal(existing.LocalPath, local.Text);
        }
        finally
        {
            editor.Close();
        }
    }

    private static void VerifyExistingManagedJob(
        ApplicationPaths paths, MountSettings enrolled, IReadOnlySet<Guid> registered)
    {
        var existing = Job(@"C:\PreviousDataDirectory\managed-sync\old-copy", managed: true);
        var editor = new SyncEditorWindow(paths, [enrolled], registered, enrolled.Id, existing);
        try
        {
            var managed = Control<CheckBox>(editor, "ManagedLocalCopyBox");
            var local = Control<TextBox>(editor, "LocalPathBox");
            Assert.True(managed.IsChecked);
            Assert.Equal(paths.ManagedSyncFolder(existing.Id), local.Text);
            Assert.Contains("previous folder stays in place", Control<TextBlock>(editor, "LocalCopyNotice").Text, StringComparison.Ordinal);
        }
        finally
        {
            editor.Close();
        }

        existing = existing with { LocalPath = paths.ManagedSyncFolder(existing.Id) };
        editor = new SyncEditorWindow(paths, [enrolled], registered, enrolled.Id, existing);
        try
        {
            Control<CheckBox>(editor, "ManagedLocalCopyBox").IsChecked = false;
            Assert.Empty(Control<TextBox>(editor, "LocalPathBox").Text);
            Assert.Contains("remain covered", Control<TextBlock>(editor, "LocalCopyNotice").Text, StringComparison.Ordinal);
        }
        finally
        {
            editor.Close();
        }
    }

    private static T Control<T>(System.Windows.Window editor, string name) where T : FrameworkElement =>
        Assert.IsType<T>(editor.FindName(name));

    private static void VerifySyncInputBoundary(ApplicationPaths paths, MountSettings mount)
    {
        var existing = Job(@"C:\External\Downloads", managed: false) with
        {
            Schedule = new SyncScheduleSettings { Enabled = false, IntervalMinutes = 90 }
        };
        var editor = new SyncEditorWindow(paths, [mount], new HashSet<Guid>(), mount.Id, existing);
        try
        {
            var interval = Control<TextBox>(editor, "IntervalBox");
            var scheduled = Control<CheckBox>(editor, "ScheduleBox");
            var save = Control<Button>(editor, "SaveButton");
            Assert.True(editor.TryGetValue(out var value, out var error), error);
            Assert.Equal(90, value!.Schedule.IntervalMinutes);
            scheduled.IsChecked = true;
            interval.Text = "120";
            scheduled.IsChecked = false;
            Assert.Equal("120", interval.Text);
            Assert.True(editor.TryGetValue(out value, out error), error);
            Assert.Equal(120, value!.Schedule.IntervalMinutes);

            scheduled.IsChecked = true;
            foreach (var invalid in new[] { "", "words", "0", "4", "1441", "-5", "+60", "1.5", "999999999999999999999" })
            {
                interval.Text = invalid;
                Assert.False(editor.TryGetValue(out value, out error));
                Assert.Null(value);
                Assert.Contains("5 to 1440", error!, StringComparison.Ordinal);
                Assert.False(save.IsEnabled);
                Assert.Equal(Visibility.Visible, Control<TextBlock>(editor, "InputFeedback").Visibility);
            }
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(editor.Value);
            Assert.Null(editor.DialogResult);
            foreach (var valid in new[] { "5", "1440" })
            {
                interval.Text = valid;
                Assert.True(editor.TryGetValue(out value, out error), error);
                Assert.Equal(int.Parse(valid, System.Globalization.CultureInfo.InvariantCulture), value!.Schedule.IntervalMinutes);
                Assert.True(save.IsEnabled);
            }

            var typed = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, interval, "x"))
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputEvent
            };
            interval.RaiseEvent(typed);
            Assert.True(typed.Handled);

            var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "words"),
                false, DataFormats.UnicodeText);
            interval.SelectAll();
            interval.RaiseEvent(paste);
            Assert.True(paste.CommandCancelled);
            paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "10000"),
                false, DataFormats.UnicodeText);
            interval.RaiseEvent(paste);
            Assert.False(paste.CommandCancelled); // Keep the full number; range validation rejects it.
            interval.Text = "60";

            foreach (var inputName in new[] { "NameBox", "RemotePathBox", "LocalPathBox" })
            {
                var input = Control<TextBox>(editor, inputName);
                var original = input.Text;
                input.Text = original + "\n";
                Assert.False(editor.TryGetValue(out _, out error));
                Assert.Contains("control characters", error!, StringComparison.Ordinal);
                input.Text = original;
            }

            Control<TextBox>(editor, "NameBox").Text = new string('N', 129);
            Assert.False(editor.TryGetValue(out _, out error));
            Assert.Contains("128", error!, StringComparison.Ordinal);
            Control<TextBox>(editor, "NameBox").Text = "Download";
            Control<TextBox>(editor, "RemotePathBox").Text = "folder//child";
            Assert.False(editor.TryGetValue(out _, out error));
            Control<TextBox>(editor, "RemotePathBox").Text = string.Empty;
            Control<TextBox>(editor, "LocalPathBox").Text = @"C:\External\..\Windows";
            Assert.False(editor.TryGetValue(out _, out error));
            Control<TextBox>(editor, "LocalPathBox").Text = existing.LocalPath;
            Control<TextBox>(editor, "ArgumentsBox").Text = "--max-size=banana";
            Assert.False(editor.TryGetValue(out _, out error));
            Assert.Contains("--max-size", error!, StringComparison.Ordinal);
            Control<TextBox>(editor, "ArgumentsBox").Text = "--max-size=3GiB";
            Assert.True(editor.TryGetValue(out value, out error), error);
            Assert.Contains("--max-size=3GiB", value!.Arguments);
            Control<ComboBox>(editor, "ModeBox").SelectedIndex = -1;
            Assert.False(editor.TryGetValue(out value, out error));
            Assert.Null(value);
            Assert.Contains("operation", error!, StringComparison.Ordinal);
        }
        finally { editor.Close(); }

        var conflictingMount = mount with { SyncJobs = [existing with { Id = Guid.NewGuid() }] };
        editor = new SyncEditorWindow(paths, [conflictingMount], new HashSet<Guid>(), conflictingMount.Id, existing);
        try
        {
            Assert.False(editor.TryGetValue(out _, out var error));
            Assert.Contains("already has", error!, StringComparison.Ordinal);
            Control<TextBox>(editor, "NameBox").Text = "Different job";
            Assert.True(editor.TryGetValue(out _, out error), error);
        }
        finally { editor.Close(); }
    }

    private static void VerifyUnsupportedSyncModes(ApplicationPaths paths, MountSettings mount)
    {
        foreach (var invalidMode in new[] { "not-a-mode", "999", nameof(SyncMode.Bisync) })
        {
            var editor = new SyncEditorWindow(paths, [mount], new HashSet<Guid>(), mount.Id,
                Job(@"C:\External\Downloads", managed: false) with { Mode = invalidMode });
            try
            {
                Assert.Null(Control<ComboBox>(editor, "ModeBox").SelectedItem);
                Assert.False(editor.TryGetValue(out var value, out var error));
                Assert.Null(value);
                Assert.Contains("operation", error!, StringComparison.Ordinal);
            }
            finally { editor.Close(); }
        }
    }

    private static MountSettings Mount(string name) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        RemoteName = name,
    };

    private static SyncJobSettings Job(string path, bool managed) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Download",
        LocalPath = path,
        Mode = nameof(SyncMode.CopyFromRemote),
        ManagedLocalCopy = managed,
    };
}
