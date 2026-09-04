using System.Windows;
using System.Windows.Media;
using CANVideoEmulator.Scenarios;

namespace CANVideoEmulator.Wpf.Controls;

/// <summary>
/// A trajectory view that keeps the current GNSS position centred and scrolls the
/// drive's own track beneath it, like a car navigator. No basemap: the track is
/// the whole picture. Optionally rotates so the direction of travel points up
/// (heading-up) and zooms out with speed.
/// </summary>
/// <remarks>
/// The builder projects each fix to local east/north metres, so drawing is a flat
/// affine map: <c>screen = centre + (point − current) / metresPerPixel</c>, with
/// north up. Heading-up wraps that in a rotation about the centre; auto-zoom
/// scales <c>metresPerPixel</c> with the interpolated speed.
/// </remarks>
public sealed class TrajectoryMap : FrameworkElement
{
    private static readonly Pen RoutePen = FrozenPen("#3A4658", 3.0);
    private static readonly Pen TrailPen = FrozenPen("#6C82A0", 2.0);
    private static readonly Brush DotFill = FrozenBrush("#8AA6CC");
    private static readonly Pen DotEdge = FrozenPen("#0B0E13", 1.5);

    // Last heading held across near-stationary frames so the map doesn't spin
    // when the car is stopped and the movement vector is noise.
    private double _lastHeadingDeg;

    /// <summary>Fixed metres per pixel when auto-zoom is off.</summary>
    public double MetresPerPixel { get; set; } = 2.6;

    /// <summary>How many seconds of recent track to highlight.</summary>
    public double TrailSeconds { get; set; } = 15.0;

    public static readonly DependencyProperty GnssProperty = DependencyProperty.Register(
        nameof(Gnss), typeof(GnssTrack), typeof(TrajectoryMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentTimeProperty = DependencyProperty.Register(
        nameof(CurrentTime), typeof(double), typeof(TrajectoryMap),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HeadingUpProperty = DependencyProperty.Register(
        nameof(HeadingUp), typeof(bool), typeof(TrajectoryMap),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoZoomProperty = DependencyProperty.Register(
        nameof(AutoZoom), typeof(bool), typeof(TrajectoryMap),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public bool HeadingUp
    {
        get => (bool)GetValue(HeadingUpProperty);
        set => SetValue(HeadingUpProperty, value);
    }

    public bool AutoZoom
    {
        get => (bool)GetValue(AutoZoomProperty);
        set => SetValue(AutoZoomProperty, value);
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
        var mpp = MetresPerPixelFor(gnss);

        Point Project(int i) => new(
            cx + (gnss.EastM[i] - curEast) / mpp,
            cy - (gnss.NorthM[i] - curNorth) / mpp);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));

        // Heading-up: rotate the scene so the travel direction points up. The dot
        // stays at the centre either way, so it is drawn after the rotation pops.
        var rotated = HeadingUp;
        if (rotated)
        {
            dc.PushTransform(new RotateTransform(-HeadingDeg(gnss), cx, cy));
        }

        dc.DrawGeometry(null, RoutePen, BuildPath(gnss.Count, 0, gnss.Count, Project));

        var trailStart = Math.Max(0, ScenarioTelemetry.LowerBound(t, CurrentTime - TrailSeconds) - 1);
        var trailEnd = Math.Min(gnss.Count, ScenarioTelemetry.LowerBound(t, CurrentTime) + 1);
        if (trailEnd - trailStart >= 2)
        {
            dc.DrawGeometry(null, TrailPen, BuildPath(gnss.Count, trailStart, trailEnd, Project));
        }

        if (rotated)
        {
            dc.Pop();
        }

        dc.DrawEllipse(DotFill, DotEdge, new Point(cx, cy), 4.0, 4.0);
        dc.Pop();
    }

    private double MetresPerPixelFor(GnssTrack gnss)
    {
        if (!AutoZoom)
        {
            return MetresPerPixel;
        }

        // Zoom out with speed so more of the road ahead is visible at highway
        // pace, in a bit more the closer to a stop. Bounds keep it sane.
        var speed = gnss.SpeedAt(CurrentTime);
        if (double.IsNaN(speed))
        {
            return MetresPerPixel;
        }

        return Math.Clamp(1.6 + speed * 0.045, 1.6, 6.0);
    }

    private double HeadingDeg(GnssTrack gnss)
    {
        // Heading from the movement over the last ~2 s; hold the previous value
        // while nearly stationary so the map doesn't spin on GPS noise.
        var t = gnss.T;
        var e1 = ScenarioTelemetry.Interpolate(t, gnss.EastM, CurrentTime);
        var n1 = ScenarioTelemetry.Interpolate(t, gnss.NorthM, CurrentTime);
        var e0 = ScenarioTelemetry.Interpolate(t, gnss.EastM, CurrentTime - 2.0);
        var n0 = ScenarioTelemetry.Interpolate(t, gnss.NorthM, CurrentTime - 2.0);
        var de = e1 - e0;
        var dn = n1 - n0;
        if (de * de + dn * dn >= 0.8 * 0.8)
        {
            _lastHeadingDeg = Math.Atan2(de, dn) * (180.0 / Math.PI);
        }

        return _lastHeadingDeg;
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
