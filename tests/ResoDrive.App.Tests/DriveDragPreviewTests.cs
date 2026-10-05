using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResoDrive.App.Controls;
using WpfBorder = System.Windows.Controls.Border;
using WpfCanvas = System.Windows.Controls.Canvas;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace ResoDrive.App.Tests;

public sealed class DriveDragPreviewTests
{
    [Theory]
    [InlineData(34, 72)]
    [InlineData(40, 140)]
    public Task RowsAwayFromTheOriginHaveACompletePreview(double left, double top) => RunOnStaAsync(() =>
    {
        var (decorator, owner, source) = Scene(300);
        WpfCanvas.SetLeft(source, left);
        WpfCanvas.SetTop(source, top);
        decorator.UpdateLayout();
        using var preview = DriveDragPreviewAdorner.TryCreate(owner, source, new WpfPoint(left + 20, top + 30), false);
        Assert.NotNull(preview);
        preview.SetPosition(new WpfPoint(left + 20, 110));
        decorator.UpdateLayout();
        Assert.True(Pixel(Render(preview), (int)left + 30, 100)[2] > 0,
            "Rows below or beside the origin must not produce a clipped or blank snapshot.");
    });

    [Fact]
    public Task RecyclingTheSourceCannotChangeTheDraggedDriveAndCleanupIsIdempotent() => RunOnStaAsync(() =>
    {
        var (decorator, owner, source) = Scene();
        using var preview = DriveDragPreviewAdorner.TryCreate(owner, source, new WpfPoint(20, 30), false);
        Assert.NotNull(preview);
        Assert.False(preview.IsHitTestVisible);
        Assert.False(preview.Focusable);
        var layer = AdornerLayer.GetAdornerLayer(owner)!;
        Assert.Single(layer.GetAdorners(owner)!);

        source.Background = System.Windows.Media.Brushes.Blue;
        source.DataContext = "Another drive reused this container";
        preview.SetPosition(new WpfPoint(20, 80));
        decorator.UpdateLayout();
        var pixel = Pixel(Render(preview), 30, 80);
        Assert.True(pixel[2] > 0, "The floating snapshot should still show the red original.");
        Assert.Equal(0, pixel[0]);

        preview.Dispose();
        preview.Dispose();
        Assert.Null(layer.GetAdorners(owner));
    });

    [Fact]
    public Task PreviewCannotPaintOutsideTheListViewport() => RunOnStaAsync(() =>
    {
        var (decorator, owner, source) = Scene();
        using var preview = DriveDragPreviewAdorner.TryCreate(owner, source, new WpfPoint(20, 30), false);
        Assert.NotNull(preview);
        preview.SetPosition(new WpfPoint(20, 145));
        decorator.UpdateLayout();
        var image = Render(preview);
        Assert.True(Pixel(image, 30, 119)[3] > 0);
        Assert.Equal(0, Pixel(image, 30, 125)[3]);
    });

    [Fact]
    public Task HighContrastKeepsThePreviewOpaque() => RunOnStaAsync(() =>
    {
        var (decorator, owner, source) = Scene();
        using var preview = DriveDragPreviewAdorner.TryCreate(owner, source, new WpfPoint(20, 30), true);
        Assert.NotNull(preview);
        preview.SetPosition(new WpfPoint(20, 80));
        decorator.UpdateLayout();
        Assert.Equal(255, Pixel(Render(preview), 30, 80)[3]);
    });

    [Fact]
    public Task UnavailableOrOversizedSnapshotsLeaveReorderingWithoutAPreview() => RunOnStaAsync(() =>
    {
        var detached = new WpfBorder { Width = 100, Height = 60 };
        Assert.Null(DriveDragPreviewAdorner.TryCreate(detached, detached, new WpfPoint(), false));
        var (decorator, owner, source) = Scene();
        source.Width = 100_000;
        source.Height = 100_000;
        decorator.UpdateLayout();
        Assert.Null(DriveDragPreviewAdorner.TryCreate(owner, source, new WpfPoint(), false));
        Assert.Null(AdornerLayer.GetAdornerLayer(owner)!.GetAdorners(owner));
    });

    [Fact]
    public Task ScrollbarsAndPaddingAreNotDropTargetsOrAutoScrollAreas() => RunOnStaAsync(() =>
    {
        var (_, list) = ListScene(1);
        var viewport = DriveOrderViewport.Bounds(list);
        Assert.True(viewport.Left > 0 && viewport.Top > 0);
        Assert.True(viewport.Right < list.ActualWidth - 8);
        var emptySpace = new WpfPoint(viewport.Left + 20, viewport.Bottom - 5);
        var target = DriveOrderViewport.FindDropTarget(list, emptySpace);
        Assert.NotNull(target);
        Assert.True(target.Value.After);
        Assert.Same(list.ItemContainerGenerator.ContainerFromIndex(0), target.Value.Container);

        var scrollbar = new WpfPoint(list.ActualWidth - 8, emptySpace.Y);
        Assert.Null(DriveOrderViewport.FindDropTarget(list, scrollbar));
        Assert.Equal(0, DriveOrderViewport.ScrollDirection(list, scrollbar));
        Assert.Null(DriveOrderViewport.FindDropTarget(list, new WpfPoint(viewport.Left - 1, emptySpace.Y)));
        Assert.Equal(-1, DriveOrderViewport.ScrollDirection(list, new WpfPoint(emptySpace.X, viewport.Top + 1)));
        Assert.Equal(1, DriveOrderViewport.ScrollDirection(list, emptySpace));
        Assert.Equal(0, DriveOrderViewport.ScrollDirection(list,
            new WpfPoint(emptySpace.X, viewport.Top + viewport.Height / 2)));
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public Task PreviewFollowsTheContentViewportWhenScrollbarWidthChanges(double scale) => RunOnStaAsync(() =>
    {
        var (decorator, list) = ListScene(1);
        var source = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        using var preview = DriveDragPreviewAdorner.TryCreate(list, source,
            source.TranslatePoint(new WpfPoint(20, 30), list), false);
        Assert.NotNull(preview);
        var viewport = DriveOrderViewport.Bounds(list);
        preview.SetPosition(new WpfPoint(viewport.Left + 20, viewport.Top + 35));
        decorator.UpdateLayout();
        var oldRight = viewport.Right;

        var viewer = FindVisual<ScrollViewer>(list)!;
        var scrollbar = Assert.IsType<System.Windows.Controls.Primitives.ScrollBar>(viewer.Template
            .FindName("PART_VerticalScrollBar", viewer));
        scrollbar.Width = scrollbar.ActualWidth + 12;
        decorator.UpdateLayout();
        viewport = DriveOrderViewport.Bounds(list);
        Assert.True(viewport.Right < oldRight);
        // No pointer movement: a layout change must invalidate the cached drawing.
        var image = Render(preview, scale);
        var y = (int)Math.Round((viewport.Top + 30) * scale);
        Assert.True(Pixel(image, (int)Math.Floor((viewport.Right - 3) * scale), y)[3] > 0);
        Assert.Equal(0, Pixel(image, (int)Math.Ceiling((viewport.Right + 3) * scale), y)[3]);

        list.Height = 50;
        decorator.UpdateLayout();
        viewport = DriveOrderViewport.Bounds(list);
        image = Render(preview, scale);
        Assert.Equal(0, Pixel(image, (int)((viewport.Left + 20) * scale),
            (int)Math.Ceiling((viewport.Bottom + 3) * scale))[3]);
    });

    [Fact]
    public Task RecyclingARealListRowKeepsTheOriginalGhostAndUsesTheCurrentDropRow() => RunOnStaAsync(() =>
    {
        var (decorator, list) = ListScene(80);
        // InputHitTest requires a presentation source; a detached render scene is invisible.
        // This host stays hidden and offscreen, without activating a desktop window.
        using var host = new HwndSource(new HwndSourceParameters("Drive drag recycling regression")
        {
            Width = 300, Height = 120, PositionX = -10_000, PositionY = -10_000, WindowStyle = 0,
        }) { RootVisual = decorator };
        decorator.UpdateLayout();
        Assert.True(list.IsVisible);
        var source = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        using var preview = DriveDragPreviewAdorner.TryCreate(list, source,
            source.TranslatePoint(new WpfPoint(20, 30), list), false);
        Assert.NotNull(preview);
        var viewer = FindVisual<ScrollViewer>(list)!;
        for (var index = 5; index <= 40 &&
            (source.DataContext is not string item || item == "Drive 0" ||
                !ReferenceEquals(source, list.ItemContainerGenerator.ContainerFromItem(item))); index += 5)
        {
            viewer.ScrollToVerticalOffset(index);
            decorator.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            decorator.UpdateLayout();
        }
        Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));
        var recycledItem = Assert.IsType<string>(source.DataContext);
        Assert.NotEqual("Drive 0", recycledItem);
        Assert.Same(source, list.ItemContainerGenerator.ContainerFromItem(recycledItem));
        source.Background = System.Windows.Media.Brushes.Blue;
        var viewport = DriveOrderViewport.Bounds(list);
        var point = new WpfPoint(viewport.Left + 20, viewport.Top + 30);
        preview.SetPosition(point);
        decorator.UpdateLayout();
        var pixel = Pixel(Render(preview), (int)point.X, (int)point.Y);
        Assert.True(pixel[2] > 0);
        Assert.Equal(0, pixel[0]);
        var target = DriveOrderViewport.FindDropTarget(list, point);
        Assert.NotNull(target);
        Assert.NotEqual("Drive 0", target.Value.Container.DataContext);

        preview.Dispose();
        preview.SetPosition(point);
        preview.Hide();
        list.Width = 260;
        decorator.UpdateLayout();
        Assert.Null(AdornerLayer.GetAdornerLayer(list)!.GetAdorners(list));
    });

    [Fact]
    public Task StationaryPointerUsesCurrentRowGeometryAfterStatusHeightChanges() => RunOnStaAsync(() =>
    {
        var (decorator, list) = ListScene(2);
        // Leave room for the logical fixture at high display scaling.
        using var host = new HwndSource(new HwndSourceParameters("Drive status height regression")
        {
            Width = 600, Height = 400, PositionX = -10_000, PositionY = -10_000, WindowStyle = 0,
        }) { RootVisual = decorator };
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        decorator.UpdateLayout();
        var first = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        var second = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
        var point = second.TranslatePoint(new WpfPoint(20, 8), list);
        var initial = DriveOrderViewport.FindDropTarget(list, point);
        Assert.NotNull(initial);
        Assert.Same(second, initial.Value.Container);
        first.Height += 28; // A conditional status footer expands above the pointer.
        decorator.UpdateLayout();
        var expanded = DriveOrderViewport.FindDropTarget(list, point);
        Assert.NotNull(expanded);
        Assert.Same(first, expanded.Value.Container);
        Assert.True(expanded.Value.After);
        first.Height -= 28;
        decorator.UpdateLayout();
        var collapsed = DriveOrderViewport.FindDropTarget(list, point);
        Assert.NotNull(collapsed);
        Assert.Same(second, collapsed.Value.Container);
    });

    [Fact]
    public Task UnrealizedRowsCannotBecomeDropTargets() => RunOnStaAsync(() =>
    {
        var (_, list) = ListScene(80);
        Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(79));
        Assert.Null(DriveOrderViewport.FindDropTarget(list, new WpfPoint(12, list.ActualHeight)));
        Assert.Null(DriveOrderViewport.FindDropTarget(new ListBox(), new WpfPoint()));
    });

    private static (AdornerDecorator Decorator, ListBox List) ListScene(int rows)
    {
        var list = new ListBox
        {
            Width = 300, Height = 120, Padding = new(4, 6, 4, 6),
            ItemsSource = Enumerable.Range(0, rows).Select(index => $"Drive {index}").ToArray(),
            Template = (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBox">
                    <ScrollViewer CanContentScroll="True" Padding="{TemplateBinding Padding}"
                        VerticalScrollBarVisibility="Visible" HorizontalScrollBarVisibility="Disabled">
                        <ItemsPresenter />
                    </ScrollViewer>
                </ControlTemplate>
                """),
            ItemContainerStyle = (Style)XamlReader.Parse("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
                    <Setter Property="Height" Value="60" />
                    <Setter Property="Background" Value="Red" />
                    <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                    <Setter Property="Template">
                        <Setter.Value><ControlTemplate TargetType="ListBoxItem">
                            <Border Background="{TemplateBinding Background}"><ContentPresenter /></Border>
                        </ControlTemplate></Setter.Value>
                    </Setter>
                </Style>
                """),
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var decorator = new AdornerDecorator { Child = list };
        decorator.Measure(new WpfSize(300, 120));
        decorator.Arrange(new Rect(0, 0, 300, 120));
        decorator.UpdateLayout();
        return (decorator, list);
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }

    private static (AdornerDecorator Decorator, WpfCanvas Owner, WpfBorder Source) Scene(double height = 120)
    {
        var source = new WpfBorder { Width = 200, Height = 60, Background = System.Windows.Media.Brushes.Red };
        var owner = new WpfCanvas { Width = 300, Height = height };
        owner.Children.Add(source);
        var decorator = new AdornerDecorator { Child = owner };
        decorator.Measure(new WpfSize(300, height));
        decorator.Arrange(new Rect(0, 0, 300, height));
        decorator.UpdateLayout();
        return (decorator, owner, source);
    }

    private static RenderTargetBitmap Render(Visual visual, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap((int)(300 * scale), (int)(300 * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static async Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
