using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CANVideoEmulator.Wpf.Controls;

/// <summary>Small pixel-based wheel steps instead of multiple whole cards per notch.</summary>
public static class ScrollTuning
{
    public static readonly DependencyProperty PixelsPerNotchProperty = DependencyProperty.RegisterAttached(
        "PixelsPerNotch", typeof(double), typeof(ScrollTuning), new PropertyMetadata(0.0, OnChanged));
    public static double GetPixelsPerNotch(DependencyObject element) => (double)element.GetValue(PixelsPerNotchProperty);
    public static void SetPixelsPerNotch(DependencyObject element, double value) => element.SetValue(PixelsPerNotchProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement control) return;
        control.PreviewMouseWheel -= OnWheel;
        if ((double)args.NewValue > 0) control.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var control = (UIElement)sender;
        var viewer = FindViewer(control);
        if (viewer is null || viewer.ScrollableHeight <= 0) return;
        viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset -
            e.Delta / 120.0 * GetPixelsPerNotch(control), 0, viewer.ScrollableHeight));
        e.Handled = true;
    }

    private static ScrollViewer? FindViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindViewer(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
        return null;
    }
}
