using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfPoint = System.Windows.Point;

namespace ResoDrive.App.Controls;

internal static class DriveOrderViewport
{
    internal static Rect Bounds(UIElement owner)
    {
        var bounds = new Rect(owner.RenderSize);
        if (owner is not WpfListBox)
            return bounds;
        // ListBox includes its scrollbars; only the content presenter accepts rows.
        if (FindPresenter(owner) is not { } presenter)
            return Rect.Empty;
        bounds.Intersect(presenter.TransformToAncestor(owner).TransformBounds(new Rect(presenter.RenderSize)));
        return bounds;
    }

    internal static (ListBoxItem Container, bool After)? FindDropTarget(WpfListBox list, WpfPoint point)
    {
        if (!Contains(Bounds(list), point))
            return null;
        if (list.InputHitTest(point) is DependencyObject hit &&
            ItemsControl.ContainerFromElement(list, hit) is ListBoxItem container)
        {
            var position = list.TranslatePoint(point, container);
            return (container, position.Y >= container.ActualHeight / 2);
        }
        // Only a realized final row can define the empty space below the list.
        if (list.Items.Count > 0 &&
            list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) is ListBoxItem last &&
            last.TranslatePoint(new WpfPoint(0, last.ActualHeight), list).Y <= point.Y)
            return (last, true);
        return null;
    }

    internal static int ScrollDirection(UIElement owner, WpfPoint point)
    {
        var bounds = Bounds(owner);
        if (!Contains(bounds, point))
            return 0;
        var edge = Math.Min(24, bounds.Height / 3);
        return point.Y < bounds.Top + edge ? -1 : point.Y > bounds.Bottom - edge ? 1 : 0;
    }

    internal static bool Contains(Rect bounds, WpfPoint point) => !bounds.IsEmpty &&
        point.X >= bounds.Left && point.Y >= bounds.Top && point.X < bounds.Right && point.Y < bounds.Bottom;

    private static ScrollContentPresenter? FindPresenter(DependencyObject parent)
    {
        if (parent is ScrollContentPresenter presenter)
            return presenter;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindPresenter(VisualTreeHelper.GetChild(parent, index)) is { } found)
                return found;
        return null;
    }
}
