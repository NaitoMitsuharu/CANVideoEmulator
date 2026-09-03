using System.Windows;
using System.Windows.Media;
using CANVideoEmulator.Scenarios;

namespace CANVideoEmulator.Wpf.Controls;

/// <summary>
/// A north-up trajectory view that keeps the current GNSS position centred and
/// scrolls the path beneath it, like a car navigator. No basemap: the drive's
/// own track is the whole picture.
/// </summary>
/// <remarks>
/// The builder projects each fix to local east/north metres, so drawing is a flat
/// affine map: <c>screen = centre + (point − current) / metresPerPixel</c>, with
/// north pointing up. The full route is drawn faintly and the recent
/// <see cref="TrailSeconds"/> brighter, so both "where am I on the route" and
/// "where am I heading" read at a glance.
/// </remarks>
public sealed class TrajectoryMap : FrameworkElement
{
    private static readonly Pen RoutePen = FrozenPen("#3A4658", 3.0);
    private static readonly Pen TrailPen = FrozenPen("#6C82A0", 2.0);
    private static readonly Brush DotFill = FrozenBrush("#8AA6CC");
    private static readonly Pen DotEdge = FrozenPen("#0B0E13", 1.5);

    /// <summary>Metres per pixel; a constant zoom so the map scrolls like a navigator.</summary>
    public double MetresPerPixel { get; set; } = 2.6;

    /// <summary>How many seconds of recent track to highlight.</summary>
    public double TrailSeconds { get; set; } = 15.0;

    public static readonly DependencyProperty GnssProperty = DependencyProperty.Register(
        nameof(Gnss), typeof(GnssTrack), typeof(TrajectoryMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentTimeProperty = DependencyProperty.Register(
        nameof(CurrentTime), typeof(double), typeof(TrajectoryMap),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public GnssTrack? Gnss
    {
        get => (GnssTrack?)GetValue(GnssProperty);
        set => SetValue(GnssProperty, value);
    }

    public double CurrentTime
    {
        get => (double)GetValue(CurrentTimeProperty);
        set => SetValue(CurrentTimeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var gnss = Gnss;
        double w = ActualWidth, h = ActualHeight;
        if (gnss is null || gnss.Count == 0 || w <= 1 || h <= 1)
        {
            return;
        }

        var t = gnss.T;
        var curEast = ScenarioTelemetry.Interpolate(t, gnss.EastM, CurrentTime);
        var curNorth = ScenarioTelemetry.Interpolate(t, gnss.NorthM, CurrentTime);
        var cx = w / 2;
        var cy = h / 2;

        Point Project(int i) => new(
            cx + (gnss.EastM[i] - curEast) / MetresPerPixel,
            cy - (gnss.NorthM[i] - curNorth) / MetresPerPixel);

        // Clip to the control so the off-screen route doesn't paint outside.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));

        dc.DrawGeometry(null, RoutePen, BuildPath(gnss.Count, 0, gnss.Count, Project));

        var trailStart = Math.Max(0, ScenarioTelemetry.LowerBound(t, CurrentTime - TrailSeconds) - 1);
        var trailEnd = Math.Min(gnss.Count, ScenarioTelemetry.LowerBound(t, CurrentTime) + 1);
        if (trailEnd - trailStart >= 2)
        {
            dc.DrawGeometry(null, TrailPen, BuildPath(gnss.Count, trailStart, trailEnd, Project));
        }

        dc.DrawEllipse(DotFill, DotEdge, new Point(cx, cy), 4.0, 4.0);
        dc.Pop();
    }

    private static Geometry BuildPath(int count, int start, int end, Func<int, Point> project)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(project(start), false, false);
            for (var i = start + 1; i < end && i < count; i++)
            {
                ctx.LineTo(project(i), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Pen FrozenPen(string hex, double thickness)
    {
        var pen = new Pen(FrozenBrush(hex), thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static Brush FrozenBrush(string hex)
    {
        var color = (Color)(ColorConverter.ConvertFromString(hex) ?? Colors.Black);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
