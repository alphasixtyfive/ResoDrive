using System.Windows;

namespace ResoDrive.App.Tests;

public sealed class TrayFlyoutPlacementTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 1920, 1032, 1488, 800)] // Bottom taskbar.
    [InlineData(0, 0, 0, 48, 1920, 1032, 1488, 60)] // Top taskbar.
    [InlineData(0, 0, 48, 0, 1872, 1080, 60, 848)] // Left taskbar.
    [InlineData(0, 0, 0, 0, 1872, 1080, 1440, 848)] // Right taskbar.
    [InlineData(-1920, -1080, -1920, -1080, 1920, 1032, -432, -280)]
    public void FlyoutUsesTheTaskbarEdgeOnItsMonitor(double monitorX, double monitorY,
        double workX, double workY, double workWidth, double workHeight, double expectedX, double expectedY)
    {
        var point = WindowAppearance.CalculateFlyoutPosition(new Rect(monitorX, monitorY, 1920, 1080),
            new Rect(workX, workY, workWidth, workHeight), new Size(420, 220), 12);
        Assert.Equal(new Point(expectedX, expectedY), point);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void GrowingQueueStaysAboveTheTaskbarAtDifferentScales(double scale)
    {
        var work = new Rect(1920, 0, 2560, 1392);
        var monitor = new Rect(1920, 0, 2560, 1440);
        var compact = WindowAppearance.CalculateFlyoutPosition(monitor, work, new Size(420 * scale, 120 * scale), 12 * scale);
        var busy = WindowAppearance.CalculateFlyoutPosition(monitor, work, new Size(420 * scale, 400 * scale), 12 * scale);

        Assert.Equal(compact.X, busy.X);
        Assert.Equal(work.Bottom - 12 * scale, compact.Y + 120 * scale);
        Assert.Equal(work.Bottom - 12 * scale, busy.Y + 400 * scale);
        Assert.True(busy.Y < compact.Y);
    }

    [Fact]
    public void SmallWorkAreaKeepsThePositionInsideItsBounds()
    {
        var work = new Rect(-200, 0, 200, 180);
        var point = WindowAppearance.CalculateFlyoutPosition(work, work, new Size(420, 400), 12);
        Assert.Equal(new Point(-188, 12), point);
    }
}
