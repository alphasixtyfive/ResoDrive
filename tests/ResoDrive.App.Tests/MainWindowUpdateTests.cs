using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResoDrive.Core;
using ResoDrive.Windows;

namespace ResoDrive.App.Tests;

internal static class MainWindowUpdateTests
{
    // Run in the existing STA/Application fixture, without app startup, host or settings IO.
    internal static void VerifyWithApplicationResources()
    {
        var window = new MainWindow();
        var loaded = Method("MainWindow_Loaded").CreateDelegate<RoutedEventHandler>(window);
        window.Loaded -= loaded;
        var check = Assert.IsType<Button>(window.FindName("ApplicationUpdateCheckButton"));
        var install = Assert.IsType<Button>(window.FindName("ApplicationUpdateActionButton"));
        try
        {
            Assert.Equal(Visibility.Collapsed, check.Visibility);
            Assert.Equal("Check for updates", AutomationProperties.GetName(check));
            Set(window, "_applicationUpdate", new ApplicationUpdateCheck("0.3.35", "0.3.36", true, ProductLinks.LatestRelease));
            Method("RefreshApplicationUpdateAction").Invoke(window, null);
            Assert.Equal(Visibility.Visible, check.Visibility);
            Assert.True(check.IsEnabled);
            Assert.True(install.IsEnabled);

            Method("SetApplicationUpdateBusy").Invoke(window, [true]);
            Assert.False(check.IsEnabled);
            Assert.False(install.IsEnabled);
            // The compiled click handler must honor the busy guard, even for a routed/programmatic click.
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(Field("_applicationDownloadCancellation").GetValue(window));
            Assert.Equal("0.3.36", ((ApplicationUpdateCheck)Field("_applicationUpdate").GetValue(window)!).AvailableVersion);
            Method("SetApplicationUpdateBusy").Invoke(window, [false]);

            Method("SelectPage").Invoke(window, ["Settings"]);
            Assert.IsType<TextBlock>(window.FindName("ApplicationUpdateStatusText")).Text = "Version 0.3.36 is available";
            window.Show();
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            {
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                foreach (var size in new[] { new Size(920, 610), new Size(640, 440) })
                {
                    window.Width = size.Width;
                    window.Height = size.Height;
                    window.UpdateLayout();
                    var scroll = Assert.IsType<ScrollViewer>(window.FindName("SettingsScrollViewer"));
                    scroll.ScrollToBottom();
                    check.BringIntoView();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    window.UpdateLayout();
                    var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    var checkBounds = check.TransformToAncestor(root).TransformBounds(new Rect(check.RenderSize));
                    var installBounds = install.TransformToAncestor(root).TransformBounds(new Rect(install.RenderSize));
                    var viewportBounds = scroll.TransformToAncestor(root).TransformBounds(new Rect(scroll.RenderSize));
                    Assert.True(checkBounds.Width >= 33 && checkBounds.Height >= 30);
                    Assert.True(checkBounds.Right < installBounds.Left);
                    Assert.True(installBounds.Right <= root.ActualWidth);
                    Assert.True(checkBounds.Top >= viewportBounds.Top && installBounds.Bottom <= viewportBounds.Bottom);
                    Capture(root, scale, size);
                }
            }

            Set(window, "_applicationUpdate", null);
            Set(window, "_applicationUpdateCheckFailed", true);
            Method("RefreshApplicationUpdateAction").Invoke(window, null);
            Assert.Equal(Visibility.Collapsed, check.Visibility);
            Assert.True(install.IsEnabled);
            Assert.Equal("Retry", Assert.IsType<TextBlock>(window.FindName("ApplicationUpdateActionText")).Text);
        }
        finally
        {
            Set(window, "_exitRequested", true);
            window.Close();
        }
    }

    private static void Capture(FrameworkElement root, double scale, Size size)
    {
        var directory = Environment.GetEnvironmentVariable("RESODRIVE_UI_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"update-{size.Width:0}-{scale * 100:0}.png"));
        encoder.Save(file);
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Set(MainWindow window, string name, object? value) => Field(name).SetValue(window, value);
}
