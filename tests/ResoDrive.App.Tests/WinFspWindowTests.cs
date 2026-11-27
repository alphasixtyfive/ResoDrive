using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ResoDrive.Core.Contracts;
using ResoDrive.Core.Results;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

internal static class WinFspWindowTests
{
    internal static void VerifyWithApplicationResources()
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { VerifyWindows(); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static void VerifyWindows()
    {
        var installed = false;
        TaskCompletionSource<OperationResult<PrerequisiteStatus>>? pending = null;
        var refresh = new WinFspStatusRefresh(token => pending is null
            ? Task.FromResult(Result.Success(new PrerequisiteStatus(installed, installed ? "2.1" : null)))
            : pending.Task.WaitAsync(token));
        var window = new MainWindow(false, null, refresh);
        window.Loaded -= Method(window, "MainWindow_Loaded").CreateDelegate<RoutedEventHandler>(window);
        try
        {
            window.Show();
            Method(window, "SelectPage").Invoke(window, ["Settings"]);
            typeof(MainWindow).GetProperty("IsStartupReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Method(window, "MainWindow_Activated").Invoke(window, [window, EventArgs.Empty]);
            Drain();
            Assert.Contains("not installed", Control<TextBlock>(window, "WinFspStatusText").Text, StringComparison.Ordinal);
            Capture(window, "winfsp-missing");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var update = (Task)Method(window, "UpdateWinFspStatusAsync").Invoke(window, [true])!;
            Method(window, "SetRcloneUpdateBusy").Invoke(window, [true, null, true]);
            pending.SetResult(Result.Success(new PrerequisiteStatus(true, "2.1")));
            Complete(update);
            Assert.Contains("2.1 is installed", Control<TextBlock>(window, "WinFspStatusText").Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Hidden, Control<Button>(window, "WinFspReleasesButton").Visibility);
            Method(window, "SetRcloneUpdateBusy").Invoke(window, [false, null, false]);
            pending = null;
            installed = true;
            Method(window, "MainWindow_Activated").Invoke(window, [window, EventArgs.Empty]);
            Drain();
            Capture(window, "winfsp-installed");
        }
        finally
        {
            typeof(MainWindow).GetField("_exitRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }

        var paths = new ApplicationPaths(Path.Combine(Path.GetTempPath(), "winfsp-form-" + Guid.NewGuid().ToString("N")));
        installed = false;
        var setup = new SetupWindow(paths, false, null, null, refresh);
        setup.Loaded -= Method(setup, "SetupWindow_Loaded").CreateDelegate<RoutedEventHandler>(setup);
        try
        {
            setup.Show();
            Control<TextBox>(setup, "UsernameBox").Text = "editing draft";
            Control<CheckBox>(setup, "AutoMountBox").IsChecked = true;
            Complete((Task)Method(setup, "RefreshWinFspAsync").Invoke(setup, [true, false])!);
            Assert.True(Control<CheckBox>(setup, "AutoMountBox").IsChecked);
            installed = true;
            Complete((Task)Method(setup, "RefreshWinFspAsync").Invoke(setup, [true, false])!);
            Assert.Contains("2.1 is installed", Control<TextBlock>(setup, "WinFspText").Text, StringComparison.Ordinal);
            Assert.Equal("editing draft", Control<TextBox>(setup, "UsernameBox").Text);
            Assert.True(Control<CheckBox>(setup, "AutoMountBox").IsChecked);
            Assert.False(Directory.Exists(paths.Root));
        }
        finally { setup.Close(); }
    }

    private static void Capture(MainWindow window, string prefix)
    {
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        {
            VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
            foreach (var size in new[] { new Size(920, 720), new Size(640, 440) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                window.UpdateLayout();
                MainWindowUpdateTests.Capture((FrameworkElement)window.Content, scale, size, prefix);
            }
        }
    }

    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Drain(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    private static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Control<T>(FrameworkElement target, string name) where T : FrameworkElement => Assert.IsType<T>(target.FindName(name));
}
