using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using ResoDrive.Core.Domain;
using ResoDrive.Core.Settings;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

[CollectionDefinition("Sync editor application", DisableParallelization = true)]
public sealed class SyncEditorApplicationTestsGroup;

[Collection("Sync editor application")]
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
            Assert.Contains("remote-wipe status", Control<TextBlock>(editor, "LocalCopyNotice").Text, StringComparison.Ordinal);

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
            Assert.Contains("Existing files stay", Control<TextBlock>(editor, "LocalCopyNotice").Text, StringComparison.Ordinal);
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
            Assert.Contains("previous location stay there", Control<TextBlock>(editor, "LocalCopyNotice").Text, StringComparison.Ordinal);
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

    private static T Control<T>(SyncEditorWindow editor, string name) where T : FrameworkElement =>
        Assert.IsType<T>(editor.FindName(name));

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
