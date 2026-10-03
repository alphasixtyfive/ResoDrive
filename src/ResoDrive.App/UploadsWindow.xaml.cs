using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ResoDrive.App;

public partial class UploadsWindow : System.Windows.Window
{
    private const int WorkAreaChanged = 0x001A;
    private const int DisplayChanged = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private readonly System.Windows.Size _preferredSize;
    private readonly Action _openMainWindow;
    private System.Drawing.Point _screenAnchor;
    private bool _positioning;
    private bool _showing;

    internal UploadsWindow(UploadsViewModel model, Action openMainWindow)
    {
        InitializeComponent();
        DataContext = model;
        _openMainWindow = openMainWindow;
        _preferredSize = new System.Windows.Size(MaxWidth, MaxHeight);
        SourceInitialized += (_, _) =>
        {
            WindowAppearance.ApplyFlyoutFrame(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        };
        SizeChanged += (_, _) => PositionFlyout();
        Deactivated += (_, _) => { if (!_showing && IsVisible) Hide(); };
    }

    internal void ShowFlyout(System.Drawing.Point screenAnchor)
    {
        _showing = true;
        try
        {
            _screenAnchor = screenAnchor;
            // Move to the requested monitor before measuring, without showing an initial frame there.
            Opacity = 0;
            _ = new WindowInteropHelper(this).EnsureHandle();
            PositionFlyout();
            Show();
            UpdateLayout();
            PositionFlyout();
            Opacity = 1;
            WindowAppearance.BringToForeground(new WindowInteropHelper(this).Handle);
            if (Activate() || IsActive) DismissButton.Focus();
            else Hide();
        }
        finally
        {
            _showing = false;
        }
    }

    private void PositionFlyout()
    {
        if (_positioning || new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        _positioning = true;
        try
        {
            WindowAppearance.PositionFlyout(this, _screenAnchor, _preferredSize);
        }
        finally
        {
            _positioning = false;
        }
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Hide();

    private void OpenMainWindow_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _openMainWindow();
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Taskbar/work-area and display changes can move the anchor without changing our content.
        if (IsVisible && message is WorkAreaChanged or DisplayChanged or WmDpiChanged)
            Dispatcher.BeginInvoke(new Action(PositionFlyout));
        return IntPtr.Zero;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
