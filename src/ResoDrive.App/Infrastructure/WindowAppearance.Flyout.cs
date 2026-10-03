using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ResoDrive.App;

internal static partial class WindowAppearance
{
    private const int WindowCornerPreference = 33;
    private const int WindowBorderColor = 34;
    private const uint MoveWithoutResizingOrActivating = 0x0001 | 0x0004 | 0x0010;

    internal static void ApplyFlyoutFrame(System.Windows.Window window)
    {
        ApplyDarkTitleBar(window);
        var handle = new WindowInteropHelper(window).Handle;
        var preference = 2; // DWMWCP_ROUND; unsupported Windows versions keep their standard corners.
        _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref preference, sizeof(int));
        if (window.TryFindResource("BorderBrush") is System.Windows.Media.SolidColorBrush border)
        {
            var color = border.Color.R | (border.Color.G << 8) | (border.Color.B << 16);
            _ = DwmSetWindowAttribute(handle, WindowBorderColor, ref color, sizeof(int));
        }
    }

    internal static void PositionFlyout(System.Windows.Window window, System.Drawing.Point screenAnchor, System.Windows.Size preferredSize)
    {
        const double gap = 12;
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null) return;
        var screen = System.Windows.Forms.Screen.FromPoint(screenAnchor);
        var workArea = screen.WorkingArea;
        var bounds = screen.Bounds;
        var work = new Rect(workArea.X, workArea.Y, workArea.Width, workArea.Height);
        var monitor = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);

        void Move()
        {
            var scale = source.CompositionTarget.TransformToDevice;
            var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            var height = window.ActualHeight > 0 ? window.ActualHeight : Math.Min(preferredSize.Height, work.Height / scale.M22);
            var size = new System.Windows.Size(width * scale.M11, height * scale.M22);
            var point = CalculateFlyoutPosition(monitor, work, size, gap * scale.M11);
            _ = SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(point.X), (int)Math.Round(point.Y), 0, 0,
                MoveWithoutResizingOrActivating);
        }

        // Native coordinates avoid scaling the origin of monitors with different DPI.
        Move();
        var dpi = source.CompositionTarget.TransformToDevice;
        window.MaxWidth = Math.Max(1, Math.Min(preferredSize.Width, work.Width / dpi.M11 - 2 * gap));
        window.MaxHeight = Math.Max(1, Math.Min(preferredSize.Height, work.Height / dpi.M22 - 2 * gap));
        window.Width = window.MaxWidth;
        Move();
    }

    internal static System.Windows.Point CalculateFlyoutPosition(Rect monitor, Rect workArea, System.Windows.Size size, double gap)
    {
        var insetX = Math.Min(gap, workArea.Width / 2);
        var insetY = Math.Min(gap, workArea.Height / 2);
        var minimumX = workArea.Left + insetX;
        var minimumY = workArea.Top + insetY;
        var maximumX = Math.Max(minimumX, workArea.Right - size.Width - insetX);
        var maximumY = Math.Max(minimumY, workArea.Bottom - size.Height - insetY);
        return new System.Windows.Point(workArea.Left > monitor.Left ? minimumX : maximumX,
            workArea.Top > monitor.Top ? minimumY : maximumY);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
