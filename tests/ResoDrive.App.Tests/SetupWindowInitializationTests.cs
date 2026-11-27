using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;
using ResoDrive.Core.Setup;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

internal static class SetupWindowInitializationTests
{
    internal static void VerifyWithApplicationResources()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            VerifyDelayedInitialization(closeBeforeCompletion: false);
            VerifyDelayedInitialization(closeBeforeCompletion: true);
            VerifyProfileDrivePreference();
            VerifyProfileSelectionBoundary();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void VerifyProfileDrivePreference()
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), "setup-profile-choice-" + Guid.NewGuid().ToString("N")));
        var window = new SetupWindow(paths, reservedDriveLetters: ['V']);
        try
        {
            var drive = Control<ComboBox>(window, "DriveBox");
            drive.Items.Add('S');
            drive.Items.Add('U');
            ApplyProfile(window, 'U');
            Assert.Equal('U', drive.SelectedItem);
            ApplyProfile(window, 'S');
            Assert.Equal('U', drive.SelectedItem); // Keep an existing available user choice.

            drive.SelectedItem = null;
            drive.Items.Remove('U');
            ApplyProfile(window, 'U');
            Assert.Null(drive.SelectedItem); // Never substitute another letter for an unavailable preference.

            drive.Items.Add('?');
            drive.SelectedItem = '?';
            ApplyProfile(window, 'S');
            Assert.Equal('S', drive.SelectedItem);

            drive.Items.Add('V'); // A stale/injected item must not bypass the reserved catalog.
            drive.SelectedItem = null;
            ApplyProfile(window, 'V');
            Assert.Null(drive.SelectedItem);
            Assert.False(Directory.Exists(paths.Root));
        }
        finally { window.Close(); }
    }

    private static void VerifyProfileSelectionBoundary()
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), "setup-profile-boundary-" + Guid.NewGuid().ToString("N")));
        var window = new SetupWindow(paths, reservedDriveLetters: ['V']);
        try
        {
            var drive = Control<ComboBox>(window, "DriveBox");
            drive.Items.Add('S');
            var (profile, choice) = ApplyProfile(window, 'S');
            var profiles = Control<ComboBox>(window, "ProfileCombo");
            profiles.Items.Add(choice);
            profiles.SelectedItem = choice;
            SetField(window, "_catalog", new SetupProfileCatalog([profile], ProfileCatalogSource.UserFile));
            Control<TextBox>(window, "UsernameBox").Text = "fixture-user";
            Control<PasswordBox>(window, "PasswordBox").Password = "fixture-password";
            Control<TextBox>(window, "DisplayNameBox").Text = "Files";
            Assert.False(window.TryPrepareSetup(out _, out _));
            Assert.Contains("finish loading", Control<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
            Method(window, "Connect_Click").Invoke(window, [Control<Button>(window, "ConnectButton"), new RoutedEventArgs()]);
            Assert.Null(window.Result);
            Assert.False(Directory.Exists(paths.Root));

            SetField(window, "_prerequisitesReady", true);
            Assert.True(window.TryPrepareSetup(out _, out _));
            profiles.SelectedItem = null;
            Assert.False(window.TryPrepareSetup(out var catalog, out var request));
            Assert.Null(catalog);
            Assert.Null(request);
            Assert.Contains("Choose a connection profile", Control<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
            Method(window, "Connect_Click").Invoke(window, [Control<Button>(window, "ConnectButton"), new RoutedEventArgs()]);
            Assert.Null(window.Result);
            Assert.False(Directory.Exists(paths.Root));

            var (_, unrelatedChoice) = ApplyProfile(window, 'S', "Other files");
            profiles.Items.Add(unrelatedChoice);
            profiles.SelectedItem = unrelatedChoice;
            Assert.False(window.TryPrepareSetup(out _, out _));
            Assert.Contains("Choose a connection profile", Control<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
            profiles.SelectedItem = choice;
            drive.Items.Add('V');
            drive.SelectedItem = 'V';
            Assert.False(window.TryPrepareSetup(out _, out _));
            Assert.Contains("Choose a free drive letter", Control<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
            Method(window, "Connect_Click").Invoke(window, [Control<Button>(window, "ConnectButton"), new RoutedEventArgs()]);
            Assert.Null(window.Result);
            Assert.False(Directory.Exists(paths.Root));
        }
        finally { window.Close(); }
    }

    private static (SetupProfile Profile, object Choice) ApplyProfile(SetupWindow window, char preferred, string displayName = "Files")
    {
        var profile = new SetupProfile
        {
            Id = "files", DisplayName = displayName, Description = "Fixture profile", RemoteName = "Files",
            Connection = new WebDavConnectionDefinition
            {
                BaseUrl = new Uri("https://example.test/"), PathTemplate = "/dav/{username}", Vendor = WebDavVendor.Other,
            },
            DefaultDriveLetter = preferred,
        };
        var choiceType = typeof(SetupWindow).GetNestedType("SetupChoice", BindingFlags.NonPublic)!;
        var choice = choiceType.GetConstructor([typeof(string), typeof(string), typeof(SetupProfile)])!
            .Invoke([profile.DisplayName, profile.Description, profile]);
        Method(window, "ApplyChoice").Invoke(window, [choice]);
        return (profile, choice);
    }

    private static void VerifyDelayedInitialization(bool closeBeforeCompletion)
    {
        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), "setup-initialization-" + Guid.NewGuid().ToString("N")));
        var inspection = new TaskCompletionSource<OperationResult<PrerequisiteStatus>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspecting = false;
        var refresh = new WinFspStatusRefresh(_ => { inspecting = true; return inspection.Task; });
        var window = new SetupWindow(paths, false, null, null, refresh);
        window.Loaded -= Method(window, "SetupWindow_Loaded").CreateDelegate<RoutedEventHandler>(window);
        try
        {
            var form = Control<ScrollViewer>(window, "SetupScrollViewer");
            var cancel = Control<Button>(window, "CancelButton");
            var submit = Control<Button>(window, "ConnectButton");
            Assert.False(form.IsEnabled);
            Assert.False(submit.IsEnabled);
            Assert.True(cancel.IsEnabled);
            window.Show();
            var lifetime = (CancellationTokenSource)typeof(SetupWindow)
                .GetField("_lifetimeCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var initialize = (Task)Method(window, "InitializeAsync").Invoke(window, [lifetime.Token])!;
            Until(() => inspecting);
            Assert.False(initialize.IsCompleted);
            Assert.False(form.IsEnabled);
            Assert.False(Control<TextBox>(window, "UsernameBox").IsEnabled);
            Assert.False(submit.IsEnabled);
            Assert.True(cancel.IsEnabled);

            if (!closeBeforeCompletion)
                MainWindowUpdateTests.VerifyEditorLayout(window, "SetupScrollViewer", "ConnectButton", "setup-loading");
            if (closeBeforeCompletion) window.Close();
            inspection.SetResult(Result.Success(new PrerequisiteStatus(true, "2.1")));
            Until(() => initialize.IsCompleted);
            initialize.GetAwaiter().GetResult();
            if (closeBeforeCompletion)
            {
                Assert.False(window.IsLoaded);
                Assert.False(form.IsEnabled);
                Assert.False(submit.IsEnabled);
                Assert.False((bool)typeof(SetupWindow)
                    .GetField("_prerequisitesReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            }
            else
            {
                Assert.True(form.IsEnabled);
                Assert.True(Control<TextBox>(window, "UsernameBox").IsEnabled);
                Assert.Equal(Control<ComboBox>(window, "DriveBox").Items.Count > 0, submit.IsEnabled);
                Assert.Contains("2.1 is installed", Control<TextBlock>(window, "WinFspText").Text, StringComparison.Ordinal);
                MainWindowUpdateTests.VerifyEditorLayout(window, "SetupScrollViewer", "ConnectButton", "setup-ready");
            }
            Assert.False(Directory.Exists(paths.Root));
        }
        finally
        {
            inspection.TrySetResult(Result.Success(new PrerequisiteStatus(true, "2.1")));
            window.Close();
        }
    }

    private static void Until(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(7);
        while (!completed() && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        Assert.True(completed());
    }

    private static MethodInfo Method(object target, string name) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void SetField(SetupWindow window, string name, object value) => typeof(SetupWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static T Control<T>(Window window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
}
