using System.Windows;
using System.Windows.Media;
using CANVideoEmulator.Scenarios;

namespace CANVideoEmulator.Wpf.Controls;

/// <summary>
/// A small three-axis strip chart showing the last <see cref="WindowSeconds"/>
/// of one IMU stream, ending at <see cref="CurrentTime"/>. Redraws whenever the
/// time advances, so a seek to 30 s shows the 20–30 s window.
/// </summary>
/// <remarks>
/// The three axes share one auto-scaled vertical range so they stay comparable.
/// Colours are deliberately low-saturation (requirement: don't distract from the
/// video). Everything is recomputed in <see cref="OnRender"/> from parallel
/// arrays; at ~25 Hz over 10 s that is a few hundred points, cheap at the UI's
/// 15 Hz tick.
/// </remarks>
public sealed class RollingGraph : FrameworkElement
{
    // Muted axis colours: blue-grey / sage / tan.
    private static readonly Pen PenX = FrozenPen("#6C82A0");
    private static readonly Pen PenY = FrozenPen("#7E947F");
    private static readonly Pen PenZ = FrozenPen("#A2937A");
    private static readonly Pen BaselinePen = FrozenPen("#3A424E", 0.5);

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(ImuSeries), typeof(RollingGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentTimeProperty = DependencyProperty.Register(
        nameof(CurrentTime), typeof(double), typeof(RollingGraph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(RollingGraph),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImuSeries? Series
    {
        get => (ImuSeries?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double CurrentTime
    {
        get => (double)GetValue(CurrentTimeProperty);
        set => SetValue(CurrentTimeProperty, value);
    }

    public double WindowSeconds
    {
        get => (double)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var series = Series;
        double w = ActualWidth, h = ActualHeight;
        if (series is null || series.T.Length == 0 || w <= 1 || h <= 1)
        {
            return;
        }

        var to = CurrentTime;
        var from = to - WindowSeconds;
        var t = series.T;

        // Window is [from, to]; include one sample either side so lines reach the
        // edges instead of stopping at the first/last in-window point.
        var start = Math.Max(0, ScenarioTelemetry.LowerBound(t, from) - 1);
        var end = Math.Min(t.Length, ScenarioTelemetry.LowerBound(t, to) + 1);
        if (end - start < 1)
        {
            return;
        }

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (var i = start; i < end; i++)
        {
            Extend(ref min, ref max, series.X[i]);
            Extend(ref min, ref max, series.Y[i]);
            Extend(ref min, ref max, series.Z[i]);
        }

        if (double.IsInfinity(min) || double.IsInfinity(max))
        {
            return;
        }

        if (max - min < 1e-9)
        {
            // Flat window: give it a nominal span so the line sits mid-height.
            min -= 1.0;
            max += 1.0;
        }

        var pad = (max - min) * 0.12;
        min -= pad;
        max += pad;

        double X(double time) => (time - from) / WindowSeconds * w;
        double Y(double value) => h - (value - min) / (max - min) * h;

        if (min < 0 && max > 0)
        {
            var y0 = Y(0);
            dc.DrawLine(BaselinePen, new Point(0, y0), new Point(w, y0));
        }

        dc.DrawGeometry(null, PenX, BuildLine(t, series.X, start, end, X, Y));
        dc.DrawGeometry(null, PenY, BuildLine(t, series.Y, start, end, X, Y));
        dc.DrawGeometry(null, PenZ, BuildLine(t, series.Z, start, end, X, Y));
    }

    private static Geometry BuildLine(double[] t, double[] v, int start, int end,
                                      Func<double, double> x, Func<double, double> y)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x(t[start]), y(v[start])), false, false);
            for (var i = start + 1; i < end; i++)
            {
                ctx.LineTo(new Point(x(t[i]), y(v[i])), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static void Extend(ref double min, ref double max, double value)
    {
        if (value < min) min = value;
        if (value > max) max = value;
    }

    private static Pen FrozenPen(string hex, double thickness = 1.0)
    {
        var color = (Color)(ColorConverter.ConvertFromString(hex) ?? Colors.Black);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
