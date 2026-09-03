using System.Windows;
using System.Windows.Controls;

namespace CANVideoEmulator.Wpf.Controls;

/// <summary>Equal-width compact tiles, wrapping into rows as the viewport narrows.</summary>
public sealed class AdaptiveGridPanel : Panel
{
    public static readonly DependencyProperty MinimumItemWidthProperty = DependencyProperty.Register(
        nameof(MinimumItemWidth), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns), typeof(int), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(AdaptiveGridPanel),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinimumItemWidth { get => (double)GetValue(MinimumItemWidthProperty); set => SetValue(MinimumItemWidthProperty, value); }
    public int MaximumColumns { get => (int)GetValue(MaximumColumnsProperty); set => SetValue(MaximumColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public int ColumnCount { get; private set; } = 1;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinimumItemWidth : availableSize.Width;
        var gap = Math.Max(0, Gap);
        ColumnCount = Math.Clamp((int)((width + gap) / Math.Max(1, MinimumItemWidth + gap)), 1, Math.Max(1, MaximumColumns));
        var cellWidth = Math.Max(0, (width - gap * (ColumnCount - 1)) / ColumnCount);
        _rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }
        var rows = (InternalChildren.Count + ColumnCount - 1) / ColumnCount;
        return new Size(width, rows * _rowHeight + Math.Max(0, rows - 1) * gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var gap = Math.Max(0, Gap);
        var cellWidth = Math.Max(0, (finalSize.Width - gap * (ColumnCount - 1)) / ColumnCount);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(i % ColumnCount * (cellWidth + gap),
                i / ColumnCount * (_rowHeight + gap), cellWidth, _rowHeight));
        return finalSize;
    }
}
