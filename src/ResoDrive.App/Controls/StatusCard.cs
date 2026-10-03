using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace ResoDrive.App.Controls;

/// <summary>Draws a status rail as part of the card's rounded chrome.</summary>
public sealed class StatusCard : Border
{
    public static readonly DependencyProperty StatusBrushProperty = DependencyProperty.Register(
        nameof(StatusBrush), typeof(MediaBrush), typeof(StatusCard),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StatusRailWidthProperty = DependencyProperty.Register(
        nameof(StatusRailWidth), typeof(double), typeof(StatusCard),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double width && double.IsFinite(width) && width >= 0);

    private System.Windows.Size _geometrySize;
    private CornerRadius _geometryRadius;
    private Thickness _geometryThickness;
    private StreamGeometry? _outerGeometry;
    private StreamGeometry? _innerGeometry;
    private GeometryGroup? _borderGeometry;
    private Rect _innerBounds;

    public MediaBrush? StatusBrush
    {
        get => (MediaBrush?)GetValue(StatusBrushProperty);
        set => SetValue(StatusBrushProperty, value);
    }

    public double StatusRailWidth
    {
        get => (double)GetValue(StatusRailWidthProperty);
        set => SetValue(StatusRailWidthProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        UpdateGeometry();

        // One outer contour defines the entire card. Border is painted inside it,
        // and both background and rail use its deflated inner contour.
        dc.DrawGeometry(BorderBrush, null, _borderGeometry);
        if (_innerGeometry is null) return;
        dc.DrawGeometry(Background, null, _innerGeometry);
        if (StatusBrush is null || StatusRailWidth <= 0) return;

        dc.PushClip(_innerGeometry);
        dc.DrawRectangle(StatusBrush, null,
            new Rect(_innerBounds.X, _innerBounds.Y,
                Math.Min(StatusRailWidth, _innerBounds.Width), _innerBounds.Height));
        dc.Pop();
    }

    private void UpdateGeometry()
    {
        var radius = CornerRadius;
        var thickness = BorderThickness;
        if (_outerGeometry is not null && _geometrySize == RenderSize &&
            _geometryRadius == radius && _geometryThickness == thickness) return;
        _geometrySize = RenderSize;
        _geometryRadius = radius;
        _geometryThickness = thickness;

        var outer = new Rect(RenderSize);
        var radii = CornerRadii.Create(radius).Fit(outer.Size);
        _outerGeometry = RoundedGeometry(outer, radii);
        _innerBounds = new Rect(Math.Min(thickness.Left, outer.Width), Math.Min(thickness.Top, outer.Height),
            Math.Max(0, outer.Width - thickness.Left - thickness.Right),
            Math.Max(0, outer.Height - thickness.Top - thickness.Bottom));
        _innerGeometry = _innerBounds.Width > 0 && _innerBounds.Height > 0
            ? RoundedGeometry(_innerBounds, radii.Deflate(thickness).Fit(_innerBounds.Size)) : null;
        _borderGeometry = null;
        if (thickness.Left > 0 || thickness.Top > 0 || thickness.Right > 0 || thickness.Bottom > 0)
        {
            _borderGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            _borderGeometry.Children.Add(_outerGeometry);
            if (_innerGeometry is not null) _borderGeometry.Children.Add(_innerGeometry);
            _borderGeometry.Freeze();
        }
    }

    private static StreamGeometry RoundedGeometry(Rect bounds, CornerRadii radii)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new System.Windows.Point(bounds.Left + radii.TopLeft.Width, bounds.Top), true, true);
            context.LineTo(new(bounds.Right - radii.TopRight.Width, bounds.Top), true, false);
            Corner(context, new(bounds.Right, bounds.Top + radii.TopRight.Height), radii.TopRight);
            context.LineTo(new(bounds.Right, bounds.Bottom - radii.BottomRight.Height), true, false);
            Corner(context, new(bounds.Right - radii.BottomRight.Width, bounds.Bottom), radii.BottomRight);
            context.LineTo(new(bounds.Left + radii.BottomLeft.Width, bounds.Bottom), true, false);
            Corner(context, new(bounds.Left, bounds.Bottom - radii.BottomLeft.Height), radii.BottomLeft);
            context.LineTo(new(bounds.Left, bounds.Top + radii.TopLeft.Height), true, false);
            Corner(context, new(bounds.Left + radii.TopLeft.Width, bounds.Top), radii.TopLeft);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void Corner(StreamGeometryContext context, System.Windows.Point point, System.Windows.Size radius)
    {
        if (radius.Width == 0 || radius.Height == 0) context.LineTo(point, true, false);
        else context.ArcTo(point, radius, 0, false, SweepDirection.Clockwise, true, false);
    }

    private readonly record struct CornerRadii(System.Windows.Size TopLeft, System.Windows.Size TopRight,
        System.Windows.Size BottomRight, System.Windows.Size BottomLeft)
    {
        internal static CornerRadii Create(CornerRadius radius) => new(
            new(radius.TopLeft, radius.TopLeft), new(radius.TopRight, radius.TopRight),
            new(radius.BottomRight, radius.BottomRight), new(radius.BottomLeft, radius.BottomLeft));

        internal CornerRadii Deflate(Thickness thickness) => new(
            Deflate(TopLeft, thickness.Left, thickness.Top), Deflate(TopRight, thickness.Right, thickness.Top),
            Deflate(BottomRight, thickness.Right, thickness.Bottom), Deflate(BottomLeft, thickness.Left, thickness.Bottom));

        internal CornerRadii Fit(System.Windows.Size size)
        {
            var scale = Math.Min(1, Math.Min(Math.Min(Ratio(size.Width, TopLeft.Width, TopRight.Width),
                Ratio(size.Width, BottomLeft.Width, BottomRight.Width)), Math.Min(
                Ratio(size.Height, TopLeft.Height, BottomLeft.Height), Ratio(size.Height, TopRight.Height, BottomRight.Height))));
            return new(Scale(TopLeft, scale), Scale(TopRight, scale), Scale(BottomRight, scale), Scale(BottomLeft, scale));
        }

        private static double Ratio(double available, double first, double second)
        {
            var largest = Math.Max(first, second);
            return largest == 0 ? 1 : available / largest / (first / largest + second / largest);
        }
        private static System.Windows.Size Scale(System.Windows.Size radius, double scale) => new(radius.Width * scale, radius.Height * scale);
        private static System.Windows.Size Deflate(System.Windows.Size radius, double horizontal, double vertical) =>
            new(Math.Max(0, radius.Width - horizontal), Math.Max(0, radius.Height - vertical));
    }
}
