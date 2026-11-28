using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ResoDrive.App.Tests;

internal static class MigrationStartupWindowTests
{
    internal static void VerifyWithApplicationResources()
    {
        var window = new MigrationStartupWindow();
        var close = Assert.IsType<Button>(window.FindName("CloseButton"));
        var progress = Assert.IsType<ProgressBar>(window.FindName("Progress"));
        try
        {
            window.Show();
            Assert.Equal(Visibility.Collapsed, close.Visibility);
            Assert.True(progress.IsIndeterminate);
            window.Close(); // Closing cannot interrupt a rename midway through.
            Assert.True(window.IsVisible);
            foreach (var failed in new[] { false, true })
            {
                if (failed) window.ShowFailure(string.Concat(Enumerable.Repeat("A file is still in use. ", 100)));
                foreach (var scale in new[] { 1d, 1.5d, 2d })
                {
                    VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                    foreach (var width in new[] { 540d, 420d })
                    {
                        window.Width = width;
                        window.UpdateLayout();
                        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                        Assert.True(window.ActualHeight < 520);
                        if (failed)
                        {
                            Assert.Equal(Visibility.Visible, close.Visibility);
                            Assert.Equal(Visibility.Collapsed, progress.Visibility);
                            var bounds = close.TransformToAncestor(root).TransformBounds(new Rect(close.RenderSize));
                            Assert.True(bounds.Right <= root.ActualWidth && bounds.Bottom <= root.ActualHeight);
                        }
                        MainWindowUpdateTests.Capture(root, scale, new Size(width, root.ActualHeight),
                            failed ? "migration-error" : "migration-progress");
                    }
                }
            }
        }
        finally { window.Finish(); }
    }
}
