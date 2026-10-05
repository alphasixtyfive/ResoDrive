using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace ResoDrive.App.Tests;

internal static class LogWindowTests
{
    internal static void VerifyWithApplicationResources()
    {
        var previousData = Environment.GetEnvironmentVariable("RDRIVE_DATA_DIR");
        Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var main = new MainWindow(); // Do not show it: no host or startup work.
            var model = (ShellViewModel)main.DataContext;
            model.AddLogEntry("Upload error · A long drive name", "First line\nSecond line", LogSeverity.Error);
            var entry = Assert.Single(model.Log);
            try
            {
                var list = Assert.IsType<ListBox>(main.FindName("LogRows"));
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
                var content = RenderEntry(list, entry, 400);
                var message = Assert.Single(Descendants(content).OfType<TextBlock>(),
                    block => block.Inlines.OfType<Run>().Any(run => run.Text == entry.Detail));
                Assert.Equal(TextWrapping.Wrap, message.TextWrapping);
                Assert.Equal(TextTrimming.None, message.TextTrimming);
                Assert.DoesNotContain(Descendants(content), element => element is Button);
                var longEntry = new LogRow("Upload error · " + new string('W', 128),
                    string.Join("\n", Enumerable.Range(0, 100).Select(index => $"Detailed error line {index}")),
                    DateTimeOffset.Now, LogSeverity.Error);
                var longContent = RenderEntry(list, longEntry, 300);
                var longMessage = Assert.Single(Descendants(longContent).OfType<TextBlock>(),
                    block => block.Inlines.OfType<Run>().Any(run => run.Text == longEntry.Detail));
                Assert.True(longMessage.ActualHeight > 240, "A tall event must remain readable by scrolling.");
                Assert.True(longMessage.ActualWidth <= 160, "The event must wrap inside the narrow log column.");
                for (var index = 0; index < 100; index++)
                    model.AddLogEntry($"Mounted · Drive {index}", "Connected", LogSeverity.Success,
                        DateTimeOffset.Now.AddMinutes(-index - 1));
                model.AddLogEntry(longEntry.Title, longEntry.Detail, longEntry.Severity, DateTimeOffset.Now.AddMinutes(1));
                var page = Assert.IsType<Grid>(main.FindName("LogPage"));
                page.Visibility = Visibility.Visible;
                page.Measure(new System.Windows.Size(420, 360));
                page.Arrange(new Rect(0, 0, 420, 360));
                page.UpdateLayout();
                var scroll = Assert.Single(Descendants(list).OfType<ScrollViewer>());
                Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
                var bar = Assert.Single(Descendants(list).OfType<ScrollBar>(), item => item.Orientation == Orientation.Vertical);
                var barLeft = bar.TranslatePoint(new Point(), list).X;
                Assert.True(barLeft + bar.ActualWidth <= list.ActualWidth, "The scrollbar must stay inside the log.");
                var firstRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var visibleMessage = Assert.Single(Descendants(firstRow).OfType<TextBlock>(),
                    block => block.Inlines.OfType<Run>().Any(run => run.Text == longEntry.Detail));
                var textRight = visibleMessage.TranslatePoint(new Point(visibleMessage.ActualWidth, 0), list).X;
                Assert.True(textRight <= barLeft - 8, "The scrollbar must leave space beside the event text.");
                scroll.ScrollToVerticalOffset(37);
                page.UpdateLayout();
                Assert.InRange(scroll.VerticalOffset, 36.9, 37.1);
            }
            finally { main.Close(); }
        }
        finally { Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", previousData); }
    }

    private static ContentPresenter RenderEntry(ListBox list, LogRow entry, double width)
    {
        var content = new ContentPresenter { Content = entry, ContentTemplate = list.ItemTemplate };
        content.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        content.Arrange(new Rect(0, 0, width, content.DesiredSize.Height));
        content.UpdateLayout();
        return content;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
