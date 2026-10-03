using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ResoDrive.App;

internal static partial class WindowAppearance
{
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int UseImmersiveDarkMode = 20;
    private const int ShowWindowRestore = 9;

    /// <summary>
    /// Applies the shared native appearance to a dialog and keeps its first,
    /// not-yet-positioned frame out of the compositor. WPF can otherwise briefly
    /// present a size-to-content or constrained owned window at the default screen
    /// origin before CenterOwner has completed its first layout pass.
    /// </summary>
    internal static void PrepareDialog(
        System.Windows.Window window,
        double margin = 24,
        bool constrainMaximum = true)
    {
        ArgumentNullException.ThrowIfNull(window);

        var requestedOpacity = window.Opacity;
        window.Opacity = 0;
        window.SourceInitialized += (_, _) =>
        {
            ApplyDarkTitleBar(window);
            ConstrainToWorkArea(window, margin, constrainMaximum);
        };

        EventHandler? reveal = null;
        reveal = (_, _) =>
        {
            window.ContentRendered -= reveal;
            window.Opacity = requestedOpacity;
        };
        window.ContentRendered += reveal;
    }

    internal static void ApplyDarkTitleBar(System.Windows.Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(
                handle,
                UseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int)
            );
        }
    }

    internal static void ConstrainToWorkArea(
        System.Windows.Window window,
        double margin = 24,
        bool constrainMaximum = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        var source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null)
            return;

        var workArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var toDeviceIndependent = source.CompositionTarget.TransformFromDevice;
        var topLeft = toDeviceIndependent.Transform(
            new System.Windows.Point(workArea.Left, workArea.Top)
        );
        var bottomRight = toDeviceIndependent.Transform(
            new System.Windows.Point(workArea.Right, workArea.Bottom)
        );
        var availableWidth = Math.Max(320, bottomRight.X - topLeft.X - margin);
        var availableHeight = Math.Max(320, bottomRight.Y - topLeft.Y - margin);

        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);
        if (constrainMaximum)
        {
            window.MaxWidth = Math.Min(window.MaxWidth, availableWidth);
            window.MaxHeight = Math.Min(window.MaxHeight, availableHeight);
        }
        if (!double.IsNaN(window.Width))
            window.Width = Math.Min(window.Width, availableWidth);
        if (!double.IsNaN(window.Height))
            window.Height = Math.Min(window.Height, availableHeight);
    }

    internal static void Restore(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            _ = ShowWindow(window, ShowWindowRestore);
        }
    }

    internal static void BringToForeground(IntPtr window)
    {
        if (window != IntPtr.Zero)
            _ = SetForegroundWindow(window);
    }

    [LibraryImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize
    );

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial bool SetForegroundWindow(IntPtr window);
}
