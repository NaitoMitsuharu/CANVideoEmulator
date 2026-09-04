using System.Text.Json;

namespace CANVideoEmulator.Scenarios;

/// <summary>
/// The optional <c>telemetry.json</c> sidecar (GNSS + IMU), parsed for the HUD
/// overlay. Every timestamp is seconds from the scenario's t=0 (the first CAN
/// frame) -- the same base <see cref="ReplaySession.Position"/> reports -- so a
/// lookup is just <c>at = clock.CurrentTime.TotalSeconds</c>.
/// </summary>
/// <remarks>
/// The overlay is cosmetic and must never stop playback, so
/// <see cref="ScenarioPackage"/> swallows load/parse failures and simply leaves
/// the overlay hidden. Arrays are stored parallel (SoA) because the controls
/// slice them by time window every frame and that is the cheap shape to do it in.
/// </remarks>
public sealed class ScenarioTelemetry
{
    // IMU series are surfaced in this fixed display order (accel, gyro, mag).
    private static readonly string[] ImuOrder = ["accelerometer", "gyro", "magnetometer"];

    private static readonly Dictionary<string, string> ImuLabels = new()
    {
        ["accelerometer"] = "ACCEL",
        ["gyro"] = "GYRO",
        ["magnetometer"] = "MAG",
    };

    private ScenarioTelemetry(GnssTrack? gnss, IReadOnlyList<ImuSeries> imu)
    {
        Gnss = gnss;
        Imu = imu;
    }

    public GnssTrack? Gnss { get; }

    /// <summary>IMU series in display order; empty when the segment had no IMU.</summary>
    public IReadOnlyList<ImuSeries> Imu { get; }

    public bool HasGnss => Gnss is { Count: > 0 };

    public bool HasImu => Imu.Count > 0;

    public bool HasAny => HasGnss || HasImu;

    public static ScenarioTelemetry Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        GnssTrack? gnss = null;
        if (root.TryGetProperty("gnss", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            var t = ReadArray(g, "t");
            if (t.Length > 0)
            {
                gnss = new GnssTrack(
                    t,
                    ReadArrayAligned(g, "speed_kmh", t.Length),
                        ReadArrayAligned(g, "lat", t.Length),
                        ReadArrayAligned(g, "lon", t.Length),
                    ReadArrayAligned(g, "east_m", t.Length),
                    ReadArrayAligned(g, "north_m", t.Length));
            }
        }

        var imu = new List<ImuSeries>();
        if (root.TryGetProperty("imu", out var imuNode) && imuNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in ImuOrder)
            {
                if (!imuNode.TryGetProperty(key, out var s) || s.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var t = ReadArray(s, "t");
                if (t.Length == 0)
                {
                    continue;
                }

                var unit = s.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString() ?? string.Empty
                    : string.Empty;
                imu.Add(new ImuSeries(
                    ImuLabels.GetValueOrDefault(key, key.ToUpperInvariant()), unit, t,
                    ReadArrayAligned(s, "x", t.Length),
                    ReadArrayAligned(s, "y", t.Length),
                    ReadArrayAligned(s, "z", t.Length)));
            }
        }

        return new ScenarioTelemetry(gnss, imu);
    }

    private static double[] ReadArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new double[array.GetArrayLength()];
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            result[i++] = item.GetDouble();
        }

        return result;
    }

    /// <summary>Read a value array, padding/truncating to match the time array.</summary>
    private static double[] ReadArrayAligned(JsonElement parent, string name, int length)
    {
        var raw = ReadArray(parent, name);
        if (raw.Length == length)
        {
            return raw;
        }

        var aligned = new double[length];
        Array.Copy(raw, aligned, Math.Min(raw.Length, length));
        return aligned;
    }

    /// <summary>
    /// Linear interpolation over a parallel (time, value) pair, clamped to the
    /// endpoints. Times are assumed ascending, as the builder writes them.
    /// </summary>
    public static double Interpolate(double[] t, double[] v, double at)
    {
        if (t.Length == 0)
        {
            return double.NaN;
        }

        if (at <= t[0])
        {
            return v[0];
        }

        if (at >= t[^1])
        {
            return v[^1];
        }

        var hi = LowerBound(t, at);
        var lo = hi - 1;
        var span = t[hi] - t[lo];
        if (span <= 0)
        {
            return v[hi];
        }

        var frac = (at - t[lo]) / span;
        return v[lo] + (v[hi] - v[lo]) * frac;
    }

    /// <summary>First index whose time is &gt;= <paramref name="at"/> (binary search).</summary>
    public static int LowerBound(double[] t, double at)
    {
        int lo = 0, hi = t.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (t[mid] < at)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}

/// <summary>GNSS fixes with geographic coordinates and a local metre projection; speed is in km/h.</summary>
public sealed class GnssTrack(
    double[] t,
    double[] speedKmh,
    double[] latitude,
    double[] longitude,
    double[] eastM,
    double[] northM)
{
    public double[] T { get; } = t;
    public double[] SpeedKmh { get; } = speedKmh;
    public double[] Latitude { get; } = latitude;
    public double[] Longitude { get; } = longitude;
    public double[] EastM { get; } = eastM;
    public double[] NorthM { get; } = northM;

    public int Count => T.Length;

    /// <summary>Interpolated speed (km/h) at scenario time <paramref name="at"/>.</summary>
    public double SpeedAt(double at) => ScenarioTelemetry.Interpolate(T, SpeedKmh, at);

    public double LatitudeAt(double at) => ScenarioTelemetry.Interpolate(T, Latitude, at);

    public double LongitudeAt(double at) => ScenarioTelemetry.Interpolate(T, Longitude, at);
}

/// <summary>One IMU stream: three device-frame axes [forward, right, down].</summary>
public sealed class ImuSeries(string label, string unit, double[] t, double[] x, double[] y, double[] z)
{
    public string Label { get; } = label;
    public string Unit { get; } = unit;
    public double[] T { get; } = t;
    public double[] X { get; } = x;
    public double[] Y { get; } = y;
    public double[] Z { get; } = z;
}
