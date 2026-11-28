using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ResoDrive.App.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfPoint = System.Windows.Point;

namespace ResoDrive.App;

public partial class MainWindow
{
    private const string DriveOrderDataFormat = "ResoDrive.DriveOrder";
    private readonly Guid _driveOrderWindowId = Guid.NewGuid();
    private WpfButton? _driveOrderPressedHandle;
    private Guid? _driveOrderPressedMountId;
    private WpfPoint _driveOrderPressPoint;
    private WpfPoint _driveOrderLastPoint;
    private bool _driveOrderDragging;
    private Guid? _driveOrderDraggingMountId;
    private bool _driveOrderSaveBusy;
    private DriveInsertionAdorner? _driveOrderIndicator;
    private AdornerLayer? _driveOrderIndicatorLayer;
    private DriveDragPreviewAdorner? _driveOrderPreview;
    private ScrollViewer? _driveOrderScrollViewer;
    private int _driveOrderScrollDirection;
    private bool _driveOrderPointerInside;

    private bool CanChangeDriveOrder => _store is not null && _model.Mounts.Count > 1 &&
        !_settingsClosing && !IsClosing && !_exitChecking && !_settingsSaveBusy &&
        !_driveOrderSaveBusy && !_accountData.IsBlocked;

    private void DriveOrder_MouseDown(object sender, WpfMouseButtonEventArgs e)
    {
        if (!CanChangeDriveOrder || sender is not WpfButton { DataContext: MountRow row } handle)
            return;
        _driveOrderPressedHandle = handle;
        _driveOrderPressedMountId = row.Id;
        _driveOrderPressPoint = e.GetPosition(MountRows);
        MountRows.SelectedItem = row;
        handle.Focus();
        if (!handle.CaptureMouse())
            ClearDriveOrderPress();
        e.Handled = true;
    }

    private void DriveOrder_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_driveOrderDragging || _driveOrderPressedHandle is not { } handle)
            return;
        if (handle.DataContext is not MountRow row || e.LeftButton != MouseButtonState.Pressed || !CanChangeDriveOrder ||
            _driveOrderPressedMountId != row.Id || !_model.Mounts.Any(mount => mount.Id == row.Id))
        {
            ClearDriveOrderPress();
            return;
        }
        var current = e.GetPosition(MountRows);
        if (Math.Abs(current.X - _driveOrderPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _driveOrderPressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _driveOrderDragging = true;
        _driveOrderDraggingMountId = row.Id;
        handle.ReleaseMouseCapture();
        _driveOrderScrollViewer = FindDriveOrderVisual<ScrollViewer>(MountRows);
        var scrolling = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        scrolling.Tick += DriveOrder_AutoScroll;
        scrolling.Start();
        try
        {
            if (ItemsControl.ContainerFromElement(MountRows, handle) is ListBoxItem source)
                _driveOrderPreview = DriveDragPreviewAdorner.TryCreate(MountRows, source,
                    _driveOrderPressPoint, SystemParameters.HighContrast);
            var data = new System.Windows.DataObject();
            data.SetData(DriveOrderDataFormat, new DriveDrag(_driveOrderWindowId, row.Id));
            System.Windows.DragDrop.DoDragDrop(handle, data, WpfDragDropEffects.Move);
        }
        finally
        {
            scrolling.Stop();
            scrolling.Tick -= DriveOrder_AutoScroll;
            _driveOrderScrollDirection = 0;
            _driveOrderPointerInside = false;
            _driveOrderScrollViewer = null;
            _driveOrderDragging = false;
            _driveOrderDraggingMountId = null;
            ClearDriveOrderPreview();
            ClearDriveOrderIndicator();
            ClearDriveOrderPress();
        }
        e.Handled = true;
    }

    private void DriveOrder_QueryContinueDrag(object sender, System.Windows.QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed || !CanChangeDriveOrder || !IsVisible || !MountRows.IsVisible ||
            _driveOrderDraggingMountId is not Guid sourceId || !_model.Mounts.Any(row => row.Id == sourceId))
        {
            e.Action = System.Windows.DragAction.Cancel;
            e.Handled = true;
        }
    }

    private void DriveOrder_MouseUp(object sender, WpfMouseButtonEventArgs e)
    {
        if (_driveOrderDragging || _driveOrderPressedHandle is not { } handle)
            return;
        var validPress = handle.DataContext is MountRow row && _driveOrderPressedMountId == row.Id;
        ClearDriveOrderPress();
        if (validPress)
            OpenDriveOrderMenu(handle);
        e.Handled = true;
    }

    private void DriveOrder_LostMouseCapture(object sender, WpfMouseEventArgs e)
    {
        if (!_driveOrderDragging)
            ClearDriveOrderPress();
    }

    private void ClearDriveOrderPress()
    {
        var handle = _driveOrderPressedHandle;
        _driveOrderPressedHandle = null;
        _driveOrderPressedMountId = null;
        handle?.ReleaseMouseCapture();
    }

    private void DriveOrderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton handle)
            OpenDriveOrderMenu(handle);
    }

    private void OpenDriveOrderMenu(WpfButton handle)
    {
        if (!CanChangeDriveOrder || handle.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = handle;
        menu.IsOpen = true;
    }

    private void DriveOrderMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu { DataContext: MountRow row } menu)
            return;
        var position = _model.Mounts.IndexOf(row);
        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            var offset = Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture);
            item.IsEnabled = CanChangeDriveOrder && position >= 0 &&
                position + offset >= 0 && position + offset < _model.Mounts.Count;
        }
    }

    private async void DriveOrderMenuMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { DataContext: MountRow row } item)
            await MoveDriveByAsync(row, Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture));
    }

    private async void DriveOrder_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.SystemKey is not (Key.Up or Key.Down) || !CanChangeDriveOrder)
            return;
        var focusedRow = e.OriginalSource is DependencyObject focused
            ? (ItemsControl.ContainerFromElement(MountRows, focused) as ListBoxItem)?.DataContext as MountRow
            : null;
        if ((focusedRow ?? MountRows.SelectedItem as MountRow) is not { } row)
            return;
        e.Handled = true;
        await MoveDriveByAsync(row, e.SystemKey == Key.Up ? -1 : 1);
    }

    private Task MoveDriveByAsync(MountRow row, int offset)
    {
        var position = _model.Mounts.IndexOf(row);
        var target = position + offset;
        return !CanChangeDriveOrder || position < 0 || target < 0 || target >= _model.Mounts.Count
            ? Task.CompletedTask
            : SaveDriveOrderAsync(row.Id, _model.Mounts[target].Id, offset > 0, bringIntoView: true);
    }

    private void DriveOrder_DragOver(object sender, WpfDragEventArgs e)
    {
        if (!TryReadDriveDrag(e, out var dragging))
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            _driveOrderScrollDirection = 0;
            _driveOrderPointerInside = false;
            _driveOrderPreview?.Hide();
            ClearDriveOrderIndicator();
            return;
        }
        var point = e.GetPosition(MountRows);
        _driveOrderLastPoint = point;
        _driveOrderPointerInside = DriveOrderViewport.Contains(DriveOrderViewport.Bounds(MountRows), point);
        _driveOrderScrollDirection = DriveOrderViewport.ScrollDirection(MountRows, point);
        var target = FindDriveDropTarget(point);
        e.Effects = target is not null ? WpfDragDropEffects.Move : WpfDragDropEffects.None;
        if (target is not null)
            _driveOrderPreview?.SetPosition(point);
        else
            _driveOrderPreview?.Hide();
        if (target is not null && target.Value.Row.Id != dragging.MountId)
            ShowDriveOrderIndicator(target.Value.Container, target.Value.After);
        else
            ClearDriveOrderIndicator();
        e.Handled = true;
    }

    private void DriveOrder_DragLeave(object sender, WpfDragEventArgs e)
    {
        var point = e.GetPosition(MountRows);
        if (!DriveOrderViewport.Contains(DriveOrderViewport.Bounds(MountRows), point))
        {
            _driveOrderScrollDirection = 0;
            _driveOrderPointerInside = false;
            _driveOrderPreview?.Hide();
            ClearDriveOrderIndicator();
        }
    }

    private async void DriveOrder_Drop(object sender, WpfDragEventArgs e)
    {
        e.Handled = true;
        _driveOrderScrollDirection = 0;
        _driveOrderPointerInside = false;
        ClearDriveOrderPreview();
        ClearDriveOrderIndicator();
        if (!TryReadDriveDrag(e, out var dragging) || FindDriveDropTarget(e.GetPosition(MountRows)) is not { } target)
        {
            e.Effects = WpfDragDropEffects.None;
            return;
        }
        e.Effects = WpfDragDropEffects.Move;
        if (dragging.MountId == target.Row.Id)
            return;
        await SaveDriveOrderAsync(dragging.MountId, target.Row.Id, target.After, bringIntoView: false);
    }

    private bool TryReadDriveDrag(WpfDragEventArgs e, out DriveDrag dragging)
    {
        dragging = null!;
        if (!CanChangeDriveOrder || !_driveOrderDragging ||
            !e.Data.GetDataPresent(DriveOrderDataFormat, autoConvert: false) ||
            e.Data.GetData(DriveOrderDataFormat, autoConvert: false) is not DriveDrag data ||
            data.WindowId != _driveOrderWindowId || !_model.Mounts.Any(row => row.Id == data.MountId))
            return false;
        dragging = data;
        return true;
    }

    private (MountRow Row, ListBoxItem Container, bool After)? FindDriveDropTarget(WpfPoint point)
    {
        return DriveOrderViewport.FindDropTarget(MountRows, point) is { } target &&
            target.Container.DataContext is MountRow row ? (row, target.Container, target.After) : null;
    }

    private void DriveOrder_AutoScroll(object? sender, EventArgs e)
    {
        if (!CanChangeDriveOrder || !IsVisible || !MountRows.IsVisible || !_driveOrderPointerInside)
        {
            _driveOrderScrollDirection = 0;
            _driveOrderPreview?.Hide();
            ClearDriveOrderIndicator();
            return;
        }
        if (_driveOrderScrollDirection < 0)
            _driveOrderScrollViewer?.LineUp();
        else if (_driveOrderScrollDirection > 0)
            _driveOrderScrollViewer?.LineDown();
        // Status footers can change row heights while the pointer is stationary.
        // Re-hit the current layout on every active drag tick, not only scrolling.
        MountRows.UpdateLayout();
        if (FindDriveDropTarget(_driveOrderLastPoint) is { } target)
        {
            _driveOrderPreview?.SetPosition(_driveOrderLastPoint);
            if (_driveOrderDraggingMountId is Guid sourceId && target.Row.Id != sourceId)
                ShowDriveOrderIndicator(target.Container, target.After);
            else
                ClearDriveOrderIndicator();
        }
        else
        {
            _driveOrderPreview?.Hide();
            ClearDriveOrderIndicator();
        }
    }

    private async Task SaveDriveOrderAsync(Guid sourceId, Guid targetId, bool after, bool bringIntoView)
    {
        await ExecuteUiActionAsync("Drive order could not be saved", async () =>
        {
            if (!CanChangeDriveOrder || !await EnterSettingsMutationAsync())
                return;
            _driveOrderSaveBusy = true;
            var previousFocus = Keyboard.FocusedElement;
            try
            {
                var result = await DriveOrder.SaveAsync(_store!, _settings, sourceId, targetId, after,
                    _lifetimeCancellation.Token);
                if (!result.Succeeded || result.Value is null)
                {
                    ShowError("Drive order was not saved", result.Error?.Message ?? "The previous order was kept.");
                    return;
                }
                _settings = result.Value;
                if (_settingsClosing || IsClosing || _accountData.IsBlocked)
                    return;
                _model.ReorderMounts(_settings.Mounts.Select(mount => mount.Id).ToArray());
                UpdateTrayStatus();
                var moved = _model.Mounts.FirstOrDefault(row => row.Id == sourceId);
                if (moved is not null)
                {
                    MountRows.SelectedItem = moved;
                    if (bringIntoView)
                        MountRows.ScrollIntoView(moved);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_settingsClosing || IsClosing)
                            return;
                        if (previousFocus is UIElement { IsVisible: true, IsEnabled: true } focus &&
                            focus.IsDescendantOf(MountRows) &&
                            (ItemsControl.ContainerFromElement(MountRows, focus) as ListBoxItem)?.DataContext is MountRow focusedRow && focusedRow.Id == sourceId)
                            focus.Focus();
                        else if (MountRows.ItemContainerGenerator.ContainerFromItem(moved) is ListBoxItem container)
                            FindDriveOrderVisual<WpfButton>(container, "DriveOrderHandle")?.Focus();
                    }, DispatcherPriority.Loaded);
                }
            }
            finally
            {
                _driveOrderSaveBusy = false;
                _settingsMutationGate.Release();
            }
        });
    }

    private void ShowDriveOrderIndicator(ListBoxItem target, bool after)
    {
        var layer = AdornerLayer.GetAdornerLayer(MountRows);
        if (layer is null)
            return;
        if (_driveOrderIndicator is null)
        {
            _driveOrderIndicator = new DriveInsertionAdorner(MountRows,
                (System.Windows.Media.Brush)FindResource("AccentBrush"));
            layer.Add(_driveOrderIndicator);
            _driveOrderIndicatorLayer = layer;
        }
        var edge = target.TranslatePoint(new WpfPoint(0, after ? target.ActualHeight : 0), MountRows);
        _driveOrderIndicator.SetPosition(edge.X + 3, edge.Y, Math.Max(0, target.ActualWidth - 6));
    }

    private void ClearDriveOrderIndicator()
    {
        if (_driveOrderIndicator is null)
            return;
        _driveOrderIndicator.Dispose();
        _driveOrderIndicatorLayer?.Remove(_driveOrderIndicator);
        _driveOrderIndicator = null;
        _driveOrderIndicatorLayer = null;
    }

    private void ClearDriveOrderPreview()
    {
        _driveOrderPreview?.Dispose();
        _driveOrderPreview = null;
    }

    private static T? FindDriveOrderVisual<T>(DependencyObject parent, string? name = null) where T : DependencyObject
    {
        if (parent is T match && (name is null || parent is FrameworkElement element && element.Name == name))
            return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (FindDriveOrderVisual<T>(VisualTreeHelper.GetChild(parent, index), name) is { } found)
                return found;
        }
        return null;
    }

    private sealed record DriveDrag(Guid WindowId, Guid MountId);

    private sealed class DriveInsertionAdorner : Adorner, IDisposable
    {
        private readonly System.Windows.Media.Pen _pen;
        private Rect _viewport;
        private WpfPoint _start;
        private WpfPoint _end;

        public DriveInsertionAdorner(UIElement element, System.Windows.Media.Brush brush) : base(element)
        {
            IsHitTestVisible = false;
            _viewport = DriveOrderViewport.Bounds(element);
            Opacity = SystemParameters.HighContrast ? 1 : 0.75;
            _pen = new System.Windows.Media.Pen(brush, SystemParameters.HighContrast ? 2 : 1.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            element.LayoutUpdated += Element_LayoutUpdated;
        }

        public void SetPosition(double x, double y, double width)
        {
            var start = new WpfPoint(x, y);
            var end = new WpfPoint(x + width, y);
            if (_start == start && _end == end) return;
            _start = start;
            _end = end;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var viewport = DriveOrderViewport.Bounds(AdornedElement);
            if (viewport.IsEmpty)
                return;
            drawingContext.PushClip(new RectangleGeometry(viewport));
            drawingContext.DrawLine(_pen, _start, _end);
            drawingContext.Pop();
        }

        public void Dispose() => AdornedElement.LayoutUpdated -= Element_LayoutUpdated;

        private void Element_LayoutUpdated(object? sender, EventArgs e)
        {
            var viewport = DriveOrderViewport.Bounds(AdornedElement);
            if (_viewport == viewport)
                return;
            _viewport = viewport;
            InvalidateVisual();
        }
    }
}
