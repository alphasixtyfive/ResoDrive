using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace ResoDrive.App.Controls;

internal sealed class DriveDragPreviewAdorner : Adorner, IDisposable
{
    private const long MaximumSnapshotPixels = 4_194_304;
    private readonly AdornerLayer _layer;
    private readonly BitmapSource _snapshot;
    private readonly WpfSize _size;
    private readonly double _left;
    private readonly double _pointerOffset;
    private readonly System.Windows.Media.Pen? _outline;
    private Rect _viewport;
    private double _top;
    private bool _disposed;

    private DriveDragPreviewAdorner(UIElement owner, AdornerLayer layer, BitmapSource snapshot,
        WpfSize size, WpfPoint sourcePosition, WpfPoint pressPoint, bool highContrast) : base(owner)
    {
        _layer = layer;
        _snapshot = snapshot;
        _size = size;
        _left = sourcePosition.X;
        _pointerOffset = pressPoint.Y - sourcePosition.Y;
        _top = sourcePosition.Y;
        _viewport = DriveOrderViewport.Bounds(owner);
        IsHitTestVisible = false;
        Focusable = false;
        Opacity = highContrast ? 1 : 0.45;
        _outline = highContrast ? new System.Windows.Media.Pen(System.Windows.SystemColors.HighlightBrush, 2) : null;
    }

    internal static DriveDragPreviewAdorner? TryCreate(UIElement owner, FrameworkElement source,
        WpfPoint pressPoint, bool highContrast)
    {
        var layer = AdornerLayer.GetAdornerLayer(owner);
        if (layer is null || source.ActualWidth <= 0 || source.ActualHeight <= 0)
            return null;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(source);
            var width = checked((int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX));
            var height = checked((int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY));
            if (width <= 0 || height <= 0 || (long)width * height > MaximumSnapshotPixels)
                return null;
            // A frozen image keeps the original drive when scrolling recycles its row container.
            var snapshot = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX,
                dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bounds = new Rect(source.RenderSize);
            var offset = VisualTreeHelper.GetOffset(source);
            var brush = new VisualBrush(source)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(new WpfPoint(offset.X, offset.Y), source.RenderSize),
                Stretch = Stretch.Fill,
            };
            var surface = new DrawingVisual();
            using (var drawing = surface.RenderOpen())
                drawing.DrawRectangle(brush, null, bounds);
            try { snapshot.Render(surface); }
            finally { brush.Visual = null; }
            snapshot.Freeze();
            var preview = new DriveDragPreviewAdorner(owner, layer, snapshot, source.RenderSize,
                source.TranslatePoint(new WpfPoint(), owner), pressPoint, highContrast);
            layer.Add(preview);
            owner.LayoutUpdated += preview.Owner_LayoutUpdated;
            return preview;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            NotSupportedException or OverflowException)
        {
            // Reordering and native cursor feedback remain available without a decorative snapshot.
            return null;
        }
    }

    internal void SetPosition(WpfPoint point)
    {
        if (_disposed)
            return;
        var top = point.Y - _pointerOffset + 4;
        if (_top == top && Visibility == Visibility.Visible) return;
        _top = top;
        Visibility = Visibility.Visible;
        InvalidateVisual();
    }

    internal void Hide()
    {
        if (!_disposed)
            Visibility = Visibility.Hidden;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var viewport = DriveOrderViewport.Bounds(AdornedElement);
        if (viewport.IsEmpty)
            return;
        drawingContext.PushClip(new RectangleGeometry(viewport));
        var bounds = new Rect(new WpfPoint(_left, _top), _size);
        drawingContext.DrawImage(_snapshot, bounds);
        if (_outline is not null)
            drawingContext.DrawRectangle(null, _outline, bounds);
        drawingContext.Pop();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AdornedElement.LayoutUpdated -= Owner_LayoutUpdated;
        _layer.Remove(this);
    }

    private void Owner_LayoutUpdated(object? sender, EventArgs e)
    {
        var viewport = DriveOrderViewport.Bounds(AdornedElement);
        if (_viewport == viewport)
            return;
        _viewport = viewport;
        InvalidateVisual();
    }
}
