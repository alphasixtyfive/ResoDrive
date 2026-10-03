using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ResoDrive.App.Controls;

namespace ResoDrive.App.Tests;

public sealed class StatusCardTests
{
    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void RailFollowsTheCardContourAtFractionalDpi(double scale) => RunOnStaThread(() =>
    {
        var card = Card(withContent: true);
        var image = Render(card, 160, 62, scale);

        AssertRailInsideContour(image, card.CornerRadius, scale);
        Assert.Equal(0, image.Pixel(0, 0).Alpha);
        Assert.Equal(0, image.Pixel(0, image.Height - 1).Alpha);
        Assert.True(IsGreen(image.At(2, 31, scale)));
        var outline = image.At(0, 31, scale);
        Assert.True(outline.Red > 120 && outline.Green > 120 && outline.Blue > 120);
    });

    [Fact]
    public void ResizingAndChangingTheRadiusRebuildTheSharedContour() => RunOnStaThread(() =>
    {
        var card = Card(withContent: true);
        AssertRailInsideContour(Render(card, 160, 62, 1.25), card.CornerRadius, 1.25);
        card.CornerRadius = new(20);
        AssertRailInsideContour(Render(card, 92, 28, 1.25), card.CornerRadius, 1.25);
        card.CornerRadius = new(3, 11, 19, 7);
        card.BorderThickness = new(2.25, 1.5, 5, 3.25);
        var image = Render(card, 160, 62, 1.25);

        AssertRailInsideContour(image, card.CornerRadius, 1.25);
        Assert.False(IsGreen(image.At(1, 31, 1.25)));
        Assert.True(IsGreen(image.At(3.5, 31, 1.25)));
    });

    [Theory]
    [InlineData(9d, 7d)]
    [InlineData(5d, 5d)]
    [InlineData(2d, 2d)]
    public void NarrowCardsAndOversizedCornersCannotLetTheRailEscape(double width, double height) => RunOnStaThread(() =>
    {
        var card = Card(withContent: true);
        card.CornerRadius = new(40);
        card.StatusRailWidth = 20;
        var image = Render(card, width, height, 2);

        AssertRailInsideContour(image, card.CornerRadius, 2, requireRail: width > 2 && height > 2);
        card.BorderThickness = new(12, 9, 12, 9);
        Assert.DoesNotContain(Render(card, width, height, 2).Pixels(), IsGreen);
    });

    [Fact]
    public void ColorChangesAndRailWidthChangesUpdateTheExistingCard() => RunOnStaThread(() =>
    {
        var brush = new SolidColorBrush(Colors.Lime);
        var card = Card();
        card.CornerRadius = new(0);
        card.StatusBrush = brush;
        Assert.True(IsGreen(Render(card, 60, 30).At(2, 15)));

        brush.Color = Colors.Red;
        var red = Render(card, 60, 30).At(2, 15);
        Assert.True(red.Red > 200 && red.Green < 20 && red.Blue < 20);

        card.StatusBrush = Brushes.Lime;
        card.StatusRailWidth = 8;
        Assert.True(IsGreen(Render(card, 60, 30).At(7, 15)));
        card.StatusRailWidth = 0;
        Assert.DoesNotContain(Render(card, 60, 30).Pixels(), IsGreen);
        card.StatusRailWidth = 3;
        card.StatusBrush = null;
        Assert.DoesNotContain(Render(card, 60, 30).Pixels(), IsGreen);
    });

    [Fact]
    public void MaximumFiniteCornersAndRailWidthKeepTheCardContourValid() => RunOnStaThread(() =>
    {
        var card = Card();
        card.CornerRadius = new(double.MaxValue);
        card.StatusRailWidth = double.MaxValue;
        var image = Render(card, 60, 30, 1.5);

        Assert.Equal(0, image.Pixel(0, 0).Alpha);
        Assert.Equal(0, image.Pixel(0, image.Height - 1).Alpha);
        Assert.True(IsGreen(image.At(30, 15, 1.5)));
        Assert.True(image.At(0, 15, 1.5).Alpha > 200);
    });

    [Fact]
    public void ChildLayoutKeepsBorderAndPaddingSemantics() => RunOnStaThread(() =>
    {
        var card = Card();
        var child = new Grid();
        card.Child = child;
        card.BorderThickness = new(2, 3, 4, 5);
        card.Padding = new(7, 11, 13, 17);
        Render(card, 100, 80);

        Assert.Equal(new System.Windows.Size(74, 44), child.RenderSize);
        Assert.Equal(new System.Windows.Point(9, 14), child.TranslatePoint(new(), card));
    });

    [Fact]
    public void BorderDoesNotPaintBehindATranslucentBackground() => RunOnStaThread(() =>
    {
        var card = Card();
        card.StatusBrush = null;
        card.Background = new SolidColorBrush(Color.FromArgb(128, 43, 43, 43));
        var image = Render(card, 60, 30);

        Assert.InRange(image.At(30, 15).Alpha, 127, 129);
        Assert.True(image.At(0, 15).Alpha > 200);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullAndTransparentBackgroundsKeepTheInteriorTransparent(bool transparentBrush) => RunOnStaThread(() =>
    {
        var card = Card();
        card.Background = transparentBrush ? Brushes.Transparent : null;
        var image = Render(card, 60, 30);

        Assert.Equal(0, image.At(30, 15).Alpha);
        Assert.True(IsGreen(image.At(2, 15)));
        Assert.True(image.At(0, 15).Alpha > 200);
    });

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidRailWidthsAreRejected(double width) => RunOnStaThread(() =>
        Assert.Throws<ArgumentException>(() => Card().StatusRailWidth = width));

    private static StatusCard Card(bool withContent = false)
    {
        var card = new StatusCard
        {
            Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)), BorderBrush = Brushes.White,
            BorderThickness = new(1), CornerRadius = new(8), StatusBrush = Brushes.Lime,
        };
        if (withContent)
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new() { Width = new GridLength(3) });
            content.ColumnDefinitions.Add(new() { Width = new GridLength(53) });
            content.ColumnDefinitions.Add(new());
            var icon = new Border { Width = 36, Height = 36, Background = Brushes.SlateBlue,
                CornerRadius = new(6), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 1);
            content.Children.Add(icon);
            var label = new TextBlock { Text = "Cloud drive", Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 2);
            content.Children.Add(label);
            card.Child = content;
        }
        return card;
    }

    private static PixelImage Render(StatusCard card, double width, double height, double scale = 1)
    {
        card.Width = width;
        card.Height = height;
        card.Measure(new(width, height));
        card.Arrange(new Rect(0, 0, width, height));
        card.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(card);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new(bitmap.PixelWidth, bitmap.PixelHeight, width, height, pixels);
    }

    private static void AssertRailInsideContour(PixelImage image, CornerRadius radius, double scale, bool requireRail = true)
    {
        var radii = new[] { radius.TopLeft, radius.TopRight, radius.BottomRight, radius.BottomLeft };
        var factor = Math.Min(1, Math.Min(Math.Min(Ratio(image.LogicalWidth, radii[0] + radii[1]),
            Ratio(image.LogicalWidth, radii[2] + radii[3])), Math.Min(Ratio(image.LogicalHeight, radii[0] + radii[3]),
            Ratio(image.LogicalHeight, radii[1] + radii[2]))));
        for (var index = 0; index < radii.Length; index++) radii[index] *= factor;
        var green = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                if (!IsGreen(image.Pixel(x, y))) continue;
                green++;
                var point = new System.Windows.Point((x + .5) / scale, (y + .5) / scale);
                var corners = new[]
                {
                    (new System.Windows.Point(radii[0], radii[0]), radii[0], point.X < radii[0] && point.Y < radii[0]),
                    (new System.Windows.Point(image.LogicalWidth - radii[1], radii[1]), radii[1], point.X > image.LogicalWidth - radii[1] && point.Y < radii[1]),
                    (new System.Windows.Point(image.LogicalWidth - radii[2], image.LogicalHeight - radii[2]), radii[2], point.X > image.LogicalWidth - radii[2] && point.Y > image.LogicalHeight - radii[2]),
                    (new System.Windows.Point(radii[3], image.LogicalHeight - radii[3]), radii[3], point.X < radii[3] && point.Y > image.LogicalHeight - radii[3]),
                };
                foreach (var (center, r, applies) in corners)
                    if (applies) Assert.True((point - center).Length <= r + .5 / scale, $"Rail escaped the card at pixel {x},{y}.");
            }
        if (requireRail) Assert.True(green > 0, "The status rail was not rendered.");
    }

    private static double Ratio(double available, double requested) => requested == 0 ? 1 : available / requested;
    private static bool IsGreen(Pixel pixel) => pixel.Alpha > 128 && pixel.Green > 2 * pixel.Red && pixel.Green > 2 * pixel.Blue;

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private readonly record struct Pixel(byte Red, byte Green, byte Blue, byte Alpha);
    private sealed record PixelImage(int Width, int Height, double LogicalWidth, double LogicalHeight, byte[] Data)
    {
        internal Pixel Pixel(int x, int y)
        {
            var offset = (y * Width + x) * 4;
            return new(Data[offset + 2], Data[offset + 1], Data[offset], Data[offset + 3]);
        }
        internal Pixel At(double x, double y, double scale = 1) =>
            Pixel(Math.Min(Width - 1, (int)(x * scale)), Math.Min(Height - 1, (int)(y * scale)));
        internal IEnumerable<Pixel> Pixels()
        {
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++) yield return Pixel(x, y);
        }
    }
}
