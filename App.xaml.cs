using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HpPowerSwitcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Smooth horizontal scrolling on mouse wheel for horizontal scroll regions or when Shift is held
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel));
    }

    private static void OnScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        bool isHorizontalOnly = sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                                (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled || sv.ScrollableHeight == 0);

        bool isShiftDown = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if ((isHorizontalOnly || isShiftDown) && sv.ScrollableWidth > 0)
        {
            if (e.Delta < 0)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset + 48);
            }
            else if (e.Delta > 0)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - 48);
            }
            e.Handled = true;
        }
    }
}
