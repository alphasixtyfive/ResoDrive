using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ResoDrive.App;

public partial class TransfersWindow : System.Windows.Window
{
    private const int WorkAreaChanged = 0x001A;
    private const int DisplayChanged = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private readonly System.Windows.Size _preferredSize;
    private readonly Action _openMainWindow;
    private readonly Action _openSettings;
    private System.Drawing.Point _screenAnchor;
    private bool _positioning;
    private bool _showing;
    private bool _closingOrClosed;

    internal TransfersWindow(TransfersViewModel model, Action openMainWindow, Action openSettings)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(openMainWindow);
        ArgumentNullException.ThrowIfNull(openSettings);
        InitializeComponent();
        DataContext = model;
        _openMainWindow = openMainWindow;
        _openSettings = openSettings;
        _preferredSize = new System.Windows.Size(MaxWidth, MaxHeight);
        SourceInitialized += (_, _) =>
        {
            WindowAppearance.ApplyFlyoutFrame(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        };
        SizeChanged += (_, _) => PositionFlyout();
        Deactivated += (_, _) => { if (!_showing && IsVisible) Dismiss(); };
        Closed += (_, _) => _closingOrClosed = true;
    }

    internal void ShowFlyout(System.Drawing.Point screenAnchor)
    {
        if (_closingOrClosed) return;
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
            else Dismiss();
        }
        finally
        {
            _showing = false;
        }
    }

    private void PositionFlyout()
    {
        if (_closingOrClosed || _positioning || new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
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

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void Dismiss()
    {
        if (!_closingOrClosed) Hide();
    }

    private void OpenMainWindow_Click(object sender, RoutedEventArgs e) => Navigate(_openMainWindow);

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Navigate(_openSettings);

    private void Navigate(Action destination)
    {
        if (_closingOrClosed) return;
        Dismiss();
        destination();
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Taskbar/work-area and display changes can move the anchor without changing our content.
        if (!_closingOrClosed && IsVisible && message is WorkAreaChanged or DisplayChanged or WmDpiChanged)
            Dispatcher.BeginInvoke(new Action(PositionFlyout));
        return IntPtr.Zero;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closingOrClosed = true;
        base.OnClosing(e);
        _closingOrClosed = !e.Cancel;
    }
}
